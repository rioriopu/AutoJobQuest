using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AutoJobQuest;

/// <summary>
/// 設定（キャラクターごと）。
///
/// 【キャラクターごとに保存する】
/// Dalamud の設定ファイルは1プラグイン1ファイルで、ゲームを2つ起動すると、2つのゲームが同じファイルを保存し合い、
/// 他のプラグインを一時的に変えた控え（戻しの手がかり）を古い中身で上書きしえた（別のプラグインでも4キャラ共有の事故が起きている）。
/// そこで、ログインしているキャラクターごとのファイル（設定フォルダ\characters\&lt;ContentId&gt;.json。<see cref="Core.CharacterConfigStore"/>）に置く。
/// 同じキャラクターは2つのゲームに同時にログインできないので、取り合わない。
/// ログインしていない間は既定の値で、保存しない。キャラクターが替わったら、前のキャラクターの分を保存してから中身を入れ替える（<see cref="SwitchTo"/>）。
/// ログインの前に使う設定（起動時に画面を開く・記録の置き場所）だけは、全体で1つ（<see cref="GlobalConfiguration"/>）。
/// キャラクターの進み具合（どのクエストが済んだか等）は保存せず、毎回ゲームから読む。
/// </summary>
[Serializable]
public sealed class Configuration
{
    public int Version { get; set; } = 2;

    /// <summary>この設定の持ち主のキャラクター名（ファイルを開いた人が分かるように書くだけ。読み込みには使わない）。</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>持ち主のホームワールド名（同上）。</summary>
    public string HomeWorld { get; set; } = string.Empty;

    /// <summary>AutoRetainerの抑制をこちらが変更し、まだ復元を確認できていないか。</summary>
    public bool RetainerSuppressionPendingRestore { get; set; }

    /// <summary>上の抑制をこちらが立てた時刻（UTC。戻しの再試行の期限に使う）。</summary>
    public DateTime RetainerSuppressionSetAt { get; set; } = DateTime.MinValue;

    /// <summary>
    /// 開始時に呼び鈴でリテイナーの在庫を読み、足りない分を引き出すか（既定 true）。
    /// 切ると、手持ちだけで計画する（リテイナーにある品もマーケット等で集める）。
    /// </summary>
    public bool UseRetainerStock { get; set; } = true;

    /// <summary>
    /// 鞄に残しておく空き枠（既定 5）。リテイナーから引き出すとき・職ごとに進めるかを決めるときに、これだけは空けておく
    /// （製作・採集・購入でできた品や、クエストでもらう品を置く場所）。
    /// </summary>
    public int KeepFreeBagSlots { get; set; } = 5;

    /// <summary>ジョブクエを回すか（木工→調理→採掘・園芸・漁師。保存済み設定との互換のためプロパティ名を維持）。</summary>
    public bool[] SelectedCrafters { get; set; } = new bool[11];

    /// <summary>
    /// マーケットボードの購入で、1回の合計金額がこれを超えたら確認窓を出す（ギル）。
    /// 既定は 500,000。
    /// </summary>
    public int ConfirmPurchaseAboveGil { get; set; } = 500_000;

    /// <summary>
    /// マーケットの購入で、出品の単価が「最近の取引（マーケットの取引履歴）の単価の中央値」の何倍を超えたら確認窓を出すか。
    /// 0 なら確かめない。誤ってギルを大量に使ってしまわないための安全策。
    /// 出品が少ない品で、相場から外れた高値の出品しか残っていないときに気づくため（1回の額が基準以下でも確かめる）。
    /// 取引履歴が届かない品では確かめられない（そのときは記録に残す）。
    /// </summary>
    public double ConfirmUnitPriceRatio { get; set; } = 3.0;

    /// <summary>
    /// この実行でマーケットに払った合計が、これを超えそうになったら確認窓を出す（ギル）。0 なら確かめない（既定）。
    /// 1回ごとの確認（<see cref="ConfirmPurchaseAboveGil"/>）とは別の、任意の上限。
    /// 「はい」で続けると、次はさらにこの額を払ったところで、また確かめる。
    /// </summary>
    public long ConfirmRunTotalAboveGil { get; set; }

    /// <summary>
    /// 必要な数より大きいまとまりしか無いとき、余りの分（(個数 − 必要数) × 単価）の額がこれを超えたら確認窓を出す（ギル）。0 なら確かめない。
    /// 以前は、1個ほしいのに 99個×5,000＝495,000 ギルの出品しか無いと、1回の額の確認（50万）にかからずに
    /// 余りごと買っていた。1回ごとの確認とは別の、余りの無駄に気づくための確認。
    /// </summary>
    public long ConfirmExcessAboveGil { get; set; } = 100_000;

    /// <summary>
    /// 結果が確かめられていないマーケットの購入（送る前に保存し、買えた・断られたと分かったら消す）。
    /// 残っていれば、次に始めるとき事前点検で利用者に確かめてもらう（二重に買わないため、自動では買い直さない）。
    /// </summary>
    public PendingPurchaseRecord? PendingPurchase { get; set; }

    /// <summary>
    /// 素材集め・製作を立て直す周回の上限の基準（集めても足りない：この数＋2 周、作っても足りない：この数＋4 周で止める。RoundPolicy）。
    /// 無限に立て直すと素材とギルを食い潰すので上限を持つ。
    /// 以前は HQ にならなかったときの作り直しの上限も兼ねていた（<see cref="HqRetryRounds"/> に分けた）。
    /// </summary>
    public int MaxRetryRounds { get; set; } = 3;

    /// <summary>
    /// HQ 指定の品が NQ になったとき、止めずに作り直す回数（既定 0＝その場で止めて、チャットで知らせる）。
    /// NQ を誤って作っても、製作の失敗と同じく素材を失うため。
    /// 受注後に作る品（Lv61〜70。材料はクエストがくれる）は別の設定（<see cref="QuestCraftRetryRounds"/>）。
    /// </summary>
    public int HqRetryRounds { get; set; }

    /// <summary>
    /// 受注後に作る品（Lv61〜70 の32品。材料はクエストがくれて、何度でももらい直せる）が HQ にならなかったとき、何回まで作り直すか（既定10）。
    /// 失うのは1回ごとのクリスタルだけなので、ほかの製作の上限（<see cref="MaxRetryRounds"/>）とは分ける。クリスタルの予備も、この回数分を先に用意する。
    /// </summary>
    public int QuestCraftRetryRounds { get; set; } = 10;

    /// <summary>
    /// 選ばなかった製作職で中間素材を作るときも、ギアセットの主道具・副道具・頭・胴・腕・脚・足が Lv68 以上であることを求めるか
    /// （既定 true。製作の失敗で素材を失わないため）。満たさない職の中間素材は、作らずにマーケットボードで買う。
    /// 切ると、レシピのレベルに届けば装備を問わず作る（ギアセットは要る）。
    /// </summary>
    public bool RequireGearForOtherCrafters { get; set; } = true;

    /// <summary>
    /// 紫貨を稼ぐために作って納品する収集品（収集用のシーダーロングボウ＝30970）。
    /// 戦闘でしか取れない素材が無く、Lv50 の木工レシピで作れるため。別の品にしたい場合はここを変える。
    /// </summary>
    public uint ScripCollectableItemId { get; set; } = 30970;

    /// <summary>
    /// 納品物に「任意のマテリア」を付けるクエスト（各クラフター Lv20 の7本）で、買って付けるマテリア
    /// （剛柔のマテリア＝5679。能力値「不屈」・アイテムLv15 をゲームデータで確認）。
    /// 実行時に「マテリアであること」「納品物のアイテムLv以下であること」を確かめ、合わなければ始める前に止める。
    /// </summary>
    public uint AnyMateriaItemId { get; set; } = 5679;

    /// <summary>
    /// 特殊通貨の番号 → アイテムの控え。クライアントの表に無いとき（まだ触れていないスクリップ）だけ使う。
    /// この対応はゲームのシートに無いので外部の控えとして持つしかない。中身は
    /// Data\special_currency_map.json と同じ（1=詩学 28、2=クラフタースクリップ:紫貨 33913、4=ギャザラー紫貨 33914、
    /// 6=クラフター橙貨 41784、7=ギャザラー橙貨 41785）。スクリップの階層が増えると番号が入れ替わるので、そのときは直す。
    /// </summary>
    public Dictionary<int, uint> SpecialCurrencyFallback { get; set; } = new()
    {
        [1] = 28,
        [2] = 33913,
        [4] = 33914,
        [6] = 41784,
        [7] = 41785,
    };

    /// <summary>実行していないときも、ショップ・マーケット等の画面を記録するか（手動操作の値を取りたいとき用）。</summary>
    public bool AlwaysRecordAddons { get; set; }

    // ---- 他プラグインを一時的に変えた控え（落ちたときに次回起動で戻すため） ----
    // GBR の Save() で一時状態がファイルに残るので、戻す手がかりをこちらにも書いておく。

    /// <summary>
    /// こちらが一時的に無効にした GBR の自動採集リスト（名前とフォルダ）。
    /// GBR は同じ名前のリストを許すので、名前だけでは戻し先を取り違える。
    /// </summary>
    public List<GbrListRef> GbrDisabledListRefs { get; set; } = [];

    /// <summary>旧形式（名前だけ）の控え。読み込み時に新形式へ移す。</summary>
    public List<string> GbrDisabledLists { get; set; } = [];

    /// <summary>こちらが一時的に書き換えた GBR の自動採集設定（名前 → 元の値）。</summary>
    public Dictionary<string, bool> GbrConfigOriginals { get; set; } = [];

    /// <summary>
    /// GBR の設定を元の値に戻したが、GBR の保存ファイルに書かれたとまだ確かめていないもの（名前 → 書かれているはずの値）。
    /// GBR の Save は旗を立てるだけで後から書くので、書かれたと確かめるまで控えを残す。
    /// </summary>
    public Dictionary<string, bool> GbrConfigAwaitingSave { get; set; } = [];

    /// <summary><see cref="GbrConfigAwaitingSave"/> を待ち始めた時刻（UTC）。</summary>
    public DateTime? GbrConfigAwaitingSince { get; set; }

    /// <summary>こちらの GBR リスト「AutoJobQuest」を有効にしたままか。</summary>
    public bool GbrOwnListActive { get; set; }

    /// <summary>
    /// GBR の NPC 購入リスト「AutoJobQuest」に品目を入れたまま、まだ消していない（購入が止まってから消す）。
    /// 止めた直後は GBR がまだ購入中と答えるので、その場で消せないことがある。
    /// </summary>
    public bool GbrVendorListPending { get; set; }

    /// <summary>
    /// 一時的に無効にしたが、戻すときに1つに決まらなかった（見つからない・同じ名前が複数ある）GBR のリスト。
    /// 自動ではやり直さない（時間がたっても変わらないため）。画面に出し、利用者が GBR で確かめて「確認した」を押すまで残す。
    /// </summary>
    public List<GbrListRef> GbrUnresolvedListRefs { get; set; } = [];

    /// <summary>
    /// 読み込みの解除（更新・無効化）で止めたとき、Artisan の「遅れて始まる製作」の見張りが残っていたなら、その見張りの期限（UTC）。
    /// 解除の後は見張れないので、次に読み込んだとき、期限までの残りの時間だけ見張りを置き直す（期限を過ぎていれば捨てる）。
    /// </summary>
    public DateTime? PendingArtisanWatchUtc { get; set; }

    /// <summary>
    /// ジョブクエの製作で、Artisan の既定の食事・薬を使うか（既定 false＝使わない）。
    /// Artisan は、レシピごとの設定が無い製作に「既定の食事・薬」（利用者の設定。例：アリペブレ HQ・魔匠の薬液 HQ）を使い、
    /// ジョブのレベルがレシピより10を超えて高くなければ、ジョブクエの Lv1〜60 の製作でも食べる・飲む（Artisan の RecipeConfig.cs・
    /// ConsumableChecker.cs）。高価な消耗品を低いレベルの製作で使わないよう、こちらが頼む製作の間だけ、
    /// Artisan の IPC（ChangeFood・ChangePotion の一時指定＝保存されない）で「使わない」にし、終わったら戻す。
    /// HQ 指定の品が HQ にならないときは、これを true にすると食事・薬の分だけ HQ が出やすくなる。
    /// </summary>
    public bool UseArtisanConsumables { get; set; }

    /// <summary>
    /// 実行の間、受注中のほかのクエストをジャーナルで非表示にするか（既定 false。実機での確認がまだ少ないため）。
    /// 実行していない間に元の状態へ戻す（控え <see cref="JournalHiddenByMe"/>）。Automation.JournalHide の説明を参照。
    /// </summary>
    public bool HideOtherQuestsDuringRun { get; set; }

    /// <summary>こちらがジャーナルで非表示にしたクエスト（クエスト番号 → 元の状態：0＝通常・1＝優先表示）。戻ったのを確かめたら消す。</summary>
    public Dictionary<ushort, byte> JournalHiddenByMe { get; set; } = [];

    /// <summary>
    /// Artisan の食事・薬を一時的に「使わない」にしたまま、まだ戻していないレシピ（読み込みの解除をまたいで戻すための控え）。
    /// Artisan の一時指定は保存されない（Artisan を読み込み直せば消える）が、Artisan が動いたままなら残るので、次に読み込んだとき戻す。
    /// </summary>
    public List<uint> ArtisanTempConsumableRecipes { get; set; } = [];

    /// <summary>
    /// こちらが RSR を Henched にしたまま、まだ使う前のモードへ戻していない（控え。戻したら false）。
    /// プラグインの読み込み直しなどで覚えていたことが消えても、残った Henched を「利用者の使う前のモード」と取り違えないため
    /// （戻し損ねると、次の戦闘の後も Henched のまま残る）。
    /// </summary>
    public bool RsrHenchedPending { get; set; }

    /// <summary><see cref="RsrHenchedPending"/> のときの、使う前のモード（読めなかったなら null＝Off に戻す）。</summary>
    public byte? RsrOriginalMode { get; set; }

    /// <summary>
    /// こちらが RSR の範囲攻撃（AoEType）を Off にしたまま、まだ使う前の値へ戻していない（控え。戻ったと確かめたら false）。
    /// 戦闘の間だけ Off にする（対象モンスター以外は攻撃しないため。Henched でも範囲攻撃は指定外の敵を
    /// 巻き込み、自分中心の範囲攻撃はハードターゲットが無くても近くの敵に撃つ：RSR の ActionTargetInfo.cs で確認）。
    /// RSR は設定画面を閉じたときと終了時に設定を保存するので、戻し損ねると Off のまま利用者の設定に残る。そのため控えを残す。
    /// </summary>
    public bool RsrAoePending { get; set; }

    /// <summary><see cref="RsrAoePending"/> のときの、使う前の範囲攻撃の設定（RSR の AoEType：Off=0, Cleave=1, Full=2）。</summary>
    public byte? RsrAoeOriginal { get; set; }

    /// <summary>
    /// こちらが Henched の間だけ変えた RSR の真偽の設定（名前 → 使う前の値）。戻したと確かめるまで残す（読み込みの解除をまたいで戻すため）。
    /// 変えるのは TargetFreely（狙いが空になると RSR が一番近い敵を自分で狙う）と IgnoreNonFateInFate（FATE の中で FATE 以外を殴らない・
    /// FATE の外で FATE の敵を殴らない）。どちらも Henched の間は false にする。
    /// </summary>
    public Dictionary<string, bool> RsrBoolOriginals { get; set; } = [];

    [NonSerialized]
    private string? charactersDirectory;

    [NonSerialized]
    private ulong owner;

    /// <summary>いまの持ち主のキャラクター（ContentId）。ログインしていなければ 0（保存しない）。</summary>
    [JsonIgnore]
    public ulong OwnerContentId => this.owner;

    /// <summary>直近の保存の失敗の理由（無ければ null）。</summary>
    [JsonIgnore]
    public string? LastSaveProblem { get; private set; }

    /// <summary>保存先のフォルダを決める（読み込みはキャラクターが決まってから：<see cref="SwitchTo"/>）。</summary>
    public void Initialize(string charactersDirectoryPath)
    {
        this.charactersDirectory = charactersDirectoryPath;
        this.Normalize();
    }

    /// <summary>
    /// 持ち主のキャラクターを切り替える。今の持ち主の分を保存してから、新しい持ち主のファイルを読む（無ければ既定の値で作る）。
    /// <paramref name="contentId"/> が 0（ログアウト）なら既定の値に戻し、保存しない状態にする（前のキャラクターの値を残さない）。
    /// 読めないファイルがあったときは、その知らせを返す（無ければ null）。
    /// </summary>
    public string? SwitchTo(ulong contentId, string characterName, string homeWorld)
    {
        if (contentId == this.owner)
            return null;
        this.Save();

        string? note = null;
        var loaded = contentId == 0 || this.charactersDirectory == null
            ? new Configuration()
            : Core.CharacterConfigStore.Load(this.charactersDirectory, contentId, out note);
        Core.CharacterConfigStore.CopyInto(this, loaded);
        this.owner = contentId;
        if (contentId != 0)
        {
            this.CharacterName = characterName;
            this.HomeWorld = homeWorld;
        }

        this.Normalize();
        this.Save();
        return note;
    }

    /// <summary>読み込んだ後の整え（古い形の控えを新しい形へ・長さの合わない配列・静的な引き当て先の入れ直し）。</summary>
    private void Normalize()
    {
        // 旧形式の控え（名前だけ）を新形式へ移す。フォルダは分からないので空（＝一番上）として扱う
        foreach (var name in this.GbrDisabledLists)
            if (!this.GbrDisabledListRefs.Any(r => r.Name == name))
                this.GbrDisabledListRefs.Add(new GbrListRef(name, string.Empty));
        this.GbrDisabledLists.Clear();

        // 特殊通貨の控えを引き当て係へ渡す
        Data.SpecialCurrency.Fallback = this.SpecialCurrencyFallback;

        // 受注後に作る品の作り直しの予備（そのクリスタルを先に用意する）。開始するときにも入れ直す（JobQuestFlow）
        Planning.PlanBuilder.QuestCraftSpare = Math.Max(0, this.QuestCraftRetryRounds);

        // 選ばなかった製作職の装備の条件（作れる職の判定へ渡す）
        Data.CraftAbility.RequireGear = this.RequireGearForOtherCrafters;

        this.HqRetryRounds = Math.Clamp(this.HqRetryRounds, 0, 10);

        // 古い設定ファイルで配列の長さが違うと、チェックボックスの描画で範囲外になる
        if (this.SelectedCrafters is not { Length: 11 })
        {
            var fixedArr = new bool[11];
            if (this.SelectedCrafters != null)
                Array.Copy(this.SelectedCrafters, fixedArr, Math.Min(11, this.SelectedCrafters.Length));
            this.SelectedCrafters = fixedArr;
        }
    }

    /// <summary>いまの持ち主のファイルに保存する。ログインしていなければ保存しない（false）。</summary>
    public bool Save()
    {
        if (this.owner == 0 || this.charactersDirectory == null)
            return false;
        var ok = Core.CharacterConfigStore.Save(this, Core.CharacterConfigStore.PathFor(this.charactersDirectory, this.owner), out var problem);
        this.LastSaveProblem = problem;
        if (!ok)
            Core.DebugLog.Current?.Line("設定", $"⚠ 設定を保存できませんでした：{problem}");
        return ok;
    }
}

/// <summary>
/// 結果を確かめられていないマーケットの購入の控え。
/// State は "Sent"（送った。結果待ち）か "Unknown"（結果が分からないまま止めた）。
/// </summary>
[Serializable]
public sealed record PendingPurchaseRecord(
    ulong ContentId,
    uint ItemId,
    string ItemName,
    ulong ListingId,
    int Quantity,
    long Total,
    long GilBefore,
    int CountBefore,
    DateTime SentUtc,
    string State)
{
    /// <summary>利用者に見せる説明。</summary>
    public string Describe()
        => $"{ItemName}×{Quantity}（{Total:N0}ギル、{SentUtc.ToLocalTime():M/d HH:mm:ss} に送信。送る前のギル {GilBefore:N0}・所持 {CountBefore}）";
}

/// <summary>GBR の自動採集リストを指す控え（名前とフォルダの組）。</summary>
[Serializable]
public sealed record GbrListRef(string Name, string FolderPath);
