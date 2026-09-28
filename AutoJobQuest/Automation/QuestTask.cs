using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// ジョブクエ1本を Questionable で進める（受注・移動・会話・報告は Questionable に任せる）。
///
/// 前提：納品物（HQ 指定なら HQ、マテリア指定なら装着済み）がそろっていること。そろっていなければ始めない。
///
/// Questionable の性質（ソースで確認済み。QuestionableIpc の説明を参照）への対処:
///  ・「Craft」の手順で Artisan の既製リスト（クエスト番号のリスト）が動く。既製リストは「持っていれば飛ばす」が
///    OFF なので、手元に材料があれば追加で作る。→ 先に全部作って材料を使い切ってから進める。
///    始める前に「既製リストが手元の材料で作れてしまう品」を数えて記録に残す（利用者の手持ちが使われる恐れ）。
///  ・木工 Lv20 の「WaitForManualProgress」（マテリア装着待ち）は Questionable の中では終わらない。
///    ここに来たら、装着済みを確かめてから Questionable を止め、報告だけこちらで行う
///    （報告先の NPC＝Quest.TargetEnd、位置＝その手順の座標。会話と納品の入力は TextAdvance に任せる）。
/// </summary>
public sealed unsafe class QuestTask : AutoTask
{
    private readonly JobQuest quest;
    private bool started;
    private bool everStarted; // 一度でも Questionable に頼んだか（やり直し待ちの間も納品窓を扱えるように）
    private int restarts;
    private int notRunningFrames;

    // 報告を自前で行うとき
    private bool manualTurnIn;
    private MoveToTask? moving;
    private Vector3? turnInPos;
    private DateTime interactedAt = DateTime.MinValue;

    // 納品窓の扱い（窓ごとに状態を持つ。準備待ちなら次のフレームで続きから）
    private readonly RequestFiller filler = new();
    private readonly HashSet<uint> questItems;
    private DateTime claimedAt = DateTime.MinValue;
    private bool foreignRequestLogged;

    // 渡す操作を送った後の確かめ（求めた品が減ったか）
    private DateTime? submittedAt;
    private Dictionary<uint, int> countsBeforeSubmit = [];

    public QuestTask(JobQuest quest)
    {
        this.quest = quest;
        this.questItems = quest.Items.Select(i => i.ItemId).ToHashSet();
    }

    public override string Name => $"クエスト: {Jobs.Name(this.quest.ClassJobId)} {this.quest}";

    private bool IsComplete => QuestManager.IsQuestComplete(this.quest.RowId);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.IsComplete)
            return TaskResult.Done;

        // 納品物がそろっているか
        var inv = Inventory.Snapshot();
        foreach (var r in this.quest.Items)
        {
            var have = r.Hq ? inv.CountHq(r.ItemId) : inv.CountAll(r.ItemId);
            if (have < r.Count)
                return this.Fail($"納品物 {CraftPlanner.ItemName(r.ItemId)}{(r.Hq ? "(HQ)" : string.Empty)} が {have}/{r.Count} しかありません");

            if (r.Hq && inv.CountNq(r.ItemId) > 0)
                ctx.Log.Debug("クエスト", $"{CraftPlanner.ItemName(r.ItemId)} を NQ でも持っています（納品窓ではこちらが HQ を選んで入れます）");
        }

        if (this.quest.Materia is { } m)
        {
            var hq = this.quest.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;
            if (!Inventory.HasMelded(m.TargetItemId, hq, m.MateriaItemId))
                return this.Fail($"{CraftPlanner.ItemName(m.TargetItemId)} にマテリアが付いていません");
        }

        // 既製リストが手元の材料で作れてしまう品（記録だけ）
        var risky = PremadeRisk(ctx, this.quest, inv);
        if (risky.Count > 0)
            ctx.Log.Warn("クエスト", $"Questionable が Artisan の既製リストを動かしたとき、手持ちの材料で追加製作される可能性があります：{string.Join("、", risky)}");

        if (ctx.Questionable.IsRunning() == true)
            return this.Fail("Questionable がすでに動いています（利用者の操作を横取りしないため止めました）");

        // 始める前から開いている納品窓は、利用者か他の操作のもの。触らない。
        // 開いたままだと Questionable が進めないので、閉じてもらう
        if (GameUi.IsVisible("Request"))
            return this.Fail("クエストの納品窓が開いています（こちらが始めたクエストのものではないので触りません）。閉じてからやり直してください");

        // ここから開いた納品窓を「自分のクエストの窓」の候補として記録する（要求品の照合と合わせて判断する）
        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        this.claimedAt = DateTime.UtcNow;
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.IsComplete)
        {
            ctx.Log.Write("クエスト", $"{this.quest} を完了しました");
            return TaskResult.Done;
        }

        // 納品窓が開いたら、条件（HQ・マテリア）に合う品をこちらで自動で入れて渡す（確認は出さない）。
        // TextAdvance は一覧の先頭を入れるので、NQ と HQ を両方持っていると NQ が入る恐れがあった
        if (this.HandleRequest(ctx) is { } requestFailure)
            return this.Fail(requestFailure);

        if (this.Elapsed > TimeSpan.FromMinutes(30))
            return this.Fail("30分たってもクエストが完了しません");

        if (this.manualTurnIn)
            return this.ManualTurnIn(ctx);

        if (!this.started)
        {
            if (!GameUi.PlayerFree())
                return TaskResult.Running;

            if (!ctx.Questionable.StartSingleQuest(this.quest.RowId))
                return this.Fail("Questionable がこのクエストを始められませんでした（経路データが無い・受注条件を満たしていない等）");

            this.started = true;
            this.everStarted = true;
            this.NextPhase("Questionable が進めています");
            return TaskResult.Running;
        }

        // マテリア装着待ちの手順に来たら、報告だけこちらで行う
        var stepData = ctx.Questionable.GetCurrentStepData();
        if (stepData != null && stepData.QuestId == QuestionableIpc.ToQuestId(this.quest.RowId))
        {
            this.Status = $"Questionable: 手順 {stepData.Sequence}-{stepData.Step} {stepData.InteractionType}";
            if (stepData.InteractionType == "WaitForManualProgress")
            {
                ctx.Log.Write("クエスト", "Questionable が手動の操作（マテリア装着）を待っています。装着済みなので、報告はこちらで行います");
                ctx.Questionable.Stop(Plugin.InternalNameConst);
                this.turnInPos = stepData.Position;
                this.manualTurnIn = true;
                this.NextPhase("報告に向かいます");
                return TaskResult.Running;
            }
        }

        // Questionable の IsRunning は「単体クエストの進行中（SingleQuestA/B）なら true」で、止まるか終わると false になる
        // （QuestionableIpc.cs:142）。クエストの完了は上で先に見ているので、false が続けば止まったとみなす。
        // 1フレームのずれを避けるため、続けて3回 false を見てから判断する（時間ではなく状態で判断する）
        var running = ctx.Questionable.IsRunning();
        if (running == false)
        {
            if (++this.notRunningFrames >= 3)
            {
                if (this.restarts++ >= 3)
                    return this.Fail("Questionable が途中で止まりました（3回やり直しても進みません）");

                ctx.Log.Warn("クエスト", "Questionable が止まったので、もう一度始めます");
                this.started = false;
                this.notRunningFrames = 0;
            }
        }
        else
        {
            this.notRunningFrames = 0;
        }

        return TaskResult.Running;
    }

    /// <summary>
    /// 納品窓を扱う。扱うのは次の全部を満たす窓だけ：
    ///  ・こちらがクエストを始めた（Questionable に頼んだ・報告に向かった）後であること
    ///  ・窓がこのクエストの間に開いたこと（AddonOwnership の記録。始める前から開いていた窓は OnStart で止めている）
    ///  ・窓が求める品が、このクエストの納品物に含まれること（RequestFiller が確かめる）
    /// 失敗（条件に合う品が無い等）なら理由を返す。
    /// </summary>
    private string? HandleRequest(TaskContext ctx)
    {
        // 渡した後：求めた品が減ったかを確かめる（渡す操作を送ったことを「納品した」とはしない）
        if (this.submittedAt is { } at)
        {
            var inv = Inventory.Snapshot();
            var dropped = this.countsBeforeSubmit.Where(kv => inv.CountAll(kv.Key) < kv.Value).ToList();
            if (dropped.Count > 0)
            {
                this.submittedAt = null;
                ctx.Log.Write("納品", $"納品を確かめました（{string.Join("、", dropped.Select(kv => $"{CraftPlanner.ItemName(kv.Key)} {kv.Value}→{inv.CountAll(kv.Key)}"))}）");
            }
            else if (DateTime.UtcNow - at > TimeSpan.FromSeconds(15))
            {
                // 二度は送らない（二重に渡さないため）。クエストが進まなければ、全体の上限で止まる
                this.submittedAt = null;
                ctx.Log.Warn("納品", "渡す操作を送ってから15秒たっても、納品物の所持数が減っていません（渡せていない可能性。同じ窓にはもう一度は送りません）");
            }
        }

        if (!GameUi.IsReady("Request", out var request))
        {
            // 窓が閉じた。次に開く窓は新しい窓として扱う
            this.filler.Reset();
            this.foreignRequestLogged = false;
            return null;
        }

        var openedAt = DateTime.MinValue;
        var ours = (this.everStarted || this.manualTurnIn)
                   && ctx.Ownership.TryGetOwnedSince("Request", this.claimedAt, out var own, out openedAt) && own == request;
        if (!ours)
        {
            if (!this.foreignRequestLogged)
            {
                this.foreignRequestLogged = true;
                ctx.Log.Warn("納品", "納品窓が開いていますが、このクエストを始めた後に開いたものと確かめられないので触りません");
            }

            return null;
        }

        var result = this.filler.Tick(GameRequestWindow.Instance, (nint)request, openedAt, this.questItems, out var detail);
        switch (result)
        {
            case RequestFiller.Outcome.Submitted:
                this.submittedAt = DateTime.UtcNow;
                this.countsBeforeSubmit = new Dictionary<uint, int>(this.filler.CountsBeforeSubmit);
                ctx.Log.Write("納品", detail);
                break;
            case RequestFiller.Outcome.NotOurs:
                ctx.Log.Warn("納品", detail);
                break;
            case RequestFiller.Outcome.Failed:
                return detail;
            case RequestFiller.Outcome.Busy:
                this.Status = "納品窓で別の操作が選択中なので待っています";
                break;
        }

        return null;
    }

    /// <summary>報告だけを自前で行う（NPC の前まで移動して話しかける。会話と納品は TextAdvance）。</summary>
    private TaskResult ManualTurnIn(TaskContext ctx)
    {
        var questRow = Svc.Data.GetExcelSheet<Quest>().GetRow(this.quest.RowId);
        var npcId = questRow.TargetEnd.RowId;

        if (this.turnInPos == null)
        {
            // 手順の座標が無ければ、TODO の最後の位置（Level）を使う
            var levels = Svc.Data.GetExcelSheet<Level>();
            foreach (var todo in questRow.TodoParams.Reverse())
            {
                var lv = todo.ToDoLocation.FirstOrDefault(l => l.RowId != 0);
                if (lv.RowId != 0 && levels.TryGetRow(lv.RowId, out var row) && row.Territory.RowId == Me.Territory)
                {
                    this.turnInPos = new Vector3(row.X, row.Y, row.Z);
                    break;
                }
            }

            if (this.turnInPos == null)
                return this.Fail("報告先の位置が分かりません");
        }

        if (this.moving == null && Vector3.Distance(Me.Position, this.turnInPos.Value) > 4f)
            this.moving = new MoveToTask(this.turnInPos.Value, 3f, "報告先");

        if (this.moving != null)
        {
            var r = this.moving.Step(ctx);
            this.Status = this.moving.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.moving.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.moving.FailReason : null;
            this.moving = null;
            if (failed != null)
                return this.Fail(failed);
        }

        if (!ctx.TextAdvance.TakeControlForTurnIn() && ctx.TextAdvance.IsInExternalControl() != true)
            ctx.Log.Warn("クエスト", "TextAdvance の外部制御を取れませんでした（会話と納品の入力が進まない可能性）");

        if (!GameUi.PlayerFree())
        {
            this.Status = "会話・納品中";
            return TaskResult.Running;
        }

        if (DateTime.UtcNow - this.interactedAt < TimeSpan.FromSeconds(3))
            return TaskResult.Running;

        var npc = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && o.BaseId == npcId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
        if (npc == null)
        {
            if (this.PhaseElapsed > TimeSpan.FromSeconds(30))
                return this.Fail("報告先の NPC が見つかりません");
            this.Status = "報告先の NPC を探しています";
            return TaskResult.Running;
        }

        GameUi.Interact(npc);
        this.interactedAt = DateTime.UtcNow;
        this.Status = $"{npc.Name} に話しかけました";
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.moving?.Cleanup(ctx);
        this.moving = null;

        // 自分が始めた進行がまだ動いていれば止める
        if (this.started && !this.manualTurnIn && ctx.Questionable.IsRunning() == true
            && ctx.Questionable.GetCurrentQuestId() == QuestionableIpc.ToQuestId(this.quest.RowId))
            ctx.Questionable.Stop(Plugin.InternalNameConst);

        ctx.TextAdvance.ReleaseControl();
        ctx.Ownership.Clear();
    }

    /// <summary>
    /// Artisan の既製リスト（納品物とその中間素材）のうち、手元の材料だけで1回作れてしまうレシピの品名。
    /// 既製リストの中身そのものは読めないので、こちらのレシピ選択で近似する。
    /// </summary>
    private static List<string> PremadeRisk(TaskContext ctx, JobQuest q, Inventory inv)
    {
        var result = new List<string>();
        var planner = ctx.Data.Planner;
        if (planner == null)
            return result;

        var seen = new HashSet<uint>();
        var stack = new Stack<uint>(q.Items.Select(x => x.ItemId));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (!seen.Add(item))
                continue;
            var r = planner.Pick(item);
            if (r == null)
                continue;

            var ok = true;
            foreach (var (ing, amount) in CraftPlanner.Ingredients(r.Value))
            {
                if (inv.CountAll(ing) < amount)
                    ok = false;
                stack.Push(ing);
            }

            if (ok)
                result.Add(CraftPlanner.ItemName(item));
        }

        return result;
    }
}
