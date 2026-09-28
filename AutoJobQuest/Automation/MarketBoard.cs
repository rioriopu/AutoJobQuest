using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed record MarketNeed(List<uint> Candidates, int Need, string Label, int? TargetOwned = null);

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
    private static readonly Dictionary<uint, List<Vector3>> Cache = [];

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

    /// <summary>そのエリアの MB の位置（無ければ空）。</summary>
    public static List<Vector3> BoardsIn(uint territory)
    {
        if (Cache.TryGetValue(territory, out var cached))
            return cached;

        var result = new List<Vector3>();
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        if (terr.TryGetRow(territory, out var t))
        {
            var bg = t.Bg.ExtractText();
            var at = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (at >= 0)
            {
                var dir = bg[..(at + "/level/".Length)];
                foreach (var file in new[] { "planlive.lgb", "planevent.lgb", "planmap.lgb" })
                    Scan($"bg/{dir}{file}", result);
                if (result.Count == 0)
                    Scan($"bg/{dir}bg.lgb", result);
            }
        }

        Cache[territory] = result;
        return result;
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
        foreach (var a in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!a.IsAetheryte || !unlocked.Contains(a.RowId))
                continue;
            var tid = a.Territory.RowId;
            if (!terr.TryGetRow(tid, out var tt) || tt.TerritoryIntendedUse.RowId != 0)
                continue;
            if (BoardsIn(tid).Count > 0)
                return tid;
        }

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

    public int Serial { get; private set; }

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
    }

    public bool Available => this.hook != null;

    private void Detour(InfoProxyItemSearch* thisPtr, byte resultCount, int errorCode)
    {
        this.hook!.Original(thisPtr, resultCount, errorCode);
        try
        {
            this.LastCount = resultCount;
            this.LastError = errorCode;
            this.Serial++;
        }
        catch
        {
            // フックの中では例外を外へ出さない
        }
    }

    public void Dispose() => this.hook?.Dispose();
}

/// <summary>
/// マーケットボードで買う。
///
/// 流れ:
///   MB の場所をゲームデータから取得 → 座標まで自動移動 → アクセス → 欲しいアイテムを検索窓に入力
///   → 結果から欲しいアイテムを選択 → 出品一覧から「必要数以上の数が出ていて、合計金額が一番低い」出品を買う
///   （例：5個ほしいとき、4個と7個の出品なら7個を買う）→ 買ったら MB を閉じる。
/// 必ずカバンの所持数を確かめ、不足分だけを買う（呼び出し側が不足数を渡し、ここでも買う直前に数え直す）。
/// 1回の購入額が設定値（既定 500,000 ギル）を超えるとき、またはこの実行での MB の合計支払額が
/// 設定値を超えるときは確認窓を出す。「いいえ」で自動動作を止める。
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

    /// <summary>この実行で MB に払った合計（ギル）。</summary>
    public static long SpentThisRun { get; set; }

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

                return this.RunSub(ctx, () => new MoveToTask(this.board, 2.5f, "マーケットボード"), Phase.Open);
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
        if (GameUi.IsReady("ItemSearch", out _))
        {
            this.Go(Phase.Next, "マーケットボードを開きました");
            return TaskResult.Running;
        }

        if (this.TimedOut(TimeSpan.FromSeconds(15)))
        {
            if (this.attempts++ >= 2)
                return this.Fail("マーケットボードを開けませんでした");
            this.NextPhase("もう一度話しかけます");
        }

        if (this.PhaseElapsed < TimeSpan.FromSeconds(0.5) && this.attempts > 0)
            return TaskResult.Running;

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

        if (this.Status != "話しかけています")
        {
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
        this.candidateIndex = 0;
        this.bestByCandidate.Clear();
        this.boughtForCurrent = 0;
        this.buyingItem = 0;

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
        this.searching = itemId;
        this.attempts = 0;
        this.Go(Phase.Search, $"{CraftPlanner.ItemName(itemId)} を検索します");
    }

    // ---- 検索窓に入力して検索 ----

    private TaskResult Search(TaskContext ctx)
    {
        if (!GameUi.IsReady("ItemSearch", out var a))
            return this.Fail("マーケットボードの画面が閉じられました");

        // 同じ品の出品一覧が開いたままだと、同じ品をクリックしても再要求されない。先に閉じる
        if (GameUi.Addon("ItemSearchResult") is var isr && isr != null)
        {
            isr->Close(true);
            return TaskResult.Running;
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

        this.expectedCount = Math.Min(this.watcher.LastCount, 100);
        if (proxy->ListingCount >= this.expectedCount)
        {
            this.Go(Phase.Decide, string.Empty);
            return TaskResult.Running;
        }

        this.Status = $"出品一覧を受信中（{proxy->ListingCount}/{this.expectedCount}件）";
        if (this.TimedOut(TimeSpan.FromSeconds(20)))
            return this.Retry(ctx, "出品一覧を全部受け取れませんでした");
        return TaskResult.Running;
    }

    private TaskResult Retry(TaskContext ctx, string why)
    {
        if (this.attempts++ >= 3)
            return this.SkipCandidate(ctx, why);

        ctx.Log.Warn("マーケット", $"{why}。検索し直します");
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

        // 候補が複数（種類不問のマテリアなど）のときは、全候補の最安を見てから決める
        if (this.current!.Candidates.Count > 1 && this.buyingItem == 0)
        {
            var best = listings.Where(l => l.Quantity >= need).OrderBy(l => l.Total).FirstOrDefault();
            if (best.ListingId != 0)
                this.bestByCandidate[this.searching] = best.Total;

            this.candidateIndex++;
            if (this.candidateIndex < this.current.Candidates.Count)
            {
                this.StartSearch(this.current.Candidates[this.candidateIndex]);
                return TaskResult.Running;
            }

            if (this.bestByCandidate.Count == 0)
                return this.GiveUpCurrent(ctx, "どの候補にも必要数以上の出品がありません");

            // 一番安い候補をもう一度検索して、その出品を買う
            this.buyingItem = this.bestByCandidate.OrderBy(x => x.Value).First().Key;
            ctx.Log.Write("マーケット", $"{this.current.Label}: 一番安い {CraftPlanner.ItemName(this.buyingItem)}（{this.bestByCandidate[this.buyingItem]:N0}ギル）を買います");
            if (this.searching != this.buyingItem)
            {
                this.StartSearch(this.buyingItem);
                return TaskResult.Running;
            }
        }
        else
        {
            this.buyingItem = this.searching;
        }

        // 必要数以上の出品のうち、合計金額が一番低いもの
        var pick = listings.Where(l => l.Quantity >= need).OrderBy(l => l.Total).FirstOrDefault();
        if (pick.ListingId == 0)
        {
            // 1件で足りる出品が無い → 単価の安い順に買って満たす
            pick = listings.OrderBy(l => l.UnitPrice).ThenByDescending(l => l.Quantity).FirstOrDefault();
            if (pick.ListingId == 0)
                return this.GiveUpCurrent(ctx, "出品がありません");
            ctx.Log.Warn("マーケット", $"{CraftPlanner.ItemName(this.buyingItem)}: {need}個以上の出品が無いので、単価の安い出品（{pick.Quantity}個）から順に買います");
        }

        this.buyingListingId = pick.ListingId;
        this.buyingTotal = pick.Total;
        this.buyingQuantity = pick.Quantity;

        var limit = ctx.Config.ConfirmPurchaseAboveGil;
        var gil = Inventory.Gil();
        if (pick.Total > gil)
            return this.GiveUpCurrent(ctx, $"ギルが足りません（必要 {pick.Total:N0} / 所持 {gil:N0}）");

        if (limit > 0 && (pick.Total > limit || (SpentThisRun <= limit && SpentThisRun + pick.Total > limit)))
        {
            this.confirmTicket = ctx.Confirm.Ask(
                "マーケットでの購入額の確認",
                $"{CraftPlanner.ItemName(this.buyingItem)} ×{pick.Quantity} を {pick.Total:N0} ギル（手数料込み）で買おうとしています。\n"
                + $"この実行でマーケットに払った額：{SpentThisRun:N0} ギル → 購入後 {SpentThisRun + pick.Total:N0} ギル\n"
                + $"（確認する基準：{limit:N0} ギル）\n\n"
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

        this.Go(Phase.Buy, string.Empty);
        return TaskResult.Running;
    }

    private TaskResult Buy(TaskContext ctx)
    {
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

        if (!proxy->SetLastPurchasedItem(target) || !proxy->SendPurchaseRequestPacket())
            return this.Retry(ctx, "購入の要求を送れませんでした");

        this.Go(Phase.WaitBought, $"{CraftPlanner.ItemName(this.buyingItem)} ×{this.buyingQuantity} を購入中");
        return TaskResult.Running;
    }

    private TaskResult WaitBought(TaskContext ctx)
    {
        var gil = Inventory.Gil();
        var count = Inventory.CountNow(this.buyingItem);

        // 減った AND 増えた
        if (gil < this.gilBefore && count > this.countBefore)
        {
            var paid = this.gilBefore - gil;
            SpentThisRun += paid;
            this.boughtForCurrent += count - this.countBefore;
            ctx.Log.Write("マーケット", $"{CraftPlanner.ItemName(this.buyingItem)} ×{count - this.countBefore} を {paid:N0} ギルで買いました（この実行の合計 {SpentThisRun:N0} ギル）");

            if (this.RemainingNeed() > 0)
            {
                this.Go(Phase.Decide, string.Empty);
                return TaskResult.Running;
            }

            this.Go(Phase.Next, string.Empty);
            return TaskResult.Running;
        }

        if (this.TimedOut(TimeSpan.FromSeconds(15)))
            return this.Retry(ctx, "購入が確認できませんでした（売り切れ・混雑・カバンがいっぱい等）");
        return TaskResult.Running;
    }

    private int RemainingNeed()
    {
        if (this.current == null)
            return 0;
        if (this.current.Candidates.Count > 1 || this.current.TargetOwned == null)
            return this.current.Need - this.boughtForCurrent;

        // 指定品：「持っていたい総数 − 今の所持数」（カバンを毎回数え直す）
        return this.current.TargetOwned.Value - Inventory.CountNow(this.current.Candidates[0]);
    }

    private TaskResult SkipCandidate(TaskContext ctx, string why)
    {
        ctx.Log.Warn("マーケット", $"{CraftPlanner.ItemName(this.searching)}: {why}");
        if (this.current!.Candidates.Count > 1 && this.buyingItem == 0)
        {
            this.candidateIndex++;
            if (this.candidateIndex < this.current.Candidates.Count)
            {
                this.StartSearch(this.current.Candidates[this.candidateIndex]);
                return TaskResult.Running;
            }

            if (this.bestByCandidate.Count > 0)
            {
                this.Go(Phase.Decide, string.Empty);
                return TaskResult.Running;
            }
        }

        return this.GiveUpCurrent(ctx, why);
    }

    private TaskResult GiveUpCurrent(TaskContext ctx, string why)
    {
        ctx.Log.Warn("マーケット", $"{this.current!.Label} を買えませんでした：{why}");
        this.Unfinished.Add(this.current.Label);
        this.UnfinishedItems.AddRange(this.current.Candidates);
        this.buyingItem = 0;
        this.Go(Phase.Next, string.Empty);
        return TaskResult.Running;
    }

    // ---- 閉じる ----

    private TaskResult CloseBoard(TaskContext ctx)
    {
        if (GameUi.Addon("ItemSearchResult") is var r && r != null)
        {
            r->Close(true);
            return TaskResult.Running;
        }

        if (GameUi.Addon("ItemSearch") is var s && s != null)
        {
            s->Close(true);
            if (this.TimedOut(TimeSpan.FromSeconds(5)))
                return TaskResult.Done;
            return TaskResult.Running;
        }

        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
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
        for (var i = 0; i < proxy->ListingCount && i < 100; i++)
        {
            var l = proxy->Listings[i];
            if (l.ItemId != this.searching || l.UnitPrice == 0 || l.Quantity == 0)
                continue;
            if (l.IsSellingAsSet || own.Contains(l.RetainerId))
                continue;

            list.Add(new Offer(l.ListingId, (int)l.Quantity, l.UnitPrice, (long)l.UnitPrice * l.Quantity + l.TotalTax));
        }

        return list;
    }

    private static HashSet<ulong> OwnRetainers()
    {
        var set = new HashSet<ulong>();
        var rm = RetainerManager.Instance();
        if (rm == null)
            return set;
        var n = rm->GetRetainerCount();
        for (var i = 0; i < 10; i++)
        {
            var r = rm->Retainers[i];
            if (r.RetainerId != 0)
                set.Add(r.RetainerId);
        }

        return set;
    }
}
