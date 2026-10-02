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

    // Questionable の見張り：次に動かし直してよい時刻・最後に見た手順（止まったときの記録用）
    private DateTime restartNotBefore = DateTime.MinValue;
    private string lastStepText = "不明";

    /// <summary>こちらが自分で作業する間の状態の表示に付ける言葉（Questionable が止まっている理由を見えるようにする）。</summary>
    public const string OwnWorkNote = "（こちらで行う間は Questionable を止めています。終われば動かし直します）";

    // 報告を自前で行うとき
    private bool manualTurnIn;
    private MoveToTask? moving;
    private Vector3? turnInPos;
    private DateTime interactedAt = DateTime.MinValue;
    private int interactions;
    private int interactAttempts;
    private bool textAdvanceWarned;
    private string? selectedMenu;
    private DateTime manualInteractionAt = DateTime.MinValue;

    // 手動の報告で話しかける回数の上限（会話が終わっても完了しないときに、延々と話しかけ続けないため）
    private const int MaxInteractions = 10;

    // 納品窓の扱い（窓ごとに状態を持つ。準備待ちなら次のフレームで続きから）
    private readonly RequestFiller filler = new();
    private readonly HashSet<uint> questItems;
    private DateTime claimedAt = DateTime.MinValue;
    private bool foreignRequestLogged;
    private bool materiaNoteLogged;

    // 始める前に Artisan のリストが動いていたか（止めた後、Questionable が動かしたリストが残っていないかを見分ける）
    private bool? artisanListAtStart;

    // 渡す操作を送った後の確かめ（求めた品が減ったか）
    private DateTime? submittedAt;

    // 最後に渡した段（渡した直後に、受注後の製作の段の引き取りを誤って行わないため）
    private int submittedInSequence = -1;
    private Dictionary<uint, int> countsBeforeSubmit = [];

    // 渡す前のクエストの段（見せるだけの納品は品が減らないので、クエストが進んだかでも確かめる）
    private byte seqBeforeSubmit;

    // 頼むまでの準備と進行中の見張り（着替え・受注できるか・優先リスト・TextAdvance・別のクエストへ移った）
    private readonly QuestionableStarter starter;

    // Questionable の経路データ（全手順・製作の手順）。読めなければ null（引き継ぎはせず、従来どおり）
    private IReadOnlyList<QuestionableStep>? paths;
    private IReadOnlyList<QuestionableCraftStep>? pathCrafts;

    // 受注後にクエストの材料から作る品を、こちらで HQ まで作っている（Lv61〜70 の製作職：QuestCraftTask）
    private QuestCraftTask? questCraft;

    // Questionable が手で行う手順（Instruction：漁師 Lv68 の刺突漁）で待っている間（その時間は上限に数えない）
    private DateTime? manualWaitSince;
    private TimeSpan manualWaitTotal;
    private bool manualWaitNotified;

    // 手で行う手順の刺突漁を、こちらが自動で行っている（漁師 Lv68 の大方士）。その時間も上限に数えない
    private SpearfishTask? spearfish;
    private bool spearfishTried;
    private DateTime? spearfishSince;
    private TimeSpan spearfishTotal;

    // 受注後の品をこちらで作っている時間（HQ の作り直しは回数の上限で止めるので、クエストの時間の上限には数えない）
    private DateTime? questCraftSince;
    private TimeSpan questCraftTotal;
    private int instructionSeq;
    private int instructionStep;

    /// <summary>手で行う手順を待つ上限。</summary>
    private static readonly TimeSpan ManualWaitLimit = TimeSpan.FromMinutes(60);

    // こちらで行う手順（Questionable から引き継いだ段の残り）と、いま行っている手順・引き継いだ段
    private Queue<QuestionableStep>? own;
    private AutoTask? ownTask;

    // 受注前の段の新しいクラス向けの準備を確かめたか
    private bool gearsetSetupChecked;
    private int ownFromSeq;

    // Questionable が推奨装備に着替えた（園芸師のジョブクエ17本）。完了したら、その職のギアセットに着直す
    private bool gearChangedByQuestionable;
    private EquipJobTask? reequip;

    // Lv1 の受注をこちらで行う前の準備（着替え → 受注前の確かめ）
    private QuestionableStep? lv1Accept;
    private AutoTask? lv1Equip;

    /// <summary>このクエスト（Quest シートの行）。</summary>
    public uint QuestRowId => this.quest.RowId;

    /// <summary>後回しにしてよいか（ほかに進められる職のジョブクエがあるとき。JobQuestFlow が決める）。</summary>
    public bool CanDefer { get; init; }

    /// <summary>
    /// 採集の手順の品が時限の採集点でしか採れず、今は出ていないので、このクエストを後回しにした：次に採れるようになる時刻（UTC）。後回しにしていなければ null。
    /// 不具合の例：採掘師と園芸師を選んで始めたら、採掘師 Lv70 の硬拳石（ET 8〜10時・20〜22時だけ出る採集点）の手順で、
    /// Questionable が出ていない採集点の前で黙って待ち（画面は「手順 3-0 Gather」のまま）、止まったように見えた。待つ間に園芸師を進められた
    /// </summary>
    public DateTime? DeferredUntil { get; private set; }

    /// <summary>時刻を待つのがこれより長ければ、ほかの職のジョブクエを先に進める（短ければ、ここで待つ）。</summary>
    public static readonly TimeSpan DeferOver = TimeSpan.FromMinutes(5);

    // 時限の採集点を待っている間の、状態の欄に足す説明・同じことを2度書かない控え
    private string? timedWaitNote;
    private string lastTimedNote = string.Empty;

    public QuestTask(JobQuest quest)
    {
        this.quest = quest;
        this.questItems = quest.Items.Select(i => i.ItemId).ToHashSet();
        // クエストのスクリプトが参照する専用品だけを追加する。窓の要求を無条件に信用しない。
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(quest.RowId, out var row))
            foreach (var p in row.QuestParams)
                if (p.ScriptInstruction.ExtractText().Contains("ITEM", StringComparison.Ordinal)
                    && p.ScriptArg >= 2_000_000 && Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().TryGetRow(p.ScriptArg, out _))
                    this.questItems.Add(p.ScriptArg);
        this.starter = new QuestionableStarter(quest.RowId, quest.ToString(), takeTextAdvance: true);
    }

    public override string Name => $"クエスト: {Jobs.Name(this.quest.ClassJobId)} {this.quest}";

    private bool IsComplete => GameMemory.IsQuestComplete(this.quest.RowId);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.IsComplete)
            return TaskResult.Done;

        // 納品物がそろっているか。途中の段で渡すクエストは、今の段によってはもう要らない・手持ちで進める
        // （渡した後に止めて再開したとき、無い品を理由に止まらないように）
        var inv = Inventory.Snapshot();
        var seq = GameMemory.QuestSequence(this.quest.RowId);
        var needs = QuestItemNeeds.Decide(this.quest.Items, seq, this.quest.FirstItemSeq, this.quest.LastItemSeq, this.quest.Handovers,
            todo => QuestTodo.IsChecked(this.quest.RowId, todo), (item, hq) => hq ? inv.CountHq(item) : inv.CountAll(item));
        var stage = needs.Stage;
        if (needs.Detail.Length > 0)
            ctx.Log.Write("クエスト", $"{this.quest} の今の段 {seq} の相手：{needs.Detail}（変数 {NpcStepTask.Hex(NpcStepTask.QuestState(this.quest.RowId).Vars)}）");
        if (stage != QuestItemStage.Stage.All)
        {
            ctx.Log.Write("クエスト", stage == QuestItemStage.Stage.None
                ? $"{this.quest} は納品物を渡し終えています（今の段 {seq}・品を使う最後の段 {this.quest.LastItemSeq}）。残りの手順を進めます"
                : $"{this.quest} は納品物を途中まで渡しています（今の段 {seq}）。まだ要る品：{(needs.Needed.Count == 0 ? "なし" : string.Join("、", needs.Needed.Select(n => $"{CraftPlanner.ItemName(n.ItemId)}×{n.Count}")))}");
        }

        // 受注した後にしか手に入らない品（Lv61〜70 の取引できない納品物）は、始める時点では確かめない（受注後に Questionable が作る・採る）。
        // 採集職の取引できる品（Lv1〜60）は計画で先に集めているので、製作職と同じく確かめる（以前は採集職を丸ごと飛ばし、
        // 足りないまま納品窓や釣りで止まった）
        // 採集職の納品物のうち、Questionable が経路の採集・釣りの手順で自分で採る品も確かめない（先に集めきれなかった品を任せる）
        var questGathers = Jobs.IsGatherer(this.quest.ClassJobId) ? QuestionablePaths.GatheredItems(this.quest.ShortId) : new HashSet<uint>();
        foreach (var r in needs.Needed.Where(n => !this.quest.AfterAcceptItems.Contains(n.ItemId) && !questGathers.Contains(n.ItemId)))
        {
            var have = r.Hq ? inv.CountHq(r.ItemId) : inv.CountAll(r.ItemId);
            if (have < r.Count)
                return this.Fail($"納品物 {CraftPlanner.ItemName(r.ItemId)}{(r.Hq ? "(HQ)" : string.Empty)} が {have}/{r.Count} しかありません");

            if (r.Hq && inv.CountNq(r.ItemId) > 0)
                ctx.Log.Debug("クエスト", $"{CraftPlanner.ItemName(r.ItemId)} を NQ でも持っています（納品窓ではこちらが HQ を選んで入れます）");
        }

        if (this.quest.Materia is { } m && needs.Needed.Any(n => n.ItemId == m.TargetItemId))
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

        // Questionable の釣りの手順は AutoHook が無いと作業を作らず、そこで止まる（魚を持っていても手順の種類だけで止まる）。
        // 理由の出ない失敗にしないよう、今の段より後に釣りの手順が残るなら始める前に止める
        if (!ctx.AutoHook.IsLoaded && this.paths?.Any(s => s.Type == "Fish" && s.Sequence >= seq) == true)
            return this.Fail($"{this.quest} には Questionable の釣りの手順があり、AutoHook が要ります。AutoHook を読み込んでから再開してください（続きから進みます）");

        // YesAlready の納品窓の自動入力は一覧の先頭を入れる（HQ 指定でも NQ が入りうる）。こちらが入れるので、
        // クエストの間は止めてもらう（止めるのは自分の停止要求を入れるだけで、設定は変えない）
        ctx.YesAlready.Suppress();
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.IsComplete)
        {
            // 完了の直後の後片付け：NPC が続けて出す窓（別のクエストの受注の窓・普段のメニュー・会話）を閉じてから終える（WindDownAfterComplete）
            if (!this.windDownDone)
            {
                if (this.WindDownAfterComplete(ctx) == TaskResult.Running)
                    return TaskResult.Running;
                this.windDownDone = true;
            }

            // 推奨装備に着替えていたら、完了の後にその職のギアセットに着直す（途中で着直すと、Questionable が段の頭からやり直して、また着替えるため）
            if (this.gearChangedByQuestionable)
            {
                this.reequip ??= new EquipJobTask(this.quest.ClassJobId, reequip: true);
                var r = this.reequip.Step(ctx);
                this.Status = this.reequip.Status;
                if (r == TaskResult.Running)
                    return TaskResult.Running;
                this.reequip.Cleanup(ctx);
                if (r == TaskResult.Failed)
                    ctx.Log.Warn("クエスト", $"{Jobs.Name(this.quest.ClassJobId)} のギアセットに着直せませんでした（{this.reequip.FailReason}）。ギアセットで着替え直してください");
                else
                    ctx.Log.Write("クエスト", $"推奨装備から {Jobs.Name(this.quest.ClassJobId)} のギアセットに着直しました");
                this.gearChangedByQuestionable = false;
                this.reequip = null;
            }

            ctx.Log.Write("クエスト", $"{this.quest} を完了しました");
            return TaskResult.Done;
        }

        // Questionable の購入の手順で出た購入の確認に、こちらで OK を押す（QuestPurchaseConfirm）
        this.HandlePurchaseConfirm(ctx);

        // Questionable が止まっている間（こちらが代わりに手順を行う間）に出た HQ 品を渡す確認・手順の会話の選択肢に答える
        this.HandleQuestDialogs(ctx);

        // Questionable の釣りの手順の前に、手順の指定の餌を付けておく（FishBaitPrep）
        this.HandleFishBait(ctx);

        // Questionable が飛んだまま話しかけてゲームに断られたら、こちらで着地させる
        this.HandleFlyingRefusal(ctx);

        // Questionable の釣りの位置で投げられなければ、向きを変えて試す
        this.HandleCastFacing(ctx);

        // Questionable が買えない・買わなかった品を、こちらで NPC から買う（QuestOwnPurchase）
        if (this.ownBuyOrders != null)
        {
            this.KeepFishingStopped();
            return this.RunOwnBuy(ctx);
        }

        // 天気の限られた魚で、いまの天気で釣れないなら、Questionable を止めてマーケットボードで買う（WeatherFishBuy）
        if (this.weatherBuy != null)
        {
            this.KeepFishingStopped();
            return this.RunWeatherBuy(ctx);
        }

        if (this.relocate != null)
        {
            this.KeepFishingStopped();
            return this.RunRelocate(ctx);
        }

        // 手順を終えたまま段が変わらない Questionable を止めた：乗っていれば降りてから頼み直す（StepDoneStall）
        if (this.stallDismountSince != null)
            return this.RunStallRecovery(ctx);

        // 釣りの手順の魚がもうそろっているのに、Questionable がその手前にいるなら、残り（報告）をこちらで行う（QuestTakeOver.AfterFishReady）
        if (this.HandleFishReady(ctx))
            return TaskResult.Running;
        if (this.HandleWeatherFish(ctx))
            return TaskResult.Running;

        // 採集の手順の品が時限の採集点でしか採れず、今は出ていない：ほかの職のジョブクエがあれば、このクエストを後回しにする（後始末で Questionable を止める）
        if (this.HandleTimedGather(ctx))
            return TaskResult.Done;
        if (this.HandleMissingBait(ctx, out var baitFail))
            return baitFail != null ? this.Fail(baitFail) : TaskResult.Running;

        // 納品窓が開いたら、条件（HQ・マテリア）に合う品をこちらで自動で入れて渡す（確認は出さない）。
        // TextAdvance は一覧の先頭を入れるので、NQ と HQ を両方持っていると NQ が入る恐れがあった
        if (this.HandleRequest(ctx) is { } requestFailure)
            return this.Fail(requestFailure);

        // 上限：採集・釣りの手順があるクエストは長くする（時間限定の採集点で最長 ET10時間＝約29分待つ）。
        // 手で行う手順を待った時間は数えない
        var waited = this.manualWaitTotal + (this.manualWaitSince is { } ws ? DateTime.UtcNow - ws : TimeSpan.Zero);
        var spearWaited = this.spearfishTotal + (this.spearfishSince is { } ss ? DateTime.UtcNow - ss : TimeSpan.Zero);
        var craftWaited = this.questCraftTotal + (this.questCraftSince is { } cs ? DateTime.UtcNow - cs : TimeSpan.Zero);
        var limit = TimeSpan.FromMinutes(this.paths?.Any(s => s.Type is "Gather" or "Fish") == true ? 90 : 30);
        if (this.Elapsed - waited - spearWaited - craftWaited - this.weatherBuyTotal - this.ownBuyTotal > limit)
            return this.Fail($"{limit.TotalMinutes:0}分たってもクエストが完了しません"
                             + (this.restarts > 0 ? $"（その間に Questionable を {this.restarts} 回動かし直しました。最後の手順 {this.lastStepText}）" : string.Empty));
        if (waited > ManualWaitLimit)
            return this.Fail($"手で行う手順を {ManualWaitLimit.TotalMinutes:0} 分待っても進みませんでした。手順を終えてから再開してください");

        // Lv1 の受注をこちらで行う前の準備（着替え → Questionable に頼むときと同じ受注前の確かめ）
        if (this.lv1Accept != null)
            return this.PrepareOwnAccept(ctx);

        if ((this.own != null || this.manualTurnIn || this.questCraft != null) && !ctx.TextAdvance.EnsureTurnInControl())
            return this.Fail($"TextAdvance の操作権を確保できないため、自前の会話・納品を止めました（{ctx.TextAdvance.LossReason ?? "理由を読めません"}）。操作権が空いてから再開してください");

        if (this.spearfish != null)
            return this.RunSpearfish(ctx);

        if (this.own != null)
        {
            this.KeepFishingStopped();

            // 製作の手順で止めたときは、Questionable が動かした Artisan の製作が終わるのを待ってから進める（ArtisanDrain）
            if (this.ownDrainSince is { } drainSince)
            {
                switch (ArtisanDrain.Decide(ctx.Artisan.IsBusy(), ctx.Artisan.IsListRunning(), DateTime.UtcNow - drainSince))
                {
                    case ArtisanDrain.Verdict.Wait:
                        this.Status = "Questionable が動かした Artisan の製作が終わるのを待っています" + OwnWorkNote;
                        return TaskResult.Running;
                    case ArtisanDrain.Verdict.GiveUp:
                        return this.Fail($"Questionable が動かした Artisan の製作が {ArtisanDrain.Limit.TotalMinutes:0} 分たっても終わりません。Artisan の画面で製作を止めてから、もう一度開始してください（続きから進みます）");
                }

                this.ownDrainSince = null;
            }

            return this.RunOwnSteps(ctx);
        }

        if (this.questCraft != null)
            return this.RunQuestCraft(ctx);

        if (this.manualTurnIn)
            return this.ManualTurnIn(ctx);

        // Lv1 の「My First ～」：受注前の段の新しいクラス向けの準備（ギアセットの作成・上書き）をさせず、受注だけこちらで行う
        if (!this.started && !this.gearsetSetupChecked)
        {
            this.gearsetSetupChecked = true;
            if (GameMemory.QuestAccepted(this.quest.RowId) == false && this.paths != null && QuestTakeOver.GearsetSetupAccept(this.paths) is { } accept)
            {
                ctx.Log.Write("クエスト", $"{this.quest} の受注前の段には、新しいクラス向けの準備（道具の装備・ギアセットの作成と上書き）があります。"
                                     + $"Lv70 の職では要らず、ギアセットを書き換えるので、受注だけこちらで行います（{NpcStepTask.NpcName(accept.DataId)} に話しかける）");
                if (Unlocks.PickJobFor(this.quest.RowId) is { } job && job != Jobs.CurrentClassJob)
                    this.lv1Equip = new EquipJobTask(job);
                this.lv1Accept = accept;
                this.NextPhase("受注の前の確かめ");
                return TaskResult.Running;
            }
        }

        // 受注の前（段 0）の購入の手順の品は、Questionable が店のメニューを選べず買えないので、頼む前にこちらで買う（QuestOwnPurchase）
        if (!this.started && this.paths != null && GameMemory.QuestAccepted(this.quest.RowId) == false
            && QuestOwnPurchase.BeforeAccept(this.paths, GameBait.Current.Owned) is { Count: > 0 } before)
        {
            if (this.OwnBuyLimitReached(before) is { } over)
                return this.Fail(over);
            if (ctx.Questionable.IsRunning() == true)
                ctx.Questionable.Stop(Plugin.InternalNameConst);
            this.CountOwnBuys(before);
            this.ownBuyOrders = before;
            this.ownBuyStopTerritory = 0; // 受注の前：買った後は、そのまま Questionable に頼む（経路データの購入の手順は、持っていれば飛ばされる）
            ctx.Log.Write("クエスト", $"{this.quest} の受注の前の購入の手順（{string.Join("、", before.Select(o => $"{CraftPlanner.ItemName(o.ItemId)}×{o.Count}（{NpcStepTask.NpcName(o.Npc)}）"))}）は、"
                                   + "Questionable が店のメニューを選べず買えないので、頼む前にこちらで NPC から買います");
            this.NextPhase("受注の前に要る品を NPC から買います");
            return TaskResult.Running;
        }

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
            this.Status = $"Questionable: 手順 {stepData.Sequence}-{stepData.Step} {stepData.InteractionType}"
                          + (stepData.InteractionType == "Gather" && this.timedWaitNote is { } note ? $"（{note}）" : string.Empty);
            this.lastStepText = $"{stepData.Sequence}-{stepData.Step} {stepData.InteractionType}";

            // Questionable が推奨装備に着替える手順（園芸師のジョブクエ17本）。完了したら、その職のギアセットに着直す
            if (stepData.InteractionType == "EquipRecommended" && !this.gearChangedByQuestionable)
            {
                this.gearChangedByQuestionable = true;
                ctx.Log.Write("クエスト", $"Questionable が推奨装備に着替えます。クエストが完了したら {Jobs.Name(this.quest.ClassJobId)} のギアセットに着直します");
            }

            // 手で行う手順（Instruction）：Questionable は段が変わるまで待つ。知らせて待つ（その間は上限に数えない）。
            // この段で渡す品がそろい、残りが話しかけるだけなら、渡すところからこちらで行う
            if (stepData.InteractionType == "Instruction")
            {
                this.manualWaitSince ??= DateTime.UtcNow;
                var handIn = this.paths?.Where(s => s.Sequence == stepData.Sequence && s.Index > stepData.Step && s.Type is "Interact" or "CompleteQuest")
                    .Select(s => s.DataId).FirstOrDefault(d => d != null);
                var wanted = QuestTakeOver.InstructionItems(this.quest, stepData.Sequence);

                // 刺突漁でしか取れない品（魚影の魚）なら、自動で刺突漁を行う（設定で切れる。1つのクエストで1回だけ試す）
                var instructionStepData = this.paths?.FirstOrDefault(s => s.Sequence == stepData.Sequence && s.Index == stepData.Step && s.Type == "Instruction");
                if (!this.spearfishTried && ctx.Config.AutoSpearfish && instructionStepData != null && GameUi.PlayerFree()
                    && SpearfishTask.Want(wanted, id => Inventory.CountNow(id), ctx.Data.Sources) is { } spear)
                {
                    this.spearfishTried = true;
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.instructionSeq = stepData.Sequence;
                    this.instructionStep = stepData.Step;
                    this.manualWaitTotal += DateTime.UtcNow - this.manualWaitSince.Value;
                    this.manualWaitSince = null;
                    this.spearfishSince = DateTime.UtcNow;
                    this.spearfish = new SpearfishTask(this.quest, spear.ItemId, spear.Count, instructionStepData);
                    ctx.Log.Write("クエスト", $"{Jobs.Name(this.quest.ClassJobId)} {this.quest} の手で行う手順（段 {stepData.Sequence}）の {CraftPlanner.ItemName(spear.ItemId)}×{spear.Count} は刺突漁でしか取れないので、"
                                         + "Questionable を止めて、GBR と AutoHook で自動で集めます（そろったら、渡すところからこちらで続けます）");
                    this.NextPhase("刺突漁を自動で行います");
                    return TaskResult.Running;
                }

                if (!this.manualWaitNotified)
                {
                    this.manualWaitNotified = true;
                    var what = wanted.Count > 0 ? $"（そろえる品：{string.Join("、", wanted.Select(w => $"{CraftPlanner.ItemName(w.ItemId)}×{w.Count}"))}）" : string.Empty;
                    var msg = $"{Jobs.Name(this.quest.ClassJobId)} {this.quest} は、手で行う手順（段 {stepData.Sequence}）があります{what}。"
                              + "Questionable の画面に出ている手順（刺突漁など）を手で行ってください。"
                              + (handIn is { } npc && wanted.Count > 0
                                  ? $"品がそろったら、{NpcStepTask.NpcName(npc)} に渡すところからこちらで続けます"
                                  : "段が進むと自動で続けます");
                    ctx.Log.Warn("クエスト", msg);
                    Svc.Chat.Print($"[AutoJobQuest] {msg}");
                }

                var inv = Inventory.Snapshot();
                var ready = wanted.Count > 0 && wanted.All(w => (w.Hq ? inv.CountHq(w.ItemId) : inv.CountAll(w.ItemId)) >= w.Count);
                if (this.paths != null && GameUi.PlayerFree()
                    && QuestTakeOver.AfterInstruction(this.paths, stepData.Sequence, stepData.Step, stepData.InteractionType, ready) is { } rest)
                {
                    this.manualWaitTotal += DateTime.UtcNow - this.manualWaitSince.Value;
                    this.manualWaitSince = null;
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.own = new Queue<QuestionableStep>(rest);
                    this.ownFromSeq = stepData.Sequence;
                    ctx.Log.Write("クエスト", $"手で行う手順の品がそろったので、Questionable を止め、段 {stepData.Sequence} の残り"
                                         + $"（{string.Join("→", rest.Select(x => $"{x.Type}（{NpcStepTask.NpcName(x.DataId)}）"))}）をこちらで行います");
                    this.NextPhase("手で行う手順の後をこちらで行います");
                    return TaskResult.Running;
                }

                this.Status = $"手で行う手順を待っています（段 {stepData.Sequence}）";
            }
            else if (this.manualWaitSince is { } since)
            {
                this.manualWaitTotal += DateTime.UtcNow - since;
                this.manualWaitSince = null;
            }

            // 受注後にクエストの材料から作る品（Lv61〜70 の製作職）の製作手順に入ったら、止めてこちらで HQ まで作る（QuestCraftTask）
            // 釣りの手順は AutoHook が要る（途中で外された場合。始める前にも確かめている）
            if (stepData.InteractionType == "Fish" && !ctx.AutoHook.IsLoaded)
                return this.Fail($"Questionable が釣りの手順（段 {stepData.Sequence}）に入りましたが、AutoHook が読み込まれていません。AutoHook を読み込んでから再開してください（続きから進みます）");

            // 必要数を持っていれば引き取らない（Questionable が「持っていれば飛ばす」で先へ進む）
            // 製作の手順でなくても、その段の品の HQ が足りなければ引き取る（Questionable は NQ を持っていると製作を飛ばすため）
            if (this.quest.QuestCrafts.Count > 0 && (stepData.InteractionType == "Craft" || this.paths != null))
            {
                var stepItem = this.paths?.FirstOrDefault(p => p.Sequence == stepData.Sequence && p.Index == stepData.Step)?.ItemId;
                var inv = Inventory.Snapshot();
                Func<uint, bool, int> held = (item, hq) => hq ? inv.CountHq(item) : inv.CountAll(item);
                // 段の単位の引き取りは、動ける状態で、この段でまだ何も渡しておらず、NQ と HQ を合わせた数は足りているのに HQ が足りないときだけ
                // （Questionable が製作を飛ばす形そのもの。渡した直後〔品が消えて段が進む前〕に誤って引き取らない）
                var qc = QuestTakeOver.QuestCraftToTake(stepData.InteractionType, stepItem, this.quest.QuestCrafts, held);
                if (qc == null && this.paths != null && GameUi.PlayerFree() && this.submittedInSequence != stepData.Sequence
                    && QuestTakeOver.QuestCraftInSequence(stepData.Sequence, this.paths, this.quest.QuestCrafts, held) is { } skipped
                    && inv.CountAll(skipped.ItemId) >= skipped.Count)
                    qc = skipped;
                if (qc != null)
                {
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.questCraft = new QuestCraftTask(this.quest, qc, this.paths);
                    this.questCraftSince = DateTime.UtcNow;
                    ctx.Log.Write("クエスト", $"Questionable が {CraftPlanner.ItemName(qc.ItemId)} の製作の段（段 {stepData.Sequence}・手順 {stepData.InteractionType}）に入ったので止め、"
                                         + $"こちらで{(qc.Hq ? " HQ になるまで" : string.Empty)}作ります"
                                         + "（Questionable の製作は品質を見ないため。製作は Artisan に任せます）");
                    this.NextPhase("受注後の品をこちらで作ります");
                    return TaskResult.Running;
                }
            }

            // 材料の購入・製作の手順に入ったら、止めて残りをこちらで行う（既製リストが動く・材料を買い足すのを防ぐ）
            if (this.paths != null && this.pathCrafts != null)
            {
                var inv = Inventory.Snapshot();
                var seqNow = (byte)stepData.Sequence;
                var rest = QuestTakeOver.Decide(this.paths, stepData.Sequence, stepData.Step, stepData.InteractionType, this.pathCrafts,
                    (item, count, hq) => (hq ? inv.CountHq(item) : inv.CountAll(item)) >= count, npc => this.RecipientDone(seqNow, npc));
                if (rest != null)
                {
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.own = new Queue<QuestionableStep>(rest);
                    this.ownFromSeq = stepData.Sequence;
                    if (stepData.InteractionType == "Craft")
                        this.ownDrainSince = DateTime.UtcNow; // Questionable が動かした Artisan の製作が終わるのを待ってから進める（ArtisanDrain）
                    ctx.Log.Write("クエスト", $"Questionable が段 {stepData.Sequence} の{(stepData.InteractionType == "Craft" ? "製作" : "材料の購入")}に入ったので止め、"
                                         + $"この段の残り（{string.Join("→", rest.Select(x => $"{x.Type}（{NpcStepTask.NpcName(x.DataId)}）"))}）をこちらで行います"
                                         + "（納品物は用意済み。既製リストの追加製作・材料の買い足しをさせないため）");
                    this.NextPhase("この段の残りをこちらで行います");
                    return TaskResult.Running;
                }
            }

            // 同じ段で複数の相手に渡すクエストで、Questionable が渡し済みの相手との手順に来たら、止めて残りの相手だけをこちらで行う
            if (this.paths != null && this.quest.Handovers.Count(h => h.Seq == stepData.Sequence) >= 2)
            {
                var seqNow = (byte)stepData.Sequence;
                var rest = QuestTakeOver.SkipDone(this.paths, stepData.Sequence, stepData.Step, stepData.InteractionType, npc => this.RecipientDone(seqNow, npc));
                if (rest != null)
                {
                    ctx.Questionable.Stop(Plugin.InternalNameConst);
                    this.own = new Queue<QuestionableStep>(rest);
                    this.ownFromSeq = stepData.Sequence;
                    ctx.Log.Write("クエスト", $"Questionable が段 {stepData.Sequence} の渡し済みの相手（{NpcStepTask.NpcName(this.paths.FirstOrDefault(s => s.Sequence == stepData.Sequence && s.Index == stepData.Step)?.DataId)}）に向かったので止め、"
                                         + $"残りの相手（{string.Join("→", rest.Select(x => NpcStepTask.NpcName(x.DataId)))}）にこちらで渡します（日誌の✓で渡し済みを確かめた）");
                    this.NextPhase("残りの相手にこちらで渡します");
                    return TaskResult.Running;
                }
            }

            // マテリア装着待ちの手順に来たら、報告だけこちらで行う（経路データが読めないときの従来の扱い）
            if (stepData.InteractionType == "WaitForManualProgress" && this.quest.Materia != null)
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
        // 止まっていたら、何度でも動かし直す（QuestionableKeepAlive。以前は3回やり直したら全体を止めていた）
        var running = ctx.Questionable.IsRunning();
        if (running == false)
        {
            if (++this.notRunningFrames >= 3)
            {
                if (DateTime.UtcNow < this.restartNotBefore)
                {
                    this.Status = $"Questionable が止まっています（{(this.restartNotBefore - DateTime.UtcNow).TotalSeconds:0} 秒後にもう一度動かします）";
                    return TaskResult.Running;
                }

                this.restarts++;
                this.restartNotBefore = DateTime.UtcNow + QuestionableKeepAlive.WaitAfter(this.restarts);
                var msg = $"Questionable が止まっていたので、もう一度動かします（{this.restarts} 回目・止まったときの手順 {this.lastStepText}）";
                ctx.Log.Warn("クエスト", msg);
                if (QuestionableKeepAlive.Notify(this.restarts))
                    Svc.Chat.Print($"[AutoJobQuest] {msg}");
                this.started = false;
                this.notRunningFrames = 0;
            }
        }
        else
        {
            this.notRunningFrames = 0;
            if (running == true && stepData != null && stepData.QuestId == QuestionableIpc.ToQuestId(this.quest.RowId) && this.WatchStuckElsewhere(ctx, stepData))
                return TaskResult.Running;
            if (running == true && this.WatchStepDoneStall(ctx, stepData) is { } stall)
                return stall;
        }

        return TaskResult.Running;
    }

    /// <summary>
    /// Questionable は動いているのに、今の手順のエリアの外で、動ける状態のまま動かずにいたら（StuckElsewhere）、Questionable を止めて
    /// 手順の場所まで運び（既存の GoToTask：テレポ・エーテライト網・歩き）、着いたら頼み直す。運び始めたら true。
    /// </summary>
    private bool WatchStuckElsewhere(TaskContext ctx, QuestionableIpc.StepData step)
    {
        var here = GameWeather.CurrentTerritory;
        var pos = Me.Position;
        var free = GameUi.PlayerFree();
        var key = $"{step.Sequence}-{step.Step}";

        // 「手順が同じまま・動ける状態で・手順のエリアの外で・動かずに」が続いた時間だけ数える（以前は釣りの間も
        // 数えていたので、釣り終えて次の手順〔別のエリアの報告〕に切り替わった0.3秒後に、Questionable がテレポを始める前に割り込んだ）
        if (this.stillSince == null || !free || key != this.stillStep || step.TerritoryId == 0 || step.TerritoryId == here
            || System.Numerics.Vector3.Distance(pos, this.stillAt) > 1f)
        {
            this.stillAt = pos;
            this.stillSince = DateTime.UtcNow;
            this.stillStep = key;
        }

        var verdict = StuckElsewhere.Decide(step.TerritoryId, here, step.InteractionType, free, DateTime.UtcNow - this.stillSince.Value, this.relocations);
        if (verdict == StuckElsewhere.Verdict.None && this.relocations >= StuckElsewhere.MaxRelocations && step.TerritoryId != 0 && step.TerritoryId != here
            && !this.relocateLimitNoted && DateTime.UtcNow - this.stillSince.Value >= StuckElsewhere.Still)
        {
            this.relocateLimitNoted = true;
            var msg = $"Questionable が {AreaAccess.Name(step.TerritoryId)} の手順（{step.Sequence}-{step.Step} {step.InteractionType}）を、{AreaAccess.Name(here)} で待ち続けています。"
                      + $"{StuckElsewhere.MaxRelocations} 回運んでも進まないので、これ以上は運びません。手で {AreaAccess.Name(step.TerritoryId)} へ移動してください（Questionable がそのまま続けます）";
            ctx.Log.Warn("クエスト", msg);
            Svc.Chat.Print($"[AutoJobQuest] {msg}");
        }

        if (verdict != StuckElsewhere.Verdict.Relocate)
            return false;

        this.relocations++;
        this.stillSince = null;
        ctx.Questionable.Stop(Plugin.InternalNameConst);
        this.PauseAutoFishing(ctx);
        ctx.Log.Warn("クエスト", $"Questionable が {AreaAccess.Name(step.TerritoryId)} の手順（{step.Sequence}-{step.Step} {step.InteractionType}）を、"
                                + $"{AreaAccess.Name(here)} で {StuckElsewhere.Still.TotalSeconds:0} 秒動かずに待っています（たどり着けないエリアを待ち続ける形）。"
                                + $"Questionable を止めて {AreaAccess.Name(step.TerritoryId)} へ運び、着いたら頼み直します（{this.relocations}/{StuckElsewhere.MaxRelocations} 回目）");
        this.relocate = TestReturnTask?.Invoke(step.TerritoryId)
                        ?? (step.Position is { } p
                            ? new GoToTask(step.TerritoryId, p, 10f, AreaAccess.Name(step.TerritoryId))
                            : new TeleportTask(step.TerritoryId));
        this.NextPhase($"{AreaAccess.Name(step.TerritoryId)} へ運んでいます");
        return true;
    }

    /// <summary>手順の場所へ運ぶ作業を進める。終わったら（着けなくても）Questionable に頼み直す。</summary>
    private TaskResult RunRelocate(TaskContext ctx)
    {
        var r = this.relocate!.Step(ctx);
        this.Status = this.relocate.Status + OwnWorkNote;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        if (r == TaskResult.Failed)
            ctx.Log.Warn("クエスト", $"手順の場所へ運べませんでした（{this.relocate.FailReason}）。そのまま Questionable に頼み直します");
        this.relocate.Cleanup(ctx);
        this.relocate = null;
        this.RestoreAutoFishing(ctx);
        this.started = false; // Questionable に頼み直す
        this.notRunningFrames = 0;
        return TaskResult.Running;
    }

    /// <summary>
    /// その段の品を受け取る相手に渡し済みか。同じ段に相手が2人以上いるときだけ、日誌の✓（ゲームの判定）で見る。
    /// ✓が読めなければ「渡し済みではない」とする（飛ばさない：話しかけても変わらなければ NpcStepTask が「済んだ相手の可能性」として先へ進む）。
    /// </summary>
    private bool RecipientDone(byte seq, uint npc)
    {
        var here = this.quest.Handovers.Where(h => h.Seq == seq).ToList();
        if (here.Count < 2)
            return false;
        return here.Where(h => h.Npc == npc).Any(h => QuestTodo.IsChecked(this.quest.RowId, h.Todo) == true);
    }

    /// <summary>
    /// 納品窓を扱う。扱うのは次の全部を満たす窓だけ：
    ///  ・こちらがクエストを始めた（Questionable に頼んだ・報告に向かった）後であること
    ///  ・窓がこのクエストの間に開いたこと（AddonOwnership の記録。始める前から開いていた窓は OnStart で止めている）
    ///  ・窓が求める品が、このクエストの納品物に含まれること（RequestFiller が確かめる）
    /// 失敗（条件に合う品が無い等）なら理由を返す。
    /// </summary>
    // 購入の確認：最初に見えた窓（Questionable に1フレーム譲る）・押した窓・同じ理由を2度書かない控え
    private nint purchaseSeen;
    private nint purchasePressed;
    private string lastPurchaseNote = string.Empty;

    // 購入の確認の文面（ゲームデータ Addon#3406。Questionable の YesNoChoiceHandler と同じ行）から作った照合の形
    private const uint PurchaseConfirmAddon = 3406;
    private static QuestPurchaseConfirm.Pattern? purchasePattern;

    public static QuestPurchaseConfirm.Pattern? PurchasePattern()
    {
        if (purchasePattern != null)
            return purchasePattern;
        if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().TryGetRow(PurchaseConfirmAddon, out var row))
            return null;
        var parts = new List<QuestPurchaseConfirm.Part>();
        foreach (var p in row.Text)
        {
            if (p.Type == Lumina.Text.ReadOnly.ReadOnlySePayloadType.Text)
            {
                parts.Add(new(true, p.ToString(), string.Empty));
                continue;
            }

            var kind = p.MacroCode switch
            {
                Lumina.Text.Payloads.MacroCode.Sheet when p.ToString().Contains("Item", StringComparison.Ordinal) => "item",
                Lumina.Text.Payloads.MacroCode.Num => "count",
                Lumina.Text.Payloads.MacroCode.Kilo => "price",
                Lumina.Text.Payloads.MacroCode.NewLine => "br",
                _ => string.Empty,
            };
            parts.Add(new(false, string.Empty, kind));
        }

        return purchasePattern = QuestPurchaseConfirm.Build(parts);
    }

    /// <summary>
    /// Questionable の購入の手順で出た購入の確認（SelectYesno）に OK を押す。押すかは <see cref="QuestPurchaseConfirm.Decide"/> で決める。
    /// 窓が見えた最初のフレームは Questionable に譲り（Questionable が押せれば閉じる）、次のフレームでも開いていれば押す。
    /// </summary>
    private void HandlePurchaseConfirm(TaskContext ctx)
    {
        var body = GameUi.YesnoText(out var addon);
        if (body == null || addon == null)
        {
            this.purchaseSeen = 0;
            this.purchasePressed = 0;
            return;
        }

        var ptr = (nint)addon;
        if (ptr == this.purchasePressed)
            return;
        if (ptr != this.purchaseSeen)
        {
            this.purchaseSeen = ptr;
            return;
        }

        var step = ctx.Questionable.GetCurrentStepData();
        var purchaseStep = step?.InteractionType == "PurchaseItem" && ctx.Questionable.IsRunning() == true;
        var shopOpen = GameUi.IsVisible("Shop");
        var expected = (QuestionablePaths.Steps(this.quest.ShortId) ?? [])
            .Where(s => s.Type == "PurchaseItem" && s.ItemId is { } id && id != 0)
            .Select(s => (CraftPlanner.ItemName(s.ItemId!.Value), s.ItemCount))
            .ToList();
        var parsed = PurchasePattern() is { } pattern ? QuestPurchaseConfirm.Parse(pattern, body) : null;
        var verdict = QuestPurchaseConfirm.Decide(purchaseStep, shopOpen, body, parsed, expected, ctx.Config.ConfirmPurchaseAboveGil);
        if (verdict == QuestPurchaseConfirm.Verdict.Press)
        {
            if (GameUi.ClickYes(addon))
            {
                this.purchasePressed = ptr;
                ctx.Log.Write("クエスト", $"Questionable の購入の確認に OK を押しました：{body.Replace("\n", " ")}（{parsed?.Item}×{parsed?.Count}・{parsed?.Price:N0} ギル）");
            }

            return;
        }

        // 押さない理由は、同じ窓・同じ理由につき1回だけ記録する（Questionable の手順が購入でないときは、ほかの確認なので書かない）
        var note = $"{ptr}:{verdict}";
        if (note != this.lastPurchaseNote && verdict != QuestPurchaseConfirm.Verdict.NotPurchaseStep)
        {
            this.lastPurchaseNote = note;
            ctx.Log.Debug("クエスト", $"購入の確認に OK を押しません（{verdict}）：{body.Replace("\n", " ")}"
                                     + $"（読んだ品 {parsed?.Item ?? "なし"}×{parsed?.Count}・{parsed?.Price} ギル／手順の品 {string.Join("、", expected.Select(e => $"{e.Item1}×{e.ItemCount}"))}）");
        }
    }

    // HQ 品を渡す確認の文面（ゲームデータ Addon#102434「ハイクオリティ品がトレードされようとしています。本当によろしいですか？」。
    // Questionable の YesNoChoiceHandler と同じ行）
    private const uint HqTradeConfirmAddon = 102434;

    // マテリアを付けた品を渡す確認の文面（ゲームデータ Addon#102433「マテリアが装着されたアイテムをトレードしようとしています。
    // 装着されているマテリアは戻ってきませんが、本当によろしいですか？」）。MeldTask でマテリアを付けた納品物を渡すときに出る。
    // Questionable はこの確認を扱わない（YesNoChoiceHandler の一覧に無い）
    private const uint MateriaTradeConfirmAddon = 102433;

    // 会話の窓に答える（HandleQuestDialogs）：最初に見た窓・答えた窓（どちらも「窓の場所:本文」。同じ場所に続けて別の窓が開いても取り違えない）
    private string? dialogSeen;
    private string? dialogAnswered;

    /// <summary>
    /// クエストの間に出た次の窓に、Questionable と同じ答え方で答える。Questionable が動いているかどうかに関係なく、窓が見えた最初のフレームは見送り、
    /// 次のフレームでも開いたままなら答える（Questionable は窓が開いたとき〔PostSetup〕に答えるので、答えていれば次のフレームには閉じている）。
    ///  ・HQ 品を渡す確認（Addon#102434）：OK（コールバック 0。「ハイクオリティ品を渡す」にチェックを入れるまで OK ボタンは押せないので、
    ///    Questionable と同じくコールバックで答える）。どの職のどの納品も、納品窓はこの作業（HandleRequest）だけが扱うので、ここで全部を押さえる
    ///  ・マテリアを付けた品を渡す確認（Addon#102433）：OK（同じくコールバック 0）。MeldTask でマテリアを付けた納品物を渡すときに出る。
    ///    Questionable はこの確認を扱わないので、こちらが答えないと必ず止まる
    ///  ・今の段の手順の会話の選択肢（経路の DialogueChoices）：「はい／いいえ」は本文が問いに合えば指定どおり。一覧（SelectString・SelectIconString・
    ///    映像中の CutSceneSelectString）は、問いが合い、答えに合う項目があればその番号（Questionable の DialogueChoiceHandler と同じ読み方）。
    /// 不具合の例：
    ///  ①錬金術師 Lv1 の報告をこちらで行い（Questionable を止めていた）、HQ の蒸留水を渡す確認のまま止まった。Questionable はこれらの窓を
    ///    自分が動いている間だけ扱い（ShouldHandleUiInteractions）、YesAlready はクエストの間こちらが止めているので、誰も答えなかった
    ///  ②①を直した後、Lv5（578）では Questionable が動いている間に同じ確認のまま止まった。Questionable は窓を受け取った（YesNoChoiceHandler: TravelYesNo）が
    ///    HQ の確認と判定しなかった（日本語の本文の途中の改行に照合が合わないと見られる）。①の直しは「Questionable が動いていれば任せる」だったので押さなかった
    /// </summary>
    private void HandleQuestDialogs(TaskContext ctx)
    {
        var yesnoBody = GameUi.YesnoText(out var yesno);
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* menu = null;
        string? menuPrompt = null;
        List<string>? menuEntries = null;
        if (yesno == null)
        {
            if (GameUi.IsReady("SelectString", out var ss))
            {
                menu = ss;
                menuPrompt = GameUi.AtkValueText(ss, 2);
                menuEntries = GameUi.MenuEntries(out _);
            }
            else if (GameUi.IsReady("SelectIconString", out var si))
            {
                menu = si;
                menuPrompt = GameUi.AtkValueText(si, 3);
                menuEntries = GameUi.MenuEntries(out _);
            }
            else if (GameUi.TestBackend == null && GameUi.IsReady("CutSceneSelectString", out var cs))
            {
                menu = cs;
                menuPrompt = GameUi.AtkValueText(cs, 2);
                menuEntries = [];
                for (var i = 5; i < cs->AtkValuesCount; i++)
                    menuEntries.Add(GameUi.AtkValueText(cs, i) ?? string.Empty);
            }
        }

        var ptr = yesno != null ? (nint)yesno : (nint)menu;
        if (ptr == 0)
        {
            this.dialogSeen = null;
            this.dialogAnswered = null;
            return;
        }

        var key = yesno != null ? $"{ptr}:{yesnoBody}" : $"{ptr}:{menuPrompt}:{string.Join("|", menuEntries ?? [])}";
        if (key == this.dialogAnswered)
            return;
        if (key != this.dialogSeen)
        {
            this.dialogSeen = key;
            return;
        }

        // 渡す確認（マテリア・HQ）は、ほかのプレイヤーとの取引（Trade）でも出る。取引の窓が開いていれば、クエストの納品ではないので答えない
        if (yesno != null && !GameUi.IsVisible("Trade"))
        {
            if (QuestDialogue.Matches(yesnoBody, QuestDialogue.ResolveAddon(MateriaTradeConfirmAddon)))
            {
                GameUi.Fire(yesno, true, 0);
                this.dialogAnswered = key;
                ctx.Log.Write("クエスト", "マテリアを付けた品を渡す確認に OK と答えました（クエストの納品物。Questionable はこの確認を扱わない）");
                return;
            }

            if (QuestDialogue.Matches(yesnoBody, QuestDialogue.ResolveAddon(HqTradeConfirmAddon)))
            {
                GameUi.Fire(yesno, true, 0);
                this.dialogAnswered = key;
                ctx.Log.Write("クエスト", $"HQ 品を渡す確認に OK と答えました（Questionable が{(ctx.Questionable.IsRunning() == true ? "動いているのに答えなかったため" : "止まっている間の納品のため")}。Questionable と同じ答え方）");
                return;
            }
        }

        var seq = GameMemory.QuestSequence(this.quest.RowId);
        var choices = (QuestionablePaths.Steps(this.quest.ShortId) ?? [])
            .Where(s => s.Sequence == seq && s.Choices != null)
            .SelectMany(s => s.Choices!)
            .ToList();
        if (choices.Count == 0)
            return;

        if (yesno != null)
        {
            foreach (var c in choices.Where(c => c.Type == "YesNo" && c.Prompt != null))
            {
                if (!QuestDialogue.Matches(yesnoBody, QuestDialogue.Resolve(this.quest.ShortId, c.Prompt!)))
                    continue;
                GameUi.Fire(yesno, true, c.Yes ? 0 : 1);
                this.dialogAnswered = key;
                ctx.Log.Write("クエスト", $"手順の会話の選択肢に「{(c.Yes ? "はい" : "いいえ")}」と答えました：{yesnoBody?.Replace("\n", " ")}（Questionable が答えなかったため。経路の指定どおり）");
                return;
            }

            return;
        }

        if (menuEntries == null || menuEntries.Count == 0)
            return;
        foreach (var c in choices.Where(c => c.Type == "List" && c.Answer != null))
        {
            if (c.Prompt != null && !QuestDialogue.Matches(menuPrompt, QuestDialogue.Resolve(this.quest.ShortId, c.Prompt)))
                continue;
            var answer = QuestDialogue.Resolve(this.quest.ShortId, c.Answer!);
            var index = menuEntries.FindIndex(e => QuestDialogue.Matches(e, answer));
            if (index < 0)
                continue;
            GameUi.Fire(menu, true, index);
            this.dialogAnswered = key;
            ctx.Log.Write("クエスト", $"手順の会話の選択肢で「{menuEntries[index]}」を選びました：{menuPrompt?.Replace("\n", " ")}（Questionable が答えなかったため。経路の指定どおり）");
            return;
        }
    }

    // こちらで NPC から買う（QuestOwnPurchase）：買う品・動かしている作業・試した品・止めた時点のエリア・戻っているか・始めた時刻・かかった時間
    private List<QuestOwnPurchase.Order>? ownBuyOrders;
    private AutoTask? ownBuy;
    private readonly Dictionary<uint, int> ownBuys = [];

    /// <summary>1つのクエストで、同じ品をこちらで買う回数の上限（餌を使い切ったら買い直す。上限まで買ってもまた無くなるなら止める）。</summary>
    public const int OwnBuyLimit = 3;
    private uint ownBuyStopTerritory;
    private bool ownBuyReturning;
    private DateTime? ownBuySince;
    private TimeSpan ownBuyTotal;

    /// <summary>検証の仕組み用：設定すると、NPC 購入（GBR）の代わりにこれが作る作業を使う。本番では null のまま。</summary>
    public static Func<IReadOnlyList<VendorNeed>, AutoTask>? TestVendorTask { get; set; }

    /// <summary>
    /// Questionable の今の手順から後に、同じ段の釣りの手順があり、その餌を持っておらず、Questionable が釣りの前に買う手順も無いなら、
    /// Questionable を止めて、こちらで NPC から買う（QuestOwnPurchase.MissingBait。同じ品は <see cref="OwnBuyLimit"/> 回まで：餌を使い切ったら買い直す）。
    /// 買いに行ったら true。上限まで買っていれば true と止める理由（<paramref name="fail"/>）。
    /// 受注の前の購入（段 0）を Questionable が買えなかったとき・利用者が餌を手放したとき・受注の後の購入が何かで買えなかったときの立て直し。
    /// </summary>
    private bool HandleMissingBait(TaskContext ctx, out string? fail)
    {
        fail = null;
        var step = ctx.Questionable.GetCurrentStepData();
        if (step == null || ctx.Questionable.IsRunning() != true || step.QuestId != this.quest.ShortId.ToString() || this.paths == null || GameUi.BetweenAreas)
            return false;
        if (QuestOwnPurchase.MissingBait(this.paths, step.Sequence, step.Step, GameBait.Current.Owned) is not { } order)
            return false;
        if (this.OwnBuyLimitReached([order]) is { } over)
        {
            fail = over;
            return true;
        }

        ctx.Questionable.Stop(Plugin.InternalNameConst);
        this.PauseAutoFishing(ctx);
        this.CountOwnBuys([order]);
        this.ownBuyOrders = [order];
        this.ownBuyStopTerritory = GameWeather.CurrentTerritory;
        ctx.Log.Write("クエスト", $"釣りの手順（段 {step.Sequence}）の餌 {CraftPlanner.ItemName(order.ItemId)} を持っておらず、Questionable が釣りの前に買う手順もありません"
                               + "（受注の前の購入の手順は、Questionable が店のメニューを選べず買えない）。"
                               + $"Questionable を止めて、{NpcStepTask.NpcName(order.Npc)} から {order.Count} 個買ってから、釣りに戻ります");
        this.NextPhase($"釣りの餌 {CraftPlanner.ItemName(order.ItemId)} を NPC から買います");
        return true;
    }

    /// <summary>上限まで買った品があれば、止める理由（無ければ null）。</summary>
    private string? OwnBuyLimitReached(IEnumerable<QuestOwnPurchase.Order> orders)
    {
        var over = orders.Where(o => this.ownBuys.GetValueOrDefault(o.ItemId) >= OwnBuyLimit).ToList();
        return over.Count == 0
            ? null
            : $"{string.Join("、", over.Select(o => CraftPlanner.ItemName(o.ItemId)))} をこのクエストで {OwnBuyLimit} 回 NPC から買いましたが、また足りなくなりました"
              + $"（{string.Join("、", over.Select(o => $"{GameBait.Current.Owned(o.ItemId)}／{o.Count}"))}）。持ち物（売った・捨てた・預けた）と釣りの餌を確かめてから再開してください（続きから進みます）";
    }

    private void CountOwnBuys(IEnumerable<QuestOwnPurchase.Order> orders)
    {
        foreach (var o in orders)
            this.ownBuys[o.ItemId] = this.ownBuys.GetValueOrDefault(o.ItemId) + 1;
    }

    /// <summary>
    /// こちらの NPC 購入を進める。動ける状態になってから GBR の NPC 購入を始める（釣りの構えは KeepFishingStopped で解く）。
    /// 買いきれなければ止める（その品が無いと Questionable の手順が進まない：餌が無ければ目当ての魚が釣れない）。
    /// 買えたら、止めた時点のエリアへ戻ってから（釣りの手順の途中で止めたとき）Questionable に頼み直す。
    /// </summary>
    private TaskResult RunOwnBuy(TaskContext ctx)
    {
        if (this.ownBuy == null)
        {
            if (!GameUi.PlayerFree() || GameUi.BetweenAreas)
            {
                this.Status = "動ける状態になるのを待ってから、NPC から買います" + OwnWorkNote;
                return TaskResult.Running;
            }

            var needs = this.ownBuyOrders!.Select(o => new VendorNeed(o.ItemId, o.Count, o.Npc)).ToList();
            this.ownBuy = TestVendorTask?.Invoke(needs) ?? new VendorTask(needs);
            this.ownBuySince = DateTime.UtcNow;
        }

        var r = this.ownBuy.Step(ctx);
        this.Status = this.ownBuy.Status + OwnWorkNote;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        var failed = r == TaskResult.Failed ? this.ownBuy.FailReason : null;
        this.ownBuy.Cleanup(ctx);
        this.ownBuy = null;
        if (this.ownBuySince is { } since)
            this.ownBuyTotal += DateTime.UtcNow - since;
        this.ownBuySince = null;

        // 戻りのテレポが終わった：Questionable に頼み直す
        if (this.ownBuyReturning)
        {
            this.ownBuyReturning = false;
            this.ownBuyOrders = null;
            if (failed != null)
                ctx.Log.Warn("クエスト", $"{AreaAccess.Name(this.ownBuyStopTerritory)} へ戻れませんでした（{failed}）。そのまま Questionable に頼み直します");
            this.RestoreAutoFishing(ctx);
            this.started = false;
            return TaskResult.Running;
        }

        var orders = this.ownBuyOrders!;
        // 1個も無ければ止める（Questionable の手順が進まない）。足りないが1個以上あれば、記録して続ける（釣りにも、購入の手順を飛ばすにも1個で足りる）
        var missing = orders.Where(o => GameBait.Current.Owned(o.ItemId) <= 0).ToList();
        foreach (var o in orders.Where(o => GameBait.Current.Owned(o.ItemId) is > 0 and var n && n < o.Count))
            ctx.Log.Warn("クエスト", $"{CraftPlanner.ItemName(o.ItemId)} を買いきれませんでした（{GameBait.Current.Owned(o.ItemId)}／{o.Count}）。1個以上あるので続けます");
        if (missing.Count > 0)
            return this.Fail($"{string.Join("、", missing.Select(o => $"{CraftPlanner.ItemName(o.ItemId)}（{GameBait.Current.Owned(o.ItemId)}／{o.Count}）"))} を NPC から買えませんでした"
                             + (failed != null ? $"（{failed}）" : string.Empty)
                             + $"。{string.Join("、", missing.Select(o => $"{NpcStepTask.NpcName(o.Npc)} で {CraftPlanner.ItemName(o.ItemId)} を {o.Count} 個"))}買ってから再開してください（続きから進みます）");

        ctx.Log.Write("クエスト", $"NPC から買いました：{string.Join("、", orders.Select(o => $"{CraftPlanner.ItemName(o.ItemId)} {GameBait.Current.Owned(o.ItemId)}／{o.Count}"))}");

        // 釣りの手順の途中で止めて別の街へ移っていたら、止めた時点のエリアへ戻ってから頼み直す（経路データの釣りの手順はテレポを持たないことがある）
        if (this.ownBuyStopTerritory != 0 && GameWeather.CurrentTerritory != this.ownBuyStopTerritory)
        {
            ctx.Log.Write("クエスト", $"買うために別の街へ移っていたので、{AreaAccess.Name(this.ownBuyStopTerritory)} へ戻ってから Questionable に頼み直します");
            this.ownBuyReturning = true;
            this.ownBuySince = DateTime.UtcNow;
            this.ownBuy = TestReturnTask?.Invoke(this.ownBuyStopTerritory) ?? new TeleportTask(this.ownBuyStopTerritory);
            return TaskResult.Running;
        }

        this.ownBuyOrders = null;
        this.RestoreAutoFishing(ctx);
        this.started = false; // Questionable に頼む（受注の前なら初めて、釣りの途中なら頼み直し）
        return TaskResult.Running;
    }

    // 天気の限られた魚の購入（HandleWeatherFish）：動かしている購入・試したか・始めた時刻・かかった時間・同じことを2度書かない控え
    private AutoTask? weatherBuy;
    private bool weatherBuyTried;
    private DateTime? weatherBuySince;
    private TimeSpan weatherBuyTotal;
    private uint weatherBuyItem;
    private int weatherBuyCount;
    private QuestionableStep? weatherFishStep;
    private int weatherStopStep;
    private uint weatherStopTerritory;
    private bool weatherReturning;
    private string lastWeatherNote = string.Empty;

    /// <summary>検証の仕組み用：設定すると、マーケットボードの購入の代わりにこれが作る作業を使う。本番では null のまま。</summary>
    public static Func<MarketNeed, AutoTask>? TestMarketTask { get; set; }

    /// <summary>検証の仕組み用：設定すると、止めた時点のエリアへ戻るテレポの代わりにこれが作る作業を使う。本番では null のまま。</summary>
    public static Func<uint, AutoTask>? TestReturnTask { get; set; }

    /// <summary>
    /// Questionable の今の手順が採集（Gather）で、その品が時限の採集点でしか採れず、今は出ていないとき（硬拳石：ET 8〜10時・20〜22時）。
    /// Questionable は、出ていない採集点の前で、出るまで黙って待つ（Questionable の GatheringController：採集点がどれも触れなければ何もしない。
    /// 出れば採り始める）。こちらは、待っている理由と次に採れる時刻を状態の欄と記録に出す。
    /// 次に採れるまで <see cref="DeferOver"/> より長く、ほかの職のジョブクエを進められるなら（<see cref="CanDefer"/>）、このクエストを後回しにする（true）。
    /// 品がそろっている・いつも出ている点がある・品の採集点が分からないときは何もしない。
    /// </summary>
    private bool HandleTimedGather(TaskContext ctx)
    {
        this.timedWaitNote = null;
        var step = ctx.Questionable.GetCurrentStepData();
        if (step == null || step.InteractionType != "Gather" || ctx.Questionable.IsRunning() != true
            || step.QuestId != QuestionableIpc.ToQuestId(this.quest.RowId) || ctx.Data.Sources is not { } sources)
            return false;
        var gather = this.paths?.FirstOrDefault(s => s.Sequence == step.Sequence && s.Index == step.Step && s.Type == "Gather");
        if (gather?.GatherItemId is not { } item || Inventory.CountNow(item) >= (gather.GatherCount ?? 1))
            return false;
        var spots = sources.Get(item).Gather;
        if (spots.Count == 0 || spots.Any(g => g.UpHours == GatherSpot.AllHours))
            return false;

        var mask = spots.Aggregate(0u, (m, g) => m | g.UpHours);
        var hour = EorzeaTime.Hour();
        if (Planning.PlanBuilder.HoursUntilUp(mask, hour) is not { } until || until <= 0)
            return false;

        var wait = TimeSpan.FromSeconds(until * EorzeaTime.SecondsPerHour);
        var name = CraftPlanner.ItemName(item);
        var opens = EorzeaTime.Clock((hour + until) % 24);
        var why = $"{name} は採集点が {Planning.PlanBuilder.UpHoursText(mask)} にしか出ません。いまは ET {EorzeaTime.Clock(hour)} で、次に採れるのは ET {opens}（約 {Math.Ceiling(wait.TotalMinutes):0} 分後）";
        if (this.CanDefer && wait > DeferOver)
        {
            this.DeferredUntil = DateTime.UtcNow + wait;
            ctx.Questionable.Stop(Plugin.InternalNameConst);
            ctx.Log.Write("クエスト", $"{why}なので、それまで {this.quest} を後回しにして、ほかの職のジョブクエを先に進めます（採れる時刻が近づいたら戻ります）");
            return true;
        }

        this.timedWaitNote = $"{name} の採集点が出る ET {opens} まで待っています。あと約 {Math.Ceiling(wait.TotalMinutes):0} 分";
        var key = $"{item}:{opens}";
        if (this.lastTimedNote != key)
        {
            this.lastTimedNote = key;
            ctx.Log.Write("クエスト", $"{why}なので、Questionable が採集点の前で出るのを待ちます（出れば Questionable が採ります）"
                                     + (this.CanDefer ? string.Empty : "。ほかに先に進められるジョブクエがありません"));
        }

        return false;
    }

    /// <summary>
    /// Questionable の今の手順から後に、同じ段の釣りの手順があり、その魚が天気・時間帯の限られた魚なら、釣りの手順のエリアに着いたところで天気・時間を見る
    /// （WeatherFishBuy の説明）。天気が合わなければ、Questionable を止めて、足りない分（NQ）をマーケットボードで買う（1つのクエストで1回だけ）。
    /// 天気が合っていれば何もしない（Questionable が釣る）。買いに行ったら true。
    /// </summary>
    private bool HandleWeatherFish(TaskContext ctx)
    {
        var step = ctx.Questionable.GetCurrentStepData();
        if (step == null || ctx.Questionable.IsRunning() != true || step.QuestId != this.quest.ShortId.ToString() || GameUi.BetweenAreas)
            return false;
        if (QuestionablePaths.Steps(this.quest.ShortId) is not { } steps || WeatherFishBuy.FishAhead(steps, step.Sequence, step.Step) is not { } fish)
            return false;

        var item = fish.GatherItemId!.Value;
        var count = fish.GatherCount!.Value;
        var weathers = FishConditions.Weathers(item, ctx.AutoHook.LoadedVersion) ?? [];
        // 時間帯の限られた魚も同じ扱い（時限性・天候の採取物は待たずに買う。フルムーンサーディンは ET 18:00〜06:00 だけ）
        var window = FishConditions.Window(item, ctx.AutoHook.LoadedVersion);
        if (weathers.Count == 0 && window == null)
            return false;

        var nq = Inventory.Snapshot().CountNq(item);
        var inArea = GameWeather.CurrentTerritory == fish.Territory;
        var weather = inArea && weathers.Count > 0 ? GameWeather.CurrentIn(fish.Territory) : null;
        double? hour = inArea && window != null ? EorzeaTime.Hour() : null;
        var verdict = WeatherFishBuy.Decide(weathers, nq, count, inArea, weather, WeatherFishBuy.Marketable(item), this.weatherBuyTried, window, hour);
        var name = CraftPlanner.ItemName(item);
        // 文言：天気は「天気が「雨・雪」」、時間帯は「時間が「ET 18:00〜06:00」」（天気だけの魚は従来の言い回しのまま）
        var want = string.Join("・", new[]
        {
            weathers.Count > 0 ? $"天気が「{string.Join("・", weathers.Select(GameWeather.Name))}」" : null,
            window is { } wnd ? $"時間が「{EorzeaTime.Text(wnd)}」" : null,
        }.Where(x => x != null));
        var now = string.Join("・", new[]
        {
            weathers.Count > 0 ? $"天気は「{(weather is { } w ? GameWeather.Name(w) : "不明")}」" : null,
            hour is { } h ? $"時間は「ET {EorzeaTime.Clock(h)}」" : null,
        }.Where(x => x != null));
        var zone = AreaAccess.Name(fish.Territory);
        switch (verdict)
        {
            case WeatherFishBuy.Verdict.Buy:
                var need = count - nq;
                ctx.Questionable.Stop(Plugin.InternalNameConst);
                this.PauseAutoFishing(ctx);
                this.weatherBuyTried = true;
                this.weatherBuySince = DateTime.UtcNow;
                this.weatherBuyItem = item;
                this.weatherBuyCount = count;
                this.weatherFishStep = fish;
                this.weatherStopStep = step.Step;
                this.weatherStopTerritory = GameWeather.CurrentTerritory;
                var order = new MarketNeed([item], need, name, Inventory.CountNow(item) + need, NqOnly: true);
                this.weatherBuy = TestMarketTask?.Invoke(order) ?? new MarketBoardTask([order], ctx.MarketWatcher);
                ctx.Log.Write("クエスト", $"{name} は{want}のときしか釣れません。いまの{zone}の{now}なので、Questionable を止めて、"
                                       + $"足りない {need} 匹（NQ。持っている NQ {nq}／要る {count}）をマーケットボードで買います"
                                       + "（天気・時間が合わないときは、待たずに買います）");
                this.NextPhase($"天気・時間が合わないので {name} をマーケットボードで買います");
                return true;
            case WeatherFishBuy.Verdict.InWeather:
                this.NoteWeather(ctx, $"{item}:In:{weather}:{(hour is { } ih ? (int)ih : -1)}", $"{name} は{want}のときに釣れます。いまの{zone}の{now}なので、Questionable が釣ります");
                return false;
            case WeatherFishBuy.Verdict.CannotBuy:
                this.NoteWeather(ctx, $"{item}:NoMarket", $"{name} は{want}のときしか釣れず、いまの{zone}の{now}ですが、マーケットで売買できない品なので、"
                                                        + "天気・時間が合うのを待って Questionable が釣ります", warn: true);
                return false;
            case WeatherFishBuy.Verdict.AlreadyTried:
                this.NoteWeather(ctx, $"{item}:Tried", $"{name} をマーケットボードで買いきれなかったので、{want}に変わるのを待って Questionable が釣ります"
                                                     + $"（いまの{zone}の{now}・NQ {nq}／{count}）", warn: true);
                return false;
            case WeatherFishBuy.Verdict.WeatherUnknown:
                this.NoteWeather(ctx, $"{item}:Unknown", $"{zone}の天気・時間を読めないので、{name} は Questionable に釣りを任せます（釣れる条件：{want}）", warn: true);
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 天気の限られた魚の購入を進める。そろったら、釣りの後の残りの手順（報告）をこちらで行う（Questionable は段の頭からやり直し、
    /// 経路データの「餌を持っていればテレポを飛ばす」で、マーケットのある街から戻れずに待ち続けるため）。
    /// こちらで行えない形なら、止めた時点のエリアへ戻ってから Questionable に頼み直す。買いきれなければ、同じく戻って頼み直す（天気を待って釣る）。
    /// </summary>
    private TaskResult RunWeatherBuy(TaskContext ctx)
    {
        var r = this.weatherBuy!.Step(ctx);
        this.Status = this.weatherBuy.Status + OwnWorkNote;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        var failed = r == TaskResult.Failed ? this.weatherBuy.FailReason : null;
        this.weatherBuy.Cleanup(ctx);
        this.weatherBuy = null;
        if (this.weatherBuySince is { } since)
            this.weatherBuyTotal += DateTime.UtcNow - since;
        this.weatherBuySince = null;
        var name = CraftPlanner.ItemName(this.weatherBuyItem);

        // 戻りのテレポが終わった：Questionable に頼み直す
        if (this.weatherReturning)
        {
            this.weatherReturning = false;
            if (failed != null)
                ctx.Log.Warn("クエスト", $"{AreaAccess.Name(this.weatherStopTerritory)} へ戻れませんでした（{failed}）。そのまま Questionable に頼み直します");
            this.RestoreAutoFishing(ctx);
            this.started = false;
            return TaskResult.Running;
        }

        // 購入の確認で「いいえ」が押された・マーケットが使えない等は、流れ全体の購入と同じく止める
        if (failed != null)
            return this.Fail($"{name} をマーケットボードで買えませんでした：{failed}");

        var nq = Inventory.Snapshot().CountNq(this.weatherBuyItem);
        if (nq >= this.weatherBuyCount)
        {
            if (this.paths != null && this.weatherFishStep is { } fish
                && QuestTakeOver.AfterFishReady(this.paths, fish.Sequence, this.weatherStopStep, true, includeFishStep: true) is { } rest)
            {
                this.TakeOverAfterFish(ctx, fish, rest, nq);
                return TaskResult.Running;
            }

            ctx.Log.Write("クエスト", $"{name} がそろいました（NQ {nq}／{this.weatherBuyCount}）。Questionable に戻します（釣りの手順は、魚がそろっているので飛ばされます）");
        }
        else
        {
            ctx.Log.Warn("クエスト", $"{name} をマーケットボードで買いきれませんでした（NQ {nq}／{this.weatherBuyCount}）。Questionable に戻し、天気が変わるのを待って釣ります");
        }

        // マーケットのある街へ移っていたら、止めた時点のエリアへ戻ってから頼み直す（経路データはその辺りにいる前提で、テレポを飛ばすことがある）
        if (this.weatherStopTerritory != 0 && GameWeather.CurrentTerritory != this.weatherStopTerritory)
        {
            ctx.Log.Write("クエスト", $"マーケットボードのある街へ移っていたので、{AreaAccess.Name(this.weatherStopTerritory)} へ戻ってから Questionable に頼み直します");
            this.weatherReturning = true;
            this.weatherBuySince = DateTime.UtcNow;
            this.weatherBuy = TestReturnTask?.Invoke(this.weatherStopTerritory) ?? new TeleportTask(this.weatherStopTerritory);
            return TaskResult.Running;
        }

        this.RestoreAutoFishing(ctx);
        this.started = false; // Questionable に頼み直す
        return TaskResult.Running;
    }

    /// <summary>
    /// 釣りの手順の魚がもうそろっているのに、Questionable がまだその手前（同じ段の、店で買う・歩く手順）にいるなら、Questionable を止めて、
    /// 残りの手順（報告）をこちらで行う（QuestTakeOver.AfterFishReady。止まった状態から始め直したときも、ここで先へ進める）。行ったら true。
    /// </summary>
    private bool HandleFishReady(TaskContext ctx)
    {
        var step = ctx.Questionable.GetCurrentStepData();
        if (step == null || ctx.Questionable.IsRunning() != true || step.QuestId != this.quest.ShortId.ToString() || this.paths == null)
            return false;
        if (WeatherFishBuy.FishAhead(this.paths, step.Sequence, step.Step) is not { } fish)
            return false;
        var have = Inventory.CountNow(fish.GatherItemId!.Value);
        if (QuestTakeOver.AfterFishReady(this.paths, step.Sequence, step.Step, have >= fish.GatherCount!.Value) is not { } rest)
            return false;
        ctx.Questionable.Stop(Plugin.InternalNameConst);
        this.PauseAutoFishing(ctx);
        this.TakeOverAfterFish(ctx, fish, rest, have);
        return true;
    }

    // こちらが Questionable を止めて自分で動く間の、自動の釣りの止め（不具合の例：漁師 Lv45 で Questionable を止めた後も、
    // AutoHook が有効のまま残り、釣り場で自分で竿を投げ続けて、こちらの移動が始まらなかった。Questionable の釣りの手順は、終わると AutoHook を
    // 「手順を始めたときの状態」に戻す。この実行では始めから有効だった）
    private bool? autoHookBefore;

    // 製作の手順で止めて残りをこちらで行うとき、Artisan の製作が終わるのを待ち始めた時刻（ArtisanDrain）
    private DateTime? ownDrainSince;

    // たどり着けないエリアを待ち続ける形の見張り（StuckElsewhere）：運んでいる作業・動かずにいる位置と時刻・運んだ回数・知らせた印
    private AutoTask? relocate;
    private System.Numerics.Vector3 stillAt;
    private DateTime? stillSince;
    private string stillStep = string.Empty;
    private int relocations;

    // 手順を終えたまま段が変わらない形の見張り（StepDoneStall）：数え始めた時刻・そのときの段・立て直した回数・降り始めた時刻・最後に降りる操作を送った時刻
    private DateTime? stallSince;
    private byte stallSeq;
    private int stallRecoveries;
    private DateTime? stallDismountSince;
    private DateTime stallDismountSentAt = DateTime.MinValue;

    /// <summary>検証の仕組み用：設定すると、降りる操作（一般アクション 23）の代わりにこれを呼ぶ。本番では null のまま。</summary>
    public static Func<bool>? TestDismount { get; set; }

    // 釣りの位置で投げられないときの立ち位置の合わせ直し（HandleCastFacing）：投げられない状態が始まった時刻・試す立ち位置・いま試している番号・
    // 移動中か・段階の時刻・あきらめを知らせたか
    private DateTime? castBlockedSince;
    private List<Data.CastSpot>? castSpots;
    private int castIndex = -1;
    private bool castMoving;
    private DateTime castPhaseAt = DateTime.MinValue;
    private bool castGaveUpNoted;

    /// <summary>投げる行動（Action 289「キャスティング」。GBR・AutoHook と同じ番号）。</summary>
    public const uint CastAction = 289;

    /// <summary>検証の仕組み用：設定すると、投げる行動の状態をこれで読む。本番では null のまま（偽物のゲームでは、設定しなければ見ない）。</summary>
    public static Func<uint>? TestCastStatus { get; set; }

    /// <summary>検証の仕組み用：向きの変更。</summary>
    public static Action<float>? TestSetRotation { get; set; }

    /// <summary>
    /// Questionable の釣りの手順の位置（10m以内）に、動ける・乗っていない・構えていない状態で着いたのに、投げる行動が2秒使えないままなら、
    /// GBR の釣りの記録（実際に投げた立ち位置と向き：Data.FishCastSpots）のうち、手順の位置に近いものへ順に移り、記録の向きに合わせて試す（最大5か所）。
    /// 投げられるようになったら止める（Questionable の釣りの手順は1秒おきに /ahstart をやり直すので、そのまま投げる）。
    /// 不具合の例：漁師 Lv58（2088）で、Questionable の釣りの位置（445.4,-31.7,222.6）では投げる行動が使えず（状態 1128）、
    /// その場で向きを一周させても使えなかった（その場で回すのはやめた）。
    /// 約1m先（446.1,-31.5,222.2）へ動いて谷間へ向いたら投げられた。ゲームは立ち位置と向きで投げられるかを決め、GBR は記録の位置へ動いて向きを合わせる。
    /// </summary>
    private void HandleCastFacing(TaskContext ctx)
    {
        if (GameUi.TestBackend != null && TestCastStatus == null)
            return;

        var step = ctx.Questionable.GetCurrentStepData();
        var c = Svc.Condition;
        var target = step?.Position;
        if (step == null || step.InteractionType != "Fish" || step.QuestId != this.quest.ShortId.ToString() || target == null
            || System.Numerics.Vector3.Distance(Me.Position, target.Value) > 10f
            || ctx.Questionable.IsRunning() != true || Jobs.CurrentClassJob != SpearfishTask.Fisher
            || c[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted] || c[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight]
            || c[Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering] || GameUi.BetweenAreas)
        {
            this.ResetCastSpot(ctx);
            return;
        }

        var status = TestCastStatus?.Invoke() ?? GameUi.ActionStatus(CastAction);
        var now = DateTime.UtcNow;
        if (status == 0)
        {
            if (this.castIndex >= 0 && this.castSpots is { } tried && this.castIndex < tried.Count)
                ctx.Log.Write("クエスト", $"記録の立ち位置 {this.castIndex + 1} か所目（{Fmt(tried[this.castIndex].Position)}）に移って向きを合わせたら、投げられるようになりました"
                                       + "（Questionable が次のやり直しで投げます）");
            this.ResetCastSpot(ctx);
            return;
        }

        this.castBlockedSince ??= now;
        if (now - this.castBlockedSince.Value < TimeSpan.FromSeconds(2))
            return;

        // 移動中：着いたら（0.3m以内・止まった・10秒）記録の向きに合わせ、1秒待って状態を見る
        if (this.castMoving)
        {
            var spot = this.castSpots![this.castIndex];
            if (System.Numerics.Vector3.Distance(Me.Position, spot.Position) > 0.3f && ctx.Navmesh.IsMoving() && now - this.castPhaseAt < TimeSpan.FromSeconds(10))
                return;
            if (ctx.Navmesh.IsMoving())
                ctx.Navmesh.Stop();
            this.castMoving = false;
            this.castPhaseAt = now;
            if (TestSetRotation is { } set)
                set(spot.Rotation);
            else
                GameUi.SetPlayerRotation(spot.Rotation);
            return;
        }

        if (this.castIndex >= 0 && now - this.castPhaseAt < TimeSpan.FromSeconds(1))
            return;

        // 試す立ち位置を決める（記録は裏で読む。読み終わるまで待つ）
        if (this.castSpots == null)
        {
            if (Data.FishCastSpots.All is not { } all)
                return;
            this.castSpots = Data.FishCastSpots.Near(all, target.Value);
            ctx.Log.Write("クエスト", $"釣りの位置に着きましたが、投げられません（投げる行動の状態 {status}）。ゲームは立ち位置と向きで投げられるかを決めます。"
                                   + $"GBR の釣りの記録で実際に投げられた立ち位置のうち、手順の位置に近い {this.castSpots.Count} か所へ順に移り、記録の向きに合わせて試します"
                                   + (Data.FishCastSpots.LoadError is { } err ? $"（記録を読めませんでした：{err}）" : string.Empty));
        }

        if (++this.castIndex >= this.castSpots.Count)
        {
            if (!this.castGaveUpNoted)
            {
                this.castGaveUpNoted = true;
                ctx.Log.Warn("クエスト", $"記録の立ち位置 {this.castSpots.Count} か所のどこでも投げられませんでした（投げる行動の状態 {status}）。"
                                        + "釣り場の水辺へ少し動いて水の方を向いてください（Questionable がそのまま投げます）");
            }

            return;
        }

        var next = this.castSpots[this.castIndex];
        ctx.Navmesh.MoveCloseTo(next.Position, false, 0.1f);
        this.castMoving = true;
        this.castPhaseAt = now;
    }

    private static string Fmt(System.Numerics.Vector3 v) => $"{v.X:0.0},{v.Y:0.0},{v.Z:0.0}";

    /// <summary>立ち位置の合わせ直しを初めに戻す（こちらが動かしていれば止める）。</summary>
    private void ResetCastSpot(TaskContext ctx)
    {
        if (this.castMoving && ctx.Navmesh.IsMoving())
            ctx.Navmesh.Stop();
        this.castMoving = false;
        this.castBlockedSince = null;
        this.castSpots = null;
        this.castIndex = -1;
    }

    // 飛んだまま話しかけて断られたときの着地：最後に着地の操作を送った時刻・知らせたか
    private DateTime landSentAt = DateTime.MinValue;
    private bool landNoted;

    /// <summary>
    /// Questionable が動いている間に、ゲームが「飛行中のため、その操作はできません」と断り、まだ飛んでいたら、着地させる
    /// （一般アクション 23「降りる」。飛んでいるときは着地になる＝Questionable の LandExecutor と同じ操作。1秒おき）。
    /// 原因（Questionable のソース）：Questionable は経路の手順に「飛んで行く」（Fly）と「着いたら着地」（Land）の両方があるときだけ着地する（MoveTo.cs）。
    /// 漁師 Lv58（2088）の段 3（モグックに話しかける）は Fly だけで Land が無く、飛んだまま話しかけ続けて断られた。
    /// Questionable の話しかけは断られても0.5秒おきにやり直すので、着地すれば次のやり直しで話しかけられる（地上なら乗ったままでも話しかけられる）。
    /// </summary>
    private void HandleFlyingRefusal(TaskContext ctx)
    {
        if (DateTime.UtcNow - ChatRecorder.LastFlyingRefusal > TimeSpan.FromSeconds(3)
            || !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight]
            || ctx.Questionable.IsRunning() != true || DateTime.UtcNow - this.landSentAt < TimeSpan.FromSeconds(1))
            return;

        // 話しかける相手のそば（水平10m以内）にいるときだけ（移動の途中で着地させて、Questionable の飛行を崩さない）。
        // 手順が無い（終えたことにした）ときは、断られた直後なので着地させる
        var step = ctx.Questionable.GetCurrentStepData();
        if (step != null && (step.QuestId != this.quest.ShortId.ToString() || step.Position is not { } target
                             || System.Numerics.Vector2.Distance(new(Me.Position.X, Me.Position.Z), new(target.X, target.Z)) > 10f))
            return;

        this.landSentAt = DateTime.UtcNow;
        if (!this.landNoted)
        {
            this.landNoted = true;
            ctx.Log.Write("クエスト", "Questionable が飛んだまま話しかけて、ゲームに「飛行中のため、その操作はできません」と断られました"
                                   + "（経路の手順に着地の指定が無い）。こちらで着地させます（一般アクション 23「降りる」）");
        }

        if (TestDismount is { } test)
            test();
        else
            GameUi.UseGeneralAction(23);
    }

    /// <summary>
    /// Questionable が動いていて、手順が無い（終えた）のに、ゲームの段が変わらず、動ける状態が続いたら（StepDoneStall）、Questionable を止めて
    /// 降りてから頼み直す。止めたら Running、上限なら Failed、当てはまらなければ null。会話・カットシーン・エリア移動の間は数えない。
    /// </summary>
    private TaskResult? WatchStepDoneStall(TaskContext ctx, QuestionableIpc.StepData? stepData)
    {
        var seq = GameMemory.QuestSequence(this.quest.RowId);
        if (stepData != null || GameMemory.QuestAccepted(this.quest.RowId) != true || !GameUi.PlayerFree() || GameUi.BetweenAreas
            || this.stallSince == null || seq != this.stallSeq)
        {
            this.stallSince = stepData == null && GameMemory.QuestAccepted(this.quest.RowId) == true && GameUi.PlayerFree() && !GameUi.BetweenAreas
                ? DateTime.UtcNow
                : null;
            this.stallSeq = seq;
            return null;
        }

        switch (StepDoneStall.Decide(DateTime.UtcNow - this.stallSince.Value, this.stallRecoveries))
        {
            case StepDoneStall.Verdict.Recover:
                this.stallRecoveries++;
                this.stallSince = null;
                ctx.Questionable.Stop(Plugin.InternalNameConst);
                this.stallDismountSince = DateTime.UtcNow;
                ctx.Log.Warn("クエスト", $"Questionable が段 {seq} の手順を終えたことにしたまま、段が {StepDoneStall.Still.TotalSeconds:0} 秒変わりません"
                                        + "（飛んだまま話しかけてゲームに断られたのを、Questionable が成功と取り違えた形）。"
                                        + $"Questionable を止め、{(GameUi.Mounted ? "降りてから" : string.Empty)}頼み直します（{this.stallRecoveries} 回目）");
                this.NextPhase("手順を終えたまま止まった Questionable を立て直します");
                return TaskResult.Running;
            case StepDoneStall.Verdict.GiveUp:
                return this.Fail($"Questionable が段 {seq} の手順を終えたことにしたまま段が変わらず、{StepDoneStall.MaxRecoveries} 回頼み直しても続きます。"
                                 + "Questionable の画面の「ステップ」で手順を確かめ、手で進めてから再開してください（続きから進みます）");
            default:
                return null;
        }
    }

    /// <summary>止めた後、乗っていれば降りる（1秒おきに降りる操作。飛んでいれば1回目で着地する）。降りたら（15秒で降りられなくても）頼み直す。</summary>
    private TaskResult RunStallRecovery(TaskContext ctx)
    {
        if (GameUi.Mounted && DateTime.UtcNow - this.stallDismountSince!.Value < TimeSpan.FromSeconds(15))
        {
            if (DateTime.UtcNow - this.stallDismountSentAt >= TimeSpan.FromSeconds(1))
            {
                this.stallDismountSentAt = DateTime.UtcNow;
                if (TestDismount is { } test)
                    test();
                else
                    GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23。Questionable の Unmount と同じ）
            }

            this.Status = "マウントから降りています" + OwnWorkNote;
            return TaskResult.Running;
        }

        if (GameUi.Mounted)
            ctx.Log.Warn("クエスト", "15秒たってもマウントから降りられませんでした。そのまま Questionable に頼み直します");
        this.stallDismountSince = null;
        this.started = false; // Questionable に頼み直す（段の頭＝話しかける手順からやり直す）
        return TaskResult.Running;
    }
    private bool relocateLimitNoted;
    private DateTime quitSentAt = DateTime.MinValue;

    /// <summary>釣りの構えを解く行動（Action 299「中断」。Questionable の EAction.FSHQuit と同じ）。</summary>
    public const uint FishingQuitAction = 299;

    /// <summary>検証の仕組み用：設定すると、行動を使う代わりにこれを呼ぶ。本番では null のまま。</summary>
    public static Func<uint, bool>? TestUseAction { get; set; }

    /// <summary>AutoHook が有効なら一時的に無効にする（元の状態は覚えておき、RestoreAutoFishing で戻す）。</summary>
    private void PauseAutoFishing(TaskContext ctx)
    {
        if (this.autoHookBefore != null || ctx.AutoHook.GetPluginState() != true)
            return;
        this.autoHookBefore = true;
        ctx.AutoHook.SetPluginState(false);
        ctx.Log.Write("クエスト", "こちらで動く間は、AutoHook を一時的に無効にします（有効のままだと、釣り場で自分で竿を投げ続けるため。Questionable に戻すときに元に戻します）");
    }

    /// <summary>釣りの構え（採集の状態）のままなら、構えを解く（1秒おき。解けたかは状態で見る）。</summary>
    private void KeepFishingStopped()
    {
        // 刺突漁では「中断」で中止の確認（Addon 12683）が出る。こちらが中断を送った後に出たものだけ「はい」を押し、確認が出ている間は中断を送らない
        // （不具合の例：漁師 Lv68 で大方士がそろった後、確認が出ている間にも1秒おきに中断を送り、確認が閉じては開くのを2秒おきにくり返した）
        var now = DateTime.UtcNow;
        if (GameUi.YesnoText(out var confirm) is { } body && confirm != null && IsSpearfishQuitConfirm(body))
        {
            if (now - this.quitSentAt < TimeSpan.FromSeconds(5) && now - this.quitConfirmedAt > TimeSpan.FromSeconds(1) && GameUi.ClickYes(confirm))
            {
                this.quitConfirmedAt = now;
                Core.DebugLog.Current?.Line("操作", "刺突漁の中止の確認に「はい」を押しました（こちらが中断を送った後の確認）");
            }

            return;
        }

        if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering] || now - this.quitSentAt < TimeSpan.FromSeconds(1))
            return;
        this.quitSentAt = DateTime.UtcNow;
        if (TestUseAction is { } test)
            test(FishingQuitAction);
        else
            GameUi.UseAction(FishingQuitAction);
    }

    /// <summary>刺突漁の中止の確認の文（Addon 12683「刺突漁を中止しますか？ ※実行中のこの漁場は消えてしまいます。」：ゲームデータで確認）。</summary>
    public const uint SpearfishQuitConfirmAddon = 12683;

    private DateTime quitConfirmedAt = DateTime.MinValue;

    /// <summary>確認の窓の文が、刺突漁の中止の確認か（空白を除いて比べる）。</summary>
    public static bool IsSpearfishQuitConfirm(string body)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().TryGetRow(SpearfishQuitConfirmAddon, out var row)
           && GameUi.Normalize(body) == GameUi.Normalize(row.Text.ExtractText());

    /// <summary>一時的に無効にした AutoHook を元に戻す。</summary>
    private void RestoreAutoFishing(TaskContext ctx)
    {
        if (this.autoHookBefore is not { } before)
            return;
        this.autoHookBefore = null;
        ctx.AutoHook.SetPluginState(before);
        ctx.Log.Write("クエスト", "一時的に無効にした AutoHook を元に戻しました");
    }

    private void TakeOverAfterFish(TaskContext ctx, QuestionableStep fish, List<QuestionableStep> rest, int have)
    {
        this.own = new Queue<QuestionableStep>(rest);
        this.ownFromSeq = fish.Sequence;
        ctx.Log.Write("クエスト", $"{CraftPlanner.ItemName(fish.GatherItemId!.Value)} はもうそろっています（{have}／{fish.GatherCount}）。"
                               + $"Questionable は段 {fish.Sequence} の頭（餌の購入・釣り場への移動）からやり直すので、"
                               + $"釣りの後の残りの手順（{string.Join("→", rest.Select(x => $"{x.Type}（{NpcStepTask.NpcName(x.DataId)}）"))}）をこちらで行います");
        this.NextPhase("釣りの後の手順をこちらで行います");
    }

    private void NoteWeather(TaskContext ctx, string key, string text, bool warn = false)
    {
        if (key == this.lastWeatherNote)
            return;
        this.lastWeatherNote = key;
        if (warn)
            ctx.Log.Warn("クエスト", text);
        else
            ctx.Log.Write("クエスト", text);
    }

    // 完了の後片付け：済んだか・始めた時刻・動ける状態になった時刻・最後に閉じた時刻・閉じた窓
    private bool windDownDone;
    private DateTime? windDownSince;
    private DateTime? windDownFreeSince;
    private DateTime windDownActAt = DateTime.MinValue;
    private readonly List<string> windDownClosed = [];

    /// <summary>
    /// 完了の後、動ける状態がこれだけ続いたら NPC の会話が終わったとみなす（ゲームは完了の約0.05秒後に同じ NPC の会話を続けることがある：
    /// 実機の記録で 0.053 秒・0.054 秒）。検証の仕組みでは短くする。
    /// </summary>
    public static TimeSpan WindDownSettle { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// クエストの完了の直後の後片付け（NpcLeftovers・AfterQuestWindDown の説明）。
    /// 始めに TextAdvance を借りたまま受注だけ切る（手放す前に、NPC が差し出した別のクエストを TextAdvance が受けないように）。
    /// NPC が出した窓は閉じる（受注の窓は受けない・選択肢は選ばない・会話は送る）。動ける状態が続いたら終える。
    /// 上限を過ぎたら記録に残して終え、次の作業の前の見張り（LeftoverWindowWatch）に任せる。
    /// </summary>
    private TaskResult WindDownAfterComplete(TaskContext ctx)
    {
        var now = DateTime.UtcNow;
        if (this.windDownSince == null)
        {
            this.windDownSince = now;
            ctx.TextAdvance.AllowQuestAccept(false);
        }

        var open = NpcLeftovers.Find(out var addon, out var detail);
        var free = GameUi.PlayerFree();
        if (free)
            this.windDownFreeSince ??= now;
        else
            this.windDownFreeSince = null;

        var freeFor = this.windDownFreeSince is { } f ? now - f : TimeSpan.Zero;
        switch (AfterQuestWindDown.Decide(open, free, freeFor, now - this.windDownSince.Value, WindDownSettle))
        {
            case AfterQuestWindDown.Verdict.Close:
                if (now - this.windDownActAt >= TimeSpan.FromMilliseconds(300))
                {
                    this.windDownActAt = now;
                    if (open != NpcLeftovers.Kind.Talk && !this.windDownClosed.Contains(detail))
                        this.windDownClosed.Add(detail);
                    NpcLeftovers.Close(open, addon);
                }

                this.Status = $"完了の後に NPC が出した窓を閉じています（{detail}）";
                return TaskResult.Running;
            case AfterQuestWindDown.Verdict.WaitFree:
            case AfterQuestWindDown.Verdict.Settling:
                this.Status = "完了の後、NPC の会話が終わるのを見ています";
                return TaskResult.Running;
            case AfterQuestWindDown.Verdict.GiveUp:
                ctx.Log.Warn("クエスト", $"完了の後 {AfterQuestWindDown.Limit.TotalSeconds:0} 秒たっても NPC の会話が終わりません"
                                        + $"（{(open == NpcLeftovers.Kind.None ? "開いている NPC の窓はありません" : detail)}）。次の作業の前の見張りに任せます");
                break;
        }

        if (this.windDownClosed.Count > 0)
            ctx.Log.Write("クエスト", $"完了の後に NPC が出した窓を閉じました：{string.Join("／", this.windDownClosed)}"
                                   + "（受注の窓は受けずに断り、選択肢は何も選んでいません。別のクエストを受けないため）");
        return TaskResult.Done;
    }

    // 釣りの手順の餌：付けようとしている餌・付いたのを見たか・送った回数・最後に送った時刻・同じことを2度書かない控え
    private uint baitTarget;
    private bool baitSettled;
    private int baitAttempts;
    private DateTime baitSentAt = DateTime.MinValue;
    private string lastBaitNote = string.Empty;

    /// <summary>付けたのに変わらないとき、次に送るまでの間（付いたかは「今付けている餌」で見る。これは送り直しの間隔。検証の仕組みでは短くする）。</summary>
    public static TimeSpan BaitRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>同じ餌を送っても変わらないとき、諦めるまでの回数（投げ直しの合間ごとに1回ずつ試す）。</summary>
    public const int BaitAttemptLimit = 20;

    /// <summary>
    /// Questionable の今の手順から後に同じ段の釣りの手順があれば、その手順の指定の餌を付けておく（理由は FishBaitPrep）。
    /// 釣りの手順に入った後も、糸を垂らしていない合間（投げ直しの間）に付け替える（先に付けられずに釣り始めたときの立て直し）。
    /// 付いたかは「今付けている餌」が変わったかで確かめる。上限まで送って変わらなければ、記録に残して送るのをやめる。
    /// 手順の餌が付いているのを一度見たら、その餌についてはもう触らない（AutoHook のプリセットが途中で意図して替える餌と取り合わないため）。
    /// </summary>
    private void HandleFishBait(TaskContext ctx)
    {
        var step = ctx.Questionable.GetCurrentStepData();
        if (step == null || ctx.Questionable.IsRunning() != true || step.QuestId != this.quest.ShortId.ToString())
            return;

        var steps = QuestionablePaths.Steps(this.quest.ShortId);
        if (steps == null)
            return;

        var target = FishBaitPrep.TargetBait(steps, step.Sequence, step.Step);
        if ((target ?? 0) != this.baitTarget)
        {
            this.baitTarget = target ?? 0;
            this.baitSettled = false;
            this.baitAttempts = 0;
            this.baitSentAt = DateTime.MinValue;
        }

        // 釣りの手順が無ければ、ゲームの状態は読まない
        if (target is not { } bait || this.baitSettled)
            return;

        var game = GameBait.Current;
        var equipped = game.Equipped;
        var owned = game.Owned(bait);
        var verdict = FishBaitPrep.Decide(bait, equipped, owned, game.Busy);
        var name = CraftPlanner.ItemName(bait);
        switch (verdict)
        {
            case FishBaitPrep.Verdict.AlreadyEquipped:
                // こちらが送って変わったときだけ書く（初めから付いていたときは書かない）
                if (this.baitAttempts > 0)
                    ctx.Log.Write("クエスト", $"釣りの手順の餌を付けました：{name}（{this.baitAttempts} 回目で変わった）");
                this.baitSettled = true;
                return;
            case FishBaitPrep.Verdict.NotOwned:
                this.NoteBait(ctx, $"{bait}:NotOwned", $"釣りの手順の餌（{name}）をまだ持っていません（Questionable が釣りの前に買う手順が無ければ、こちらで買います）");
                return;
            case FishBaitPrep.Verdict.Equip:
                break;
            default:
                return;
        }

        if (this.baitAttempts >= BaitAttemptLimit)
        {
            this.NoteBait(ctx, $"{bait}:GaveUp", $"釣りの手順の餌（{name}）を {BaitAttemptLimit} 回付けようとしましたが、付いている餌が変わりません"
                                                + $"（いま {(equipped == 0 ? "なし" : CraftPlanner.ItemName(equipped))}。これ以上は送りません。釣れないときは手で {name} に替えてください）", warn: true);
            return;
        }

        if (DateTime.UtcNow - this.baitSentAt < BaitRetry)
            return;

        if (this.baitAttempts == 0)
            ctx.Log.Write("クエスト", $"釣りの手順の餌を付けます：{name}（Questionable の手順 {step.Sequence}-{step.Step} から後の釣りの指定。"
                                   + $"いまの餌 {(equipped == 0 ? "なし" : CraftPlanner.ItemName(equipped))}・{name} を {owned} 個所持）");
        game.Equip(bait);
        this.baitAttempts++;
        this.baitSentAt = DateTime.UtcNow;
    }

    private void NoteBait(TaskContext ctx, string key, string text, bool warn = false)
    {
        if (key == this.lastBaitNote)
            return;
        this.lastBaitNote = key;
        if (warn)
            ctx.Log.Warn("クエスト", text);
        else
            ctx.Log.Debug("クエスト", text);
    }

    private string? HandleRequest(TaskContext ctx)
    {
        // 渡した後：求めた品が減ったかを確かめる（渡す操作を送ったことを「納品した」とはしない）
        if (this.submittedAt is { } at)
        {
            var inv = Inventory.Snapshot();
            var dropped = this.countsBeforeSubmit.Where(kv => GameRequestWindow.Current.CountOwned(kv.Key) < kv.Value).ToList();
            if (dropped.Count > 0)
            {
                this.submittedAt = null;
                ctx.Log.Write("納品", $"納品を確かめました（{string.Join("、", dropped.Select(kv => $"{CraftPlanner.ItemName(kv.Key)} {kv.Value}→{GameRequestWindow.Current.CountOwned(kv.Key)}"))}）");
            }
            else if (GameMemory.QuestSequence(this.quest.RowId) is var seqNow && (seqNow != this.seqBeforeSubmit || this.IsComplete))
            {
                // 品は減らないがクエストが進んだ＝見せるだけの納品（不具合の例：漁師 Lv58「モグックにバルーンパファーを見せる」で、
                // 所持数だけを見て「渡せていない可能性」と誤って警告した）
                this.submittedAt = null;
                ctx.Log.Write("納品", $"品は減りませんでしたが、クエストが進みました（見せるだけの納品。段 {this.seqBeforeSubmit}→{seqNow}）");
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
            // 次に開いた窓は新しい窓として扱う。納品入力はTextAdvanceへ委任しない。
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

        if (!ctx.TextAdvance.VerifyTurnInControlNow())
            return $"TextAdvance の操作権を確認できないため、納品窓には入力せず止めました（{ctx.TextAdvance.LossReason ?? "理由を読めません"}）";

        var result = this.filler.Tick(GameRequestWindow.Current, (nint)request, openedAt, this.questItems, out var detail);
        if (this.filler.MateriaNote is { } note && !this.materiaNoteLogged)
        {
            this.materiaNoteLogged = true;
            ctx.Log.Write("納品", note);
        }

        switch (result)
        {
            case RequestFiller.Outcome.Submitted:
                this.submittedAt = DateTime.UtcNow;
                this.submittedInSequence = GameMemory.QuestSequence(this.quest.RowId);
                this.countsBeforeSubmit = new Dictionary<uint, int>(this.filler.CountsBeforeSubmit);
                this.seqBeforeSubmit = GameMemory.QuestSequence(this.quest.RowId);
                ctx.Log.Write("納品", detail);
                break;
            case RequestFiller.Outcome.NotOurs:
                return $"{detail}。この納品窓は、このクエストの品と照合できないので自動では入れません。手で渡すか窓を閉じてから、もう一度開始してください（続きから進みます）";
            case RequestFiller.Outcome.Failed:
                return detail;
            case RequestFiller.Outcome.Busy:
                this.Status = "納品窓で別の操作が選択中なので待っています";
                break;
        }

        return null;
    }

    /// <summary>Lv1 の受注をこちらで行う前の準備（着替え → 受注前の確かめ）。済めば受注の手順をこちらの手順に積む。</summary>
    private TaskResult PrepareOwnAccept(TaskContext ctx)
    {
        if (this.lv1Equip != null)
        {
            var r = this.lv1Equip.Step(ctx);
            this.Status = this.lv1Equip.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.lv1Equip.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.lv1Equip.FailReason : null;
            this.lv1Equip = null;
            if (failed != null)
                return this.Fail(failed);
        }

        var ready = this.starter.ReadyForOwnAccept(ctx, out var fail, out var status);
        if (status.Length > 0)
            this.Status = status;
        if (ready == TaskResult.Failed)
            return this.Fail(fail ?? "受注前の確かめで止めました");
        if (ready == TaskResult.Running)
            return TaskResult.Running;

        this.own = new Queue<QuestionableStep>([this.lv1Accept!]);
        this.ownFromSeq = 0;
        this.lv1Accept = null;
        this.NextPhase("受注だけこちらで行います");
        return TaskResult.Running;
    }

    /// <summary>受注後の品の製作（QuestCraftTask）を進める。終われば Questionable に戻す（製作手順は、作った品を見て飛ばす）。</summary>
    /// <summary>
    /// 自動の刺突漁を進める。そろったら、手で行う手順の後（渡すところ）からこちらで行う。
    /// 集めきれなかったら、理由を出して手で行う形に戻す（Questionable に頼み直すと、手で行う手順で待つ）。
    /// </summary>
    private TaskResult RunSpearfish(TaskContext ctx)
    {
        var r = this.spearfish!.Step(ctx);
        this.Status = this.spearfish.Status + OwnWorkNote;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        this.spearfish.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.spearfish.FailReason : null;
        this.spearfish = null;
        if (this.spearfishSince is { } since)
            this.spearfishTotal += DateTime.UtcNow - since;
        this.spearfishSince = null;

        var wanted = QuestTakeOver.InstructionItems(this.quest, this.instructionSeq);
        var inv = Inventory.Snapshot();
        var ready = wanted.Count > 0 && wanted.All(w => (w.Hq ? inv.CountHq(w.ItemId) : inv.CountAll(w.ItemId)) >= w.Count);
        if (ready && this.paths != null
            && QuestTakeOver.AfterInstruction(this.paths, this.instructionSeq, this.instructionStep, "Instruction", true) is { } rest)
        {
            this.own = new Queue<QuestionableStep>(rest);
            this.ownFromSeq = this.instructionSeq;
            ctx.Log.Write("クエスト", $"刺突漁で品がそろったので、段 {this.instructionSeq} の残り（{string.Join("→", rest.Select(x => $"{x.Type}（{NpcStepTask.NpcName(x.DataId)}）"))}）をこちらで行います");
            this.NextPhase("刺突漁の後をこちらで行います");
            return TaskResult.Running;
        }

        var msg = $"{Jobs.Name(this.quest.ClassJobId)} {this.quest} の刺突漁を自動では集めきれませんでした（{failed ?? "品がそろいません"}）。"
                  + "Questionable の画面の手順を手で行ってください。品がそろったら、渡すところからこちらで続けます";
        ctx.Log.Warn("クエスト", msg);
        Svc.Chat.Print($"[AutoJobQuest] {msg}");
        this.manualWaitNotified = true; // 手で行う知らせは上で出した
        this.started = false;           // Questionable に頼み直す（手で行う手順で待つ）
        return TaskResult.Running;
    }

    private TaskResult RunQuestCraft(TaskContext ctx)
    {
        var r = this.questCraft!.Step(ctx);
        this.Status = this.questCraft.Status + OwnWorkNote;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        var failed = r == TaskResult.Failed ? this.questCraft.FailReason : null;
        this.questCraft.Cleanup(ctx);
        this.questCraft = null;
        if (this.questCraftSince is { } craftSince)
            this.questCraftTotal += DateTime.UtcNow - craftSince;
        this.questCraftSince = null;
        if (failed != null)
            return this.Fail(failed);

        this.started = false;
        this.notRunningFrames = 0;
        this.NextPhase("Questionable に戻します");
        return TaskResult.Running;
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
            this.Status = this.ownTask.Status + OwnWorkNote;
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
                case "AcceptQuest":
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
            this.RestoreAutoFishing(ctx);
            this.started = false;
            this.notRunningFrames = 0;
            return TaskResult.Running;
        }

        return this.Fail($"段 {this.ownFromSeq} の残りの手順をこちらで行いましたが、段が進みません（相手との会話が進まなかった可能性）");
    }

    /// <summary>報告だけを自前で行う（NPC の前まで移動して話しかける。会話と納品は TextAdvance）。</summary>
    // 報告先に話しかける前に降りる（GameUi.DismountBeforeInteract）
    private DateTime? turnInDismountSince;
    private DateTime turnInDismountSentAt = DateTime.MinValue;

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

        if (QuestMenuChoice.Handle(ctx, this.quest.RowId, this.manualInteractionAt, ref this.selectedMenu, out var menuFailure))
        {
            if (menuFailure != null)
                return this.Fail(menuFailure);
            this.Status = "クエストの選択肢を処理しています";
            return TaskResult.Running;
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

        if (GameUi.DismountBeforeInteract(ref this.turnInDismountSince, ref this.turnInDismountSentAt))
        {
            this.Status = "報告先に話しかける前に、マウントから降りています";
            return TaskResult.Running;
        }

        if (this.interactions >= MaxInteractions)
            return this.Fail($"報告先の {npc.Name} に {MaxInteractions} 回話しかけても、クエストが完了しません");
        if (++this.interactAttempts > MaxInteractions * 3)
            return this.Fail($"報告先の {npc.Name} に話しかけられません（{this.interactAttempts - 1} 回試して受け付けられませんでした）。距離・壁・高低差を確認してください");

        // 着いた後は視線判定なし（Questionable と同じ）。話しかけた回数は受け付けられたときだけ数える
        // （ターゲットを合わせただけの呼び出しを数えると、上限の文言が実際とずれた）
        this.interactedAt = DateTime.UtcNow;
        if (GameUi.Interact(npc))
        {
            this.interactions++;
            this.manualInteractionAt = this.interactedAt;
            this.selectedMenu = null;
        }
        this.Status = $"{npc.Name} に話しかけました（{this.interactions} 回目）";
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.moving?.Cleanup(ctx);
        this.moving = null;
        this.ownTask?.Cleanup(ctx);
        this.ownTask = null;
        this.lv1Equip?.Cleanup(ctx);
        this.lv1Equip = null;
        this.reequip?.Cleanup(ctx);
        this.reequip = null;
        if (this.gearChangedByQuestionable)
        {
            // 途中で止まった。装備が推奨装備のままなので、利用者に着直してもらう
            var msg = $"Questionable が {Jobs.Name(this.quest.ClassJobId)} の装備を推奨装備に替えたまま止まりました。ギアセットで着替え直してください";
            ctx.Log.Warn("クエスト", msg);
            Svc.Chat.Print($"[AutoJobQuest] {msg}");
        }
        this.questCraft?.Cleanup(ctx);
        this.spearfish?.Cleanup(ctx);
        this.spearfish = null;
        this.questCraft = null;

        // 自分が始めた進行（または別のクエストへ移ったのを見た進行）がまだ動いていれば止め、Questionable の優先リストを元に戻す
        this.starter.Cleanup(ctx);

        this.relocate?.Cleanup(ctx);
        this.relocate = null;
        this.weatherBuy?.Cleanup(ctx);
        this.weatherBuy = null;
        this.ownBuy?.Cleanup(ctx);
        this.ownBuy = null;
        this.RestoreAutoFishing(ctx);
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
            GameUi.Close(request);
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
