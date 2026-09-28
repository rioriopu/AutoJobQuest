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

/// <summary>納品窓の1つの欄が求めるもの（品番は HQ の +1,000,000 を外したもの）。</summary>
public sealed record RequestSlot(uint ItemId, int Quantity, bool WantHq, int WantMateria, bool WantCollectible, int MinCollectibility);

/// <summary>カバンの品・納品窓の候補1つ（品番は元の品番）。</summary>
public sealed record TurnInItem(uint BaseItemId, bool Hq, int Materia, bool Collectable, int Collectability, int Quantity);

/// <summary>
/// 納品窓（Request）の読み書きの口。<see cref="RequestFiller"/> の判断をゲームから切り離すためのもの
/// （本物は <see cref="GameRequestWindow"/>。ゲームを起動せずに試すときは偽物を渡す）。
/// </summary>
public interface IRequestWindow
{
    /// <summary>窓の情報（エージェント）が使える状態か。</summary>
    bool Ready { get; }

    /// <summary>欄の数（まだ入っていなければ 0）。</summary>
    int RequestCount { get; }

    RequestSlot GetRequest(int slot);

    /// <summary>いま選ばれている欄（選ばれていなければ -1）。</summary>
    int SelectedSlot { get; }

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
/// HQ 指定が無ければ NQ を先に使う（HQ を残すため）。
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

        /// <summary>候補が出るのを待っている（こちらが欄を選んだ直後）。</summary>
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
    private readonly List<string> picked = [];

    /// <param name="clock">いまの時刻（省略時は UtcNow。試すときに時計を差し替える）。</param>
    public RequestFiller(Func<DateTime>? clock = null)
    {
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>渡す直前の、求められた品の所持数（渡した後に「減った」を確かめるため。Submitted のときに入る）。</summary>
    public Dictionary<uint, int> CountsBeforeSubmit { get; } = [];

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
        this.picked.Clear();
        this.CountsBeforeSubmit.Clear();
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
            var owned = window.OwnedItems();
            for (var slot = 0; slot < count; slot++)
            {
                var req = window.GetRequest(slot);
                if (!expectedItems.Contains(req.ItemId))
                {
                    this.finished = true;
                    detail = $"納品窓が求める品（{CraftPlanner.ItemName(req.ItemId)}）はこのクエストの納品物ではないので触りません";
                    return Outcome.NotOurs;
                }

                var have = owned.Where(it => it.BaseItemId == req.ItemId && Matches(req, it)).Sum(it => Math.Max(1, it.Quantity));
                if (have < Math.Max(1, req.Quantity))
                {
                    this.finished = true;
                    detail = $"納品窓の {slot + 1} 番目（{Describe(req)}×{req.Quantity}）に合う品が {have} 個しかありません";
                    return Outcome.Failed;
                }
            }
        }

        // 他の操作が欄を選んでいる途中なら触らない（こちらが選んだ欄なら続ける）
        if (window.SelectedSlot >= 0 && window.SelectedSlot != this.selectedByUs)
            return Outcome.Busy;

        while (this.nextSlot < count)
        {
            var slot = this.nextSlot;
            var req = window.GetRequest(slot);
            if (this.selectedByUs != slot)
            {
                window.SelectSlot(slot);
                this.selectedByUs = slot;
                this.selectedAt = this.clock();
            }

            // 候補がまだ出ていなければ、次のフレームで続ける（上限を過ぎたら止める）
            if (window.SelectedSlot != slot || window.OptionCount <= 0)
            {
                if (this.clock() - this.selectedAt < OptionWaitLimit)
                    return Outcome.Waiting;

                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）を選びましたが、{OptionWaitLimit.TotalSeconds:0}秒たっても候補が出ません";
                return Outcome.Failed;
            }

            var option = Choose(req, window, out var why);
            if (option < 0)
            {
                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）の候補に、条件に合う品がありません（{why}）";
                return Outcome.Failed;
            }

            window.PutOption(option);
            this.picked.Add($"{CraftPlanner.ItemName(req.ItemId)}：{why}");
            this.selectedByUs = -1;
            this.nextSlot++;
        }

        // 渡す（ここで初めて「渡した」にする。二度は送らない）
        foreach (var id in expectedItems)
            this.CountsBeforeSubmit[id] = window.CountOwned(id);

        window.Submit();
        this.finished = true;

        detail = $"納品窓に入れて、渡す操作を送りました：{string.Join(" / ", this.picked)}";
        return Outcome.Submitted;
    }

    private static string Describe(RequestSlot req)
        => $"{CraftPlanner.ItemName(req.ItemId)}{(req.WantHq ? " HQ" : string.Empty)}"
           + (req.WantMateria > 0 ? $" マテリア{req.WantMateria}" : string.Empty)
           + (req.WantCollectible ? $" 収集価値{req.MinCollectibility}以上" : string.Empty);

    /// <summary>その品が欄の条件（HQ・マテリアの数・収集価値）に合うか（品番は呼び出し側で確かめる）。</summary>
    public static bool Matches(RequestSlot req, TurnInItem it)
        => (!req.WantHq || it.Hq)
           && it.Materia >= req.WantMateria
           && (!req.WantCollectible || (it.Collectable && it.Collectability >= req.MinCollectibility));

    /// <summary>候補の中から条件に合う品の番号を選ぶ。無ければ -1。why に選んだ理由（無ければ候補の中身）。</summary>
    public static int Choose(RequestSlot req, IRequestWindow window, out string why)
    {
        var fallback = -1;
        var seen = new List<string>();
        var n = window.OptionCount;
        for (var j = 0; j < n; j++)
        {
            var it = window.GetOption(j);
            if (it == null)
                continue;

            seen.Add($"{(it.Hq ? "HQ" : "NQ")}・マテリア{it.Materia}{(it.Collectable ? $"・収集価値{it.Collectability}" : string.Empty)}");

            if (it.BaseItemId != req.ItemId || !Matches(req, it))
                continue;

            // HQ 指定が無いときは NQ を先に使う（HQ は後の HQ 指定のために残す）
            if (!req.WantHq && it.Hq)
            {
                if (fallback < 0)
                    fallback = j;
                continue;
            }

            why = $"{(it.Hq ? "HQ" : "NQ")}の品（候補 {j + 1}/{n}）";
            return j;
        }

        if (fallback >= 0)
        {
            why = $"HQの品（NQ が無いため。候補 {fallback + 1}/{n}）";
            return fallback;
        }

        why = seen.Count == 0 ? "候補を読めませんでした" : "候補：" + string.Join("、", seen);
        return -1;
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
        var id = r.ItemId >= 1_000_000 ? r.ItemId - 1_000_000 : r.ItemId; // HQ の品番なら元の品番に直す
        return new RequestSlot(id, r.RequiredQuantity, r.WantHQ, r.WantMateriaFilledSlots, r.WantCollectible, r.MinCollectibility);
    }

    public int SelectedSlot => Agent->SelectedTurnInSlot;

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

    public IReadOnlyList<TurnInItem> OwnedItems()
    {
        var list = new List<TurnInItem>();
        var im = InventoryManager.Instance();
        if (im == null)
            return list;
        foreach (var type in Inventory.Containers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0)
                    list.Add(FromSlot(s));
            }
        }

        return list;
    }

    public int CountOwned(uint itemId) => Inventory.Snapshot().CountAll(itemId);

    private static TurnInItem FromSlot(InventoryItem* it)
        => new(
            it->GetBaseItemId(),
            (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
            it->GetMateriaCount(),
            it->IsCollectable(),
            it->IsCollectable() ? it->GetCollectability() : 0,
            it->Quantity);
}
