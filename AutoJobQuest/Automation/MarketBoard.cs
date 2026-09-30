using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>マーケットボードで買う1件。</summary>
/// <param name="Candidates">候補のアイテム（1つなら指定品。複数なら「どれか1種類を Need 個」で一番安いもの）。</param>
/// <param name="Need">要る数（カバンの所持数を引いた不足数）。</param>
/// <param name="Label">記録用の名前。</param>
/// <param name="TargetOwned">指定品のとき「持っていたい総数」（計画時点の所持数＋不足数）。買う直前にカバンを数え直す基準。</param>
/// <param name="StopIfUnavailable">
/// 買えなければ、その場で全体を止める（マテリア：代わりの手段は無いので、周回で買い直さない。
/// 出品が0件なら、自分だけに見えるチャットに「マーケットボードに○○の出品がなかった為、動作を停止しました」を出す）。
/// </param>
public sealed record MarketNeed(List<uint> Candidates, int Need, string Label, int? TargetOwned = null, bool StopIfUnavailable = false);

/// <summary>
/// マーケットボード（MB）の設置場所をゲームデータから引く。
///
///  ・MB は EObj。どの EObj が MB かは、名前（言語）ではなく「EObj.Data が MB の台本
///    （CustomTalk の名前が CmnDefMarketBoard で始まる行）を指すか」で決める。
///  ・座標は Level シートに無い。配置ファイル（lgb）を読む。planlive / planevent / planmap / bg に散っている。
///    planner.lgb は一部で例外になり1件に76秒かかるので読まない。
///  ・起動時には読まない。必要になった街のぶんだけ読む（小さい3種を先に、無ければ bg.lgb）。
/// </summary>
public static class MarketBoardLocator
{
    private static HashSet<uint>? mbBaseIds;

    // 小さい3種（planlive / planevent / planmap）だけで見つかった位置と、bg.lgb まで読んだ位置
    private static readonly Dictionary<uint, List<Vector3>> SmallCache = [];
    private static readonly Dictionary<uint, List<Vector3>> BgCache = [];

    /// <summary>MB の EObj の行 ID。</summary>
    public static HashSet<uint> BaseIds
    {
        get
        {
            if (mbBaseIds != null)
                return mbBaseIds;

            var talkIds = Svc.Data.GetExcelSheet<CustomTalk>()
                .Where(r => r.Name.ExtractText().StartsWith("CmnDefMarketBoard", StringComparison.Ordinal))
                .Select(r => r.RowId)
                .ToHashSet();
            mbBaseIds = Svc.Data.GetExcelSheet<EObj>()
                .Where(r => talkIds.Contains(r.Data.RowId))
                .Select(r => r.RowId)
                .ToHashSet();
            return mbBaseIds;
        }
    }

    /// <summary>
    /// そのエリアの MB の位置（無ければ空）。
    /// <paramref name="allowBg"/> が false なら小さい3種だけを読む（大きい bg.lgb は、どの街でも見つからなかったときの2周目に回す）。
    /// </summary>
    public static List<Vector3> BoardsIn(uint territory, bool allowBg = true)
    {
        var dir = LevelDir(territory);
        if (!SmallCache.TryGetValue(territory, out var small))
        {
            small = [];
            if (dir != null)
                foreach (var file in new[] { "planlive.lgb", "planevent.lgb", "planmap.lgb" })
                    Scan($"bg/{dir}{file}", small);
            SmallCache[territory] = small;
        }

        if (small.Count > 0 || !allowBg)
            return small;

        if (!BgCache.TryGetValue(territory, out var full))
        {
            full = [];
            if (dir != null)
                Scan($"bg/{dir}bg.lgb", full);
            BgCache[territory] = full;
        }

        return full;
    }

    private static string? LevelDir(uint territory)
    {
        if (!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t))
            return null;
        var bg = t.Bg.ExtractText();
        var at = bg.IndexOf("/level/", StringComparison.Ordinal);
        return at >= 0 ? bg[..(at + "/level/".Length)] : null;
    }

    private static void Scan(string path, List<Vector3> into)
    {
        LgbFile? lgb;
        try
        {
            lgb = Svc.Data.GetFile<LgbFile>(path);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[AutoJobQuest] {path} を読めませんでした: {ex.GetType().Name}");
            return;
        }

        if (lgb == null)
            return;

        var ids = BaseIds;
        foreach (var layer in lgb.Layers)
        {
            foreach (var io in layer.InstanceObjects)
            {
                if (io.AssetType != LayerEntryType.EventObject || io.Object is not LayerCommon.EventInstanceObject eo)
                    continue;
                if (!ids.Contains(eo.ParentData.BaseId))
                    continue;
                var tr = io.Transform.Translation;
                into.Add(new Vector3(tr.X, tr.Y, tr.Z));
            }
        }
    }

    /// <summary>
    /// 行き先の街を選ぶ。条件：街（TerritoryIntendedUse=0）で、解放済みのメインのエーテライトが同じエリアにあり、MB があること。
    /// いまいる街に MB があればそこを使う。
    /// </summary>
    public static uint? ChooseTown()
    {
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        if (terr.TryGetRow(Me.Territory, out var here) && here.TerritoryIntendedUse.RowId == 0 && BoardsIn(Me.Territory).Count > 0)
            return Me.Territory;

        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        var towns = new List<uint>();
        foreach (var a in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!a.IsAetheryte || !unlocked.Contains(a.RowId))
                continue;
            var tid = a.Territory.RowId;
            if (!terr.TryGetRow(tid, out var tt) || tt.TerritoryIntendedUse.RowId != 0 || towns.Contains(tid))
                continue;
            towns.Add(tid);
        }

        // 1周目は小さい配置ファイルだけで探し、見つからなければ2周目で bg.lgb も読む（大きいファイルを先に読まない）
        foreach (var allowBg in new[] { false, true })
            foreach (var tid in towns)
                if (BoardsIn(tid, allowBg).Count > 0)
                    return tid;

        return null;
    }
}

/// <summary>
/// 検索結果（件数）の通知を受けるフック。InfoProxyItemSearch.ProcessRequestResult(件数, エラー) は
/// 検索要求の応答で呼ばれる（HaselCommon も同じ関数で開始を捉えている）。
/// 件数が分かれば「出品一覧が全部届いたか」を ListingCount と比べて判定できる（固定時間で待たずに済む）。
/// </summary>
public sealed unsafe class MarketBoardWatcher : IDisposable
{
    private readonly Hook<InfoProxyItemSearch.Delegates.ProcessRequestResult>? hook;
    private readonly bool purchaseSubscribed;

    // 「買えた」の通知（Dalamud の IMarketBoard.ItemPurchased。サーバーの応答から出る）の通し番号と、最後に買えた品。
    // 通知がどのスレッドで来るかは確かめていないので、読み書きは Interlocked / volatile / lock で行う
    private int purchaseSerial;
    private volatile uint lastPurchasedItem;
    private bool historySubscribed;

    // 購入の応答（InfoProxyItemSearch.ProcessPurchaseResponse）。成功も断られたも、この応答の errorMessageId で決める
    // （0＝成功、0以外＝断られた理由の LogMessage の行番号：ゲーム本体の逆アセンブルで確定）。
    // 送った出品とは、応答のときの LastPurchasedMarketboardItem.ListingId で照合する（ゲームはこの欄を読むだけで書き換えない）
    private readonly Hook<InfoProxyItemSearch.Delegates.ProcessPurchaseResponse>? responseHook;
    private readonly object responseLock = new();
    private readonly List<(int Serial, ulong ListingId, uint Message)> responses = [];
    private int responseSerial;

    // 出品一覧のページの番号：件数の応答の直後にゲームが振った番号と、その番号のページを受け取った数
    private volatile byte seqAtResult;
    private int pagesForSeq;
    private readonly bool offeringsSubscribed;

    // 最後に届いた取引履歴（品と、1個あたりの売値。Dalamud の IMarketBoard.HistoryReceived：SalePrice は単価）
    private readonly object historyLock = new();
    private uint historyItem;
    private List<uint> historyUnitPrices = [];

    public int Serial { get; private set; }

    /// <summary>「買えた」の通知の通し番号（送る前に控え、送った後に増えたかを見る）。</summary>
    public int PurchaseSerial => Volatile.Read(ref this.purchaseSerial);

    /// <summary>最後に「買えた」の通知が来た品（アイテム ID）。</summary>
    public uint LastPurchasedItem => this.lastPurchasedItem;

    /// <summary>購入の応答の通し番号（送る前に控え、送った後の応答だけを見る）。</summary>
    public int ResponseSerial => Volatile.Read(ref this.responseSerial);

    /// <summary>購入の応答の記録（通し番号・出品の番号・errorMessageId。直近 20 件）。</summary>
    public List<(int Serial, ulong ListingId, uint Message)> Responses()
    {
        lock (this.responseLock)
            return [.. this.responses];
    }

    /// <summary>購入の応答を受け取れる仕組みがあるか（無ければ、結果が分からないときは買い直さずに止める）。</summary>
    public bool ResponseAvailable => this.responseHook != null;

    /// <summary>件数の応答の直後にゲームが振ったページの番号（CurrentRequestId）。</summary>
    public byte SeqAtResult => this.seqAtResult;

    /// <summary>その番号のページを受け取った数。</summary>
    public int PagesForSeq => Volatile.Read(ref this.pagesForSeq);

    /// <summary>出品一覧の通知を受け取れているか（false なら、ページの番号での照合はできない）。</summary>
    public bool OfferingsAvailable => this.offeringsSubscribed;

    /// <summary>その品の最近の取引の単価（最大20件。届いていなければ空）。</summary>
    public List<uint> HistoryUnitPrices(uint itemId)
    {
        lock (this.historyLock)
            return this.historyItem == itemId ? [.. this.historyUnitPrices] : [];
    }

    public int LastCount { get; private set; }

    public int LastError { get; private set; }

    public MarketBoardWatcher(IGameInteropProvider interop)
    {
        try
        {
            this.hook = interop.HookFromAddress<InfoProxyItemSearch.Delegates.ProcessRequestResult>(
                (nint)InfoProxyItemSearch.Addresses.ProcessRequestResult.Value, this.Detour);
            this.hook.Enable();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoJobQuest] マーケットの検索結果フックを作れませんでした");
        }

        try
        {
            Svc.MarketBoard.ItemPurchased += this.OnItemPurchased;
            this.purchaseSubscribed = true;
        }
        catch (Exception ex)
        {
            // 通知が取れなくても、結果が分からないときは買い直さずに止めるので二重には買わない（記録に残す）
            Svc.Log.Error(ex, "[AutoJobQuest] マーケットの購入の通知を受け取れませんでした");
        }

        try
        {
            Svc.MarketBoard.HistoryReceived += this.OnHistory;
            this.historySubscribed = true;
        }
        catch (Exception ex)
        {
            // 取引履歴が取れないと、相場から外れた高値の確認ができない（1回の額の確認は働く）
            Svc.Log.Error(ex, "[AutoJobQuest] マーケットの取引履歴の通知を受け取れませんでした");
        }

        try
        {
            Svc.MarketBoard.OfferingsReceived += this.OnOfferings;
            this.offeringsSubscribed = true;
        }
        catch (Exception ex)
        {
            // 受け取れないと、同じ品の検索し直しで古い一覧を今回の分と取り違えうる。買う前に止める（MarketBoardTask.OnStart）
            Svc.Log.Error(ex, "[AutoJobQuest] マーケットの出品一覧の通知を受け取れませんでした");
        }

        try
        {
            this.responseHook = interop.HookFromAddress<InfoProxyItemSearch.Delegates.ProcessPurchaseResponse>(
                (nint)InfoProxyItemSearch.Addresses.ProcessPurchaseResponse.Value, this.ResponseDetour);
            this.responseHook.Enable();
        }
        catch (Exception ex)
        {
            // 断られたことが分からないと、何も変わらない購入は「分からない」で止まる（二重には買わない）
            Svc.Log.Error(ex, "[AutoJobQuest] マーケットの購入の応答のフックを作れませんでした");
        }
    }

    private void ResponseDetour(InfoProxyItemSearch* thisPtr, uint itemId, uint errorMessageId)
    {
        // 送った出品の番号は、元の処理の前に読む（元の処理が一覧を詰め直しても、この欄は書き換えないが、念のため先に取る）
        ulong listing = 0;
        try
        {
            listing = thisPtr != null ? thisPtr->LastPurchasedMarketboardItem.ListingId : 0;
        }
        catch
        {
            // 読めなければ 0（照合できない＝応答なしとして扱い、分からなければ止める）
        }

        this.responseHook!.Original(thisPtr, itemId, errorMessageId);
        try
        {
            // errorMessageId：0＝成功、0以外＝断られた理由（LogMessage の行番号。ゲームがそのままチャットに出す）。
            // どの失敗にどの値が来るかはサーバーが決める（記録に残す）。品目 ID は HQ のとき +1,000,000 が付いてくる
            Core.DebugLog.Current?.Line("マーケット",
                $"購入の応答：品 {PurchaseReply.NormalizeItem(itemId)}（生 {itemId}）・出品 {listing}・errorMessageId {errorMessageId}{MessageText(errorMessageId)}");
            lock (this.responseLock)
            {
                var serial = Interlocked.Increment(ref this.responseSerial);
                this.responses.Add((serial, listing, errorMessageId));
                if (this.responses.Count > 20)
                    this.responses.RemoveAt(0);
            }
        }
        catch
        {
            // フックの中では例外を外へ出さない
        }
    }

    private void OnOfferings(Dalamud.Game.Network.Structures.IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            // ゲームもページの 0x5A2 の1バイトと CurrentRequestId を比べて、今回の分かを決める（違うページは取り込まない）
            if ((byte)offerings.RequestId == this.seqAtResult)
                Interlocked.Increment(ref this.pagesForSeq);
        }
        catch
        {
            // 通知の中では例外を外へ出さない
        }
    }

    /// <summary>LogMessage の文言（無ければ空）。記録用。</summary>
    public static string MessageText(uint id)
        => id != 0 && Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().TryGetRow(id, out var row)
            ? $"（LogMessage#{id}「{row.Text.ExtractText()}」）"
            : string.Empty;

    private void OnHistory(Dalamud.Game.Network.Structures.IMarketBoardHistory history)
    {
        try
        {
            var prices = history.HistoryListings.Where(h => h.SalePrice > 0).Select(h => h.SalePrice).ToList();
            lock (this.historyLock)
            {
                this.historyItem = history.ItemId;
                this.historyUnitPrices = prices;
            }
        }
        catch
        {
            // 通知の中では例外を外へ出さない
        }
    }

    private void OnItemPurchased(Dalamud.Game.Network.Structures.IMarketBoardPurchase purchase)
    {
        try
        {
            this.lastPurchasedItem = purchase.CatalogId;
            Interlocked.Increment(ref this.purchaseSerial);
        }
        catch
        {
            // 通知の中では例外を外へ出さない
        }
    }

    public bool Available => this.hook != null;

    private void Detour(InfoProxyItemSearch* thisPtr, byte resultCount, int errorCode)
    {
        this.hook!.Original(thisPtr, resultCount, errorCode);
        try
        {
            this.LastCount = resultCount;
            this.LastError = errorCode;

            // 件数 > 0 なら、ゲームはこの中で今回のページの番号を振っている（CurrentRequestId）。控えて、ページの数を数え直す
            this.seqAtResult = thisPtr->CurrentRequestId;
            Interlocked.Exchange(ref this.pagesForSeq, 0);
            this.Serial++;
        }
        catch
        {
            // フックの中では例外を外へ出さない
        }
    }

    public void Dispose()
    {
        this.hook?.Dispose();
        this.responseHook?.Dispose();
        if (this.purchaseSubscribed)
        {
            try
            {
                Svc.MarketBoard.ItemPurchased -= this.OnItemPurchased;
            }
            catch
            {
                // アンロード中なので握り潰す
            }
        }

        if (this.historySubscribed)
        {
            try
            {
                Svc.MarketBoard.HistoryReceived -= this.OnHistory;
            }
            catch
            {
                // アンロード中なので握り潰す
            }
        }

        if (this.offeringsSubscribed)
        {
            try
            {
                Svc.MarketBoard.OfferingsReceived -= this.OnOfferings;
            }
            catch
            {
                // アンロード中なので握り潰す
            }
        }
    }
}

/// <summary>
/// マーケットボードで買う。
///
/// 流れ:
///   MB の場所をゲームデータから取得 → 座標まで自動移動 → アクセス → 欲しいアイテムを検索窓に入力
///   → 結果から欲しいアイテムを選択 → 出品一覧から「必要数以上の数が出ていて、合計金額が一番低い」出品を買う
///   （例：5個ほしいとき、4個と7個の出品なら7個を買う）→ 買ったら MB を閉じる。
/// 必ずカバンの所持数を確かめ、不足分だけを買う（呼び出し側が不足数を渡し、ここでも買う直前に数え直す）。
/// 1回の購入額が設定値（既定 500,000 ギル）を超えるときは確認窓を出す（
/// 合計ではなく1回ごとで判定）。「いいえ」で自動動作を止める。
/// さらに、単価が最近の取引の中央値の3倍（設定）を超えるとき、この実行の合計が任意の上限（設定。既定は使わない）を超えるときも
/// 確認窓を出す（誤ってギルを大量に使わないため。PurchaseGuard.ConfirmReasons）。
/// 購入の要求を送る前に取引の控えを設定ファイルに保存し、結果が分からないまま止まったら次の開始時に利用者に確かめてもらう。
///
/// 購入そのものは関数経路（SetLastPurchasedItem → SendPurchaseRequestPacket）で行う。
/// 画面経路の「行を選ぶ」操作は、どの実装にも撃って動いた記録が無く未確認のため使わない。
/// 関数経路はゲームの確認ダイアログを通らないので、送る直前に出品の中身（品・個数・単価）を照合し、
/// 送った後は「ギルが減った AND 品が増えた」で確かめる。
/// 必要数を満たす出品が1件も無いときは、単価の安い順に複数の出品を買って満たす（記録に残す）。
/// </summary>
public sealed unsafe class MarketBoardTask : AutoTask
{
    private enum Phase
    {
        Travel,
        Approach,
        Open,
        Next,
        Search,
        WaitResults,
        PickItem,
        WaitListings,
        Decide,
        WaitConfirm,
        Buy,
        WaitBought,
        Close,
    }

    private readonly List<MarketNeed> needs;
    private readonly MarketBoardWatcher watcher;

    private Phase phase = Phase.Travel;
    private uint town;
    private Vector3 board;
    private AutoTask? sub;

    private int needIndex = -1;
    private MarketNeed? current;
    private int candidateIndex;
    private uint searching;
    private int serialBefore;
    private int expectedCount;

    // 候補ごとの最安（候補が複数のとき、全部見てから一番安いものを買う）
    private readonly Dictionary<uint, long> bestByCandidate = [];
    private uint buyingItem;
    private ulong buyingListingId;
    private long buyingTotal;
    private int buyingQuantity;
    private long gilBefore;
    private int countBefore;
    private int confirmTicket = -1;
    private int boughtForCurrent;
    private int attempts;

    // 買えなければ止める品（マテリア）で、出品0件を見た回数（2回続けて見たら止める：前の要求の応答が遅れて届いた0件と取り違えないため）
    private int zeroListingsSeen;

    // いま見ている品の、自分のリテイナーの出品の数（止めるときの文言に使う）
    private int ownListings;

    // 出品を選んだときの不足数（送る直前に変わっていたら選び直す）
    private int needAtDecide;

    // 購入額の確認で「はい」をもらった出品と金額（取り直した一覧で同じ出品・同額以下なら、確認をやり直さない）
    private ulong approvedListingId;
    private long approvedTotal;

    // 検索の失敗（混雑など）のあと、次に検索してよい時刻。失敗が続くほど間をあける
    // （SimpleTweaks RefreshMarketPrices と同じ考え方：2秒＋0.5秒×(回数−1)。進む条件ではなく、要求を出しすぎない抑え）
    private DateTime searchNotBefore = DateTime.MinValue;

    // 検索と検索の最小の間隔（要求を出しすぎない抑え。進む条件ではない）
    private static readonly TimeSpan MinSearchInterval = TimeSpan.FromSeconds(1);

    // こちらが話しかけて MB を開いたか（止めたときに、自分が開いた画面だけを閉じるため）
    private bool openedByMe;
    private DateTime interactAt = DateTime.MinValue;

    // 画面を閉じる操作の間隔（毎フレーム閉じる命令を送らない）
    private DateTime closeAt = DateTime.MinValue;

    // 買い終えた出品（買った直後の一覧は古いまま残るので、同じ出品を選び直さない）
    private readonly HashSet<ulong> boughtListings = [];

    // 断られた出品（選び直さない。断られた後の一覧に残っていることがある）
    private readonly HashSet<ulong> rejectedListings = [];

    // 同じ品を取り直すとき、品名の検索をやり直さずに今の検索結果の行を押す（品名の検索結果が更新される前に
    // 古い結果を押すことを防ぐ。同じ語の検索結果は変わらない）
    private bool reuseResults;

    // 候補が複数のときの「始めた時点の候補の所持数の合計」（不足数をカバンの増え方で数えるため）
    private int candidatesOwnedAtStart;

    // 購入の要求を送る前の「買えた」の通知の通し番号（送った後に、その品の通知が来たかを見る）
    private int purchaseSerialBefore;

    /// <summary>この実行で MB に払った合計（ギル）。</summary>
    public static long SpentThisRun { get; set; }

    /// <summary>この実行の合計を、いくらまで了承済みか（任意の上限を使わないなら 0。実行の始めに設定の値にする）。</summary>
    public static long RunApprovedUpTo { get; set; }

    // 確認を出した理由に「この実行の合計」が入っていたか（了承されたら、了承済みの額を上げる）
    private bool confirmIncludesRunTotal;

    // 購入の要求を送る前の、購入の応答の通し番号
    private int responseSerialBefore;

    /// <summary>買えなかったもの（記録用）。</summary>
    public List<string> Unfinished { get; } = [];

    /// <summary>買えなかった品目の ID（呼び出し側が別の手段を選ぶのに使う）。</summary>
    public List<uint> UnfinishedItems { get; } = [];

    public MarketBoardTask(IEnumerable<MarketNeed> needs, MarketBoardWatcher watcher)
    {
        this.needs = needs.Where(n => n.Need > 0 && n.Candidates.Count > 0).ToList();
        this.watcher = watcher;
    }

    public override string Name => "マーケットで購入";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.needs.Count == 0)
            return TaskResult.Done;
        if (!this.watcher.Available)
            return this.Fail("マーケットの検索結果を受け取る仕組みを用意できませんでした");
        if (!this.watcher.OfferingsAvailable)
            return this.Fail("マーケットの出品一覧の通知を受け取れないので、古い一覧と取り違えないよう買いません");

        // 結果の分からない購入が残っているうちは買わない（二重に買わないため。事前点検で利用者が確かめると消える）
        if (ctx.Config.PendingPurchase is { } pending)
            return this.Fail($"前回のマーケット購入の結果が確かめられていません：{pending.Describe()}。マーケットの取引履歴で確かめてから、もう一度始めてください（事前点検の確認で「はい」を押すと控えを消します）");

        var town = MarketBoardLocator.ChooseTown();
        if (town == null)
            return this.Fail("マーケットボードのある街に、解放済みのエーテライトがありません");

        this.town = town.Value;
        ctx.Log.Write("マーケット", $"{TeleportTask.TerritoryName(this.town)} のマーケットボードで買います：{string.Join("、", this.needs.Select(n => $"{n.Label}×{n.Need}"))}");
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.phase)
        {
            case Phase.Travel:
                return this.RunSub(ctx, () => new TeleportTask(this.town), Phase.Approach);

            case Phase.Approach:
            {
                if (this.sub == null)
                {
                    var boards = MarketBoardLocator.BoardsIn(this.town);
                    this.board = boards.OrderBy(b => Vector3.Distance(b, Me.Position)).First();
                }

                // MB の座標は台の中心なので、経路の終点が台の手前で止まることがある。話しかけられる距離（6m 以内）を見込んで 3.5m にする
                return this.RunSub(ctx, () => new MoveToTask(this.board, 3.5f, "マーケットボード"), Phase.Open);
            }

            case Phase.Open:
                return this.OpenBoard(ctx);

            case Phase.Next:
                return this.NextNeed(ctx);

            case Phase.Search:
                return this.Search(ctx);

            case Phase.WaitResults:
                return this.WaitSearchResults(ctx);

            case Phase.PickItem:
                return this.PickItem(ctx);

            case Phase.WaitListings:
                return this.WaitListings(ctx);

            case Phase.Decide:
                return this.Decide(ctx);

            case Phase.WaitConfirm:
                return this.WaitConfirm(ctx);

            case Phase.Buy:
                return this.Buy(ctx);

            case Phase.WaitBought:
                return this.WaitBought(ctx);

            case Phase.Close:
                return this.CloseBoard(ctx);
        }

        return TaskResult.Running;
    }

    private TaskResult RunSub(TaskContext ctx, Func<AutoTask> make, Phase next)
    {
        this.sub ??= make();
        var r = this.sub.Step(ctx);
        this.Status = this.sub.Status;
        if (r == TaskResult.Running)
            return TaskResult.Running;

        this.sub.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
        this.sub = null;
        if (failed != null)
            return this.Fail(failed);

        this.Go(next, string.Empty);
        return TaskResult.Running;
    }

    private void Go(Phase p, string status)
    {
        this.phase = p;
        this.NextPhase(status);
    }

    // ---- MB を開く ----

    private TaskResult OpenBoard(TaskContext ctx)
    {
        // 「自分が開いた」の印は、こちらが話しかけたときだけ立てる（最初から開いていた画面は利用者のものなので、最後に閉じない）。
        // 話しかける前から開いていた画面は使わない（以前はそのまま検索・購入に使っていた）
        if (GameUi.IsVisible("ItemSearch") || GameUi.IsVisible("ItemSearchResult"))
        {
            if (!this.openedByMe)
                return this.Fail("マーケットボードの画面が開いています（自分が開いたものではないので使いません）。閉じてからやり直してください");

            if (GameUi.IsReady("ItemSearch", out _))
            {
                this.Go(Phase.Next, "マーケットボードを開きました");
                return TaskResult.Running;
            }
        }

        if (this.TimedOut(TimeSpan.FromSeconds(15)))
        {
            if (this.attempts++ >= 2)
                return this.Fail("マーケットボードを開けませんでした");
            this.NextPhase("もう一度話しかけます");
        }

        if (!GameUi.PlayerFree())
            return TaskResult.Running;

        var ids = MarketBoardLocator.BaseIds;
        var obj = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj && ids.Contains(o.BaseId) && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
        if (obj == null || Vector3.Distance(obj.Position, Me.Position) > 6f)
        {
            this.Status = "近くにマーケットボードが見つかりません";
            if (this.TimedOut(TimeSpan.FromSeconds(10)))
                return this.Fail("マーケットボードの前に着いたのに、話しかけられる MB が見つかりません");
            return TaskResult.Running;
        }

        // Interact は「ターゲット → 次の呼び出しで話しかけ」の2段なので、画面が開くまで間をあけて繰り返す
        // （1秒は操作を連打しない抑え。開いたかどうかは画面の状態で判断する）
        if (DateTime.UtcNow - this.interactAt >= TimeSpan.FromSeconds(1))
        {
            this.interactAt = DateTime.UtcNow;
            this.openedByMe = true;
            GameUi.Interact(obj);
            this.Status = "話しかけています";
        }

        return TaskResult.Running;
    }

    // ---- 次の品へ ----

    private TaskResult NextNeed(TaskContext ctx)
    {
        this.needIndex++;
        if (this.needIndex >= this.needs.Count)
        {
            this.Go(Phase.Close, "マーケットボードを閉じます");
            return TaskResult.Running;
        }

        this.current = this.needs[this.needIndex];
        this.zeroListingsSeen = 0;
        this.candidateIndex = 0;
        this.bestByCandidate.Clear();
        this.boughtForCurrent = 0;
        this.buyingItem = 0;
        this.approvedListingId = 0;
        this.approvedTotal = 0;
        this.candidatesOwnedAtStart = this.current.Candidates.Sum(c => Inventory.CountNow(c));

        // 買う直前にカバンを数え直し、不足分だけにする
        if (this.RemainingNeed() <= 0)
        {
            ctx.Log.Write("マーケット", $"{this.current.Label} はもう足りています");
            return TaskResult.Running;
        }

        this.StartSearch(this.current.Candidates[0]);
        return TaskResult.Running;
    }

    private void StartSearch(uint itemId)
    {
        this.reuseResults = false;
        this.searching = itemId;
        this.attempts = 0;
        this.Go(Phase.Search, $"{CraftPlanner.ItemName(itemId)} を検索します");
    }

    // ---- 検索窓に入力して検索 ----

    private TaskResult Search(TaskContext ctx)
    {
        if (!GameUi.IsReady("ItemSearch", out var a))
            return this.Fail("マーケットボードの画面が閉じられました");

        if (DateTime.UtcNow < this.searchNotBefore)
        {
            this.Status = "検索の間隔をあけています";
            return TaskResult.Running;
        }

        // 同じ品の出品一覧が開いたままだと、同じ品をクリックしても再要求されない。先に閉じる
        if (GameUi.Addon("ItemSearchResult") is var isr && isr != null)
        {
            if (DateTime.UtcNow - this.closeAt >= TimeSpan.FromSeconds(0.5))
            {
                this.closeAt = DateTime.UtcNow;
                isr->Close(true);
            }

            if (this.TimedOut(TimeSpan.FromSeconds(10)))
                return this.Fail("出品一覧の画面が閉じません");
            return TaskResult.Running;
        }

        // 同じ品の取り直しで、今の品名の検索結果にその品があれば、検索し直さずにその行を押す
        if (this.reuseResults)
        {
            this.reuseResults = false;
            var ag = AgentItemSearch.Instance();
            if (ag != null)
            {
                for (var i = 0; i < ag->ListingPageItemCount && i < 100; i++)
                {
                    if (ag->ListingPageItemIds[i] == this.searching)
                    {
                        ctx.Log.Debug("マーケット", $"{CraftPlanner.ItemName(this.searching)} は今の検索結果にあるので、検索し直さずに出品一覧を取り直します");
                        this.Go(Phase.PickItem, string.Empty);
                        return TaskResult.Running;
                    }
                }
            }
        }

        var addon = (AddonItemSearch*)a;
        var name = CraftPlanner.ItemName(this.searching);
        if (name.Length > 40)
            name = name[..40];

        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            addon->SearchText.SetString(p);
            addon->SearchTextInput->SetText(p);
        }

        addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, -1);
        addon->RunSearch(false);
        this.searchNotBefore = DateTime.UtcNow + MinSearchInterval;
        ctx.Log.Debug("マーケット", $"検索窓に「{name}」を入れて検索しました");
        this.Go(Phase.WaitResults, $"{name} の検索結果を待っています");
        return TaskResult.Running;
    }

    private TaskResult WaitSearchResults(TaskContext ctx)
    {
        var agent = AgentItemSearch.Instance();
        if (agent != null && agent->ListingPageItemCount > 0)
        {
            for (var i = 0; i < agent->ListingPageItemCount && i < 100; i++)
            {
                if (agent->ListingPageItemIds[i] == this.searching)
                {
                    this.Go(Phase.PickItem, string.Empty);
                    return TaskResult.Running;
                }
            }
        }

        if (this.TimedOut(TimeSpan.FromSeconds(10)))
            return this.SkipCandidate(ctx, "検索結果に出てきません");
        return TaskResult.Running;
    }

    // ---- 結果から品を選ぶ（出品一覧が開き、要求が飛ぶ） ----

    private TaskResult PickItem(TaskContext ctx)
    {
        if (!GameUi.IsReady("ItemSearch", out var a))
            return this.Fail("マーケットボードの画面が閉じられました");

        var agent = AgentItemSearch.Instance();
        if (agent == null)
            return this.Fail("マーケットの検索結果を読めません");

        var index = -1;
        for (var i = 0; i < agent->ListingPageItemCount && i < 100; i++)
        {
            if (agent->ListingPageItemIds[i] == this.searching)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return this.SkipCandidate(ctx, "検索結果から消えました");

        this.serialBefore = this.watcher.Serial;
        ((AddonItemSearch*)a)->ResultsList->DispatchItemEvent(index, AtkEventType.ListItemClick);
        this.Go(Phase.WaitListings, $"{CraftPlanner.ItemName(this.searching)} の出品一覧を待っています");
        return TaskResult.Running;
    }

    private TaskResult WaitListings(TaskContext ctx)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null)
            return this.Fail("マーケットの情報を読めません");

        if (this.watcher.Serial == this.serialBefore || proxy->SearchItemId != this.searching)
        {
            if (this.TimedOut(TimeSpan.FromSeconds(15)))
                return this.Retry(ctx, "出品一覧の応答がありません");
            return TaskResult.Running;
        }

        if (this.watcher.LastError != 0)
            return this.Retry(ctx, $"検索が受け付けられませんでした（エラー {this.watcher.LastError}。混雑の可能性）");

        // 全部届いたか：件数だけでなく、今回の番号のページがそろい、ゲームが受け取った行数が件数に届き、
        // 先頭から件数ぶんが「いま検索した品で単価が入っている」行になっているかを見る
        // （同じ品を検索し直すと、前の検索の行が「その品で単価が入っている」を満たしてしまうため）
        this.expectedCount = Math.Min(this.watcher.LastCount, 100);
        var filled = 0;
        for (var i = 0; i < proxy->ListingCount && i < 100; i++)
        {
            var l = proxy->Listings[i];
            if (l.ItemId == this.searching && l.UnitPrice > 0)
                filled++;
        }

        var receipt = ListingReceipt.Decide(this.watcher.LastCount, this.watcher.PagesForSeq, proxy->CurrentRequestId, this.watcher.SeqAtResult,
            proxy->EntryCount, filled);
        if (receipt != ListingReceipt.Verdict.Wait)
        {
            this.Go(Phase.Decide, string.Empty);
            return TaskResult.Running;
        }

        this.Status = $"出品一覧を受信中（{filled}/{this.expectedCount}件・ページ {this.watcher.PagesForSeq}/{ListingReceipt.PagesFor(this.watcher.LastCount)}）";
        if (this.TimedOut(TimeSpan.FromSeconds(20)))
            return this.Retry(ctx, "出品一覧を全部受け取れませんでした");
        return TaskResult.Running;
    }

    private TaskResult Retry(TaskContext ctx, string why)
    {
        if (this.attempts++ >= 3)
            return this.SkipCandidate(ctx, why);

        this.searchNotBefore = DateTime.UtcNow + TimeSpan.FromSeconds(2 + (0.5 * (this.attempts - 1)));
        ctx.Log.Warn("マーケット", $"{why}。少し間をあけて検索し直します（{this.attempts}回目）");
        this.reuseResults = true;
        this.Go(Phase.Search, "検索し直します");
        return TaskResult.Running;
    }

    // ---- 出品を選ぶ ----

    private TaskResult Decide(TaskContext ctx)
    {
        var need = this.RemainingNeed();
        if (need <= 0)
        {
            this.Go(Phase.Next, string.Empty);
            return TaskResult.Running;
        }

        var listings = this.ReadListings();

        // 読み取った出品を全部記録する（どの出品を選んだかを後から確かめられるように）
        ctx.Log.Debug("マーケット",
            $"{CraftPlanner.ItemName(this.searching)} の出品 {listings.Count} 件（必要 {need} 個）："
            + string.Join(" / ", listings.OrderBy(l => l.Total).Select(l => $"{l.Quantity}個×{l.UnitPrice:N0}＝合計{l.Total:N0}（出品{l.ListingId}）")));

        // 候補が複数のときは、全候補の最安を見てから決める
        if (this.current!.Candidates.Count > 1 && this.buyingItem == 0)
        {
            var best = listings.Where(l => l.Quantity >= need).OrderBy(l => l.Total).FirstOrDefault();
            if (best.ListingId != 0)
                this.bestByCandidate[this.searching] = best.Total;
            return this.AdvanceCandidate(ctx);
        }

        this.buyingItem = this.searching;

        // 必要数以上の出品のうち、合計金額が一番低いもの
        var pick = listings.Where(l => l.Quantity >= need).OrderBy(l => l.Total).FirstOrDefault();
        if (pick.ListingId == 0)
        {
            // 1件で足りる出品が無い → 単価の安い順に買って満たす
            pick = listings.OrderBy(l => l.UnitPrice).ThenByDescending(l => l.Quantity).FirstOrDefault();
            if (pick.ListingId == 0)
            {
                // 買えなければ止める品（マテリア）は、同じ行を押し直してもう一度0件を確かめる（件数の応答には品目も番号も無いので、
                // 前の要求の応答が遅れて届いた0件と取り違える余地がある）
                if (this.current!.StopIfUnavailable && UnavailableListing.Recheck(listings.Count, this.zeroListingsSeen))
                {
                    this.zeroListingsSeen++;
                    ctx.Log.Warn("マーケット", $"{CraftPlanner.ItemName(this.searching)} の買える出品が0件でした（ゲームが返した件数 {this.watcher.LastCount}・自分のリテイナーの出品 {this.ownListings}）。取り違えでないか、もう一度確かめます");
                    this.searchNotBefore = DateTime.UtcNow + MinSearchInterval;
                    this.reuseResults = true;
                    this.Go(Phase.Search, "出品が0件か確かめ直します");
                    return TaskResult.Running;
                }

                return this.GiveUpCurrent(ctx, this.ownListings > 0 ? $"買える出品がありません（自分のリテイナーの出品 {this.ownListings} 件だけ）" : "出品がありません",
                    noListings: listings.Count == 0, ownListings: this.ownListings);
            }
            ctx.Log.Warn("マーケット", $"{CraftPlanner.ItemName(this.buyingItem)}: {need}個以上の出品が無いので、単価の安い出品（{pick.Quantity}個）から順に買います");
        }

        this.buyingListingId = pick.ListingId;
        this.buyingTotal = pick.Total;
        this.buyingQuantity = pick.Quantity;
        this.needAtDecide = need;
        ctx.Log.Debug("マーケット", $"選んだ出品：{CraftPlanner.ItemName(this.buyingItem)} {pick.Quantity}個×{pick.UnitPrice:N0}＝合計{pick.Total:N0}（出品{pick.ListingId}）");

        var gil = Inventory.Gil();
        if (pick.Total > gil)
            return this.GiveUpCurrent(ctx, $"ギルが足りません（必要 {pick.Total:N0} / 所持 {gil:N0}）");

        // 確かめる理由（PurchaseGuard.ConfirmReasons）：
        //  ・1回の購入額が基準（既定 500,000 ギル）を超える
        //  ・単価が最近の取引の中央値の何倍か（設定）を超える／この実行の合計が任意の上限を超える
        // 確認をもらった後に取り直した一覧で、同じ出品が同じ額以下のままなら、出品ごとの確認はやり直さない
        var approved = PurchaseGuard.IsApproved(pick.ListingId, pick.Total, this.approvedListingId, this.approvedTotal);
        var history = this.watcher.HistoryUnitPrices(this.buyingItem);
        var market = PurchaseGuard.MedianUnitPrice(history);
        if (market == null && ctx.Config.ConfirmUnitPriceRatio > 0)
            ctx.Log.Debug("マーケット", $"{CraftPlanner.ItemName(this.buyingItem)} の取引履歴が届いていないので、相場から外れた高値かは確かめられません");
        var excess = Math.Max(0, pick.Quantity - need) * (long)pick.UnitPrice;
        var reasons = PurchaseGuard.ConfirmReasons(pick.Total, pick.UnitPrice, ctx.Config.ConfirmPurchaseAboveGil, approved,
            market, ctx.Config.ConfirmUnitPriceRatio, SpentThisRun, RunApprovedUpTo, excess, ctx.Config.ConfirmExcessAboveGil);
        if (reasons.Count > 0)
        {
            this.confirmIncludesRunTotal = RunApprovedUpTo > 0 && SpentThisRun + pick.Total > RunApprovedUpTo;
            ctx.Log.Warn("マーケット", $"買う前に確認を出します：{string.Join(" / ", reasons)}");
            this.confirmTicket = ctx.Confirm.Ask(
                "マーケットでの購入の確認",
                $"{CraftPlanner.ItemName(this.buyingItem)} ×{pick.Quantity} を {pick.Total:N0} ギル（単価 {pick.UnitPrice:N0}、手数料込み）で買おうとしています。\n"
                + string.Join("\n", reasons.Select(r => "・" + r)) + "\n"
                + (market is { } med ? $"（参考：最近の取引の単価の中央値 {med:N0} ギル・{history.Count} 件）\n" : string.Empty)
                + $"（参考：この実行でマーケットに払った額 {SpentThisRun:N0} ギル）\n\n"
                + "本当に購入してよいですか？「はい」で購入して自動動作を続けます。「いいえ」で自動動作を止めます。");
            this.Go(Phase.WaitConfirm, "購入の確認を待っています");
            return TaskResult.Running;
        }

        this.Go(Phase.Buy, string.Empty);
        return TaskResult.Running;
    }

    private TaskResult WaitConfirm(TaskContext ctx)
    {
        var ans = ctx.Confirm.Poll(this.confirmTicket);
        if (ans == null)
            return TaskResult.Running;
        if (ans == false)
            return this.Fail("購入の確認で「いいえ」が選ばれました");

        // 確認を待つ間に出品が売れた・値が変わった可能性があるので、一覧を取り直してから選び直す
        // （手元の一覧は確認を出した時点のもの。取り直した一覧で同じ出品が同じ額以下なら、そのまま買う）
        this.approvedListingId = this.buyingListingId;
        this.approvedTotal = this.buyingTotal;

        // この実行の合計の確認だったなら、了承済みの額を上げる（今回の分＋上限の額。次はさらにその額を払ったところで確かめる）
        if (this.confirmIncludesRunTotal)
        {
            RunApprovedUpTo = SpentThisRun + this.buyingTotal + Math.Max(0, ctx.Config.ConfirmRunTotalAboveGil);
            this.confirmIncludesRunTotal = false;
        }

        ctx.Log.Write("マーケット", "購入の確認がとれました。出品一覧を取り直してから買います");
        this.StartSearch(this.buyingItem);
        this.reuseResults = true;
        return TaskResult.Running;
    }

    private TaskResult Buy(TaskContext ctx)
    {
        // 送る直前に、不足数とギルを数え直す（以前は出品の中身だけを照合していたので、
        // 確認を待つ間に別の手段で足りても、古い不足数のまま買っていた）
        var need = this.RemainingNeed();
        var gilNow = Inventory.Gil();
        switch (PurchaseGuard.Check(need, this.needAtDecide, gilNow, this.buyingTotal))
        {
            case PurchaseGuard.Verdict.AlreadyEnough:
                ctx.Log.Write("マーケット", $"{this.current!.Label} は買う前に足りたので、買いません");
                this.Go(Phase.Next, string.Empty);
                return TaskResult.Running;
            case PurchaseGuard.Verdict.Reselect:
                ctx.Log.Warn("マーケット", $"不足数が変わった（{this.needAtDecide}→{need}）ので、出品を選び直します");
                this.Go(Phase.Decide, string.Empty);
                return TaskResult.Running;
            case PurchaseGuard.Verdict.NotEnoughGil:
                return this.GiveUpCurrent(ctx, $"ギルが足りません（必要 {this.buyingTotal:N0} / 所持 {gilNow:N0}）");
        }

        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != this.buyingItem)
            return this.Retry(ctx, "出品一覧が別の品に変わっていました");

        // 送る直前に、選んだ出品がまだ一覧にあり、中身が同じかを照合する（関数経路はゲームの確認を通らないため）
        MarketBoardListing* target = null;
        for (var i = 0; i < proxy->ListingCount && i < 100; i++)
        {
            var l = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref proxy->Listings[i]);
            if (l->ListingId == this.buyingListingId)
            {
                target = l;
                break;
            }
        }

        if (target == null || target->ItemId != this.buyingItem || target->Quantity != this.buyingQuantity
            || (long)target->UnitPrice * target->Quantity + target->TotalTax != this.buyingTotal)
        {
            ctx.Log.Warn("マーケット", "選んだ出品が変わったので選び直します");
            this.Go(Phase.Decide, string.Empty);
            return TaskResult.Running;
        }

        this.gilBefore = Inventory.Gil();
        this.countBefore = Inventory.CountNow(this.buyingItem);
        this.purchaseSerialBefore = this.watcher.PurchaseSerial;
        this.responseSerialBefore = this.watcher.ResponseSerial;

        if (!proxy->SetLastPurchasedItem(target))
            return this.Retry(ctx, "購入する出品を用意できませんでした（まだ送っていません）");

        // 送る前に取引の控えを保存する。保存できなければ送らない（結果が分からないまま読み込み直しても、控えから照合できるように）
        try
        {
            ctx.Config.PendingPurchase = new PendingPurchaseRecord(
                Svc.PlayerState.ContentId, this.buyingItem, CraftPlanner.ItemName(this.buyingItem), this.buyingListingId,
                this.buyingQuantity, this.buyingTotal, this.gilBefore, this.countBefore, DateTime.UtcNow, "Sent");
            ctx.Config.Save();
        }
        catch (Exception ex)
        {
            ctx.Config.PendingPurchase = null;
            return this.Fail($"購入の控えを保存できないので、買いませんでした（{ex.GetType().Name}: {ex.Message}）");
        }

        if (!proxy->SendPurchaseRequestPacket())
        {
            // 送る関数が失敗を返した＝送っていない。控えを消してやり直す
            this.ClearPending(ctx);
            return this.Retry(ctx, "購入の要求を送れませんでした");
        }

        this.Go(Phase.WaitBought, $"{CraftPlanner.ItemName(this.buyingItem)} ×{this.buyingQuantity} を購入中");
        return TaskResult.Running;
    }

    private TaskResult WaitBought(TaskContext ctx)
    {
        var gil = Inventory.Gil();
        var count = Inventory.CountNow(this.buyingItem);

        // 送った出品への購入の応答（出品の番号で照合。成功・断られたは errorMessageId で決める）
        var (response, message) = PurchaseReply.Find(this.watcher.Responses(), this.responseSerialBefore, this.buyingListingId);
        var reject = PurchaseReply.Classify(message);
        // 送った購入の応答待ちは、流れが中断しても止めない（実際の時間で測る）
        var verdict = PurchaseOutcome.Decide(this.gilBefore, gil, this.countBefore, count, response, reject, this.PhaseWallElapsed);

        // 減った AND 増えた
        if (verdict == PurchaseOutcome.Verdict.Bought)
        {
            this.ClearPending(ctx);
            var paid = this.gilBefore - gil;
            SpentThisRun += paid;
            this.boughtForCurrent += count - this.countBefore;
            this.boughtListings.Add(this.buyingListingId);
            ctx.Log.Write("マーケット", $"{CraftPlanner.ItemName(this.buyingItem)} ×{count - this.countBefore} を {paid:N0} ギルで買いました（この実行の合計 {SpentThisRun:N0} ギル）");

            if (this.RemainingNeed() > 0)
            {
                this.Go(Phase.Decide, string.Empty);
                return TaskResult.Running;
            }

            this.Go(Phase.Next, string.Empty);
            return TaskResult.Running;
        }

        switch (verdict)
        {
            case PurchaseOutcome.Verdict.Unknown:
            {
                // 片方だけ変わった、または上限まで待っても何も変わらない＝買えたかどうか分からない。
                // もう一度買うと二重購入になりうるので止める。控えは「分からない」として残す
                if (ctx.Config.PendingPurchase is { } p)
                {
                    ctx.Config.PendingPurchase = p with { State = "Unknown" };
                    ctx.Config.Save();
                }

                var said = response switch
                {
                    PurchaseResponse.Success => "、ゲームの応答は「成功」",
                    PurchaseResponse.Rejected => $"、ゲームの応答は「断られた」{MarketBoardWatcher.MessageText(message)}",
                    _ => "、ゲームの応答なし",
                };
                return this.Fail($"購入の結果を確かめられません（ギル {this.gilBefore:N0}→{gil:N0}、{CraftPlanner.ItemName(this.buyingItem)} {this.countBefore}→{count}{said}）。"
                                 + "二重に買わないよう止めました。マーケットの取引履歴で買えたか確かめてから、もう一度始めてください");
            }

            case PurchaseOutcome.Verdict.Retry:
            {
                // はっきり断られた（ギルも所持も変わらない）。控えを消し、理由ごとに扱いを分ける
                this.ClearPending(ctx);
                var why = $"購入が断られました{MarketBoardWatcher.MessageText(message)}";
                switch (reject)
                {
                    case RejectKind.StopAll:
                        // 所持品がいっぱい・ギル不足は、検索し直しても変わらない。全体を止めて理由を出す
                        return this.Fail($"{why}。続けても買えないので止めました");
                    case RejectKind.GiveUpItem:
                        return this.GiveUpCurrent(ctx, why);
                    case RejectKind.ExcludeListing:
                    case RejectKind.Reselect:
                        // その出品は選び直さない（断られた後の一覧に残っていることがある）
                        this.rejectedListings.Add(this.buyingListingId);
                        return this.Retry(ctx, why);
                    default:
                        // しばらく待って取り直す（出品は外さない）
                        return this.Retry(ctx, why);
                }
            }

            case PurchaseOutcome.Verdict.Wait when this.PhaseWallElapsed >= PurchaseOutcome.RetryAfter:
                this.Status = response == PurchaseResponse.Success
                    ? "ゲームは成功と返しました。所持に反映されるのを待っています"
                    : $"購入の結果を待っています（{this.PhaseWallElapsed.TotalSeconds:0}/{PurchaseOutcome.ConfirmedLimit.TotalSeconds:0}秒。分からなければ買い直さずに止めます）";
                break;
        }

        return TaskResult.Running;
    }

    /// <summary>取引の控えを消す（買えた・断られた・送らなかったと分かったとき）。</summary>
    private void ClearPending(TaskContext ctx)
    {
        if (ctx.Config.PendingPurchase == null)
            return;
        ctx.Config.PendingPurchase = null;
        ctx.Config.Save();
    }

    private int RemainingNeed()
    {
        if (this.current == null)
            return 0;

        // 指定品：「持っていたい総数 − 今の所持数」（カバンを毎回数え直す）
        if (this.current.Candidates.Count == 1 && this.current.TargetOwned != null)
            return this.current.TargetOwned.Value - Inventory.CountNow(this.current.Candidates[0]);

        // 候補が複数：始めた時点からの増えた数を引く（カバンを数え直す）
        var gained = this.current.Candidates.Sum(c => Inventory.CountNow(c)) - this.candidatesOwnedAtStart;
        return this.current.Need - Math.Max(gained, this.boughtForCurrent);
    }

    /// <summary>候補が複数のとき、次の候補へ進む。全部見終えたら一番安い候補を買いに行く。</summary>
    private TaskResult AdvanceCandidate(TaskContext ctx)
    {
        this.candidateIndex++;
        if (this.candidateIndex < this.current!.Candidates.Count)
        {
            this.StartSearch(this.current.Candidates[this.candidateIndex]);
            return TaskResult.Running;
        }

        if (this.bestByCandidate.Count == 0)
            return this.GiveUpCurrent(ctx, "どの候補にも必要数以上の出品がありません");

        this.buyingItem = this.bestByCandidate.OrderBy(x => x.Value).First().Key;
        ctx.Log.Write("マーケット", $"{this.current.Label}: 一番安い {CraftPlanner.ItemName(this.buyingItem)}（{this.bestByCandidate[this.buyingItem]:N0}ギル）を買います");

        // いま開いている一覧が買う品でなければ、検索し直す
        if (this.searching != this.buyingItem)
            this.StartSearch(this.buyingItem);
        else
            this.Go(Phase.Decide, string.Empty);
        return TaskResult.Running;
    }

    private TaskResult SkipCandidate(TaskContext ctx, string why)
    {
        ctx.Log.Warn("マーケット", $"{CraftPlanner.ItemName(this.searching)}: {why}");
        if (this.current!.Candidates.Count > 1 && this.buyingItem == 0)
            return this.AdvanceCandidate(ctx);

        return this.GiveUpCurrent(ctx, why);
    }

    private TaskResult GiveUpCurrent(TaskContext ctx, string why, bool noListings = false, int ownListings = 0)
    {
        ctx.Log.Warn("マーケット", $"{this.current!.Label} を買えませんでした：{why}");

        // 買えなければ止める品（マテリア）：周回で買い直さずに、その場で止める。
        // 入手してから開始すれば、計画は手持ちから作り直すので続きから進む
        if (this.current.StopIfUnavailable)
        {
            var name = CraftPlanner.ItemName(this.searching != 0 ? this.searching : this.current.Candidates[0]);
            if (noListings)
            {
                var text = UnavailableListing.StopText(name, ownListings);
                Svc.Chat.Print(text);
                return this.Fail($"{text}。{name}を手に入れてから開始すると、続きから進みます");
            }

            Svc.Chat.Print($"マーケットボードで{name}を買えなかった為、動作を停止しました（{why}）");
            return this.Fail($"マーケットボードで{name}を買えなかった為、動作を停止しました（{why}）。{name}を手に入れてから開始すると、続きから進みます");
        }

        this.Unfinished.Add(this.current.Label);
        this.UnfinishedItems.AddRange(this.current.Candidates);
        this.buyingItem = 0;
        this.Go(Phase.Next, string.Empty);
        return TaskResult.Running;
    }

    // ---- 閉じる ----

    private TaskResult CloseBoard(TaskContext ctx)
    {
        if (!this.openedByMe || !CloseOwnWindows(ref this.closeAt))
            return TaskResult.Done;

        if (this.TimedOut(TimeSpan.FromSeconds(10)))
        {
            ctx.Log.Warn("マーケット", "マーケットボードの画面が閉じません（開いたまま次へ進みます）");
            return TaskResult.Done;
        }

        return TaskResult.Running;
    }

    /// <summary>
    /// MB の画面（出品一覧 → 検索窓の順）を閉じる。まだ開いている画面があれば true。
    /// 閉じる命令は 0.5 秒に1回まで（毎フレーム送らない）。
    /// </summary>
    private static bool CloseOwnWindows(ref DateTime lastClose)
    {
        var target = GameUi.Addon("ItemSearchResult");
        if (target == null)
            target = GameUi.Addon("ItemSearch");
        if (target == null)
            return false;

        if (DateTime.UtcNow - lastClose >= TimeSpan.FromSeconds(0.5))
        {
            lastClose = DateTime.UtcNow;
            Core.DebugLog.Current?.Line("操作", $"画面を閉じます: {target->NameString}");
            target->Close(true);
        }

        return true;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;

        // 途中で止まったとき、こちらが開いた MB の画面を閉じる（開いたままだと
        // 「ショップ系の画面が開いている間は移動しない」決まりで次の作業が進まない）
        if (this.openedByMe)
        {
            foreach (var name in new[] { "ItemSearchResult", "ItemSearch" })
            {
                if (GameUi.Addon(name) is var w && w != null)
                {
                    Core.DebugLog.Current?.Line("操作", $"止めたので画面を閉じます: {name}");
                    w->Close(true);
                }
            }
        }
    }

    // ---- 出品の読み取り ----

    private readonly record struct Offer(ulong ListingId, int Quantity, uint UnitPrice, long Total);

    /// <summary>
    /// 今の出品一覧から、買ってよいものだけを読む。
    /// 除外：別の品・単価0（購入後に末尾へ残る古い値）・セット販売・自分のリテイナーの出品。
    /// 合計＝単価×個数＋手数料（出品ごとの TotalTax。確認ダイアログの「手数料込み」と同じ）。
    /// </summary>
    private List<Offer> ReadListings()
    {
        var list = new List<Offer>();
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != this.searching)
            return list;

        var own = OwnRetainers();
        this.ownListings = 0;
        for (var i = 0; i < proxy->ListingCount && i < 100; i++)
        {
            var l = proxy->Listings[i];
            if (l.ItemId != this.searching || l.UnitPrice == 0 || l.Quantity == 0)
                continue;
            if (own.Contains(l.RetainerId) && !this.boughtListings.Contains(l.ListingId))
                this.ownListings++;
            if (l.IsSellingAsSet || own.Contains(l.RetainerId) || this.boughtListings.Contains(l.ListingId) || this.rejectedListings.Contains(l.ListingId))
                continue;

            list.Add(new Offer(l.ListingId, (int)l.Quantity, l.UnitPrice, (long)l.UnitPrice * l.Quantity + l.TotalTax));
        }

        return list;
    }

    private static HashSet<ulong> OwnRetainers()
    {
        var set = new HashSet<ulong>();
        // リテイナーの情報がまだ届いていない（IsReady=false）と ID が空なので、自分の出品を見分けられない。
        // その場合は除外できないことを記録に残す（買っても自分のギルが自分に戻るだけで、実害は手数料のみ）
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            Core.DebugLog.Current?.Line("マーケット", "リテイナー情報がまだ読めないので、自分の出品を除外できません");
            return set;
        }

        for (var i = 0; i < 10; i++)
        {
            var r = rm->Retainers[i];
            if (r.RetainerId != 0)
                set.Add(r.RetainerId);
        }

        return set;
    }
}

/// <summary>
/// 買えなければ止める品（装着するマテリア）で、買える出品が0件だったときの扱い（
/// ゲームを起動せずに試せるように分けた）。
/// </summary>
public static class UnavailableListing
{
    /// <summary>
    /// もう一度確かめるか。件数の応答には品目も番号も無いので、前の要求の応答が遅れて届いた0件と取り違える余地がある
    /// ため、1回目の0件なら確かめ直し、2回目で止める。
    /// </summary>
    /// <param name="usable">こちらが買える出品の数（自分のリテイナーの出品・セット売り・断った出品を除く）。</param>
    /// <param name="zeroSeenBefore">これまでに0件を見た回数。</param>
    public static bool Recheck(int usable, int zeroSeenBefore) => usable == 0 && zeroSeenBefore < 1;

    /// <summary>
    /// 止めるときのチャットの文言。自分のリテイナーの出品が無ければ既定の文言そのまま。自分のリテイナーの出品しか無ければ、理由を足す
    /// （マーケットボードには自分の出品が見えるので、「出品がなかった」だけでは事実と違って見える。
    /// 以前はゲームが返した件数で書いたので、出品を買い切った後の0件で、自分が買った出品を「自分のリテイナーの出品など」と説明していた）。
    /// </summary>
    /// <param name="name">品名。</param>
    /// <param name="ownListings">自分のリテイナーの出品の数（買い終えた出品は数えない）。</param>
    public static string StopText(string name, int ownListings)
        => ownListings <= 0
            ? $"マーケットボードに{name}の出品がなかった為、動作を停止しました"
            : $"マーケットボードに{name}の出品がなかった為（自分のリテイナーの出品 {ownListings} 件だけでした）、動作を停止しました";
}
