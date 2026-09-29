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
///
/// 【こちらで行う準備と引き継ぎ】
///  ・頼むまでの準備（受注前の着替え・受注できるかの確かめ・優先リストの差し替え・TextAdvance の外部制御）と、進行中の見張り
///    （別のクエストへ移った・優先リストが書き換わった）は <see cref="QuestionableStarter"/> で行う。
///  ・Questionable がその段の「材料の購入」「製作」の手順に入ったら（既製リストが動く段か、作る品を全部持っているのに材料を買い足す段）、
///    止めて、その段の残りの手順（NPC と話す・報告する）をこちらで行う（<see cref="QuestTakeOver"/>・<see cref="NpcStepTask"/>）。
///    段が進めば Questionable に戻す。上のマテリア装着待ちも、経路データが読めればこの中で扱う（読めないときだけ従来の自前の報告）。
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
    private int interactions;
    private bool textAdvanceWarned;

    // 手動の報告で話しかける回数の上限（会話が終わっても完了しないときに、延々と話しかけ続けないため）
    private const int MaxInteractions = 10;

    // 納品窓の扱い（窓ごとに状態を持つ。準備待ちなら次のフレームで続きから）
    private readonly RequestFiller filler = new();
    private readonly HashSet<uint> questItems;
    private DateTime claimedAt = DateTime.MinValue;
    private bool foreignRequestLogged;
    private bool materiaNoteLogged;

    // TextAdvance に入力を任せた納品窓（アドレスと開いた時刻）。別の窓が開いたら、まず任せるのをやめてから判断する
    private (nint Addon, DateTime OpenedAt) delegatedWindow;

    // 始める前に Artisan のリストが動いていたか（止めた後、Questionable が動かしたリストが残っていないかを見分ける）
    private bool? artisanListAtStart;

    // 渡す操作を送った後の確かめ（求めた品が減ったか）
    private DateTime? submittedAt;
    private Dictionary<uint, int> countsBeforeSubmit = [];

    // 頼むまでの準備と進行中の見張り（着替え・受注できるか・優先リスト・TextAdvance・別のクエストへ移った）
    private readonly QuestionableStarter starter;

    // Questionable の経路データ（全手順・製作の手順）。読めなければ null（引き継ぎはせず、従来どおり）
    private IReadOnlyList<QuestionableStep>? paths;
    private IReadOnlyList<QuestionableCraftStep>? pathCrafts;

    // こちらで行う手順（Questionable から引き継いだ段の残り）と、いま行っている手順・引き継いだ段
    private Queue<QuestionableStep>? own;
    private NpcStepTask? ownTask;
    private int ownFromSeq;

    public QuestTask(JobQuest quest)
    {
        this.quest = quest;
        this.questItems = quest.Items.Select(i => i.ItemId).ToHashSet();
        this.starter = new QuestionableStarter(quest.RowId, quest.ToString(), takeTextAdvance: true);
    }

    public override string Name => $"クエスト: {Jobs.Name(this.quest.ClassJobId)} {this.quest}";

    private bool IsComplete => QuestManager.IsQuestComplete(this.quest.RowId);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.IsComplete)
            return TaskResult.Done;

        // 納品物がそろっているか。途中の段で渡すクエストは、今の段によってはもう要らない・手持ちで進める
        // （渡した後に止めて再開したとき、無い品を理由に止まらないように）
        var inv = Inventory.Snapshot();
        var seq = QuestManager.GetQuestSequence(this.quest.RowId);
        var stage = QuestItemStage.Decide(seq, this.quest.FirstItemSeq, this.quest.LastItemSeq);
        if (stage != QuestItemStage.Stage.All)
        {
            ctx.Log.Write("クエスト", stage == QuestItemStage.Stage.None
                ? $"{this.quest} は納品物を渡し終えています（今の段 {seq}・品を使う最後の段 {this.quest.LastItemSeq}）。残りの手順を進めます"
                : $"{this.quest} は納品物を途中まで渡しています（今の段 {seq}）。手持ちの品で続けます（作り足しません）");
        }

        foreach (var r in stage == QuestItemStage.Stage.All ? this.quest.Items : [])
        {
            var have = r.Hq ? inv.CountHq(r.ItemId) : inv.CountAll(r.ItemId);
            if (have < r.Count)
                return this.Fail($"納品物 {CraftPlanner.ItemName(r.ItemId)}{(r.Hq ? "(HQ)" : string.Empty)} が {have}/{r.Count} しかありません");

            if (r.Hq && inv.CountNq(r.ItemId) > 0)
                ctx.Log.Debug("クエスト", $"{CraftPlanner.ItemName(r.ItemId)} を NQ でも持っています（納品窓ではこちらが HQ を選んで入れます）");
        }

        if (stage == QuestItemStage.Stage.All && this.quest.Materia is { } m)
        {
            var hq = this.quest.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;
            if (!Inventory.HasMelded(m.TargetItemId, hq, m.MateriaItemId))
                return this.Fail($"{CraftPlanner.ItemName(m.TargetItemId)} にマテリアが付いていません");
        }

        // 既製リストが手元の材料で作れてしまう品（記録だけ）
        var risky = PremadeRisk(ctx, this.quest, inv);
        if (risky.Count > 0)
            ctx.Log.Warn("クエスト", $"Questionable が Artisan の既製リストを動かしたとき、手持ちの材料で追加製作される可能性があります：{string.Join("、", risky)}");

        // 始める前から開いている納品窓は、利用者か他の操作のもの。触らない。
        // 開いたままだと Questionable が進めないので、閉じてもらう
        if (GameUi.IsVisible("Request"))
            return this.Fail("クエストの納品窓が開いています（こちらが始めたクエストのものではないので触りません）。閉じてからやり直してください");

        // ここから開いた納品窓を「自分のクエストの窓」の候補として記録する（要求品の照合と合わせて判断する）
        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        this.claimedAt = DateTime.UtcNow;

        this.artisanListAtStart = ctx.Artisan.IsListRunning();

        // 経路データ（引き継ぎの判断に使う。読めなければ引き継がない）
        var shortId = (ushort)(this.quest.RowId & 0xFFFF);
        this.paths = QuestionablePaths.Steps(shortId);
        this.pathCrafts = QuestionablePaths.CraftSteps(shortId);
        if (this.paths == null)
            ctx.Log.Warn("クエスト", "Questionable の経路データを読めないので、材料の購入・既製リストの手順を引き継げません（Questionable にそのまま任せます）");

        // YesAlready の納品窓の自動入力は一覧の先頭を入れる（HQ 指定でも NQ が入りうる）。こちらが入れるので、
        // クエストの間は止めてもらう（止めるのは自分の停止要求を入れるだけで、設定は変えない）
        ctx.YesAlready.Suppress();
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

        if (this.own != null)
            return this.RunOwnSteps(ctx);

        if (this.manualTurnIn)
            return this.ManualTurnIn(ctx);

        if (!this.started)
        {
            // 頼むまでの準備（受注前の着替え・Questionable が止まるのを待つ・受注できるか・優先リスト・TextAdvance の外部制御）。
            // 納品窓の入力はこちら（RequestFiller）が行う。TextAdvance が同じ窓に一覧の先頭を入れないよう、クエストの間は
            // こちらが TextAdvance の外部制御を取り、納品窓の入力だけ切る
            var r = this.starter.Start(ctx, out var startFail, out var startStatus);
            if (startStatus.Length > 0)
                this.Status = startStatus;
            if (r == TaskResult.Failed)
                return this.Fail(startFail!);
            if (r == TaskResult.Running)
                return TaskResult.Running;

            this.started = true;
            this.everStarted = true;
            this.NextPhase("Questionable が進めています");
            return TaskResult.Running;
        }

        // 進行中の見張り（別のクエストへ移った・優先リストが書き換わった・TextAdvance の外部制御）
        if (this.starter.Watch(ctx) is { } watchFail)
            return this.Fail(watchFail);

        var stepData = ctx.Questionable.GetCurrentStepData();
        if (stepData != null && stepData.QuestId == QuestionableIpc.ToQuestId(this.quest.RowId))
        {
            this.Status = $"Questionable: 手順 {stepData.Sequence}-{stepData.Step} {stepData.InteractionType}";

            // 材料の購入・製作の手順に入ったら、止めて残りをこちらで行う（既製リストが動く・材料を買い足すのを防ぐ）
            if (this.paths != null && this.pathCrafts != null)
            {
                var inv = Inventory.Snapshot();
                var rest = QuestTakeOver.Decide(this.paths, stepData.Sequence, stepData.Step, stepData.InteractionType, this.pathCrafts,
                    (item, count, hq) => (hq ? inv.CountHq(item) : inv.CountAll(item)) >= count);
                if (rest != null)
                {
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.own = new Queue<QuestionableStep>(rest);
                    this.ownFromSeq = stepData.Sequence;
                    ctx.Log.Write("クエスト", $"Questionable が段 {stepData.Sequence} の{(stepData.InteractionType == "Craft" ? "製作" : "材料の購入")}に入ったので止め、"
                                         + $"この段の残り（{string.Join("→", rest.Select(x => $"{x.Type}（{NpcStepTask.NpcName(x.DataId)}）"))}）をこちらで行います"
                                         + "（納品物は用意済み。既製リストの追加製作・材料の買い足しをさせないため）");
                    this.NextPhase("この段の残りをこちらで行います");
                    return TaskResult.Running;
                }
            }

            // マテリア装着待ちの手順に来たら、報告だけこちらで行う（経路データが読めないときの従来の扱い）
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
                    return this.Fail("Questionable が途中で止まりました（3回やり直しても進みません）。Questionable の画面と /xllog の記録で、止まった理由を確かめてください");

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
            // 窓が閉じた。次に開く窓は新しい窓として扱う。TextAdvance に任せていた納品窓の入力は、会話が終わってから（動ける状態に
            // 戻ってから）こちらへ戻す。導入版の TextAdvance は埋めた欄を覚えていて、窓が閉じたのを自分の処理の中で見たときだけ忘れる。
            // 入力を任せるのを窓が閉じた最初のフレームでやめると、TextAdvance がそれを見る前に止まり、次に任せた納品窓を埋めない
            // （導入版 3.3.0.1 の ExecRequestFill）
            this.filler.Reset();
            this.foreignRequestLogged = false;
            if (ctx.TextAdvance.RequestAllowed && GameUi.PlayerFree())
                ctx.TextAdvance.AllowRequestFill(false);
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

        // TextAdvance に任せたままの間に、別の納品窓が開いた（同じ会話の続きの窓）：こちらの納品物の窓かもしれないので、
        // TextAdvance が一覧の先頭を入れる前に、まず任せるのをやめる（こちらの品でなければ下でまた任せる）
        if (ctx.TextAdvance.RequestAllowed && this.delegatedWindow != ((nint)request, openedAt))
            ctx.TextAdvance.AllowRequestFill(false);

        var result = this.filler.Tick(GameRequestWindow.Instance, (nint)request, openedAt, this.questItems, out var detail);
        if (this.filler.MateriaNote is { } note && !this.materiaNoteLogged)
        {
            this.materiaNoteLogged = true;
            ctx.Log.Write("納品", note);
        }

        switch (result)
        {
            case RequestFiller.Outcome.Submitted:
                this.submittedAt = DateTime.UtcNow;
                this.countsBeforeSubmit = new Dictionary<uint, int>(this.filler.CountsBeforeSubmit);
                ctx.Log.Write("納品", detail);
                break;
            case RequestFiller.Outcome.NotOurs:
                // このクエストの間に開いた窓だが、こちらの納品物ではない品（クエスト専用アイテム等）を求めている。
                // こちらは入れないので、この窓の間だけ TextAdvance に入力を任せる（任せないと誰も入れずに詰まる）
                ctx.Log.Warn("納品", detail);
                if (ctx.TextAdvance.OwnsControl && ctx.TextAdvance.AllowRequestFill(true))
                {
                    this.delegatedWindow = ((nint)request, openedAt);
                    ctx.Log.Write("納品", "この納品窓はこちらで扱わない品なので、会話が終わるまで TextAdvance に入力を任せます");
                }
                break;
            case RequestFiller.Outcome.Failed:
                return detail;
            case RequestFiller.Outcome.Busy:
                this.Status = "納品窓で別の操作が選択中なので待っています";
                break;
        }

        return null;
    }

    /// <summary>
    /// Questionable から引き継いだ段の残りの手順を、順にこちらで行う。移動だけの手順（WalkTo・None）は次の手順の移動に含める。
    /// マテリア装着待ちは、装着済みを確かめて進める。全部終えたら、クエストが終わっていれば完了、段が進んでいれば Questionable に戻す。
    /// </summary>
    private TaskResult RunOwnSteps(TaskContext ctx)
    {
        if (this.ownTask != null)
        {
            var r = this.ownTask.Step(ctx);
            this.Status = this.ownTask.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.ownTask.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.ownTask.FailReason : null;
            this.ownTask = null;
            if (failed != null)
                return this.Fail(failed);
        }

        while (this.own!.Count > 0)
        {
            var step = this.own.Peek();
            switch (step.Type)
            {
                case "WalkTo":
                case "None":
                    this.own.Dequeue();
                    continue;
                case "WaitForManualProgress":
                    if (this.quest.Materia is { } m)
                    {
                        var hq = this.quest.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;
                        if (!Inventory.HasMelded(m.TargetItemId, hq, m.MateriaItemId))
                            return this.Fail($"{CraftPlanner.ItemName(m.TargetItemId)} にマテリアが付いていません（マテリア装着待ちの手順）");
                    }

                    this.own.Dequeue();
                    continue;
                case "Interact":
                case "CompleteQuest":
                    this.own.Dequeue();
                    this.ownTask = new NpcStepTask(step, this.quest.RowId);
                    this.NextPhase($"{NpcStepTask.NpcName(step.DataId)} との手順をこちらで行います");
                    return TaskResult.Running;
                default:
                    return this.Fail($"手順 {step.Sequence}-{step.Index} の種類「{step.Type}」はこちらで行えません");
            }
        }

        // 全部終えた
        this.own = null;
        if (this.IsComplete)
            return TaskResult.Running; // 次のフレームの先頭で完了として終える

        var (seq, _) = NpcStepTask.QuestState(this.quest.RowId);
        if (seq != this.ownFromSeq)
        {
            ctx.Log.Write("クエスト", $"段 {this.ownFromSeq} をこちらで終えました（今の段 {seq}）。続きを Questionable に任せます");
            this.started = false;
            this.notRunningFrames = 0;
            return TaskResult.Running;
        }

        return this.Fail($"段 {this.ownFromSeq} の残りの手順をこちらで行いましたが、段が進みません（相手との会話が進まなかった可能性）");
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

        // 外部制御を取れなかったら1回だけ知らせる（毎フレーム呼ばれるので繰り返さない）。
        // ほかのプラグインが制御中なら、その設定で納品窓が入力される恐れもある（こちらの設定は納品窓の入力を任せない）
        if (!ctx.TextAdvance.TakeControlForTurnIn() && !this.textAdvanceWarned)
        {
            this.textAdvanceWarned = true;
            ctx.Log.Warn("クエスト", ctx.TextAdvance.IsInExternalControl() == true
                ? "TextAdvance はほかのプラグインが外部制御しています（こちらの設定にならないので、会話送りや納品窓の入力がその設定で動く可能性）"
                : "TextAdvance の外部制御を取れませんでした（会話と納品の入力が進まない可能性）");
        }

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

        if (this.interactions >= MaxInteractions)
            return this.Fail($"報告先の {npc.Name} に {MaxInteractions} 回話しかけても、クエストが完了しません");

        this.interactedAt = DateTime.UtcNow;
        this.interactions++;
        GameUi.Interact(npc);
        this.Status = $"{npc.Name} に話しかけました（{this.interactions} 回目）";
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.moving?.Cleanup(ctx);
        this.moving = null;
        this.ownTask?.Cleanup(ctx);
        this.ownTask = null;

        // 自分が始めた進行（または別のクエストへ移ったのを見た進行）がまだ動いていれば止め、Questionable の優先リストを元に戻す
        this.starter.Cleanup(ctx);

        ctx.TextAdvance.ReleaseControl();
        ctx.YesAlready.Release();

        // Questionable の「Craft」の手順は Artisan の既製リストを動かす。Questionable を止めても、そのリストは止まらない
        // 止めるのは Artisan の側の操作になるので、こちらからは止めずに知らせる
        if (this.everStarted && this.artisanListAtStart == false && ctx.Artisan.IsListRunning() == true)
        {
            const string msg = "Questionable が動かした Artisan のリストが、まだ動いています（手持ちの材料で作り続けることがあります）。要らなければ Artisan の画面で止めてください";
            ctx.Log.Warn("クエスト", msg);
            Svc.Chat.Print($"[AutoJobQuest] {msg}");
        }

        // こちらが欄を選ぶ・入れる操作をした納品窓が、渡さないまま残っていれば閉じる
        // （残すと、再開したとき「始める前から開いている納品窓」として止まるため。閉じれば品は渡らない）。
        // 閉じるのは、こちらが触った窓だけ（以前はクエストの間に開いた窓なら、触っていなくても閉じていた）
        if (this.claimedAt != DateTime.MinValue && this.filler.Touched && !this.filler.HasSubmitted
            && ctx.Ownership.TryGetOwnedSince("Request", this.claimedAt, out var request, out var openedAt)
            && (nint)request == this.filler.Addon && openedAt == this.filler.AddonOpenedAt)
        {
            DebugLog.Current?.Line("操作", "止めたので、こちらが入力していた納品窓を閉じます");
            request->Close(true);
        }

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
