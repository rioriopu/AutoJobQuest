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
/// </summary>
public sealed unsafe class RequestFiller
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

    private static readonly TimeSpan OptionWaitLimit = TimeSpan.FromSeconds(5);

    private nint addon;
    private DateTime addonOpenedAt;
    private bool finished;
    private bool checkedOnce;
    private int nextSlot;
    private int selectedByUs = -1;
    private DateTime selectedAt = DateTime.MinValue;
    private readonly List<string> picked = [];

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
    /// <param name="addonAddress">いま開いている納品窓のアドレス（呼び出し側が「自分の窓」と確かめたもの）。</param>
    /// <param name="openedAt">その窓が開いた時刻（同じアドレスで開き直した窓を別の窓として扱うため）。</param>
    /// <param name="expectedItems">このクエストの納品物（アイテム ID）。</param>
    /// <param name="detail">記録に残す説明（空なら残すことは無い）。</param>
    public Outcome Tick(nint addonAddress, DateTime openedAt, IReadOnlyCollection<uint> expectedItems, out string detail)
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

        var ui = UIState.Instance();
        var agent = AgentNpcTrade.Instance();
        if (ui == null || agent == null || !agent->IsAgentActive())
            return Outcome.NotReady;

        var trade = &ui->NpcTrade;
        var count = Math.Min((int)trade->Requests.Count, trade->Requests.Items.Length);
        if (count == 0)
            return Outcome.NotReady;

        // 最初の1回：求める品がこのクエストの納品物か、カバンの品で全部の欄を満たせるかを、何か入れる前に確かめる
        // （途中の欄まで入れてから足りないと分かると、選んだままの欄が残り、窓が中途半端になるため）
        if (!this.checkedOnce)
        {
            this.checkedOnce = true;
            for (var slot = 0; slot < count; slot++)
            {
                var req = trade->Requests.Items[slot];
                var baseId = BaseId(req.ItemId);
                if (!expectedItems.Contains(baseId))
                {
                    this.finished = true;
                    detail = $"納品窓が求める品（{CraftPlanner.ItemName(baseId)}）はこのクエストの納品物ではないので触りません";
                    return Outcome.NotOurs;
                }

                var have = CountMatching(req);
                if (have < Math.Max(1, req.RequiredQuantity))
                {
                    this.finished = true;
                    detail = $"納品窓の {slot + 1} 番目（{Describe(req)}×{req.RequiredQuantity}）に合う品が {have} 個しかありません";
                    return Outcome.Failed;
                }
            }
        }

        // 他の操作が欄を選んでいる途中なら触らない（こちらが選んだ欄なら続ける）
        if (agent->SelectedTurnInSlot >= 0 && agent->SelectedTurnInSlot != this.selectedByUs)
            return Outcome.Busy;

        var res = new AtkValue();
        var param = stackalloc AtkValue[4];
        for (var i = 0; i < 4; i++)
            param[i] = default;

        while (this.nextSlot < count)
        {
            var slot = this.nextSlot;
            var req = trade->Requests.Items[slot];
            if (this.selectedByUs != slot)
            {
                agent->SelectTurnInSlot((ushort)slot);
                this.selectedByUs = slot;
                this.selectedAt = DateTime.UtcNow;
            }

            // 候補がまだ出ていなければ、次のフレームで続ける（上限を過ぎたら止める）
            if (agent->SelectedTurnInSlot != slot || agent->SelectedTurnInSlotItemOptions <= 0)
            {
                if (DateTime.UtcNow - this.selectedAt < OptionWaitLimit)
                    return Outcome.Waiting;

                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）を選びましたが、{OptionWaitLimit.TotalSeconds:0}秒たっても候補が出ません";
                return Outcome.Failed;
            }

            var option = Choose(req, agent, out var why);
            if (option < 0)
            {
                this.finished = true;
                detail = $"納品窓の {slot + 1} 番目（{Describe(req)}）の候補に、条件に合う品がありません（{why}）";
                return Outcome.Failed;
            }

            param[0].SetInt(0);      // 入れる
            param[1].SetInt(option); // 候補の番号
            agent->ReceiveEvent(&res, param, 4, 1);
            this.picked.Add($"{CraftPlanner.ItemName(BaseId(req.ItemId))}：{why}");
            this.selectedByUs = -1;
            this.nextSlot++;
        }

        // 渡す（ここで初めて「渡した」にする。二度は送らない）
        var inv = Inventory.Snapshot();
        foreach (var id in expectedItems)
            this.CountsBeforeSubmit[id] = inv.CountAll(id);

        param[0].SetInt(0);
        var addonId = agent->AddonId;
        agent->ReceiveEvent(&res, param, 4, 0);
        this.finished = true;

        var window = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
        if (window != null && window->IsVisible)
            window->Close(false);

        detail = $"納品窓に入れて、渡す操作を送りました：{string.Join(" / ", this.picked)}";
        return Outcome.Submitted;
    }

    /// <summary>HQ の品番（+1,000,000）なら元の品番に直す。</summary>
    private static uint BaseId(uint itemId) => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    private static string Describe(NpcTrade.Item req)
        => $"{CraftPlanner.ItemName(BaseId(req.ItemId))}{(req.WantHQ ? " HQ" : string.Empty)}"
           + (req.WantMateriaFilledSlots > 0 ? $" マテリア{req.WantMateriaFilledSlots}" : string.Empty)
           + (req.WantCollectible ? $" 収集価値{req.MinCollectibility}以上" : string.Empty);

    /// <summary>条件に合う品の個数（カバン・クリスタル欄・アーマリー。装備中は数えない）。</summary>
    private static int CountMatching(NpcTrade.Item req)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;

        var want = BaseId(req.ItemId);
        var total = 0;
        foreach (var type in Inventory.Containers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || s->GetBaseItemId() != want)
                    continue;
                if (Matches(req, s))
                    total += Math.Max(1, s->Quantity);
            }
        }

        return total;
    }

    private static bool Matches(NpcTrade.Item req, InventoryItem* it)
    {
        var hq = (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        return (!req.WantHQ || hq)
               && it->GetMateriaCount() >= req.WantMateriaFilledSlots
               && (!req.WantCollectible || (it->IsCollectable() && it->GetCollectability() >= req.MinCollectibility));
    }

    /// <summary>候補の中から条件に合う品の番号を選ぶ。無ければ -1。why に選んだ理由（無ければ候補の中身）。</summary>
    private static int Choose(NpcTrade.Item req, AgentNpcTrade* agent, out string why)
    {
        var fallback = -1;
        var seen = new List<string>();
        var want = BaseId(req.ItemId);
        var n = Math.Min((int)agent->SelectedTurnInSlotItemOptions, agent->SelectedTurnInSlotItemOptionValues.Length);
        for (var j = 0; j < n; j++)
        {
            var it = agent->SelectedTurnInSlotItemOptionValues[j].Value;
            if (it == null || it->ItemId == 0)
                continue;

            var hq = (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var materia = it->GetMateriaCount();
            var coll = it->IsCollectable() ? it->GetCollectability() : (ushort)0;
            seen.Add($"{(hq ? "HQ" : "NQ")}・マテリア{materia}{(it->IsCollectable() ? $"・収集価値{coll}" : string.Empty)}");

            if (it->GetBaseItemId() != want || !Matches(req, it))
                continue;

            // HQ 指定が無いときは NQ を先に使う（HQ は後の HQ 指定のために残す）
            if (!req.WantHQ && hq)
            {
                if (fallback < 0)
                    fallback = j;
                continue;
            }

            why = $"{(hq ? "HQ" : "NQ")}の品（候補 {j + 1}/{n}）";
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
