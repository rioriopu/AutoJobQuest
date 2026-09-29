using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>マテリア1個（Materia シートの行と等級）。納品窓が求める種類と、品に付いている種類を比べるのに使う。</summary>
public readonly record struct MateriaRef(ushort Id, byte Grade);

/// <summary>納品窓の1つの欄が求めるもの（品番は HQ の +1,000,000・収集品の +500,000 を外したもの）。</summary>
public sealed record RequestSlot(uint ItemId, int Quantity, bool WantHq, int WantMateria, bool WantCollectible, int MinCollectibility)
{
    /// <summary>
    /// 求めるマテリアの種類（0 でないものだけ）。値は「Materia シートの行」と「0 始まりの等級」で、ゲームは優先ではなく必須の条件として
    /// 候補を絞る（合わない品は候補にも数にも入らない：ゲーム本体の逆アセンブルで確定）。
    /// </summary>
    public IReadOnlyList<MateriaRef> WantMateriaTypes { get; init; } = [];
}

/// <summary>カバンの品・納品窓の候補1つ（品番は元の品番）。</summary>
public sealed record TurnInItem(uint BaseItemId, bool Hq, int Materia, bool Collectable, int Collectability, int Quantity)
{
    /// <summary>付いているマテリアの種類。</summary>
    public IReadOnlyList<MateriaRef> MateriaTypes { get; init; } = [];

    /// <summary>入れ物（InventoryType）と枠の番号（入れた後に、受け渡しの枠がこの品を指しているかを確かめるため。分からなければ -1）。</summary>
    public int Container { get; init; } = -1;

    public int SlotIndex { get; init; } = -1;
}

/// <summary>
/// 納品窓（Request）の読み書きの口。<see cref="RequestFiller"/> の判断をゲームから切り離すためのもの
/// （本物は <see cref="GameRequestWindow"/>。ゲームを起動せずに試すときは偽物を渡す）。
/// </summary>
public interface IRequestWindow
{
    /// <summary>窓の情報（エージェント）が使える状態か。</summary>
    bool Ready { get; }

    /// <summary>
    /// その品が利用者のギアセットの品か（マテリアの種類と等級まで一致する品。渡さない）。
    /// 偽物の窓では既定で false。
    /// </summary>
    bool IsProtected(TurnInItem item) => false;

    /// <summary>ゲーム自身の判定で、手持ちで窓の条件を満たせるか（NpcTrade.CanSatisfyRequests。読めなければ null）。</summary>
    bool? CanSatisfy { get; }

    /// <summary>欄の数（まだ入っていなければ 0）。</summary>
    int RequestCount { get; }

    RequestSlot GetRequest(int slot);

    /// <summary>
    /// いま選ばれている欄（選ばれていなければ -1）。ゲームはこの値を「入れた」ときと窓を作ったときにしか -1 に戻さない
    /// （候補0件で選んだ・候補の小窓を閉じた・窓を開き直した、では前の値が残る）。
    /// </summary>
    int SelectedSlot { get; }

    /// <summary>
    /// 候補の小窓（ContextIconMenu）がこの納品窓の上に開いているか（利用者や他のプラグインが欄を選んで、候補を選んでいる途中）。
    /// 偽物の窓では既定で false。
    /// </summary>
    bool OptionMenuOpen => false;

    /// <summary>
    /// 受け渡しの枠（HandIn の欄 <paramref name="slot"/>）が、その品を指しているか（入れたかの確かめ。読めなければ null）。
    /// 偽物の窓では既定で null（確かめない）。
    /// </summary>
    bool? SlotFilledWith(int slot, TurnInItem item) => null;

    void SelectSlot(int slot);

    /// <summary>選んだ欄の候補の数（まだ出ていなければ 0 以下）。</summary>
    int OptionCount { get; }

    TurnInItem? GetOption(int index);

    /// <summary>候補を入れる。</summary>
    void PutOption(int index);

    /// <summary>渡す。</summary>
    void Submit();

    /// <summary>カバン・クリスタル欄・アーマリーの品（装備中は含めない）。</summary>
    IReadOnlyList<TurnInItem> OwnedItems();

    /// <summary>その品の所持数（計画と同じ数え方）。</summary>
    int CountOwned(uint itemId);
}

/// <summary>
/// クエストの納品窓（Request）に、求められている条件に合う品を自動で入れて渡す
/// （HQ だろうが何だろうが自動で選んで納品する。確認は出さない）。
///
/// 操作は YesAlready の Features/Request.cs と同じ：
///   AgentNpcTrade.SelectTurnInSlot(欄) → ReceiveEvent([0, 候補の番号], 4, 1) で入れる → 最後に ReceiveEvent([0], 4, 0) で渡す。
/// 違いは候補の選び方だけ。YesAlready は常に 0 番目を入れるが、こちらは窓が求める条件
/// （UIState.NpcTrade.Requests の WantHQ・WantMateriaFilledSlots・WantCollectible/MinCollectibility）を、
/// 候補のカバンの品（AgentNpcTrade.SelectedTurnInSlotItemOptionValues）と照らして、合うものを選ぶ。
/// 選ぶ順は <see cref="Choose"/>（後の欄を満たせなくなる候補は避ける → 1つの山で求める数に届く → マテリアの種類 → HQ 指定が無ければ NQ）。
///
/// 【窓1つにつき1回だけ試す、をやめた】
/// 以前は窓を見つけた時点で「処理済み」にしてから入れていたので、窓の準備（エージェントの情報）が次のフレームで
/// そろう場合や、他の操作が欄を選んでいる瞬間に当たると、その窓は二度と扱われず、クエストが30分の上限まで止まった。
/// いまは窓ごとに状態を持ち、準備待ち・他の操作が選択中なら次のフレームで続きから試す。「渡した」は渡す操作を
/// 送った後にだけ立て、二度は送らない。
///
/// 【扱うのは自分のクエストの窓だけ】
/// 窓の持ち主（自分がクエストを始めた後に開いたか）は呼び出し側が確かめる。ここでは、窓が求める品が
/// そのクエストの納品物に含まれるかを確かめ、含まれなければ一切触らない。
///
/// ゲームの読み書きは <see cref="IRequestWindow"/> 越しに行う（判断の部分をゲームを起動せずに試せるように）。
/// </summary>
public sealed class RequestFiller
{
    /// <summary>1フレームの結果。</summary>
    public enum Outcome
    {
        /// <summary>窓の準備待ち（エージェントが無効・要求がまだ0件）。次のフレームで続ける。</summary>
        NotReady,

        /// <summary>他の操作（利用者・他のプラグイン）が欄を選んでいる。触らずに待つ。</summary>
        Busy,

        /// <summary>候補が出るのを待っている（こちらが欄を選んだ直後）・入れた後に欄の選択が戻るのを待っている。</summary>
        Waiting,

        /// <summary>このクエストの納品物ではない品を求める窓。触らない。</summary>
        NotOurs,

        /// <summary>条件に合う品が足りない・候補に無い。入れずに止める（理由は detail）。</summary>
        Failed,

        /// <summary>渡す操作を送った（この窓ではもう何もしない）。</summary>
        Submitted,

        /// <summary>この窓はもう扱い終えた（渡した・自分のものでない・失敗）。</summary>
        Finished,
    }

    public static readonly TimeSpan OptionWaitLimit = TimeSpan.FromSeconds(5);

    private readonly Func<DateTime> clock;

    private nint addon;
    private DateTime addonOpenedAt;
    private bool finished;
    private bool checkedOnce;
    private int nextSlot;
    private int selectedByUs = -1;
    private DateTime selectedAt = DateTime.MinValue;

    // 入れた欄（選択が戻るのを待つ。-1 なら無し）。YesAlready は同じフレームで次の欄へ進むが、戻る前に次の欄を選ぶと
    // 取り違えるので、戻ったのを見てから進む。最後の欄の後は待たずに渡す（YesAlready と同じ）。
    // ゲームが自分で次の欄を選んだときは、それをこちらの選択として使う（戻るのを待ち続けない）
    private int putPending = -1;
    private DateTime putAt = DateTime.MinValue;
    private readonly List<string> picked = [];

    // 欄を選んでから見たフレームの数（候補は選んだその場で出る。0件なら1フレーム待って止める）
    private int framesSinceSelect;

    // 欄ごとに入れた品（渡す前に、受け渡しの枠が全部その品を指しているかを確かめる）
    private readonly Dictionary<int, TurnInItem> putItems = [];

    // この窓に入れた品（ゲームは渡すまでカバンから消さないので、後の欄の見積もりではこちらで引く）
    private readonly List<(TurnInKey Key, int Quantity)> used = [];

    private readonly record struct TurnInKey(uint Item, bool Hq, int Materia, bool Collectable, int Collectability);

    private static TurnInKey KeyOf(TurnInItem it) => new(it.BaseItemId, it.Hq, it.Materia, it.Collectable, it.Collectability);

    /// <param name="clock">いまの時刻（省略時は UtcNow。試すときに時計を差し替える）。</param>
    public RequestFiller(Func<DateTime>? clock = null)
    {
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>渡す直前の、求められた品の所持数（渡した後に「減った」を確かめるため。Submitted のときに入る）。</summary>
    public Dictionary<uint, int> CountsBeforeSubmit { get; } = [];

    /// <summary>
    /// いまの窓で、こちらが欄を選ぶ・入れる操作をしたか。止めたときに閉じてよいのは、この窓だけ
    /// （以前はクエストの間に開いた納品窓なら、触っていなくても閉じていた）。
    /// </summary>
    public bool Touched { get; private set; }

    /// <summary>いま扱っている窓のアドレス（0 なら無し）。</summary>
    public nint Addon => this.addon;

    /// <summary>いま扱っている窓の開いた時刻。</summary>
    public DateTime AddonOpenedAt => this.addonOpenedAt;

    /// <summary>いまの窓で渡す操作を送ったか。</summary>
    public bool HasSubmitted { get; private set; }

    /// <summary>納品窓が求めるマテリアの値（読めたときだけ。実機で意味を確かめるために記録に残す）。</summary>
    public string? MateriaNote { get; private set; }

    /// <summary>窓が閉じた・別の窓になったときに呼ぶ。</summary>
    public void Reset()
    {
        this.addon = 0;
        this.addonOpenedAt = DateTime.MinValue;
        this.finished = false;
        this.checkedOnce = false;
        this.nextSlot = 0;
        this.selectedByUs = -1;
        this.selectedAt = DateTime.MinValue;
        this.putPending = -1;
        this.putAt = DateTime.MinValue;
        this.picked.Clear();
        this.used.Clear();
        this.putItems.Clear();
        this.framesSinceSelect = 0;
        this.CountsBeforeSubmit.Clear();
        this.Touched = false;
        this.HasSubmitted = false;
        this.MateriaNote = null;
    }

    /// <summary>
    /// 1フレーム分進める。
    /// </summary>
    /// <param name="window">納品窓の読み書きの口。</param>
    /// <param name="addonAddress">いま開いている納品窓のアドレス（呼び出し側が「自分の窓」と確かめたもの）。</param>
    /// <param name="openedAt">その窓が開いた時刻（同じアドレスで開き直した窓を別の窓として扱うため）。</param>
    /// <param name="expectedItems">このクエストの納品物（アイテム ID）。</param>
    /// <param name="detail">記録に残す説明（空なら残すことは無い）。</param>
    public Outcome Tick(IRequestWindow window, nint addonAddress, DateTime openedAt, IReadOnlyCollection<uint> expectedItems, out string detail)
    {
        detail = string.Empty;
        if (addonAddress != this.addon || openedAt != this.addonOpenedAt)
        {
            this.Reset();
            this.addon = addonAddress;
            this.addonOpenedAt = openedAt;
        }

        if (this.finished)
            return Outcome.Finished;

        if (!window.Ready)
            return Outcome.NotReady;

        var count = window.RequestCount;
        if (count <= 0)
            return Outcome.NotReady;

        // 最初の1回：求める品がこのクエストの納品物か、カバンの品で全部の欄を満たせるかを、何か入れる前に確かめる
        // （途中の欄まで入れてから足りないと分かると、選んだままの欄が残り、窓が中途半端になるため）
        if (!this.checkedOnce)
        {
            this.checkedOnce = true;
            var reqs = Enumerable.Range(0, count).Select(window.GetRequest).ToList();
            for (var slot = 0; slot < count; slot++)
            {
                if (!expectedItems.Contains(reqs[slot].ItemId))
                {
                    this.finished = true;
                    detail = $"納品窓が求める品（{CraftPlanner.ItemName(reqs[slot].ItemId)}）はこのクエストの納品物ではないので触りません";
                    return Outcome.NotOurs;
                }
            }

            // 同じ品を求める欄が複数あっても、1つの品を2つの欄に数えない（取り置きながら数える）
            if (!Reserve(reqs, window.OwnedItems(), out var failedSlot, out var have))
            {
                this.finished = true;
                var req = reqs[failedSlot];
                detail = Math.Max(1, req.Quantity) > 1
                    ? $"納品窓の {failedSlot + 1} 番目（{Describe(req)}×{req.Quantity}）に、1つの山で {req.Quantity} 個以上の品が、ほかの欄の分を除くと見つかりません（合う品の一番大きい山 {have} 個。ゲームは山をまたいで数えない）"
                    : $"納品窓の {failedSlot + 1} 番目（{Describe(req)}）に合う品が、ほかの欄の分を除くと {have} 個しかありません";
                return Outcome.Failed;
            }

            // ゲーム自身の判定でも満たせないなら入れない（YesAlready と同じ確かめ）
            if (window.CanSatisfy == false)
            {
                this.finished = true;
                detail = $"ゲームの判定（CanSatisfyRequests）では、手持ちで納品窓の条件を満たせません（求める品：{string.Join("、", reqs.Select(r => $"{Describe(r)}×{Math.Max(1, r.Quantity)}"))}）";
                return Outcome.Failed;
            }

            var types = reqs.Where(r => r.WantMateriaTypes.Count > 0).ToList();
            if (types.Count > 0)
                this.MateriaNote = $"納品窓が求めるマテリアの値：{string.Join(" / ", types.Select(r => $"{CraftPlanner.ItemName(r.ItemId)}＝{string.Join("・", r.WantMateriaTypes.Select(m => $"{m.Id}-{m.Grade}"))}"))}（実機で意味を確かめるための記録）";
        }

        while (true)
        {
            // 入れた後、欄の選択が戻るのを待つ（上限を過ぎたら止める）
            if (this.putPending >= 0)
            {
                if (window.SelectedSlot == this.putPending)
                {
                    if (this.clock() - this.putAt < OptionWaitLimit)
                        return Outcome.Waiting;

                    this.finished = true;
                    detail = $"納品窓の {this.putPending + 1} 番目に入れた後、{OptionWaitLimit.TotalSeconds:0}秒たっても欄の選択が戻りません";
                    return Outcome.Failed;
                }

                // ゲームが自分で次の欄を選んだ：こちらの選択として続ける（「他の操作が選択中」と取り違えない）
                if (this.nextSlot < count && window.SelectedSlot == this.nextSlot)
                {
                    this.selectedByUs = this.nextSlot;
                    this.selectedAt = this.clock();
                }

                this.putPending = -1;
            }

            if (this.nextSlot >= count)
                break;

            // 他の操作が欄を選んで、候補を選んでいる途中（候補の小窓がこの窓の上に開いている）なら触らない。
            // 選ばれた欄の番号だけが残っていても、それはゲームが -1 に戻さない古い値のことがある（候補0件で選んだ・小窓を閉じた等）。
            // そのときは待たずに、こちらの欄を選び直す（選び直すとゲームは前の小窓を閉じてから開き直す。
            // 以前は古い値が残っているだけで「他の操作が選択中」として待ち続け、30分の上限まで止まりえた）
            if (window.SelectedSlot >= 0 && window.SelectedSlot != this.selectedByUs && window.OptionMenuOpen)
            {
                this.selectedByUs = -1;
                return Outcome.Busy;
            }

            var slot = this.nextSlot;
            var req = window.GetRequest(slot);
            if (this.selectedByUs != slot || window.SelectedSlot != slot)
            {
                window.SelectSlot(slot);
                this.selectedByUs = slot;
                this.selectedAt = this.clock();
                this.framesSinceSelect = 0;
                this.Touched = true;
            }
            else
            {
                this.framesSinceSelect++;
            }

            // 候補はゲームが欄を選んだその場で作る。0件は「条件に合う山が無い」で、待っても出てこない。
            // 1フレームだけ待ってから、理由を出して止める。欄の選択そのものが通っていない（番号が違う）ときだけ、上限まで待つ
            if (window.SelectedSlot != slot)
            {
                if (this.clock() - this.selectedAt < OptionWaitLimit)
                    return Outcome.Waiting;

                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）を選べませんでした（{OptionWaitLimit.TotalSeconds:0}秒）";
                return Outcome.Failed;
            }

            if (window.OptionCount <= 0)
            {
                if (this.framesSinceSelect < 1)
                    return Outcome.Waiting;

                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}×{Math.Max(1, req.Quantity)}）に、ゲームが出す候補が0件です"
                         + "（ゲームは「1つの山で求める数以上」「HQ 指定なら HQ」「求めるマテリアの種類と等級」に合う山だけを候補に出す。山が分かれている・品質やマテリアが違う可能性）";
                return Outcome.Failed;
            }

            // 後の欄を満たせなくなる候補は避ける（取り置きの確かめと同じ数え方。
            // 以前は「1つの山で届く」を先にしたので、後の HQ の欄に要る HQ を先の欄で使いうった）
            var rest = Enumerable.Range(slot + 1, count - slot - 1).Select(window.GetRequest).ToList();
            var pool = rest.Count > 0 ? this.Available(window.OwnedItems()) : null;
            var option = Choose(req, window, out var why, pool == null ? null : it => KeepsRest(rest, pool, it, req.Quantity));
            if (option < 0)
            {
                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）の候補に、条件に合う品がありません（{why}）";
                return Outcome.Failed;
            }

            var chosen = window.GetOption(option)!;
            window.PutOption(option);

            // 入れたか確かめる：受け渡しの枠がその品を指しているか（選択が -1 に戻ったことは成功の印にならない。入れる操作が失敗しても戻る）。
            // 指していなければ次へ進まず、渡さずに止める
            if (window.SlotFilledWith(slot, chosen) == false)
            {
                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）に入れましたが、受け渡しの枠が選んだ品を指していません（渡さずに止めます）";
                return Outcome.Failed;
            }

            this.putItems[slot] = chosen;
            this.used.Add((KeyOf(chosen), Math.Min(Math.Max(1, req.Quantity), Math.Max(1, chosen.Quantity))));
            this.picked.Add($"{CraftPlanner.ItemName(req.ItemId)}：{why}");
            this.selectedByUs = -1;
            this.nextSlot++;

            // 最後の欄なら待たずに渡す。まだ欄が残っていれば、選択が戻るのを見てから次へ
            if (this.nextSlot < count)
            {
                this.putPending = slot;
                this.putAt = this.clock();
            }
        }

        // 渡す前に、全部の欄の受け渡しの枠が入れた品を指しているか確かめる（1つでも外れていれば渡さない）
        foreach (var (slot, item) in this.putItems)
        {
            if (window.SlotFilledWith(slot, item) == false)
            {
                this.finished = true;
                detail = $"渡す前に確かめたら、納品窓の {slot + 1} 番目の受け渡しの枠が入れた品を指していません（渡さずに止めます）";
                return Outcome.Failed;
            }
        }

        // 渡す（ここで初めて「渡した」にする。二度は送らない）
        foreach (var id in expectedItems)
            this.CountsBeforeSubmit[id] = window.CountOwned(id);

        window.Submit();
        this.finished = true;
        this.HasSubmitted = true;

        detail = $"納品窓に入れて、渡す操作を送りました：{string.Join(" / ", this.picked)}";
        return Outcome.Submitted;
    }

    /// <summary>納品窓の品番を元の品番に直す（HQ は +1,000,000、収集品は +500,000 されている）。</summary>
    public static uint BaseItemId(uint raw)
        => raw >= 2_000_000 ? raw : raw >= 1_000_000 ? raw - 1_000_000 : raw >= 500_000 ? raw - 500_000 : raw;

    private static string Describe(RequestSlot req)
        => $"{CraftPlanner.ItemName(req.ItemId)}{(req.WantHq ? " HQ" : string.Empty)}"
           + (req.WantMateria > 0 ? $" マテリア{req.WantMateria}" : string.Empty)
           + (req.WantCollectible ? $" 収集価値{req.MinCollectibility}以上" : string.Empty);

    /// <summary>
    /// その品が欄の条件に合うか（品番は呼び出し側で確かめる）。ゲームが候補に出す条件と同じにする：
    /// HQ 指定なら HQ、マテリアの数と種類・等級（必須）、収集品を求める欄は収集品で収集価値以上・求めない欄は収集品を除く。
    /// </summary>
    public static bool Matches(RequestSlot req, TurnInItem it)
        => (!req.WantHq || it.Hq)
           && it.Materia >= req.WantMateria
           && MateriaTypesMatch(req, it)
           && (req.WantCollectible ? it.Collectable && it.Collectability >= req.MinCollectibility : !it.Collectable);

    /// <summary>求めるマテリアの種類（0 でないもの）が、その品に全部付いているか。求める種類が無ければ true。</summary>
    public static bool MateriaTypesMatch(RequestSlot req, TurnInItem it)
    {
        var want = req.WantMateriaTypes.Where(m => m.Id != 0).ToList();
        if (want.Count == 0)
            return true;
        var have = it.MateriaTypes.ToList();
        foreach (var m in want)
        {
            var at = have.IndexOf(m);
            if (at < 0)
                return false;
            have.RemoveAt(at);
        }

        return true;
    }

    /// <summary>
    /// 全部の欄を、ほかの欄の分を除いても満たせるか（同じ品を求める欄が複数あるとき、1つの品を2つの欄に数えない）。
    /// 条件の厳しい欄（収集品・HQ・マテリアの数が多い）から先に、条件を満たす品のうち価値の低いもの（NQ・マテリアの少ないもの）を取り置く。
    /// 【ゲームの規則に合わせた】1つの欄に N 個を求めるとき、ゲームは「1つの山で N 個以上」の山しか候補に出さない
    /// （山をまたいで足さない。HQ 指定の無い欄でも NQ と HQ を合算しない）。そこで取り置きも、1つの山から N 個を取る。
    /// 満たせなければ、その欄の番号と、その欄に使える一番大きい山の数を返す。
    /// </summary>
    public static bool Reserve(IReadOnlyList<RequestSlot> reqs, IReadOnlyList<TurnInItem> owned, out int failedSlot, out int have)
    {
        var left = owned.Select(it => Math.Max(1, it.Quantity)).ToArray();
        var order = Enumerable.Range(0, reqs.Count)
            .OrderByDescending(i => reqs[i].WantCollectible)
            .ThenByDescending(i => reqs[i].WantHq)
            .ThenByDescending(i => reqs[i].WantMateria)
            .ThenBy(i => i)
            .ToList();
        foreach (var i in order)
        {
            var req = reqs[i];
            var need = Math.Max(1, req.Quantity);
            var candidates = Enumerable.Range(0, owned.Count)
                .Where(j => left[j] > 0 && owned[j].BaseItemId == req.ItemId && Matches(req, owned[j]))
                .OrderBy(j => owned[j].Hq)
                .ThenBy(j => owned[j].Materia)
                .ThenBy(j => owned[j].Collectability)
                .ToList();
            var stack = candidates.FirstOrDefault(j => left[j] >= need, -1);
            if (stack < 0)
            {
                failedSlot = i;
                have = candidates.Count == 0 ? 0 : candidates.Max(j => left[j]);
                return false;
            }

            left[stack] -= need;
        }

        failedSlot = -1;
        have = 0;
        return true;
    }

    /// <summary>手持ちから、この窓にもう入れた分を引いた残り。</summary>
    private List<TurnInItem> Available(IReadOnlyList<TurnInItem> owned)
    {
        var qty = owned.Select(it => Math.Max(1, it.Quantity)).ToArray();
        foreach (var (key, q) in this.used)
        {
            var need = q;
            for (var i = 0; i < owned.Count && need > 0; i++)
            {
                if (qty[i] <= 0 || KeyOf(owned[i]) != key)
                    continue;
                var take = Math.Min(need, qty[i]);
                qty[i] -= take;
                need -= take;
            }
        }

        return owned.Select((it, i) => it with { Quantity = qty[i] }).Where(it => it.Quantity > 0).ToList();
    }

    /// <summary>その候補をこの欄に使っても、残りの欄を満たせるか（<see cref="Reserve"/> と同じ数え方）。</summary>
    public static bool KeepsRest(IReadOnlyList<RequestSlot> rest, IReadOnlyList<TurnInItem> pool, TurnInItem candidate, int quantity)
    {
        var left = pool.ToList();
        var take = Math.Min(Math.Max(1, quantity), Math.Max(1, candidate.Quantity));
        for (var i = 0; i < left.Count && take > 0; i++)
        {
            if (KeyOf(left[i]) != KeyOf(candidate))
                continue;
            var n = Math.Min(take, Math.Max(1, left[i].Quantity));
            take -= n;
            left[i] = left[i] with { Quantity = Math.Max(1, left[i].Quantity) - n };
        }

        return Reserve(rest, left.Where(it => it.Quantity > 0).ToList(), out _, out _);
    }

    /// <summary>
    /// 候補の中から条件に合う品の番号を選ぶ。無ければ -1。why に選んだ理由（無ければ候補の中身）。
    /// 条件に合う候補のうち、次の順でよいものを選ぶ（同じなら候補の並びの先のもの）：
    ///  0) 後の欄を満たせなくなる候補を避ける（<paramref name="keepsRest"/>。後の欄が無ければ見ない）
    ///  1) 1つの山で求める数に届く（重ねられる品で、山が分かれているとき。実物のゲームは届く山しか候補に出さない）
    ///  2) マテリアを求めない欄なら、マテリアの付いていない品（利用者がマテリアを付けた品を渡さない）
    ///  3) HQ 指定が無ければ NQ（HQ は後の HQ 指定のために残す。ゲームの候補の並びは HQ が先なので、先頭を選ぶと HQ が入る）
    /// マテリアの種類と等級は <see cref="Matches"/> で必須にしている（ゲームも合わない品を候補に出さない）。
    /// 利用者のギアセットの品（マテリアまで一致する品：<see cref="IRequestWindow.IsProtected"/>）は選ばない。それしか合わなければ -1。
    /// </summary>
    public static int Choose(RequestSlot req, IRequestWindow window, out string why, Func<TurnInItem, bool>? keepsRest = null)
    {
        var best = -1;
        var bestScore = (Keeps: 0, Enough: 0, Plain: 0, Nq: 0);
        TurnInItem? chosen = null;
        var seen = new List<string>();
        var n = window.OptionCount;
        for (var j = 0; j < n; j++)
        {
            var it = window.GetOption(j);
            if (it == null)
                continue;

            seen.Add($"{(it.Hq ? "HQ" : "NQ")}・{it.Quantity}個・マテリア{it.Materia}{(it.Collectable ? $"・収集価値{it.Collectability}" : string.Empty)}");

            if (it.BaseItemId != req.ItemId || !Matches(req, it))
                continue;
            if (window.IsProtected(it))
            {
                seen[^1] += "（ギアセットの品なので渡さない）";
                continue;
            }

            // マテリアの種類は Matches で必須にした（ゲームも合わない品を候補に出さない）ので、ここでは比べない
            var score = (
                Keeps: keepsRest == null || keepsRest(it) ? 1 : 0,
                Enough: it.Quantity >= Math.Max(1, req.Quantity) ? 1 : 0,
                Plain: req.WantMateria > 0 || it.Materia == 0 ? 1 : 0,
                Nq: !req.WantHq && !it.Hq ? 1 : 0);
            if (best < 0 || score.CompareTo(bestScore) > 0)
            {
                best = j;
                bestScore = score;
                chosen = it;
            }
        }

        if (best < 0 || chosen == null)
        {
            why = seen.Count == 0 ? "候補を読めませんでした" : "候補：" + string.Join("、", seen);
            return -1;
        }

        var notes = new List<string> { $"候補 {best + 1}/{n}" };
        if (bestScore.Keeps == 0)
            notes.Add("後の欄が足りなくなる恐れ（ほかに候補が無い）");
        if (bestScore.Enough == 0)
            notes.Add($"1つの山では {req.Quantity} 個に届かない");
        if (req.WantMateriaTypes.Any(m => m.Id != 0))
            notes.Add("マテリアの種類が一致");
        if (bestScore.Plain == 0)
            notes.Add("マテリアの付いていない品が無いため、マテリア付きの品");
        if (!req.WantHq && chosen.Hq)
            notes.Add("NQ が無いため");
        why = $"{(chosen.Hq ? "HQ" : "NQ")}の品（{string.Join("・", notes)}）";
        return best;
    }
}

/// <summary>
/// 本物の納品窓の読み書き（UIState.NpcTrade と AgentNpcTrade。YesAlready の Features/Request.cs と同じ操作）。
/// フレームワークのスレッドから呼ぶこと。
/// </summary>
public sealed unsafe class GameRequestWindow : IRequestWindow
{
    public static readonly GameRequestWindow Instance = new();

    private static AgentNpcTrade* Agent => AgentNpcTrade.Instance();

    public bool Ready
    {
        get
        {
            var ui = UIState.Instance();
            var agent = Agent;
            return ui != null && agent != null && agent->IsAgentActive();
        }
    }

    public bool? CanSatisfy
    {
        get
        {
            // ゲームの関数を位置（シグネチャ）で呼ぶので、ゲームの更新で見つからなくなると例外になる。
            // そのときは「読めない」（null）として、こちらの数えで進める（作業ごと止めない）
            try
            {
                var ui = UIState.Instance();
                return ui == null ? null : ui->NpcTrade.CanSatisfyRequests();
            }
            catch (Exception ex)
            {
                Core.DebugLog.Current?.Line("納品", $"CanSatisfyRequests を呼べませんでした（{ex.GetType().Name}）。こちらの数えだけで進めます");
                return null;
            }
        }
    }

    public int RequestCount
    {
        get
        {
            var ui = UIState.Instance();
            return ui == null ? 0 : Math.Min((int)ui->NpcTrade.Requests.Count, ui->NpcTrade.Requests.Items.Length);
        }
    }

    public RequestSlot GetRequest(int slot)
    {
        var r = UIState.Instance()->NpcTrade.Requests.Items[slot];
        var types = new List<MateriaRef>();
        for (var i = 0; i < r.WantMateriaIds.Length && i < r.WantMateriaGrades.Length; i++)
        {
            if (r.WantMateriaIds[i] != 0)
                types.Add(new MateriaRef(r.WantMateriaIds[i], r.WantMateriaGrades[i]));
        }

        // HQ・収集品の品番なら元の品番に直す
        return new RequestSlot(RequestFiller.BaseItemId(r.ItemId), r.RequiredQuantity, r.WantHQ, r.WantMateriaFilledSlots, r.WantCollectible, r.MinCollectibility)
        {
            WantMateriaTypes = types,
        };
    }

    public int SelectedSlot => Agent->SelectedTurnInSlot;

    public bool OptionMenuOpen
    {
        get
        {
            // 候補の小窓が見えていて、その「ふさいでいる親」が納品窓なら、誰かが候補を選んでいる途中
            var menu = GameUi.Addon("ContextIconMenu");
            var request = GameUi.Addon("Request");
            return menu != null && request != null && menu->BlockedParentId == request->Id;
        }
    }

    public bool? SlotFilledWith(int slot, TurnInItem item)
    {
        if (item.Container < 0 || item.SlotIndex < 0)
            return null;
        var im = InventoryManager.Instance();
        var c = im == null ? null : im->GetInventoryContainer(InventoryType.HandIn);
        if (c == null || !c->IsLoaded || slot >= c->Size)
            return null;
        var s = c->GetInventorySlot(slot);
        return s != null && s->IsSymbolic && s->LinkedInventoryType == (ushort)item.Container && s->LinkedItemSlot == (ushort)item.SlotIndex;
    }

    public void SelectSlot(int slot) => Agent->SelectTurnInSlot((ushort)slot);

    public int OptionCount
    {
        get
        {
            var agent = Agent;
            return Math.Min((int)agent->SelectedTurnInSlotItemOptions, agent->SelectedTurnInSlotItemOptionValues.Length);
        }
    }

    public TurnInItem? GetOption(int index)
    {
        var it = Agent->SelectedTurnInSlotItemOptionValues[index].Value;
        return it == null || it->ItemId == 0 ? null : FromSlot(it);
    }

    public void PutOption(int index)
    {
        var res = new AtkValue();
        var param = stackalloc AtkValue[4];
        for (var i = 0; i < 4; i++)
            param[i] = default;
        param[0].SetInt(0);     // 入れる
        param[1].SetInt(index); // 候補の番号
        Agent->ReceiveEvent(&res, param, 4, 1);
    }

    public void Submit()
    {
        var agent = Agent;
        var res = new AtkValue();
        var param = stackalloc AtkValue[4];
        for (var i = 0; i < 4; i++)
            param[i] = default;
        param[0].SetInt(0);
        var addonId = agent->AddonId;
        agent->ReceiveEvent(&res, param, 4, 0);

        // 渡したあと窓が見えていれば閉じる（YesAlready と同じ）
        var window = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
        if (window != null && window->IsVisible)
            window->Close(false);
    }

    // 利用者のギアセットの品（その窓の間だけ読み直す。GearsetGuard）
    private List<GearsetPiece> pieces = [];

    public bool IsProtected(TurnInItem item) => GearsetGuard.IsProtectedMelded(item.BaseItemId, item.Hq, item.MateriaTypes, this.pieces);

    public IReadOnlyList<TurnInItem> OwnedItems()
    {
        var list = new List<TurnInItem>();
        this.pieces = GearsetGuard.Read();
        var im = InventoryManager.Instance();
        if (im == null)
            return list;
        foreach (var type in Inventory.Containers.Append(InventoryType.KeyItems))
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                    continue;

                // 利用者のギアセットの品（マテリアまで一致）は、手持ちとして数えない（渡さない）
                var item = FromSlot(s);
                if (!this.IsProtected(item))
                    list.Add(item);
            }
        }

        return list;
    }

    public int CountOwned(uint itemId) => itemId >= 2_000_000
        ? this.OwnedItems().Where(x => x.BaseItemId == itemId).Sum(x => x.Quantity)
        : Inventory.Snapshot().CountAll(itemId);

    private static TurnInItem FromSlot(InventoryItem* it)
    {
        if (it->Container == InventoryType.KeyItems)
            return new TurnInItem(it->ItemId, false, 0, false, 0, it->Quantity) { Container = (int)it->Container, SlotIndex = it->Slot };
        var count = it->GetMateriaCount();

        // マテリアの種類（ゲームは必須の条件として候補を絞る）。読む関数（位置で呼ぶ）が見つからなければ空にする
        var types = new List<MateriaRef>();
        try
        {
            for (byte i = 0; i < count && i < 5; i++)
                types.Add(new MateriaRef(it->GetMateriaId(i), it->GetMateriaGrade(i)));
        }
        catch (Exception)
        {
            types.Clear();
        }

        return new TurnInItem(
            it->GetBaseItemId(),
            (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
            count,
            it->IsCollectable(),
            it->IsCollectable() ? it->GetCollectability() : 0,
            it->Quantity)
        {
            MateriaTypes = types,
            Container = (int)it->Container,
            SlotIndex = it->Slot,
        };
    }
}
