using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;

namespace AutoJobQuest.Automation;

/// <summary>
/// 全体の流れ。
///
///   ① ゲームデータの読み込みを待つ。選んだ職のジョブクエに要るメインクエストが未完了なら始めない
///   ② 事前点検（レベル・装備・プラグイン等）。動作保証外の項目があれば確認窓を出す
///   ②" 区切り（鞄があふれないよう、職ごとに。見積もりが鞄の空きに入らなければ、その職の前から何本かに縮める）。
///      区切りごとに ②r〜⑧ を行い、終わったら次の区切りへ
///   ②r 呼び鈴でリテイナーの在庫を読み、区切りに要る分を引き出す（事前点検と確認の後。何も集め・作らない区切りでは行かない）
///   ②' 機能の解放：マテリア装着（付けるマテリアが残っていれば必須）・精選（霊砂を精選で集める計画なら）が
///      未解放なら、解放クエストを Questionable で進める（前提のメインクエスト等が未完了なら自動では進めない）
///   ③ 秘伝書が要るか調べる（要るなら、紫貨のための収集品の個数を逆算して素材の計画に足す）
///   ④ 素材集め（周回）：マーケット → NPC購入 → マップごとに戦闘→採集 → 残りの採集 → 釣り
///      周回ごとに計画を立て直し、失敗した入手手段は次の周回で別の手段にする
///   ⑤ 秘伝書：クエスト「職人の新たなお仕事」→ 収集品を作る → 納品 → 交換 → 読む
///   ⑥ グリダニアの宿屋で製作（HQ が足りなければ ④ に戻る）
///   ⑦ マテリア装着
///   ⑧ ジョブクエを Questionable で1本ずつ
/// </summary>
public sealed class JobQuestFlow : AutoTask, IOutcomeHint
{
    private enum Stage
    {
        WaitData,
        Preflight,
        WaitPreflightAnswer,
        NextBatch,
        Retainers,
        Unlock,
        WaitSwitchAnswer,
        BookPrep,
        WaitBookData,
        Acquire,
        Books,
        Craft,
        Meld,
        Quests,
        WaitKnockAnswer,
        Knock,
        Done,
    }

    private readonly bool[] selected;
    private Stage stage = Stage.WaitData;

    // 今の区切りのジョブクエ。null の間は選んだ職の残り全部（事前点検で使う）
    private HashSet<uint>? batch;
    private int batchNo;
    private AutoTask? child;
    private int confirmTicket = -1;
    // 周回の上限（素材集め・製作）。決まりは RoundPolicy に1か所でまとめ、ゲームなしで試している（検証の仕組み）。
    // 始めるときに設定から作る
    private RoundPolicy rounds = new(5, 7);

    // 製作の列の打ち切り（材料が予定より少ないレシピに来たら、古い計画のまま進めず、残りを捨てて立て直す）
    private string? craftCut;

    // 製作の列の途中で、HQ 指定の品が HQ にならない回数が上限に届いた（段の終わりにこの理由で止める）
    private string? hqStop;

    // マーケットで買えなかった回数（品目ごと。2回で手段から外す）
    private readonly Dictionary<uint, int> marketFailures = [];

    // 品目ごとの最後の失敗の理由（入手手段が尽きて止めるときに出す。以前は「入手手段が残っていない」だけで、
    // ギルが足りない等の本当の理由は記録の注意の行にしか無かった）
    private readonly Dictionary<uint, string> lastFailure = [];

    // 入手に失敗した手段（品目 → 手段）
    private readonly Dictionary<uint, HashSet<Route>> excluded = [];

    // 品ごとの、戦闘で行って見つからなくなったエリア（次の周回で別のエリアにする：CombatTask.MovedOn）
    private readonly Dictionary<uint, HashSet<uint>> combatTried = [];

    // 時限の採集点を待つので後回しにしたクエストと、採れるようになる時刻（UTC。QuestTask.DeferredUntil）
    private readonly Dictionary<uint, DateTime> deferredQuests = [];

    /// <summary>後回しにしたクエストへ、採れるようになる時刻のこれだけ前に戻る（採集点まで移る時間）。</summary>
    public static readonly TimeSpan DeferReturnLead = TimeSpan.FromMinutes(2);

    // 採集・戦闘などで集めきれず、マーケット購入への切り替えを利用者が「はい」と答えた品目
    // マーケットへの切り替えを了承された品と、了承した数の合計（区切りごとに聞き直す。了承した数を超えたら聞き直す）。
    // 比べるのは、この区切りでその品を買いに行った数の合計（以前はその周回の不足数と比べたので、作り直しのたびに聞かずに買った）
    private readonly Dictionary<uint, int> marketSwitchApproved = [];

    // 開始の確認で了承を取った、本来の手段が使えずマーケットに回る品（品 → 数。採集職のレベル不足など、開始の時点で分かっているもの）。
    // 最初の区切りの了承に写す（素材集めの段で同じことを聞き直さない）
    private Dictionary<uint, int> startApproved = [];

    // 受けられないジョブクエの受注の NPC に話しかけて止める段。話しかける相手と、ほかに止まる職
    private BlockedQuest? knockTarget;
    private List<BlockedQuest> knockOthers = [];
    private KnockOnIssuerTask? knockTask;

    // 話しかけて止める段の後始末を済ませた（止めたときに全体の後始末から2回呼ばないように）
    private bool knockCleaned;
    private readonly Dictionary<uint, int> marketSwitchUsed = [];
    private List<(uint Item, int Need)> pendingSwitch = [];

    // 今の周回で作った作業（終わったあとに失敗した品目を集めるため）
    private readonly List<AutoTask> roundTasks = [];

    // 秘伝書
    private Task<BookData>? bookBuild;
    private BookData? books;
    private List<uint> requiredBookItems = [];
    private List<BookOffer> booksToBuy = [];
    private int collectablesNeeded;
    private bool booksDone;
    private int booksRound;

    // 秘伝書の段を終えた後に、未読の秘伝書が要るレシピが出てきたとき、1回だけ秘伝書の段へ戻した
    private bool booksReopened;

    // 秘伝書の段の途中で打ち切った理由（収集品の素材が足りない等）。後の手順は飛ばし、段の終わりで素材集めからやり直す
    private string? booksCut;

    // 戦闘に使えるジョブが無いことを記録に出したか（1回だけ出す）
    private bool noCombatJobLogged;

    // 今の製作の列で頼んだ製作（終わったら HQ 失敗を品目ごとに数える）
    private readonly List<CraftOneTask> craftTasks = [];
    private Ipc.ArtisanHqEstimate.Job? hqCheck;
    private JobQuestPlan? preflightPlan;

    private HqFailureTally hqFailures = new(3);

    // 攻撃されたときの反撃と、こちらの会話ではない会話の窓を閉じる
    private readonly DefenseWatch defense = new();
    private readonly ForeignTalk foreignTalk = new();

    public JobQuestFlow(bool[] selected)
    {
        this.selected = (bool[])selected.Clone();
    }

    public override string Name => "ジョブクエ自動化";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (Jobs.StartProblem(this.selected, Jobs.Level) is { } levelProblem)
            return this.Fail(levelProblem);
        ctx.Rotation.BeginRun();
        MarketBoardTask.SpentThisRun = 0;
        MarketBoardTask.RunApprovedUpTo = Math.Max(0, ctx.Config.ConfirmRunTotalAboveGil);
        Unlocks.GaveUp.Clear();

        // ギアセットの品を守る数は、始めに手持ちにあった分まで
        Inventory.GearsetKeepCap = Inventory.GearsetKeepInBags();
        this.rounds = new RoundPolicy(ctx.Config.MaxRetryRounds + 2, ctx.Config.MaxRetryRounds + 4);
        this.hqFailures = new HqFailureTally(HqLimit(ctx.Config));
        PlanBuilder.QuestCraftSpare = Math.Max(0, ctx.Config.QuestCraftRetryRounds);
        Unlocks.UnlockStagePassed = false;
        ctx.Data.EnsureBuilding();
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        // 全体の終わりの条件は、反撃や会話の窓の処理より前に見る（以前は反撃の間に早く戻るため、
        // 倒された・ログアウトしたことに流れが気づけなかった）
        if (!Svc.ClientState.IsLoggedIn)
            return this.Fail("ログアウトしました。自動動作を止めます");
        if (Svc.Objects.LocalPlayer is { } me && me.IsDead)
            return this.Fail("倒されました。自動動作を止めます");

        // 攻撃されていれば反撃する（その間は次の作業を進めない。反撃を続けられなければ全体を止める）
        switch (this.defense.Tick(ctx, out var defenseStatus))
        {
            case DefenseWatch.Result.Failed:
                return this.Fail($"反撃を続けられません：{defenseStatus}");
            case DefenseWatch.Result.Defending:
                // 子の作業を進めない間は、作業の上限の時間を止める（反撃そのものには別の上限がある）
                WorkClock.Pause(defenseStatus);
                this.Status = defenseStatus;
                return TaskResult.Running;
        }

        // こちらの会話ではない会話の窓は、状況を確かめてから閉じる（その間は次の作業を進めない）。
        // こちらの会話（話しかけた後・手動の報告で TextAdvance を借りている間）は除く
        if (!ctx.InOwnConversation && !ctx.TextAdvance.OwnsControl)
        {
            switch (this.foreignTalk.Tick(ctx, out var talkStatus))
            {
                case ForeignTalk.Result.Failed:
                    return this.Fail(talkStatus);
                case ForeignTalk.Result.Busy:
                    WorkClock.Pause(talkStatus);
                    this.Status = talkStatus;
                    return TaskResult.Running;
            }
        }
        else
        {
            this.foreignTalk.Reset();
        }

        // 中断が終わった（子の作業を進める）
        WorkClock.Resume();

        // 子の作業が動いていればそれを進める
        if (this.child != null)
        {
            var r = this.child.Step(ctx);
            this.Status = $"{this.child.Name}: {this.child.Status}";
            if (r == TaskResult.Running)
                return TaskResult.Running;

            // 子は先に外してから後始末する（後始末が例外を投げても、流れ全体の後始末で2回呼ばない）
            var done = this.child;
            this.child = null;
            var failed = r == TaskResult.Failed ? done.FailReason : null;
            if (done is QuestTask { DeferredUntil: { } until } deferred)
                this.deferredQuests[deferred.QuestRowId] = until;
            done.Cleanup(ctx);
            if (failed != null)
                return this.Fail(failed);

            return this.AfterChild(ctx);
        }

        switch (this.stage)
        {
            case Stage.WaitData:
                // 準備済みを先に見る（作り終えた後の記録で例外が出ても、表はそろっているので止めない）
                if (!ctx.Data.IsReady && ctx.Data.BuildError != null)
                    return this.Fail($"ゲームデータを読めませんでした: {ctx.Data.BuildError}");
                if (!ctx.Data.IsReady)
                {
                    this.Status = "ゲームデータを読み込んでいます";
                    return this.Elapsed > TimeSpan.FromMinutes(3) ? this.Fail("ゲームデータの読み込みが終わりません") : TaskResult.Running;
                }

                // メインクエストが未達でも開始は止めない（進められるところまで進め、受けられないジョブクエの
                // 受注の NPC に話しかけて断られたところで止める。以前は、ここで開始を止めていた）

                this.stage = Stage.Preflight;
                return TaskResult.Running;

            case Stage.Preflight:
                return this.RunPreflight(ctx);

            case Stage.WaitPreflightAnswer:
            {
                var ans = ctx.Confirm.Poll(this.confirmTicket);
                if (ans == null)
                    return TaskResult.Running;
                if (ans == false)
                    return this.Fail("事前点検の確認で「いいえ」が選ばれました");

                // HQ の見込みを計算できなかったとき（注意として確認済み）は、その照合はしない
                if ((this.hqCheck?.Error == null && this.hqCheck?.IsCurrent() != true) || this.preflightPlan == null
                    || PreflightSession.PlanKey(this.preflightPlan) != PreflightSession.PlanKey(this.Plan(ctx)))
                    return this.Fail("確認待ちの間に HQ 計算の条件が変わりました。もう一度開始して点検し直してください");

                // 結果の分からない購入の控えは、利用者が確かめたので消す（確かめる文言は事前点検の項目に出している）
                if (ctx.Config.PendingPurchase is { } pending)
                {
                    ctx.Log.Warn("マーケット", $"前回の購入（{pending.Describe()}）は利用者が確かめたので、控えを消しました");
                    ctx.Config.PendingPurchase = null;
                    ctx.Config.Save();
                }

                this.stage = Stage.NextBatch;
                return TaskResult.Running;
            }

            case Stage.NextBatch:
                return this.StartNextBatch(ctx);

            case Stage.WaitKnockAnswer:
            {
                var ans = ctx.Confirm.Poll(this.confirmTicket);
                if (ans == null)
                    return TaskResult.Running;
                if (ans == false)
                    return this.Fail("開始の確認で「いいえ」が選ばれました（進められるジョブクエはありません）");
                this.stage = Stage.Knock;
                return TaskResult.Running;
            }

            case Stage.Knock:
                return this.RunKnock(ctx);

            case Stage.Retainers:
                return this.StartRetainers(ctx);

            case Stage.Unlock:
                return this.StartUnlock(ctx);

            case Stage.WaitSwitchAnswer:
            {
                var ans = ctx.Confirm.Poll(this.confirmTicket);
                if (ans == null)
                    return TaskResult.Running;
                if (ans == false)
                    return this.Fail("マーケット購入への切り替えの確認で「いいえ」が選ばれました");
                foreach (var (id, need) in this.pendingSwitch)
                    this.marketSwitchApproved[id] = this.marketSwitchUsed.GetValueOrDefault(id) + need;
                ctx.Log.Write("素材", $"マーケット購入への切り替えが了承されました：{string.Join("、", this.pendingSwitch.Select(p => $"{CraftPlanner.ItemName(p.Item)}×{p.Need}"))}");
                this.pendingSwitch = [];
                this.stage = Stage.Acquire;
                return TaskResult.Running;
            }

            case Stage.BookPrep:
                return this.PrepareBooks(ctx);

            case Stage.WaitBookData:
                return this.WaitBookData(ctx);

            case Stage.Acquire:
                return this.StartAcquire(ctx);

            case Stage.Books:
                return this.StartBooks(ctx);

            case Stage.Craft:
                return this.StartCraft(ctx);

            case Stage.Meld:
                return this.StartMeld(ctx);

            case Stage.Quests:
                return this.StartQuests(ctx);

            case Stage.Done:
                return TaskResult.Done;
        }

        return TaskResult.Running;
    }

    private JobQuestPlan Plan(TaskContext ctx) => PlanBuilder.Build(ctx.Data, this.selected, this.excluded, this.batch);

    /// <summary>前提が未達で外したクエストを残して終わったか（結果の分類）。</summary>
    public bool HasExclusions { get; private set; }

    // ------------------------------------------------------------------
    // ② 事前点検

    private TaskResult RunPreflight(TaskContext ctx)
    {
        var plan = this.preflightPlan ??= this.Plan(ctx);
        if (plan.NothingToDo)
        {
            // 残りが全部「前提が未達で進められない」とき。メインクエストに止められる職があれば、確かめてから、その受注の NPC に話しかけて止める。
            // 無ければ、完了ではないので理由を出して止める
            if (plan.Blocked.Count > 0 && this.PickKnock(plan))
            {
                this.confirmTicket = ctx.Confirm.Ask(
                    "開始の確認（進められるジョブクエがありません）",
                    StopPlanText(plan, this.knockTarget) + "\n\n受注の NPC のところへ行き（テレポの費用がかかります）、話しかけて確かめたところで止めます。よいですか？「いいえ」で止めます。");
                this.stage = Stage.WaitKnockAnswer;
                return TaskResult.Running;
            }

            if (plan.Blocked.Count > 0)
                return this.Fail($"進められるジョブクエがありません（前提のクエストが未完了）：{string.Join(" / ", plan.BlockedSummary())}");

            ctx.Log.Write("計画", "選んだジョブのジョブクエは、すべて完了しています");
            this.stage = Stage.Done;
            return TaskResult.Running;
        }

        this.hqCheck ??= Preflight.BeginHq(plan);
        this.hqCheck.Tick();
        if (!this.hqCheck.Complete)
        {
            this.Status = this.hqCheck.Status;
            return TaskResult.Running;
        }

        ctx.Log.Write("計画", $"残りのジョブクエ {plan.RemainingQuests.Count} 本／製作 {plan.Craft.Crafts.Sum(c => c.Crafts)} 回／足りない素材 {plan.Shortfalls.Count()} 品目");
        foreach (var w in plan.Warnings)
            ctx.Log.Warn("計画", w);
        DebugLog.Current?.Block("計画", "計画の詳しい中身（事前点検の時点）", PlanDetail(plan));

        if (PreflightSession.PlanKey(plan) != PreflightSession.PlanKey(this.Plan(ctx)))
            return this.Fail("HQ 計算中に在庫・クエストの計画が変わりました。もう一度開始して点検し直してください");
        var items = Preflight.Run(ctx, plan, this.hqCheck);
        var errors = items.Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            foreach (var e in errors)
                ctx.Log.Warn("点検", e.Text);
            return this.Fail($"事前点検で止めました：{string.Join(" / ", errors.Select(e => e.Text))}");
        }

        var warns = items.Where(i => i.Severity == Severity.Warn).ToList();
        foreach (var w in warns)
            ctx.Log.Warn("点検", w.Text);

        // マーケットボードで買う予定の品（作れる職がいない中間素材・採集職のレベルが届かない素材など）。
        // 本来の手段が使えずマーケットに回る品は、この確認で了承を取る（開始の前に確かめる）
        var sources = ctx.Data.Sources!;
        var toMarket = plan.Shortfalls.Where(r => r.Route == Route.MarketBoard).OrderBy(r => r.ItemId).ToList();
        var marketLines = new List<(string Name, int Need, string? Why)>();
        var approve = new Dictionary<uint, int>();
        foreach (var r in toMarket)
        {
            string? why = null;
            if (plan.Craft.NotCraftable.TryGetValue(r.ItemId, out var notCraftable))
            {
                why = $"作れる職がいません：{notCraftable}";
            }
            else if (PlanBuilder.ChooseRoutes(sources, r.ItemId).FirstOrDefault() != Route.MarketBoard)
            {
                why = this.WhyMarket(ctx, r.ItemId);
                approve[r.ItemId] = r.Shortfall;
            }

            marketLines.Add((CraftPlanner.ItemName(r.ItemId), r.Shortfall, why));
        }

        // 装着するマテリアも並べる（計画の不足には入らず、周回の先頭で別に買う。以前は一覧から漏れ、
        // たいてい一番高い品なのに開始の確認に出なかった。マテリアは本来の手段がマーケットなので、了承の数には入れない）
        foreach (var m in MateriaMarketNeeds(ctx.Config, plan))
            marketLines.Add((m.Label, m.Need, "装着に使うマテリア。出品が無ければその場で止めます"));

        this.startApproved = approve;
        var marketPlan = Preflight.MarketPlanText(marketLines);

        // 進められないジョブクエがあれば、どこで止まるか
        this.PickKnock(plan);
        var stopPlan = plan.Blocked.Count > 0 ? StopPlanText(plan, this.knockTarget) : string.Empty;

        // 毎回必ず確かめる。先頭はマーケットボードの自動購入のリスクの文、続けて買う予定の品・止まる場所・前提と注意
        this.confirmTicket = ctx.Confirm.Ask(
            warns.Count > 0 ? "開始の確認（動作保証外の項目があります）" : "開始の確認",
            Preflight.MarketRiskText(ctx.Config)
            + (marketPlan.Length > 0 ? "\n\n" + marketPlan : string.Empty)
            + (stopPlan.Length > 0 ? "\n\n" + stopPlan : string.Empty)
            + (warns.Count > 0 ? "\n\n【注意】\n" + string.Join("\n", warns.Select(w => "・" + w.Text)) : string.Empty));
        this.stage = Stage.WaitPreflightAnswer;
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ②" 区切り（全11職を選ぶと鞄があふれるので、職ごとに区切って進める）

    /// <summary>
    /// 次の区切りを決める。選んだ職の並び（木工→…→漁師）で、残りのジョブクエがある最初の職を取り、その職の残りを前から順に入れる。
    /// 見積もり（<see cref="BagEstimate"/>）が鞄の空き（設定の「残しておく空き」を除く）に入らなければ、本数を半分ずつに縮める。
    /// 1本でも入らなければ、正しい理由（鞄の空き）を出して止める（以前は時間切れや別の理由で止まった）。
    /// 区切りが変わるたびに、周回の数・秘伝書の段・機能の解放の段をやり直す。
    /// </summary>
    private TaskResult StartNextBatch(TaskContext ctx)
    {
        this.batch = null;
        var all = this.Plan(ctx);
        if (all.NothingToDo)
        {
            // 進められるジョブクエを進め終えた。メインクエストに止められる職があれば、最後にその受注の NPC に話しかけて止める
            // （複数の職が止まるときは、ほかの職を進め終えてから最後に）
            if (this.knockTask == null && this.PickKnock(all))
            {
                this.stage = Stage.Knock;
                return TaskResult.Running;
            }

            // 前提が未達で飛ばしたクエストがあれば、終わりに改めて知らせる。
            // 結果は「前提未達のため一部除外して完了」になる
            var blockedLines = all.BlockedSummary();
            this.HasExclusions = blockedLines.Count > 0;
            foreach (var line in blockedLines)
            {
                ctx.Log.Warn("クエスト", $"前提のクエストが未完了のため進めていません：{line}");
                Svc.Chat.Print($"[AutoJobQuest] 前提のクエストが未完了のため進めていません：{line}");
            }

            this.stage = Stage.Done;
            return TaskResult.Running;
        }

        // 選んだ職のジョブクエをまとめて1回で進める（呼び鈴からまとめて引き出す。
        // 以前は職ごとに区切り、鞄に入るまで本数を縮めていた）。鞄に入らなければ止める（画面の開始ボタンも同じ判定で押せない）
        var free = Inventory.FreeBagSlots();
        var need = BagEstimate.ForPlan(all);
        if (BagEstimate.Shortage(need, free, ctx.Config.KeepFreeBagSlots) > 0)
            return this.Fail(BagEstimate.ShortageText(need, free, ctx.Config.KeepFreeBagSlots) + "（続きから進みます）");
        this.batch = all.RemainingQuests.Select(q => q.RowId).ToHashSet();

        this.batchNo++;
        ctx.Log.Write("計画", $"選んだ職のジョブクエ {all.RemainingQuests.Count} 本をまとめて進めます"
                             + $"（{string.Join("、", all.RemainingQuests.GroupBy(q => q.ClassJobId).Select(g => $"{Jobs.Name(g.Key)} {g.Count()} 本"))}）。"
                             + $"鞄の見積もり {need} 枠・使える空き {free - ctx.Config.KeepFreeBagSlots} 枠");

        // 区切りごとにやり直すもの（周回の数・秘伝書の段・機能の解放の段・製作の打ち切り・HQ の失敗の数）
        this.rounds = new RoundPolicy(ctx.Config.MaxRetryRounds + 2, ctx.Config.MaxRetryRounds + 4);
        this.hqFailures = new HqFailureTally(HqLimit(ctx.Config));
        this.books = null;
        this.bookBuild = null;
        this.requiredBookItems = [];
        this.booksToBuy = [];
        this.collectablesNeeded = 0;
        this.booksDone = false;
        this.booksReopened = false;
        this.booksRound = 0;
        this.booksCut = null;
        this.craftCut = null;
        this.craftTasks.Clear();
        this.marketFailures.Clear();
        this.lastFailure.Clear();
        // マーケットへの切り替えの了承は区切りごと（前の区切りで了承した品でも、この区切りの数で聞き直す）
        this.marketSwitchApproved.Clear();
        this.marketSwitchUsed.Clear();
        foreach (var (id, approvedNeed) in this.startApproved)
            this.marketSwitchApproved[id] = approvedNeed;
        if (this.startApproved.Count > 0)
            ctx.Log.Write("素材", $"開始の確認で了承を取った、マーケットで買う品：{string.Join("、", this.startApproved.Select(p => $"{CraftPlanner.ItemName(p.Key)}×{p.Value}"))}");
        this.startApproved = [];
        Unlocks.UnlockStagePassed = false;
        this.stage = Stage.Retainers;
        return TaskResult.Running;
    }

    /// <summary>
    /// 計画の詳しい中身（記録用。しばらくはデバッグのため詳しい記録を残す）：
    /// 残りのジョブクエ・進められないもの・製作職の状態（作れる職の判定の材料）・作る品（職・レシピのレベル）・作らずに買う品・集める素材と手段。
    /// </summary>
    public static string PlanDetail(JobQuestPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"残りのジョブクエ（{plan.RemainingQuests.Count} 本）：{string.Join("、", plan.RemainingQuests.Select(q => $"{Jobs.Name(q.ClassJobId)} {q}"))}");
        foreach (var b in plan.Blocked)
            sb.AppendLine($"進められない：{Jobs.Name(b.Quest.ClassJobId)} {b.Quest}（{b.Kind}・{b.Reason}）");
        foreach (var job in Jobs.Crafters)
        {
            var gearset = GearCheck.FindGearset(job);
            var low = gearset >= 0 ? GearCheck.LowGearsetSlots(job) : null;
            sb.AppendLine($"製作職：{Jobs.Name(job)} Lv{Jobs.Level(job)}・ギアセット {(gearset >= 0 ? (gearset + 1).ToString() : "無し")}"
                          + (low is { Count: > 0 } ? $"・Lv{GearCheck.RequiredEquipLevel} 未満の欄 {string.Join("・", low)}" : string.Empty));
        }

        foreach (var c in plan.Craft.Crafts)
            sb.AppendLine($"作る：{CraftPlanner.ItemName(c.ItemId)}×{c.Crafts}回（{Jobs.Name(c.ClassJobId)}・レシピ Lv{c.RecipeLevel}"
                          + $"{(c.WantHq ? "・HQ" : string.Empty)}{(c.Reserve ? "・取り置き" : string.Empty)}{(c.SecretRecipeBookId != 0 ? $"・秘伝書 {c.SecretRecipeBookId}" : string.Empty)}）");
        foreach (var (item, why) in plan.Craft.NotCraftable)
            sb.AppendLine($"作らずに買う：{CraftPlanner.ItemName(item)}（{why}）");
        foreach (var r in plan.Raw)
            sb.AppendLine($"素材：{CraftPlanner.ItemName(r.ItemId)} 要る {r.Total}・足りない {r.Shortfall}・手段 {Ui.MainWindow.RouteName(r.Route)}"
                          + (r.Fallbacks.Count > 0 ? $"（次に {string.Join("→", r.Fallbacks.Select(Ui.MainWindow.RouteName))}）" : string.Empty));
        foreach (var m in plan.Materia)
            sb.AppendLine($"マテリア：{CraftPlanner.ItemName(m.TargetItemId)} に {(m.MateriaItemId is { } mid ? CraftPlanner.ItemName(mid) : "任意のマテリア")}"
                          + $"{(m.AlreadyMelded ? "（装着済み）" : string.Empty)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // ②k 受けられないジョブクエの受注の NPC に話しかけて止める

    /// <summary>話しかける相手を決める（メインクエストに止められる職の、進められない最初のジョブクエ）。いなければ false。</summary>
    private bool PickKnock(JobQuestPlan plan)
    {
        this.knockOthers = PlanBuilder.KnockCandidates(plan.Blocked, MainQuestGate.IsMainScenario);
        this.knockTarget = this.knockOthers.FirstOrDefault();
        return this.knockTarget != null;
    }

    /// <summary>開始の確認に出す「進められるところまで」の文。</summary>
    private static string StopPlanText(JobQuestPlan plan, BlockedQuest? knock)
        => "【進められるところまで進めて止まります】\n"
           + string.Join("\n", plan.BlockedSummary().Select(l => "・" + l))
           + (knock != null
               ? $"\n最後に{Jobs.Name(knock.Quest.ClassJobId)}のジョブクエ「{knock.Quest.Name}」（Lv{knock.Quest.Level}）の受注の NPC に話しかけ、断られたところで止まります"
               : "\n受注の NPC には話しかけずに、その手前で止まります（メインクエストの連鎖以外の理由のため）");

    private TaskResult RunKnock(TaskContext ctx)
    {
        if (this.knockTarget == null)
        {
            this.stage = Stage.Done;
            return TaskResult.Running;
        }

        this.knockTask ??= new KnockOnIssuerTask(this.knockTarget);
        var r = this.knockTask.Step(ctx);
        this.Status = $"{this.knockTask.Name}: {this.knockTask.Status}";
        if (r == TaskResult.Running)
            return TaskResult.Running;
        this.knockTask.Cleanup(ctx);
        this.knockCleaned = true;

        var text = KnockOnIssuerTask.StopText(this.knockTarget, this.knockTask.Result, this.knockTask.Detail ?? this.knockTask.FailReason, this.knockOthers);
        ctx.Log.Warn("クエスト", text);
        Svc.Chat.Print($"[AutoJobQuest] {text}");

        // 結果は「前提未達のため一部除外して完了」。止まっているほかの理由も知らせる
        this.HasExclusions = true;
        foreach (var line in this.Plan(ctx).BlockedSummary())
            ctx.Log.Warn("クエスト", $"前提のクエストが未完了のため進めていません：{line}");
        this.stage = Stage.Done;
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ②r リテイナー（開始時にリテイナーの在庫も見る。事前点検と確認の後に行う）

    /// <summary>
    /// 区切りに要る品をリテイナーから引き出す。次のときは呼び鈴へ行かない：
    ///  ・設定で切っている（UseRetainerStock）
    ///  ・この区切りで集める・作る・買う品が何も無い（手持ちで足りている）
    /// </summary>
    private TaskResult StartRetainers(TaskContext ctx)
    {
        if (!ctx.Config.UseRetainerStock)
        {
            ctx.Log.Write("リテイナー", "設定で切っているので、リテイナーの在庫は見ません（手持ちだけで計画します）");
            this.stage = Stage.Unlock;
            return TaskResult.Running;
        }

        var plan = this.Plan(ctx);
        var nothingToGet = plan.Craft.Crafts.Count == 0 && plan.Craft.RawShortfall.Count == 0
                           && MateriaMarketNeeds(ctx.Config, plan).Count == 0 && plan.Craft.LockedBySecretBook.Count == 0;
        if (nothingToGet)
        {
            ctx.Log.Write("リテイナー", "集める・作る品は手持ちで足りているので、呼び鈴へは行きません");
            this.stage = Stage.Unlock;
            return TaskResult.Running;
        }

        this.child = new RetainerStockTask(() => this.Plan(ctx));
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ②' 機能の解放（動かす前に解放済みか確かめ、未解放なら Questionable で解放する）

    private TaskResult StartUnlock(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        var steps = new List<Func<TaskContext, AutoTask?>>();

        // マテリア装着：付けるマテリアが残っていれば必須（解放できなければ止める）
        if (plan.Materia.Any(m => !m.AlreadyMelded) && !Unlocks.IsUnlocked(Unlocks.Meld))
            steps.Add(_ => new UnlockFeatureTask(Unlocks.Meld, required: true));

        // 精選：霊砂などを精選で集められる計画なら試す（解放できなければ、霊砂はマーケットに回す。確認窓あり）
        var sources = ctx.Data.Sources!;
        var wantsReduce = plan.Craft.RawShortfall.Keys.Any(k => sources.Get(k).CanReduce && ReduceTask.UsableSources(sources, k).Count > 0);
        // この実行で解放をあきらめたなら、区切りが変わってもやり直さない（計画でも精選を手段から外している）
        if (wantsReduce && !Unlocks.IsUnlocked(Unlocks.Reduction) && !Unlocks.GaveUp.Contains(Unlocks.Reduction))
            steps.Add(_ => new UnlockFeatureTask(Unlocks.Reduction, required: false));

        if (steps.Count == 0)
        {
            Unlocks.UnlockStagePassed = true;
            this.stage = Stage.BookPrep;
            return TaskResult.Running;
        }

        this.child = new SequenceTask("機能の解放", steps);
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ③ 秘伝書の下準備

    private TaskResult PrepareBooks(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        var tomes = plan.Craft.LockedBySecretBook.Select(c => c.SecretRecipeBookId).Distinct().ToList();
        if (tomes.Count == 0)
        {
            this.booksDone = true;
            this.stage = Stage.Acquire;
            return TaskResult.Running;
        }

        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SecretRecipeBook>();
        var missingItem = tomes.Where(t => !sheet.TryGetRow(t, out var r) || r.Item.RowId == 0).ToList();
        if (missingItem.Count > 0)
            return this.Fail($"秘伝書（SecretRecipeBook {string.Join("・", missingItem)}）のアイテムがゲームデータから引けません");

        var bookItems = tomes.Select(t => sheet.GetRow(t).Item.RowId).ToList();
        this.requiredBookItems = bookItems;
        // 紫貨を稼ぐ収集品は、秘伝書の要る職に合わせて選ぶ（錬金術師なら「収集用のアルケオーニスグリモア」。
        // 設定の品＝木工師のシーダーロングボウと同じ収集品納品の Lv50 の段。以前は設定の品に決め打ちで、木工師 Lv1 のキャラクターが
        // 「入手手段が残っていない素材があります：収集用のシーダーロングボウ×3」で止まった）
        var collectable = ScripCollectable.ChooseFromGame(ctx.Config, plan.Craft.LockedBySecretBook.Select(c => c.ClassJobId), ctx.Data.Planner);
        ctx.Log.Write("秘伝書", $"未読の秘伝書：{string.Join("、", bookItems.Select(CraftPlanner.ItemName))}");
        ctx.Log.Write("秘伝書", $"紫貨が足りなければ作って納品する収集品：{ScripCollectable.Describe(collectable, ctx.Config, ctx.Data.Planner)}");
        this.bookBuild = Task.Run(() => BookData.Build(bookItems, collectable));
        this.stage = Stage.WaitBookData;
        this.NextPhase("秘伝書・収集品のデータを調べています");
        return TaskResult.Running;
    }

    private TaskResult WaitBookData(TaskContext ctx)
    {
        if (this.bookBuild == null || !this.bookBuild.IsCompleted)
        {
            this.Status = "秘伝書・収集品のデータを調べています";
            return this.PhaseElapsed > TimeSpan.FromMinutes(3)
                ? this.Fail("秘伝書・収集品のデータの計算が3分たっても終わりません")
                : TaskResult.Running;
        }

        if (this.bookBuild.IsFaulted)
            return this.Fail($"秘伝書のデータを読めませんでした: {this.bookBuild.Exception?.GetBaseException().Message}");

        this.books = this.bookBuild.Result;
        foreach (var n in this.books.Notes)
            ctx.Log.Warn("秘伝書", n);

        // 秘伝書の流れに欠かせないデータ（交換の店・報酬・通貨・窓口）がそろっているか。欠けていれば素材を集める前に止める
        // 町を選べるかは、StartBooks と同じく「収集品の納品に要るクエスト（この段で自動で進める）」を済んだものとして見る
        // （以前は既定の見方のまま検証し、自動で進められるのに素材集めの前で止まった）
        var townQuest = this.books.RequiredQuest;
        bool QuestDone(uint q) => FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q) || (q != 0 && q == townQuest);
        var problems = this.books.Validate(this.requiredBookItems, townAvailable: () => this.books.ChooseTown(questDone: QuestDone) != null, questDone: QuestDone);
        if (problems.Count > 0)
            return this.Fail($"秘伝書のデータが足りないため始めません：{string.Join(" / ", problems)}");

        this.RecomputeBookNeeds(ctx);

        // 収集品の納品には前提のクエスト（職人の新たなお仕事。その前提は蒼天のメインクエスト）が要る。
        // 納品が要るのに前提を自動で進められないなら、素材を集める前に止める（以前は秘伝書の段まで進んでから止まった）。
        // 交換の窓口を開くのにだけ要るときも、ここで見る（StartBooks と同じ条件：UnlockQuestNeeded。以前は納品のときしか見ず、
        // 紫貨が足りていて交換だけのときは、素材を集めた後の秘伝書の段で止まった）
        if (this.UnlockQuestNeeded(this.books.ChooseTown(questDone: QuestDone)) && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(this.books.RequiredQuest))
        {
            Unlocks.ChainToRun(this.books.RequiredQuest, out var blockedBy);
            if (blockedBy != null)
                return this.Fail(UnlockQuestBlocked(this.books.RequiredQuest, blockedBy));
        }

        // 紫貨のための収集品を作れる製作職がいない（どの職の同じ段の品も、レベル・ギアセット・装備が足りない）なら、素材を集める前に止める
        // （以前は素材集めの周回の初めに「入手手段が残っていない素材があります：収集用のシーダーロングボウ×3」とだけ出て、理由が分からなかった）
        if (this.NeedsDelivery() && this.collectablesNeeded > Inventory.CountCollectables(this.books.CollectableItemId, this.books.MinCollectability))
        {
            var ability = CraftAbility.FromGame();
            if (ctx.Data.Planner!.Pick(this.books.CollectableItemId, ability) == null)
                return this.Fail($"紫貨を稼ぐ収集品（収集品納品の、{CraftPlanner.ItemName(ctx.Config.ScripCollectableItemId)} と同じ段の品）を作れる製作職がいません："
                                 + ScripCollectable.WhyNone(ctx.Config, ctx.Data.Planner!, ability));
        }

        // 紫貨のための収集品は、中間素材まで別の職で作る（例：シーダーロングボウ＝木工・鍛冶・裁縫）。
        // その職のギアセットが無ければ、素材を集める前に止める（以前は開始時の点検が
        // 収集品そのものの職しか見ていなかったので、中間素材の段で止まりえた）
        var extra = this.ExtraTargets().ToList();
        if (extra.Count > 0)
        {
            var plan = this.Plan(ctx);
            var craftAll = ctx.Data.Planner!.Build(plan.Targets.Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked, CraftAbility.FromGame());
            if (MissingGearsets(craftAll) is { } missing)
                return this.Fail($"紫貨のための収集品を作る職のギアセットがありません：{missing}");
        }

        this.stage = Stage.Acquire;
        return TaskResult.Running;
    }

    /// <summary>
    /// 秘伝書の残り（交換が要る冊・紫貨の不足・要る収集品の数）を今の所持から計算し直す。
    /// 1個あたりの報酬は、確実に足りるよう「低」で見積もる（シーダーロングボウは 45。最高評価なら 54）。
    /// 収集品は持っていたい総数で覚える（手持ちは計画を立てるたびに1回だけ引く。二重に引かない）。
    /// 秘伝書は最大7冊×各100紫貨＝700で、紫貨の上限（4,000）を超えない（解析ツール jqa bookdata で確認）。
    /// </summary>
    private void RecomputeBookNeeds(TaskContext ctx)
    {
        var b = this.books!;
        var offers = b.Offers.Values.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId)).ToList();
        this.booksToBuy = offers.Where(o => Inventory.CountNow(o.BookItemId) == 0).ToList();

        var price = this.booksToBuy.Sum(o => (int)o.Price);
        var scrips = Inventory.CountSpecialCurrency(b.RewardSpecialCurrencyId, out _);
        var scripNeed = Math.Max(0, price - scrips);
        var held = Inventory.CountCollectables(b.CollectableItemId, b.MinCollectability);
        this.collectablesNeeded = BookMath.CollectablesNeeded(price, scrips, b.RewardLow);

        ctx.Log.Write("秘伝書",
            $"交換 {this.booksToBuy.Count} 冊（紫貨 {price}）、所持 {scrips}、不足 {scripNeed}。"
            + $"{CraftPlanner.ItemName(b.CollectableItemId)} が {this.collectablesNeeded} 個要ります"
            + $"（1個 {b.RewardLow}〜{b.RewardHigh}、手持ちの収集品 {held} 個 → 作るのは {Math.Max(0, this.collectablesNeeded - held)} 個）");
    }

    /// <summary>
    /// 納品で貯めたい紫貨（まだ交換していない秘伝書の値段の合計）。これに届いたら納品をやめる
    /// （以前は必要な紫貨に届いても、手持ちの収集品がある限り納品を続けていた）。
    /// </summary>
    private int ScripTarget()
        => this.books == null
            ? 0
            : BookMath.ScripTarget(this.books.Offers.Values.Select(o =>
                (ExchangeBooksTask.IsLearned(o.TomeId), Inventory.CountNow(o.BookItemId) > 0, (int)o.Price)));

    /// <summary>交換に要る紫貨に届いていない（収集品の納品が要る）か。</summary>
    private bool NeedsDelivery()
        => this.books != null && BookMath.ShouldDeliver(Inventory.CountSpecialCurrency(this.books.RewardSpecialCurrencyId, out _), this.ScripTarget());

    /// <summary>
    /// 収集品の納品に要るクエスト（職人の新たなお仕事：<see cref="BookData.RequiredQuest"/>）を、この段で使うか。
    /// 納品が要るとき、または、交換が要り（まだ読んでいなくて手元にも無い秘伝書がある：交換の手順と同じ条件）、選んだ窓口
    /// （<paramref name="town"/>）の画面を開くのにこのクエストが要るとき（モードゥナ＝67631）。
    /// 以前は交換が要らない（秘伝書を持っていて読むだけ）ときも窓口のために進め、前提のメインクエストが未完了だと止まった。
    /// </summary>
    private bool UnlockQuestNeeded((NpcSpot Collect, NpcSpot Scrip)? town)
    {
        if (this.books is not { RequiredQuest: not 0 } b)
            return false;
        if (this.NeedsDelivery())
            return true;
        var exchangeNeeded = this.booksToBuy.Any(o => !ExchangeBooksTask.IsLearned(o.TomeId) && Inventory.CountNow(o.BookItemId) == 0);
        return exchangeNeeded && town is { } t && t.Scrip.UnlockQuest != 0 && t.Scrip.UnlockQuest == b.RequiredQuest;
    }

    /// <summary>収集品の納品に要るクエストを自動で進められないときの理由（止めるときの文言）。</summary>
    private static string UnlockQuestBlocked(uint quest, string blockedBy)
        => $"紫貨を稼ぐ収集品の納品と、秘伝書を交換する窓口には、クエスト「{Unlocks.QuestName(quest)}」が要りますが、進められません：{blockedBy}";

    /// <summary>計画で使う職のうち、ギアセットの無いもの（「職（品）」の並び）。全部あれば null。</summary>
    private static string? MissingGearsets(CraftPlan plan)
        => CraftCut.MissingGearsets(plan, job => GearCheck.FindGearset(job) >= 0);

    /// <summary>
    /// 素材計画に足す、紫貨のための収集品。
    /// 計画係の所持数（Inventory.Snapshot）は収集品の枠を数えないので、手持ちの収集品はここで引く
    /// （引かないと手持ちの分まで素材を集め、マーケットで要らないギルを使う）。
    /// 作る数＝要る総数 − 納品の下限を満たす手持ち。計画係は同じ品の通常品の手持ちを引くので、その分を足して渡す（StartBooks と同じ式）。
    /// </summary>
    private IEnumerable<QuestItemReq> ExtraTargets()
    {
        if (this.booksDone || this.books == null || this.collectablesNeeded <= 0)
            yield break;

        var held = Inventory.CountCollectables(this.books.CollectableItemId, this.books.MinCollectability);
        var make = Math.Max(0, this.collectablesNeeded - held);
        if (make == 0)
            yield break;

        yield return new QuestItemReq(this.books.CollectableItemId, make + Inventory.CountNow(this.books.CollectableItemId), false, "紫貨のための収集品");
    }

    // ------------------------------------------------------------------
    // ④ 素材集め

    private TaskResult StartAcquire(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        var extra = this.ExtraTargets().ToList();
        var craftAll = plan.Craft;
        if (extra.Count > 0)
            craftAll = ctx.Data.Planner!.Build(plan.Targets.Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked, CraftAbility.FromGame());

        // 紫貨の収集品だけに使う素材（本編のジョブクエの計画に無い素材。作れる職がいなくて買う中間素材を含む）は、
        // 採集できれば採集し、できなければ買う（戦闘・釣り・精選はしない。マーケットへの切り替えの確認も出さない）。
        // 採集できる素材は採集し、採集できず中間素材として作ることもできないときは、
        // ジョブクエを進めるジョブの収集品を作るための素材をマーケットボードで買う。
        // 以前は本編の素材と同じ順（NPC 購入→採集→釣り→戦闘→精選→マーケット）で、アルケオーニスの粗皮・ディープアイの涙・
        // ダイアマイトウェブを戦闘で集めに行き、採集できない素材はマーケットへ切り替える確認を出していた
        var collectOnly = extra.Count == 0
            ? new HashSet<uint>()
            : craftAll.RawTotal.Keys.Where(k => !plan.Craft.RawTotal.ContainsKey(k)).ToHashSet();
        var raw = craftAll.RawShortfall
            .Select(kv => (Item: kv.Key, Need: kv.Value, Routes: this.RoutesFor(ctx, kv.Key, collectOnly.Contains(kv.Key))))
            .ToList();
        var collectRaw = raw.Where(r => collectOnly.Contains(r.Item)).ToList();
        if (collectRaw.Count > 0)
            ctx.Log.Write("素材", $"紫貨の収集品だけに使う素材（採集できれば採集、できなければ購入。戦闘は {ScripCollectable.CombatItemsText(ctx.Config)}）："
                                  + string.Join("、", collectRaw.Select(r => $"{CraftPlanner.ItemName(r.Item)}×{r.Need}（{(r.Routes.Count > 0 ? string.Join("→", r.Routes.Select(Ui.MainWindow.RouteName)) : "手段なし")}）")));

        var marketMateria = MateriaMarketNeeds(ctx.Config, plan);

        // 本来の最初の手段がマーケットでない品目が、マーケットに回ったときは、買う前に利用者に確かめる
        // （時間切れや拒否で、聞かずにギルを使う手段へ切り替えない。精選で集めるはずだった霊砂も同じ）。
        // 誤ってギルを大量に使わないよう、前提が未達で採集・NPC 購入などが使えず
        // マーケットに回る品（採集職のレベルが届かない素材など）も、聞かずに買わない。以前は「前の周回で失敗した」か「精選の品」のときだけだった
        var sourcesIdx = ctx.Data.Sources!;
        var switched = raw
            .Where(r => r.Routes.Count > 0 && r.Routes[0] == Route.MarketBoard
                        && PlanBuilder.ChooseRoutes(sourcesIdx, r.Item).FirstOrDefault() != Route.MarketBoard
                        && !PlanBuilder.TimeLimited(sourcesIdx, r.Item) // 出ている時間で決まる品は、確認を出さずにマーケット
                        && !collectOnly.Contains(r.Item) // 紫貨の収集品だけに使う素材は、採集できなければ買う
                        && !MarketSwitch.Approved(this.marketSwitchApproved, this.marketSwitchUsed, r.Item, r.Need))
            .ToList();
        if (switched.Count > 0)
        {
            this.pendingSwitch = switched.Select(r => (r.Item, r.Need)).ToList();
            var lines = switched.Select(r => $"・{CraftPlanner.ItemName(r.Item)}×{r.Need}（{this.WhyMarket(ctx, r.Item)}）");
            this.confirmTicket = ctx.Confirm.Ask(
                "マーケット購入への切り替えの確認",
                "次の素材は、予定の手段では集めきれませんでした。\n\n" + string.Join("\n", lines)
                + "\n\nマーケットボードで買ってよいですか？「はい」で買います（1回の購入額が基準を超えるときは、あらためて確認します）。「いいえ」で自動動作を止めます。");
            ctx.Log.Warn("素材", "集めきれなかった素材をマーケットで買うか、確認を出しました：" + string.Join(" ", lines));
            this.stage = Stage.WaitSwitchAnswer;
            return TaskResult.Running;
        }

        // 集める物が無ければ（集めきれた）、素材集めの周回を数え直して次の段へ
        if (this.rounds.EnterAcquire(raw.Count > 0 || marketMateria.Count > 0) == RoundPolicy.Verdict.AcquireExceeded)
        {
            var left = string.Join("、", raw.Select(r => $"{CraftPlanner.ItemName(r.Item)}×{r.Need}").Concat(marketMateria.Select(m => m.Label)));
            return this.Fail($"{this.rounds.AcquireLimit} 周続けて集めても足りない素材があります：{left}");
        }

        if (raw.Count == 0 && marketMateria.Count == 0)
        {
            this.stage = this.booksDone ? Stage.Craft : Stage.Books;
            return TaskResult.Running;
        }

        ctx.Log.Write("素材", $"{this.rounds.AcquireRounds}周目：{raw.Count} 品目{(marketMateria.Count > 0 ? $"＋マテリア {marketMateria.Count} 件" : string.Empty)}を集めます");

        var unknown = raw.Where(r => r.Routes.Count == 0).ToList();
        if (unknown.Count > 0)
            return this.Fail($"入手手段が残っていない素材があります：{string.Join("、", unknown.Select(r => $"{CraftPlanner.ItemName(r.Item)}×{r.Need}"
                                                                                               + (this.lastFailure.TryGetValue(r.Item, out var why) ? $"（最後の失敗 {why}）" : string.Empty)))}");

        this.roundTasks.Clear();
        var steps = new List<Func<TaskContext, AutoTask?>>();

        // 1) マーケット（クリスタル・クラスター・霊砂・デミマテリラ・マテリアなど）。
        //    マテリアを先に買う（出品が無ければその場で止めるので、ほかの品にギルを使う前に止まる）
        var inv = Inventory.Snapshot();
        var market = MarketOrder(marketMateria, raw.Where(r => r.Routes[0] == Route.MarketBoard)
            .Select(r => new MarketNeed([r.Item], r.Need, CraftPlanner.ItemName(r.Item), inv.CountAll(r.Item) + r.Need)));

        // 0) 時限の点でしか採れない品で、今その点が出ているもの（手段の順番で採集が先になった品：PlanBuilder.ReorderByTime）。
        //    出ている時間は短い（ET 2〜4 時間＝現実の 6〜12 分）ので、ほかの買い物より先に、1品ずつ採る（
        //    採れるなら採る、採れなければマーケット）。段を始める時に、まだ出ていて間に合うかをもう一度見る。間に合わなければ採らない
        //    （次の周回で、出ていなければマーケットで買う）。上限は出ている時間が終わるまで（＋1分）。集めきれなければ次の周回でマーケット
        //    マーケットという代わりが無い（売買できない・出品が無くて外した）品は、ここに入れず、今までどおり時刻を待って採る
        var timedNow = raw.Where(r => r.Routes[0] == Route.Gather && r.Routes.Contains(Route.MarketBoard) && PlanBuilder.TimedOnly(sourcesIdx.Get(r.Item))).ToList();
        foreach (var t in timedNow)
        {
            var item = t.Item;
            var targetOwned = inv.CountAll(t.Item) + t.Need;
            steps.Add(c =>
            {
                var rem = PlanBuilder.GatherUpRemaining(sourcesIdx.Get(item), AreaAccess.UnlockedNow(), Jobs.Level, GearCheck.HasGearset, EorzeaTime.Hour());
                if (rem < PlanBuilder.MinUpHours)
                {
                    c.Log.Write("素材", $"{CraftPlanner.ItemName(item)} の採集点が出ている時間に間に合わないので、今は採りません（次の周回で、出ていなければマーケットで買います）");
                    return null;
                }

                var limit = TimeSpan.FromSeconds(Math.Min(rem, 24) * EorzeaTime.SecondsPerHour + 60);
                c.Log.Write("素材", $"{CraftPlanner.ItemName(item)} の採集点が出ているので採ります（残り ET {rem:0.#} 時間＝約 {limit.TotalMinutes:0} 分。間に合わなければマーケットで買います）");
                return this.Track(new GatherTask([new GatherNeed(item, targetOwned)], null, $"時限の {CraftPlanner.ItemName(item)}", limit) { TimeBound = true });
            });
        }

        // 切り替えを了承した品は、この周回で買いに行く数を足しておく（了承した数と比べる）
        foreach (var r in raw.Where(r => r.Routes[0] == Route.MarketBoard && PlanBuilder.ChooseRoutes(sourcesIdx, r.Item).FirstOrDefault() != Route.MarketBoard))
            this.marketSwitchUsed[r.Item] = this.marketSwitchUsed.GetValueOrDefault(r.Item) + r.Need;
        if (market.Count > 0)
            steps.Add(c => this.Track(new MarketBoardTask(market, c.MarketWatcher)));

        // 2) NPC 購入（持っていたい総数で渡す。買う数は作業を始めるときの所持数から決まる）
        var vendor = raw.Where(r => r.Routes[0] == Route.Vendor).Select(r => new VendorNeed(r.Item, inv.CountAll(r.Item) + r.Need)).ToList();
        if (vendor.Count > 0)
            steps.Add(_ => this.Track(new VendorTask(vendor)));

        // 3) マップごとに：戦闘 → 同じマップで採れる素材の採集
        var combat = raw.Where(r => r.Routes[0] == Route.Combat).ToDictionary(r => r.Item, r => r.Need);
        var gather = raw.Where(r => r.Routes[0] == Route.Gather && !timedNow.Contains(r)).ToList();
        var gatheredInMap = new HashSet<uint>();

        // 隠し（HIDDEN）の品は、ほかの素材を先に集め、最後の別の作業（6）にまとめる。
        //  ・眼力が使えなければ自然に出るのを待つので時間がかかる。一緒に渡すと、GBR が隠しの品の採集点を回り続けて上限の時間を使い、
        //    ほかの品まで集めきれずにマーケットへ回るおそれがある
        //  ・隠しの品を採る間だけ GBR の「Abandon nodes without needed items」を ON にして、出ていない採集点を採らずに離れ、速く回る
        //    （眼力が使えても、GP が足りない採集点では出ないので同じ）
        var unlockedForHidden = AreaAccess.UnlockedNow();
        var hiddenItems = gather.Where(g => PlanBuilder.HiddenGather(sourcesIdx.Get(g.Item), unlockedForHidden, GatherAbilities.Usable, Jobs.Level, GearCheck.HasGearset) != null).ToList();
        if (combat.Count > 0)
        {
            var combatJob = CombatJobPicker.Pick();
            if (combatJob == null)
                return this.Fail("戦闘に使えるジョブ（ギアセットのある戦闘ジョブ）がありません");

            // 紫貨の収集品の素材など、開始時の点検に入っていなかった戦闘の素材もあるので、周回を始める前にもう一度確かめる
            // （以前は買い物や採集を済ませた後、戦闘の作業の始めで止まっていた）
            if (!ctx.Rotation.IsLoaded)
                return this.Fail($"戦闘で集める素材（{string.Join("、", combat.Select(kv => $"{CraftPlanner.ItemName(kv.Key)}×{kv.Value}"))}）がありますが、RotationSolverReborn が読み込まれていません");

            // 事前点検はリテイナーから引き出す前の計画で行うので、外部ターゲット指定を注意にとどめた場合がある。ここでもう一度確かめる
            if (Ipc.RsrStateReader.ReadTargetFreelyOverride() != false)
                return this.Fail($"戦闘で集める素材（{string.Join("、", combat.Select(kv => $"{CraftPlanner.ItemName(kv.Key)}×{kv.Value}"))}）がありますが、"
                                 + "RSR の外部ターゲット指定が有効、または読めません。指定外を狙わないと確認できるまで戦闘は始められません");

            var combatPlan = CombatPlanner.Plan(ctx.Data.Sources!, combat, out var unreachable, this.combatTried);
            foreach (var id in unreachable)
            {
                // RoutesFor で外しているので通常は来ない。来たら戦闘をあきらめて次の周回で別の手段にする
                ctx.Log.Warn("戦闘", $"{CraftPlanner.ItemName(id)} を落とすモンスターは、行けるエリア（野外・解放済みのエーテライトあり）にいません");
                this.Exclude(id, Route.Combat);
            }

            foreach (var (terr, needs, spots) in combatPlan)
            {
                Vector3? firstSpot = spots.Count > 0 ? MapCoords.ToWorld(terr, spots[0].Spot.X, spots[0].Spot.Y) : null;
                steps.Add(_ => new EquipJobTask(combatJob.Value.ClassJob));
                steps.Add(_ => new TeleportTask(terr, firstSpot));
                steps.Add(_ => this.Track(new CombatTask(terr, needs, spots, TimeSpan.FromMinutes(25))));

                // 同じ採集品が複数のマップで採れても、割り当てるのは最初のマップだけ
                // （複数のマップに同じ不足数で入れると、その数だけ余計に採る）
                var here = gather.Where(g => !gatheredInMap.Contains(g.Item) && !hiddenItems.Contains(g) && ctx.Gbr.GatherableTerritories(g.Item)?.Contains(terr) == true).ToList();
                foreach (var g in here)
                    gatheredInMap.Add(g.Item);
                if (here.Count > 0)
                {
                    steps.Add(_ => this.Track(new GatherTask(
                        here.Select(g => new GatherNeed(g.Item, inv.CountAll(g.Item) + g.Need)), terr,
                        $"{TeleportTask.TerritoryName(terr)} で採集", TimeSpan.FromMinutes(40))));
                }
            }
        }

        // 4) 残りの採集（シャード含む）。GBR が場所とジョブを選ぶ。隠しの品は、最後の別の作業（6）
        var usual = gather.Where(g => !gatheredInMap.Contains(g.Item) && !hiddenItems.Contains(g)).ToList();
        if (usual.Count > 0)
        {
            steps.Add(_ => this.Track(new GatherTask(
                usual.Select(g => new GatherNeed(g.Item, inv.CountAll(g.Item) + g.Need)), null, "採掘・園芸", TimeSpan.FromMinutes(90))));
        }


        // 4.5) 採集→精選（霊砂など。収集品を GBR に採らせて精選で得る）
        foreach (var r in raw.Where(r => r.Routes[0] == Route.Reduce))
        {
            var reduceNeed = new ReduceNeed(r.Item, inv.CountAll(r.Item) + r.Need, ReduceTask.UsableSources(ctx.Data.Sources!, r.Item), r.Routes.Contains(Route.MarketBoard));
            steps.Add(_ => this.Track(new ReduceTask(reduceNeed)));
        }

        // 5) 釣り（GBR に一任）。周回を始める前に、AutoHook が読み込まれているかを確かめる（
        //    作業の始めでももう一度確かめる。GBR の UseAutoHook と釣果送信の同意は、釣りの作業の間だけ GatherTask が ON にする）
        var fish = raw.Where(r => r.Routes[0] == Route.Fish).ToList();
        if (fish.Count > 0)
        {
            var missing = RequiredCapabilities.Fishing(ctx.AutoHook.IsLoaded);
            if (missing.Count > 0)
            {
                return this.Fail(
                    $"釣りで集める素材（{string.Join("、", fish.Select(f => $"{CraftPlanner.ItemName(f.Item)}×{f.Need}"))}）がありますが、釣りを始められません：{string.Join(" / ", missing)}");
            }

            // 釣りのエサは万能ルアー。釣りの直前に数え、全部なくなっていればリムサ・ロミンサのよろず屋で5個買う
            // （段は実行する瞬間に作るので、その時点の所持数で決まる。1〜4個なら買い足さない）
            steps.Add(_ => VersatileLure.PurchaseBefore(Inventory.CountNow(VersatileLure.ItemId)) is { } lure
                ? this.Track(new VendorTask([lure]))
                : null);
            steps.Add(_ => this.Track(new GatherTask(
                fish.Select(f => new GatherNeed(f.Item, inv.CountAll(f.Item) + f.Need)), null, "釣り", TimeSpan.FromMinutes(90), Route.Fish)));
        }

        // 6) 隠し（HIDDEN）の採集物（上の hiddenItems）。ほかの素材を集めた後に、別の作業で速く回って採る。
        //    上限までに集めきれなければ、次の周回で別の手段（マーケット）に回る
        if (hiddenItems.Count > 0)
        {
            ctx.Log.Write("素材", $"隠し（HIDDEN）の採集物は、ほかの素材を集めた後に、出ていない採集点を採らずに離れて速く回って採ります：{string.Join("、", hiddenItems.Select(g => $"{CraftPlanner.ItemName(g.Item)}×{g.Need}"))}");
            steps.Add(_ => this.Track(new GatherTask(
                hiddenItems.Select(g => new GatherNeed(g.Item, inv.CountAll(g.Item) + g.Need)), null, "採掘・園芸（隠しの品）", TimeSpan.FromMinutes(90))
            {
                FastCycle = true,
            }));
        }

        this.child = new SequenceTask($"素材集め {this.rounds.AcquireRounds}周目", steps);
        return TaskResult.Running;
    }

    /// <summary>
    /// マーケットの買い物の並び。装着するマテリア（買えなければ止める品）を先頭にする（出品が無ければ、
    /// ほかの品にギルを使う前に止める。試験できるように分けた）。
    /// </summary>
    public static List<MarketNeed> MarketOrder(IEnumerable<MarketNeed> materia, IEnumerable<MarketNeed> others)
        => materia.Concat(others).ToList();

    /// <summary>本来の手段ではなくマーケットに回った理由（確認窓の文言用）。</summary>
    private string WhyMarket(TaskContext ctx, uint item)
    {
        if (this.excluded.TryGetValue(item, out var bad) && bad.Count > 0)
            return $"{string.Join("・", bad.Select(Ui.MainWindow.RouteName))} で集めきれませんでした";

        var sources = ctx.Data.Sources!;
        var natural = PlanBuilder.ChooseRoutes(sources, item);
        var blockers = PlanBuilder.RouteBlockers(sources.Get(item), natural, FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete, AreaAccess.UnlockedNow(), GatherAbilities.Usable, Jobs.Level, GearCheck.HasGearset);
        if (blockers.Count > 0)
            return string.Join(" / ", blockers.Select(b => $"{Ui.MainWindow.RouteName(b.Route)}は使えません：{b.Reason}"));
        if (sources.Get(item).CanReduce)
            return $"精選が使えません：{(Unlocks.IsUnlocked(Unlocks.Reduction) ? "元の収集品を採れる採集職のレベルが足りないか、収集品採集が使えない" : "精選が未解放")}";
        return $"ほかの手段（{string.Join("・", natural.Where(r => r != Route.MarketBoard).Select(Ui.MainWindow.RouteName))}）が使えません";
    }

    // 使える入手手段（計画の表示と同じ判定：PlanBuilder.AvailableRoutes）。
    // 戦闘に使えるジョブが無ければ、戦闘は手段から外す（次の手段がマーケットなら、買う前に確認窓を出す）。
    // 以前は戦闘が第一の手段の素材があると、そこで止まっていた
    // 紫貨の収集品だけに使う素材（collectableOnly）は、NPC 購入・採集・マーケットだけにする（採集できれば採集、できなければ買う）。
    // 設定で指定した品（ScripCollectableCombatItems：ディープアイの涙）だけは、戦闘でも集める
    private List<Route> RoutesFor(TaskContext ctx, uint item, bool collectableOnly = false)
    {
        var routes = PlanBuilder.AvailableRoutes(ctx.Data.Sources!, item, this.excluded);
        if (collectableOnly)
        {
            var combatOk = ctx.Config.ScripCollectableCombatItems.Contains(item);
            routes.RemoveAll(r => r is Route.Fish or Route.Reduce || (r == Route.Combat && !combatOk));
        }
        if (routes.Contains(Route.Combat) && CombatJobPicker.Pick() == null)
        {
            if (!this.noCombatJobLogged)
            {
                this.noCombatJobLogged = true;
                ctx.Log.Warn("素材", "ギアセットのある戦闘ジョブが無いので、戦闘で集める素材は別の手段にします");
            }

            this.Exclude(item, Route.Combat);
            routes.Remove(Route.Combat);
        }

        return routes;
    }

    /// <summary>
    /// マテリア装着に要るマテリアのうち、カバンに無いもの（マーケットで買う）。
    ///
    /// 「種類不問」（各クラフター Lv20 の納品物）は、設定の品（剛柔のマテリア）を買って付ける。
    /// 付けられない（設定の品がマテリアでない・アイテムLvが高すぎる）ものは買わずに記録し、事前点検で止める。
    /// </summary>
    private static List<MarketNeed> MateriaMarketNeeds(Configuration config, JobQuestPlan plan)
    {
        var list = new List<MarketNeed>();
        var specific = new Dictionary<uint, int>();
        foreach (var m in plan.Materia.Where(m => !m.AlreadyMelded))
        {
            var mid = m.MateriaItemId ?? MateriaCatalog.ResolveAny(config.AnyMateriaItemId, m.TargetItemId, out _);
            if (mid is { } id)
                specific[id] = specific.GetValueOrDefault(id) + 1;
        }

        foreach (var (mid, count) in specific)
        {
            var owned = Inventory.CountNow(mid);
            if (owned < count)
                list.Add(new MarketNeed([mid], count - owned, CraftPlanner.ItemName(mid), count, StopIfUnavailable: true));
        }

        return list;
    }

    private AutoTask Track(AutoTask t)
    {
        this.roundTasks.Add(t);
        return t;
    }

    private void Exclude(uint item, Route route)
    {
        if (!this.excluded.TryGetValue(item, out var set))
            this.excluded[item] = set = [];
        set.Add(route);
    }

    /// <summary>周回の作業から、失敗した品目と手段を集める（次の周回で別の手段にする）。</summary>
    private void CollectFailures(TaskContext ctx)
    {
        foreach (var t in this.roundTasks)
        {
            switch (t)
            {
                case MarketBoardTask m:
                    // マーケットは一時的な理由（混雑・その時点で出品が無い）でも失敗するので、2回失敗するまでは外さない
                    foreach (var id in m.UnfinishedItems)
                    {
                        if (m.UnfinishedReasons.TryGetValue(id, out var why))
                            this.lastFailure[id] = $"マーケット：{why}";
                        var n = this.marketFailures[id] = this.marketFailures.GetValueOrDefault(id) + 1;
                        if (n >= 2)
                            this.Exclude(id, Route.MarketBoard);
                        else
                            ctx.Log.Warn("素材", $"{CraftPlanner.ItemName(id)} はマーケットで買えませんでした（1回目）。次の周回でもう一度マーケットを試します");
                    }

                    break;
                case VendorTask v:
                    foreach (var id in v.Unfinished)
                        this.Exclude(id, Route.Vendor);
                    break;
                case CombatTask c:
                    foreach (var id in c.Unfinished)
                    {
                        // このエリアで見つからなくなってやめた品は、まだ行っていないエリアが残っていれば、次の周回でそちらで続ける（戦闘は外さない）
                        if (c.MovedOn.Contains(id))
                        {
                            if (!this.combatTried.TryGetValue(id, out var tried))
                                this.combatTried[id] = tried = [];
                            tried.Add(c.Territory);
                            if (CombatPlanner.HasReachableSpawn(ctx.Data.Sources!, id, tried))
                            {
                                ctx.Log.Write("素材", $"{CraftPlanner.ItemName(id)} は {TeleportTask.TerritoryName(c.Territory)} で見つからなくなったので、次の周回で別のエリアで集めます");
                                continue;
                            }
                        }

                        this.Exclude(id, Route.Combat);
                    }

                    break;
                case GatherTask g:
                    // 採集と釣りのどちらで失敗したかは、作業の種類で決める（品目の性質で決めると、両方で取れる品で取り違える）。
                    // 時限の品を出ている時間の終わりで打ち切ったときは外さない（次の周回で時刻を見て決め直す）
                    foreach (var id in g.Unfinished)
                    {
                        if (g.TimeBound)
                            ctx.Log.Write("素材", $"{CraftPlanner.ItemName(id)} は採集点の出ている時間のうちに集めきれませんでした。次の周回で、出ていなければマーケットで買います");
                        else
                            this.Exclude(id, g.Route);
                    }

                    break;
                case ReduceTask rt:
                    // 元の収集品の採集点が今どれも出ていなかっただけなら外さない（同上）
                    foreach (var id in rt.Unfinished)
                    {
                        if (!rt.OutOfTime)
                            this.Exclude(id, Route.Reduce);
                    }

                    break;
            }
        }

        this.roundTasks.Clear();
    }

    // ------------------------------------------------------------------
    // ⑤ 秘伝書

    private TaskResult StartBooks(TaskContext ctx)
    {
        if (this.books == null || this.booksDone)
        {
            this.stage = Stage.Craft;
            return TaskResult.Running;
        }

        // 窓口のある町：スクリップ取引窓口の画面を開くクエストが済んでいる窓口を選ぶ。収集品の納品に要るクエスト
        // （b.RequiredQuest＝67631「職人の新たなお仕事」＝モードゥナの窓口を開くクエストでもある）は、この段で進めるので済んだものとして数える
        var townQuest = this.books.RequiredQuest;
        var town = this.books.ChooseTown(questDone: q => FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q) || (q != 0 && q == townQuest));
        if (town == null)
            return this.Fail(this.books.WhyNoTown());

        var steps = new List<Func<TaskContext, AutoTask?>>();
        var b = this.books;
        this.booksCut = null;

        // 収集品の納品に要るクエスト（職人の新たなお仕事）。要るとき（UnlockQuestNeeded）だけ、未完了の前提（同じ区分のもの）ごと進める
        // （以前は紫貨が足りていて納品しないときも進め、前提も進めなかった）
        if (this.UnlockQuestNeeded(town) && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(b.RequiredQuest))
        {
            var chain = Unlocks.ChainToRun(b.RequiredQuest, out var blockedBy);
            if (blockedBy != null)
                return this.Fail(UnlockQuestBlocked(b.RequiredQuest, blockedBy));
            ctx.Log.Write("秘伝書", $"収集品の納品・交換の窓口を開くため、「{string.Join("」→「", chain.Select(Unlocks.QuestName))}」を Questionable で進めます");
            foreach (var id in chain)
                steps.Add(_ => new RunQuestTask(id, Unlocks.QuestName(id)));
        }

        // 交換する秘伝書の店の解放クエスト（「一流の道具」等。自動で進める）。未完了なら、交換の前に前提ごと Questionable で進める。
        // 計画は、自動で進められるときだけ、このジョブクエを止めずに残している（PrereqContext.BookUnlockRunnable）
        var shopQuests = this.booksToBuy.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId))
            .SelectMany(o => o.RequiredQuests ?? [])
            .Where(q => q != 0 && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q))
            .Distinct()
            .ToList();
        var queued = new HashSet<uint>();
        foreach (var sq in shopQuests)
        {
            var chain = Unlocks.ChainToRun(sq, out var shopBlocked);
            if (shopBlocked != null)
                return this.Fail($"秘伝書を交換する店を開くクエスト「{Unlocks.QuestName(sq)}」を進められません：{shopBlocked}");
            ctx.Log.Write("秘伝書", $"秘伝書を交換する店を開くため、「{string.Join("」→「", chain.Select(Unlocks.QuestName))}」を Questionable で進めます");
            foreach (var id in chain.Where(queued.Add))
                steps.Add(_ => new RunQuestTask(id, Unlocks.QuestName(id)));
        }

        if (this.collectablesNeeded > Inventory.CountCollectables(b.CollectableItemId, b.MinCollectability))
        {
            steps.Add(_ => new GoToInnTask());
            steps.Add(c =>
            {
                // 収集品の製作（中間素材から）。この時点の所持数で計画し直す。
                // 作る数＝要る総数 − 納品の下限を満たす手持ち。計画係は同じ品の通常品の手持ち（収集品の枠は数えない）を引くので、その分を足して渡す
                var held = Inventory.CountCollectables(b.CollectableItemId, b.MinCollectability);
                var make = Math.Max(0, this.collectablesNeeded - held);
                if (make == 0)
                    return null;

                var inv = Inventory.Snapshot();
                c.Log.Write("秘伝書", $"{CraftPlanner.ItemName(b.CollectableItemId)} を {make} 個作ります（要る {this.collectablesNeeded}・使える手持ち {held}）");
                var plan = c.Data.Planner!.Build([new QuestItemReq(b.CollectableItemId, make + inv.CountAll(b.CollectableItemId), false, string.Empty)], inv, PlanBuilder.IsBookUnlocked, CraftAbility.FromGame());
                if (plan.RawShortfall.Count > 0)
                {
                    // 素材が足りない（中間素材が予定より少なくできた等）。止めずに、この段の残りを飛ばして素材集めからやり直す
                    // （以前はここで全体を止めていた。やり直しは秘伝書の段の回数の上限で止まる）。
                    // 黙って飛ばすと、後の交換で「紫貨が足りません」という別の理由に見えるので、理由を残す
                    this.booksCut = $"紫貨のための収集品（{CraftPlanner.ItemName(b.CollectableItemId)}）の素材が足りません："
                                    + string.Join("、", plan.RawShortfall.Select(x => $"{CraftPlanner.ItemName(x.Key)}×{x.Value}"));
                    c.Log.Warn("秘伝書", $"{this.booksCut}。この段の残りを飛ばして、素材集めからやり直します");
                    return null;
                }

                if (MissingGearsets(plan) is { } missing)
                    return new StopTask($"紫貨のための収集品を作る職のギアセットがありません：{missing}");

                this.craftCut = null;
                // 最後に製作の構えを解く（以前は本編の製作の列だけに入れていたので、Artisan の ExitCraftStanceEndurance が OFF だと
                // 収集品を作った後に構えのまま納品へ進み、動けないまま上限で止まった）
                return new SequenceTask("収集品の製作", plan.Crafts.Select(pc => (Func<TaskContext, AutoTask?>)(cc => this.NextCraft(cc, pc)))
                    .Append(_ => new ExitCraftStanceTask()));
            });
        }

        // 納品は、交換に要る紫貨に届いたらやめる。すでに足りていれば納品しない
        steps.Add(c =>
        {
            if (this.booksCut != null)
                return null;
            var target = this.ScripTarget();
            var have = Inventory.CountSpecialCurrency(b.RewardSpecialCurrencyId, out _);
            if (have >= target)
            {
                c.Log.Write("秘伝書", $"交換に要る紫貨はもう足りています（{have}/{target}）。収集品は納品しません");
                return null;
            }

            return new DeliverCollectablesTask(b, town.Value.Collect, this.ScripTarget);
        });
        steps.Add(_ =>
        {
            if (this.booksCut != null)
                return null;
            var left = this.booksToBuy.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId) && Inventory.CountNow(o.BookItemId) == 0).ToList();
            return left.Count == 0 ? null : new ExchangeBooksTask(b, town.Value.Scrip, left);
        });
        steps.Add(_ =>
        {
            var toUse = b.Offers.Values.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId) && Inventory.CountNow(o.BookItemId) > 0).ToList();
            return toUse.Count == 0 ? null : new UseBooksTask(toUse);
        });

        this.child = new SequenceTask("秘伝書", steps);
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ⑥ 製作

    private TaskResult StartCraft(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        if (plan.Craft.Crafts.Count == 0)
        {
            // 作る物が全部そろった。後で作り直しが要っても（マテリアの段から戻る等）、そこから数え直す
            this.rounds.CraftsDone();
            this.stage = Stage.Meld;
            return TaskResult.Running;
        }

        if (plan.Craft.RawShortfall.Count > 0)
        {
            // 素材が足りない（製作で NQ ができて作り直しが要る等）→ 集め直す
            this.stage = Stage.Acquire;
            return TaskResult.Running;
        }

        if (plan.Craft.LockedBySecretBook.Count > 0)
        {
            // 秘伝書の段を終えた後に、未読の秘伝書が要るレシピが出てきた（手持ちが減って別の中間素材を作ることになった等）。
            // 1回だけ秘伝書の段へ戻す（以前はここで止めていた）
            var lockedNames = string.Join("、", plan.Craft.LockedBySecretBook.Select(c => CraftPlanner.ItemName(c.ItemId)));
            if (this.booksReopened)
                return this.Fail($"秘伝書の段をやり直しても、秘伝書が未読のため作れない品があります：{lockedNames}");

            this.booksReopened = true;
            this.booksDone = false;
            this.booksRound = 0;
            ctx.Log.Warn("秘伝書", $"秘伝書が未読のため作れない品が出てきたので、秘伝書の段へ戻ります：{lockedNames}");
            this.stage = Stage.BookPrep;
            return TaskResult.Running;
        }

        // 製作が進んだか（残りの製作回数が前の周回より減ったか）。進まない周回（HQ ができない等）が
        // MaxRetryRounds+4 回（既定 7 回）続いたら止める。素材集めの周回はここでは触らない
        // （集めきれたら素材集めの側で数え直すので、HQ の作り直しで材料を集め直しても素材集めの上限には当たらない）
        var remaining = plan.Craft.Crafts.Sum(c => c.Crafts);
        if (this.rounds.EnterCraft(remaining) == RoundPolicy.Verdict.CraftExceeded)
            return this.Fail($"何度作っても納品物がそろいません（HQ ができない等。残りの製作 {remaining} 回のまま {this.rounds.CraftStalled} 周進みませんでした）"
                             + (this.craftCut != null ? $"。直前の打ち切り：{this.craftCut}" : string.Empty));

        if (MissingGearsets(plan.Craft) is { } missing)
            return this.Fail($"製作に使う職のギアセットがありません：{missing}");

        var steps = new List<Func<TaskContext, AutoTask?>> { _ => new GoToInnTask() };
        this.craftCut = null;
        this.hqStop = null;
        foreach (var c in plan.Craft.Crafts)
            steps.Add(cc => this.NextCraft(cc, c));

        // 最後に製作の構えを解く（Artisan の設定 ExitCraftStanceEndurance に頼らない。構えのままだと次の段が動けない）
        steps.Add(_ => new ExitCraftStanceTask());

        this.child = new SequenceTask("グリダニアの宿屋で製作", steps);
        return TaskResult.Running;
    }

    /// <summary>
    /// 製作の列の次の1レシピ。材料が計画どおりにそろっていなければ（前のレシピが予定より少なくできた等）、
    /// 古い計画のまま進めず、この先の列を打ち切る（段が終わったら計画を立て直す）。
    ///
    /// 以前は全レシピを1本の列に積み、終わってから立て直していた。中間素材が予定より
    /// 少なくできると、それを使う後のレシピが1個も作れずに失敗し、素材を集め直す前に全体が止まっていた。
    /// </summary>
    private AutoTask? NextCraft(TaskContext ctx, PlannedCraft c)
    {
        if (this.craftCut != null)
            return null;

        // 次のレシピへ進む前に、終わった製作の HQ を数える。HQ 指定の品が上限まで HQ にならなければ、ここで列を打ち切る
        // （以前は列を全部作り終えてから数えたので、止まるまでに後ろのレシピの材料も使っていた）
        if (this.TallyHqFailures(ctx) is { } stop)
        {
            this.hqStop = stop;
            this.craftCut = "HQ 指定の品が HQ になりませんでした";
            ctx.Log.Warn("製作", "HQ 指定の品が HQ にならなかったので、ここで製作の列を打ち切ります（残りのレシピは作りません）");
            return null;
        }

        // 納品用の取り置き（HQ 指定の中間素材を、親の製作の後に作る分）は、いまの手持ちで回数を数え直す。
        // 計画は「親が HQ を先に使う」（Artisan のふつうの動き）で見積もっている。もし親が NQ を先に使って HQ が残っていれば、
        // その分は作らない
        if (c.Reserve)
        {
            var now = c.ReserveCrafts(Inventory.Snapshot());
            if (now <= 0)
            {
                ctx.Log.Write("製作", $"{CraftPlanner.ItemName(c.ItemId)}（納品用の取り置き）は手持ちで足りているので作りません");
                return null;
            }

            if (now != c.Crafts)
                ctx.Log.Write("製作", $"{CraftPlanner.ItemName(c.ItemId)}（納品用の取り置き）は、親の製作の後の手持ちで数え直して {c.Crafts} 回 → {now} 回");
            c = c with { Crafts = now };
        }

        var recipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().GetRow(c.RecipeId);
        var lacking = CraftCut.Lacking(CraftPlanner.Ingredients(recipe), c.Crafts, Inventory.Snapshot());
        if (lacking.Count > 0)
        {
            this.craftCut = $"{CraftPlanner.ItemName(c.ItemId)} の材料が計画より少ない（{string.Join("、", lacking.Select(x => $"{CraftPlanner.ItemName(x.Item)} {x.Have}/{x.Need}"))}）";
            ctx.Log.Warn("製作", $"{this.craftCut}ので、ここで製作の列を打ち切って計画を立て直します");
            return null;
        }

        var task = new CraftOneTask(c);
        this.craftTasks.Add(task);
        return task;
    }

    /// <summary>HQ にならなかった回数がいくつに届いたら止めるか（設定の作り直す回数＋1。既定 0 なら1回目で止める）。</summary>
    public static int HqLimit(Configuration config) => Math.Max(0, config.HqRetryRounds) + 1;

    /// <summary>HQ 指定の品が NQ になって止めるときに、自分だけに見えるチャットへ出す文。</summary>
    public const string HqChat = "HQ にならなかったので、装備品や食事、スキル回し等を見直して下さい";

    /// <summary>
    /// 製作の列が終わったら、HQ 指定の品が HQ にならなかった回数を品目ごとに数える。
    /// 上限（設定の HqRetryRounds＋1。既定は1回目）に届いた品があれば、何を見直せばよいか（必要な品・レシピ・装備の数値・Artisan の設定・NQ になった回数）を
    /// 出して止め、自分だけに見えるチャットでも知らせる（製作の失敗と同じ扱い）。
    /// </summary>
    private string? TallyHqFailures(TaskContext ctx)
    {
        string? stop = null;
        foreach (var t in this.craftTasks.Where(t => t.Finished))
        {
            // HQ が要る数は「作る前の手持ちの HQ で足りない分」（作る回数×出来高ではない：品質を問わない納品の分まで HQ を求めない）
            if (!this.hqFailures.Record(t.Craft.ItemId, t.Craft.WantHq, t.HqNeeded, t.MadeHq))
            {
                if (t.Craft.WantHq && t.MadeHq < t.HqNeeded)
                    ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(t.Craft.ItemId)} の HQ が足りません（{t.Made}個中 HQ {t.MadeHq}個・HQ が要る数 {t.HqNeeded}個。この品の HQ 失敗 {this.hqFailures.Count(t.Craft.ItemId)}/{this.hqFailures.Limit} 回）");
                continue;
            }

            if (stop == null)
                Svc.Chat.Print($"[AutoJobQuest] {HqChat}（{CraftPlanner.ItemName(t.Craft.ItemId)}）");
            var job = t.Craft.ClassJobId;
            var gear = GearCheck.ReadGearset(job);
            var baseline = ctx.Data.GearBaselines?.GetValueOrDefault(job) ?? (0, 0);
            var recipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().TryGetRow(t.Craft.RecipeId, out var r) ? r : default;
            stop ??= $"{CraftPlanner.ItemName(t.Craft.ItemId)}（HQ 指定）を {this.hqFailures.Count(t.Craft.ItemId)} 回作っても HQ が足りません"
                     + $"（最後の回：{t.Made}個中 HQ {t.MadeHq}個、HQ が要る数 {t.HqNeeded}個）。"
                     + $"レシピ {t.Craft.RecipeId}（{Jobs.Name(job)}・レシピLv {recipe.RecipeLevelTable.RowId}）、"
                     + $"{Jobs.Name(job)} の装備：作業精度 {gear.Craftsmanship}（基準 {baseline.Item1}）・加工精度 {gear.Control}（基準 {baseline.Item2}）、"
                     + $"Artisan の簡易製作={Planning.Preflight.ReadArtisanBool("QuickSynthMode")?.ToString() ?? "読めない"}。"
                     + "装備・食事・Artisan のソルバーの設定を見直してから、もう一度始めてください";
        }

        this.craftTasks.Clear();
        return stop;
    }

    // ------------------------------------------------------------------
    // ⑦ マテリア装着

    private TaskResult StartMeld(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        var needs = plan.Materia.Where(m => !m.AlreadyMelded).ToList();
        if (needs.Count == 0)
        {
            this.stage = Stage.Quests;
            return TaskResult.Running;
        }

        // マテリアが無ければ集め直す
        if (MateriaMarketNeeds(ctx.Config, plan).Count > 0)
        {
            this.stage = Stage.Acquire;
            return TaskResult.Running;
        }

        this.child = new SequenceTask("マテリア装着", needs.Select(m => (Func<TaskContext, AutoTask?>)(_ => new MeldTask(m))));
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------
    // ⑧ クエスト

    private TaskResult StartQuests(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        if (plan.NothingToDo)
        {
            // この区切りは終わった。次の区切りへ（残りが無ければ、そこで終わりの知らせを出す）
            ctx.Log.Write("計画", $"区切り {this.batchNo} のジョブクエを終えました");
            this.stage = Stage.NextBatch;
            return TaskResult.Running;
        }

        // 納品物がそろっていないクエストがあれば、製作からやり直す
        if (plan.Craft.Crafts.Count > 0)
        {
            this.stage = Stage.Craft;
            return TaskResult.Running;
        }

        // 作る物は無いが、先に用意する素材（受注後に作る品のクリスタル・採集職の納品物など）が足りなければ、素材集めへ戻る
        // （製作の段は作る物が無ければ素材の不足を見ずに次へ進むので、ここで見る。集めきれなければ素材集めの周回の上限で止まる）
        if (plan.Craft.RawShortfall.Count > 0)
        {
            ctx.Log.Write("素材", $"クエストの前に、足りない素材を集め直します：{string.Join("、", plan.Craft.RawShortfall.Select(kv => $"{CraftPlanner.ItemName(kv.Key)}×{kv.Value}"))}");
            this.stage = Stage.Acquire;
            return TaskResult.Running;
        }

        if (plan.Materia.Any(m => !m.AlreadyMelded))
        {
            this.stage = Stage.Meld;
            return TaskResult.Running;
        }

        // 前提のクエストが未完了なら、先にそれを進める（納品物の無いクエストは一覧に入らないので、
        // 蒼天の Lv50 クエスト「槍の持ち主を訪ねて」等＝Lv53 の直接の前提が抜けていた。jqa qinfo で8職とも確認）。
        // 前提は同じ区分（クラス・ジョブ）のものだけ自動で進める。メインクエスト等が未完了なら理由を出して止める
        // （前提の結合条件＝PreviousQuestJoin の意味はソースで確かめられなかったので、全部そろえる側＝止まる側に倒す）
        // 時限の採集点を待つので後回しにしたクエスト（QuestTask.DeferredUntil。不具合の例：採掘師と園芸師を選んだら、
        // 採掘師 Lv70 の硬拳石の採集点が出るのを黙って待ち、園芸師に移らなかった）。採れる時刻の少し前になったら戻る。
        // それまでは、その職のクエスト（後のクエストは、このクエストが済まないと受けられない）を飛ばして、ほかの職のクエストを進める
        foreach (var id in this.deferredQuests.Keys.ToList())
        {
            if (plan.RemainingQuests.All(q => q.RowId != id))
                this.deferredQuests.Remove(id);
            else if (DateTime.UtcNow >= this.deferredQuests[id] - DeferReturnLead)
            {
                this.deferredQuests.Remove(id);
                ctx.Log.Write("クエスト", $"後回しにしていた {Unlocks.QuestName(id)} の採集点が、もうすぐ出るので戻ります");
            }
        }

        var heldJobs = plan.RemainingQuests.Where(q => this.deferredQuests.ContainsKey(q.RowId)).Select(q => q.ClassJobId).ToHashSet();
        var next = plan.RemainingQuests.FirstOrDefault(q => !heldJobs.Contains(q.ClassJobId));

        // このクエストも時限の採集点で後回しにしてよいか：ほかの職にまだ進められるクエストがある、または後回しにしたクエストがある
        // （両方とも採集点を待つなら、先に採れる方へ戻って待つため。戻った先〔下〕では後回しにしない＝行ったり来たりしない）
        var canDefer = next != null
                       && (plan.RemainingQuests.Any(q => q.ClassJobId != next.ClassJobId && !heldJobs.Contains(q.ClassJobId)) || this.deferredQuests.Count > 0);
        if (next == null)
        {
            // 残りが全部、後回しの職のクエスト：いちばん早く採れるようになるクエストへ戻って、そこで待つ
            var soonest = this.deferredQuests.MinBy(kv => kv.Value);
            this.deferredQuests.Remove(soonest.Key);
            next = plan.RemainingQuests.First(q => q.RowId == soonest.Key);
            ctx.Log.Write("クエスト", $"ほかに先に進められるジョブクエが無いので、後回しにしていた {next} に戻り、採集点が出るのを待ちます"
                                     + $"（約 {Math.Max(0, Math.Ceiling((soonest.Value - DateTime.UtcNow).TotalMinutes)):0} 分後）");
        }

        var chain = Unlocks.ChainToRun(next.RowId, out var blocked);
        if (blocked != null)
            return this.Fail($"{Jobs.Name(next.ClassJobId)} {next} の前提を進められません：{blocked}");

        var before = chain.Where(id => id != next.RowId).ToList();
        if (before.Count > 0)
        {
            // 納品のあるジョブクエ（選んでいない職のもの等）は、納品物の用意も納品窓の扱いも無いまま進めない
            var withItems = ctx.Data.Quests!.Quests.FirstOrDefault(q => q.RowId == before[0]);
            if (withItems != null)
                return this.Fail($"{next} の前提「{withItems}」（{Jobs.Name(withItems.ClassJobId)}）は納品のあるジョブクエです。その職も選んで始めてください");

            ctx.Log.Write("クエスト", $"{next} の前に、未完了の前提クエストを進めます：{string.Join(" → ", before.Select(Unlocks.QuestName))}");
            this.child = new RunQuestTask(before[0], Unlocks.QuestName(before[0]));
            return TaskResult.Running;
        }

        // 刺突漁が未解放なら、このジョブクエの前に解放のクエスト（「「刺突漁」で魚を狙え」）を Questionable で進める
        // （自動で解放するクエストに入れる。漁師 Lv68「減少を食い止めろ」の大方士に要る）。
        // 前提（漁師 Lv1 のクラスクエスト）は、上の同じ区分の前提として先に済んでいる
        if (MainQuestGate.GigQuestMissing([next], ctx.Data.Sources!, GatherAbilities.Usable) is var gig and not 0
            && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(gig))
        {
            var gigChain = Unlocks.ChainToRun(gig, out var gigBlocked);
            if (gigBlocked != null)
                return this.Fail($"刺突漁を解放するクエスト「{Unlocks.QuestName(gig)}」を進められません：{gigBlocked}");
            if (gigChain.Count > 0)
            {
                ctx.Log.Write("クエスト", $"{next} の前に、刺突漁を解放するクエストを進めます：{string.Join(" → ", gigChain.Select(Unlocks.QuestName))}");
                this.child = new RunQuestTask(gigChain[0], Unlocks.QuestName(gigChain[0]));
                return TaskResult.Running;
            }
        }

        // 1本ずつ進め、終わるたびに計画を立て直す（Questionable が Artisan の既製リストで
        // 手持ちの材料を使うことがあるので、次のクエストの納品物が残っているかを毎回確かめてから始める）
        this.child = new QuestTask(next) { CanDefer = canDefer };
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------

    private TaskResult AfterChild(TaskContext ctx)
    {
        switch (this.stage)
        {
            case Stage.Retainers:
                this.stage = Stage.Unlock;
                ctx.Log.Write("計画", "引き出した後の手持ちから、製作・素材の計画を作り直します（この区切りの間は呼び鈴へ戻りません）");
                break;
            case Stage.Unlock:
                // 解放の段は1回だけ。ここから先は、解放済みの機能だけを入手手段にする（ReduceTask.Usable）
                Unlocks.UnlockStagePassed = true;
                this.stage = Stage.BookPrep;
                break;
            case Stage.Acquire:
                // 失敗した手段を記録し、立て直す（足りていれば StartAcquire の中で次の段へ進む）
                this.CollectFailures(ctx);
                break;
            case Stage.Books:
            {
                // 全部読めたかを確かめてから先へ進む（以前は段が終われば読めた扱いにしていたので、製作の段で
                // 「秘伝書が未読」という別の理由で止まっていた）。
                // 残っていれば（収集品の製作を途中で打ち切った・紫貨が足りなかった等）、素材集めからやり直す
                var left = this.books!.Offers.Values.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId)).ToList();
                if (left.Count == 0)
                {
                    this.booksDone = true;
                    this.stage = Stage.Craft;
                    break;
                }

                // 段を3回行っても（最初の1回＋やり直し2回）残っていれば止める
                if (++this.booksRound >= 3)
                    return this.Fail($"秘伝書の段を {this.booksRound} 回行っても、読めていない秘伝書が残っています：{string.Join("、", left.Select(o => CraftPlanner.ItemName(o.BookItemId)))}"
                                     + (this.booksCut != null ? $"。直前の打ち切り：{this.booksCut}" : string.Empty));

                ctx.Log.Warn("秘伝書", $"まだ読めていない秘伝書があるので、素材集めからやり直します：{string.Join("、", left.Select(o => CraftPlanner.ItemName(o.BookItemId)))}");
                this.RecomputeBookNeeds(ctx);
                this.stage = Stage.Acquire;
                break;
            }
            case Stage.Meld:
                this.stage = Stage.Quests;
                break;

            case Stage.Craft:
                // 同じ段で立て直す（足りない品があれば作り直し、残りが無ければ次へ）。その前に、HQ の失敗を品目ごとに数える
                if ((this.hqStop ?? this.TallyHqFailures(ctx)) is { } hqStop)
                {
                    this.hqStop = null;
                    return this.Fail(hqStop);
                }
                break;

            // Craft・Quests は同じ段で立て直す（足りない品があれば作り直し、残りが無ければ次へ）
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.hqCheck?.Dispose();
        this.hqCheck = null;

        // 中断の時計を止めたままにしない（次の実行の作業時間を狂わせない）
        WorkClock.Resume();

        // 1つが例外で落ちても残りの後始末を必ず行う（以前は子の後始末が落ちると、
        // RSR・GBR・TextAdvance が頼んだままになった）。子は先に外しておく（2回後始末しない）
        var c = this.child;
        this.child = null;
        Safe(ctx, "作業の後始末", () => c?.Cleanup(ctx));

        // 最後の「受注の NPC に話しかけて止める」段は子ではなく knockTask で動くので、別に片付ける（
        // 以前はこの段の途中で止めても、自分が頼んだ移動と選択肢の窓が残った）
        var k = this.knockTask;
        if (k != null && !this.knockCleaned)
        {
            this.knockCleaned = true;
            Safe(ctx, "話しかけの後始末", () => k.Cleanup(ctx));
        }

        // 念のため、他プラグインへ頼んでいたことを全部戻す
        Safe(ctx, "RSR の優先ターゲットを外す", ctx.Rotation.ClearOwnPriorities);
        Safe(ctx, "RSR のモードを戻す", ctx.Rotation.ReleaseHenched);
        Safe(ctx, "GBR の設定を戻す", () => ctx.Gbr.RestoreIfIdle(ctx.GatherBuddy.IsAutoGatherEnabled(), ctx.Gbr.VendorIsBusy()));
        Safe(ctx, "TextAdvance の外部制御を戻す", ctx.TextAdvance.ReleaseControl);
        Safe(ctx, "YesAlready の停止要求を外す", ctx.YesAlready.Release);
        Safe(ctx, "反撃を終える", () => this.defense.End(ctx));
        ctx.CombatInProgress = false;
        ctx.InOwnConversation = false;
        Unlocks.UnlockStagePassed = false;
        Inventory.GearsetKeepCap = null;
    }

    private static void Safe(TaskContext ctx, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("後始末", $"{what}で例外：{ex.GetType().Name}: {ex.Message}");
            Svc.Log.Error(ex, $"[AutoJobQuest] 後始末（{what}）で例外");
        }
    }
}

/// <summary>マーケットへの切り替えの了承の判断（ゲームを起動せずに試せるように分けた）。</summary>
public static class MarketSwitch
{
    /// <summary>
    /// 聞かずに買ってよいか：この区切りでその品を買いに行った数の合計に、今回の不足数を足しても、了承した数の合計を超えないとき。
    /// </summary>
    public static bool Approved(IReadOnlyDictionary<uint, int> approved, IReadOnlyDictionary<uint, int> used, uint item, int need)
        => approved.TryGetValue(item, out var total) && used.GetValueOrDefault(item) + need <= total;
}
