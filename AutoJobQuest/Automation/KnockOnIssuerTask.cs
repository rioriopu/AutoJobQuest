using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 前提のメインクエストが未完了で受けられないジョブクエの、受注の NPC のところへ行って話しかけ、断られたのを確かめる
/// （受けられないクエストを発行している NPC に話しかけて弾かれた時点で、このプラグインの動作をそこで停止する）。
/// この作業が終わったら、流れ全体を止める（呼び出し側の <see cref="JobQuestFlow"/>）。結果（<see cref="Result"/>）は止めるときの文言に使う。
///  ・場所：クエストの受注の NPC（Quest.IssuerStart）と場所（Quest.IssuerLocation → Level）。ゲームデータから引く。
///  ・話しかけ：<see cref="TalkToNpcTask"/>（テレポ → 移動 → 話しかけ）。自分が話しかけた後に、会話・選択肢・受注の窓が出るか、
///    会話の状態（OccupiedInEvent・OccupiedInQuestEvent）になったら「返事があった」とする。
///  ・返事の後：会話は進め、選択肢は何も選ばずに閉じる。受注の窓（JournalAccept）が出たら、受けられる様子（こちらの判定と食い違い）なので、
///    受けずに閉じる。会話が終わって動ける状態が1秒続いたら「断られた」とする。
/// </summary>
public sealed unsafe class KnockOnIssuerTask : AutoTask
{
    /// <summary>確かめた結果。</summary>
    public enum Outcome
    {
        /// <summary>話しかけて、受注の窓が出ずに会話が終わった（断られた）。</summary>
        Rejected,

        /// <summary>受注の窓が出た（受けられる様子。受けずに閉じた）。</summary>
        Offered,

        /// <summary>受注の NPC の場所がゲームデータから引けない（話しかけずに止める）。</summary>
        NoLocation,

        /// <summary>NPC のところへ行けない・話しかけても返事が無い。</summary>
        CouldNotTalk,
    }

    private static readonly TimeSpan SettleFree = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchLimit = TimeSpan.FromMinutes(1);

    private readonly BlockedQuest blocked;
    private TalkToNpcTask? talk;
    private bool reacted;
    private DateTime claimedAt = DateTime.MinValue;
    private DateTime lastTalk = DateTime.MinValue;
    private DateTime? freeSince;
    private readonly List<string> menus = [];

    public KnockOnIssuerTask(BlockedQuest blocked)
    {
        this.blocked = blocked;
    }

    public override string Name => $"受けられないジョブクエの確認: {Jobs.Name(this.blocked.Quest.ClassJobId)} {this.blocked.Quest}";

    /// <summary>確かめた結果（終わるまでは null）。</summary>
    public Outcome? Result { get; private set; }

    /// <summary>結果の補足（行けなかった理由・閉じた選択肢など）。</summary>
    public string? Detail { get; private set; }

    /// <summary>そのクエストの受注の NPC と場所（ゲームデータから）。引けなければ null。</summary>
    public static NpcSpot? IssuerSpot(uint questId)
    {
        if (!Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q))
            return null;
        var npc = q.IssuerStart.RowId;
        if (npc == 0 || q.IssuerLocation.ValueNullable is not { } lv || lv.Territory.RowId == 0)
            return null;
        return new NpcSpot(npc, lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z));
    }

    /// <summary>その NPC の名前（読めなければ「受注の NPC」）。</summary>
    public static string NpcName(uint npcId)
        => Svc.Data.GetExcelSheet<ENpcResident>().TryGetRow(npcId, out var r) && r.Singular.ExtractText() is { Length: > 0 } n ? n : "受注の NPC";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (IssuerSpot(this.blocked.Quest.RowId) is not { } spot)
        {
            this.Result = Outcome.NoLocation;
            ctx.Log.Warn("クエスト", $"{this.blocked.Quest} の受注の NPC の場所がゲームデータから引けないので、話しかけずに止めます");
            return TaskResult.Done;
        }

        var name = NpcName(spot.NpcId);
        ctx.Log.Write("クエスト", $"{Jobs.Name(this.blocked.Quest.ClassJobId)} {this.blocked.Quest} は受けられない見込みです（{this.blocked.Reason}）。"
                                 + $"受注の NPC「{name}」（{TeleportTask.TerritoryName(spot.Territory)}）に話しかけて確かめます");
        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        this.claimedAt = DateTime.UtcNow;
        this.talk = new TalkToNpcTask(spot, () => this.Reacted(ctx), name);
        return TaskResult.Running;
    }

    /// <summary>自分が話しかけた後に、NPC の返事（会話・選択肢・受注の窓・会話の状態）があったか。</summary>
    private bool Reacted(TaskContext ctx)
    {
        if (this.reacted)
            return true;
        if (!ctx.InOwnConversation)
            return false; // 自分が話しかける前（移動中など）の会話は見ない
        var c = Svc.Condition;
        this.reacted = GameUi.IsVisible("Talk") || GameUi.IsVisible("JournalAccept") || GameUi.MenuEntries(out _) != null
                       || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent];
        return this.reacted;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.talk != null)
        {
            var r = this.talk.Step(ctx);
            this.Status = this.talk.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.talk.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.talk.FailReason : null;
            this.talk = null;
            if (failed != null && !this.reacted)
            {
                this.Result = Outcome.CouldNotTalk;
                this.Detail = failed;
                return TaskResult.Done;
            }

            ctx.InOwnConversation = true; // ここからの会話の窓もこちらのもの（ForeignTalk が触らない）
            this.NextPhase("NPC の返事を見ています");
        }

        // 受注の窓が出た：受けられる様子（こちらの判定と食い違い）。受けずに閉じる
        if (GameUi.IsReady("JournalAccept", out var accept))
        {
            ctx.Log.Warn("クエスト", $"{this.blocked.Quest} の受注の窓が出ました（受けられない見込みでした：{this.blocked.Reason}）。受けずに閉じます");
            accept->Close(true);
            this.Result = Outcome.Offered;
            return TaskResult.Done;
        }

        // 会話は進める
        if (GameUi.IsReady("Talk", out _))
        {
            this.freeSince = null;
            if (DateTime.UtcNow - this.lastTalk >= TimeSpan.FromMilliseconds(300))
            {
                this.lastTalk = DateTime.UtcNow;
                GameUi.AdvanceTalk();
            }

            this.Status = "NPC の会話を進めています";
            return this.WatchTimedOut();
        }

        // 選択肢は何も選ばずに閉じる（自分が話しかけた後に開いたものだけ。何を選ぶと受注になるか分からないので選ばない）
        if (GameUi.MenuEntries(out var menu) is { } entries)
        {
            this.freeSince = null;
            foreach (var name in new[] { "SelectString", "SelectIconString" })
            {
                if (ctx.Ownership.TryGetOwnedSince(name, this.claimedAt, out var own) && own == menu)
                {
                    this.menus.Add(string.Join(" / ", entries));
                    ctx.Log.Write("クエスト", $"NPC の選択肢（{string.Join(" / ", entries)}）は選ばずに閉じます");
                    GameUi.Fire(own, true, -1);
                    break;
                }
            }

            this.Status = "NPC の選択肢を閉じています";
            return this.WatchTimedOut();
        }

        if (!GameUi.PlayerFree())
        {
            this.freeSince = null;
            this.Status = "NPC の返事を待っています";
            return this.WatchTimedOut();
        }

        // 会話が終わり、動ける状態が続いたら「断られた」
        this.freeSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - this.freeSince.Value < SettleFree)
            return TaskResult.Running;
        this.Result = Outcome.Rejected;
        if (this.menus.Count > 0)
            this.Detail = $"選択肢（{string.Join(" ／ ", this.menus)}）は選ばずに閉じました";
        return TaskResult.Done;
    }

    private TaskResult WatchTimedOut()
    {
        if (!this.TimedOut(WatchLimit))
            return TaskResult.Running;

        // 会話が終わらない（動けない状態が続く）。止める点は同じなので、結果は「断られた」とし、様子を補足に残す
        this.Result = Outcome.Rejected;
        this.Detail = $"NPC の会話が {WatchLimit.TotalMinutes:0} 分たっても終わりませんでした（受注の窓は出ていません）";
        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.talk?.Cleanup(ctx);
        this.talk = null;
        ctx.InOwnConversation = false;
        ctx.Ownership.Clear();
    }

    /// <summary>止めるときの文（チャットと結果に出す）。</summary>
    public static string StopText(BlockedQuest b, Outcome? result, string? detail, IReadOnlyList<BlockedQuest> others)
    {
        var who = $"{Jobs.Name(b.Quest.ClassJobId)}のジョブクエ「{b.Quest.Name}」（Lv{b.Quest.Level}）";
        var why = b.BlockingQuest != 0 ? $"{(MainQuestGate.IsMainScenario(b.BlockingQuest) ? "メインクエスト" : "前提のクエスト")}「{Unlocks.QuestName(b.BlockingQuest)}」が未完了のため" : b.Reason;
        var head = result switch
        {
            Outcome.Rejected => $"{why}、{who}を受注の NPC に断られました。進められるジョブクエは済ませたので、ここで止めます",
            Outcome.Offered => $"{who}の受注の窓が出ました（{why}受けられない見込みでした）。受けずに閉じて止めます。判定の食い違いなので、記録を送ってください",
            Outcome.NoLocation => $"{why}、{who}は受けられません（受注の NPC の場所が分からないので、話しかけずに止めます）",
            _ => $"{why}、{who}は受けられません（受注の NPC に話しかけられませんでした：{detail}）。進められるジョブクエは済ませたので、ここで止めます",
        };
        if (result == Outcome.Rejected && detail != null)
            head += $"（{detail}）";
        var rest = others.Where(o => o.Quest.RowId != b.Quest.RowId).ToList();
        return rest.Count == 0 ? head : head + $"。ほかに止まっている職：{string.Join("、", rest.Select(o => $"{Jobs.Name(o.Quest.ClassJobId)} Lv{o.Quest.Level}"))}";
    }
}
