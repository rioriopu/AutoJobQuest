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
///   ① ゲームデータの読み込みを待つ
///   ② 事前点検（レベル・装備・プラグイン等）。動作保証外の項目があれば確認窓を出す
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
        Unlock,
        WaitSwitchAnswer,
        BookPrep,
        WaitBookData,
        Acquire,
        Books,
        Craft,
        Meld,
        Quests,
        Done,
    }

    private readonly bool[] selected;
    private Stage stage = Stage.WaitData;
    private AutoTask? child;
    private int confirmTicket = -1;
    // 周回の上限（素材集め・製作）。決まりは RoundPolicy に1か所でまとめ、ゲームなしで試している（検証の仕組み）。
    // 始めるときに設定から作る
    private RoundPolicy rounds = new(5, 7);

    // 製作の列の打ち切り（材料が予定より少ないレシピに来たら、古い計画のまま進めず、残りを捨てて立て直す）
    private string? craftCut;

    // マーケットで買えなかった回数（品目ごと。2回で手段から外す）
    private readonly Dictionary<uint, int> marketFailures = [];

    // 入手に失敗した手段（品目 → 手段）
    private readonly Dictionary<uint, HashSet<Route>> excluded = [];

    // 採集・戦闘などで集めきれず、マーケット購入への切り替えを利用者が「はい」と答えた品目
    private readonly HashSet<uint> marketSwitchApproved = [];
    private List<uint> pendingSwitch = [];

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
    private HqFailureTally hqFailures = new(3);

    // 攻撃されたときの反撃と、こちらの会話ではない会話の窓を閉じる
    private readonly DefenseWatch defense = new();
    private readonly ForeignTalk foreignTalk = new();

    public JobQuestFlow(bool[] selected)
    {
        this.selected = selected;
    }

    public override string Name => "ジョブクエ自動化";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        MarketBoardTask.SpentThisRun = 0;
        MarketBoardTask.RunApprovedUpTo = Math.Max(0, ctx.Config.ConfirmRunTotalAboveGil);
        Unlocks.GaveUp.Clear();
        this.rounds = new RoundPolicy(ctx.Config.MaxRetryRounds + 2, ctx.Config.MaxRetryRounds + 4);
        this.hqFailures = new HqFailureTally(ctx.Config.MaxRetryRounds);
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

                // 結果の分からない購入の控えは、利用者が確かめたので消す（確かめる文言は事前点検の項目に出している）
                if (ctx.Config.PendingPurchase is { } pending)
                {
                    ctx.Log.Warn("マーケット", $"前回の購入（{pending.Describe()}）は利用者が確かめたので、控えを消しました");
                    ctx.Config.PendingPurchase = null;
                    ctx.Config.Save();
                }

                this.stage = Stage.Unlock;
                return TaskResult.Running;
            }

            case Stage.Unlock:
                return this.StartUnlock(ctx);

            case Stage.WaitSwitchAnswer:
            {
                var ans = ctx.Confirm.Poll(this.confirmTicket);
                if (ans == null)
                    return TaskResult.Running;
                if (ans == false)
                    return this.Fail("マーケット購入への切り替えの確認で「いいえ」が選ばれました");
                foreach (var id in this.pendingSwitch)
                    this.marketSwitchApproved.Add(id);
                ctx.Log.Write("素材", $"マーケット購入への切り替えが了承されました：{string.Join("、", this.pendingSwitch.Select(CraftPlanner.ItemName))}");
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

    private JobQuestPlan Plan(TaskContext ctx) => PlanBuilder.Build(ctx.Data, this.selected, this.excluded);

    /// <summary>前提が未達で外したクエストを残して終わったか（結果の分類）。</summary>
    public bool HasExclusions { get; private set; }

    // ------------------------------------------------------------------
    // ② 事前点検

    private TaskResult RunPreflight(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        if (plan.NothingToDo)
        {
            // 残りが全部「前提が未達で進められない」なら、完了ではないので理由を出して止める
            if (plan.Blocked.Count > 0)
                return this.Fail($"進められるジョブクエがありません（前提のクエストが未完了）：{string.Join(" / ", plan.BlockedSummary())}");

            ctx.Log.Write("計画", "選んだジョブのジョブクエは、すべて完了しています");
            this.stage = Stage.Done;
            return TaskResult.Running;
        }

        ctx.Log.Write("計画", $"残りのジョブクエ {plan.RemainingQuests.Count} 本／製作 {plan.Craft.Crafts.Sum(c => c.Crafts)} 回／足りない素材 {plan.Shortfalls.Count()} 品目");
        foreach (var w in plan.Warnings)
            ctx.Log.Warn("計画", w);

        var items = Preflight.Run(ctx, plan);
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

        if (warns.Count > 0)
        {
            this.confirmTicket = ctx.Confirm.Ask(
                "動作保証外の項目があります",
                Preflight.Premise + "\n\n" + string.Join("\n", warns.Select(w => "・" + w.Text)) + "\n\nこのまま続けますか？「いいえ」で止めます。");
            this.stage = Stage.WaitPreflightAnswer;
            return TaskResult.Running;
        }

        this.stage = Stage.Unlock;
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
        if (wantsReduce && !Unlocks.IsUnlocked(Unlocks.Reduction))
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
        var collectable = ctx.Config.ScripCollectableItemId;
        ctx.Log.Write("秘伝書", $"未読の秘伝書：{string.Join("、", bookItems.Select(CraftPlanner.ItemName))}");
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
        var problems = this.books.Validate(this.requiredBookItems);
        if (problems.Count > 0)
            return this.Fail($"秘伝書のデータが足りないため始めません：{string.Join(" / ", problems)}");

        this.RecomputeBookNeeds(ctx);

        // 収集品の納品には前提のクエスト（職人の新たなお仕事。その前提は蒼天のメインクエスト）が要る。
        // 納品が要るのに前提を自動で進められないなら、素材を集める前に止める（以前は秘伝書の段まで進んでから止まった）
        if (this.NeedsDelivery() && this.books.RequiredQuest != 0 && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(this.books.RequiredQuest))
        {
            Unlocks.ChainToRun(this.books.RequiredQuest, out var blockedBy);
            if (blockedBy != null)
                return this.Fail($"紫貨を稼ぐ収集品の納品にはクエスト「{Unlocks.QuestName(this.books.RequiredQuest)}」が要りますが、進められません：{blockedBy}");
        }

        // 紫貨のための収集品は、中間素材まで別の職で作る（例：シーダーロングボウ＝木工・鍛冶・裁縫）。
        // その職のギアセットが無ければ、素材を集める前に止める（以前は開始時の点検が
        // 収集品そのものの職しか見ていなかったので、中間素材の段で止まりえた）
        var extra = this.ExtraTargets().ToList();
        if (extra.Count > 0)
        {
            var plan = this.Plan(ctx);
            var craftAll = ctx.Data.Planner!.Build(plan.Targets.Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked);
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
            craftAll = ctx.Data.Planner!.Build(plan.Targets.Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked);

        var raw = craftAll.RawShortfall
            .Select(kv => (Item: kv.Key, Need: kv.Value, Routes: this.RoutesFor(ctx, kv.Key)))
            .ToList();

        var marketMateria = MateriaMarketNeeds(ctx.Config, plan);

        // 本来の最初の手段がマーケットでない品目が、マーケットに回ったときは、買う前に利用者に確かめる
        // （時間切れや拒否で、聞かずにギルを使う手段へ切り替えない。精選で集めるはずだった霊砂も同じ）。
        // 誤ってギルを大量に使わないよう、前提が未達で採集・NPC 購入などが使えず
        // マーケットに回る品（眼力が未解放の隠し採集物など）も、聞かずに買わない。以前は「前の周回で失敗した」か「精選の品」のときだけだった
        var sourcesIdx = ctx.Data.Sources!;
        var switched = raw
            .Where(r => r.Routes.Count > 0 && r.Routes[0] == Route.MarketBoard
                        && PlanBuilder.ChooseRoutes(sourcesIdx, r.Item).FirstOrDefault() != Route.MarketBoard
                        && !this.marketSwitchApproved.Contains(r.Item))
            .ToList();
        if (switched.Count > 0)
        {
            this.pendingSwitch = switched.Select(r => r.Item).ToList();
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
            return this.Fail($"入手手段が残っていない素材があります：{string.Join("、", unknown.Select(r => $"{CraftPlanner.ItemName(r.Item)}×{r.Need}"))}");

        this.roundTasks.Clear();
        var steps = new List<Func<TaskContext, AutoTask?>>();

        // 1) マーケット（クリスタル・クラスター・霊砂・デミマテリラ・マテリアなど）
        var inv = Inventory.Snapshot();
        var market = raw.Where(r => r.Routes[0] == Route.MarketBoard)
            .Select(r => new MarketNeed([r.Item], r.Need, CraftPlanner.ItemName(r.Item), inv.CountAll(r.Item) + r.Need))
            .Concat(marketMateria)
            .ToList();
        if (market.Count > 0)
            steps.Add(c => this.Track(new MarketBoardTask(market, c.MarketWatcher)));

        // 2) NPC 購入（持っていたい総数で渡す。買う数は作業を始めるときの所持数から決まる）
        var vendor = raw.Where(r => r.Routes[0] == Route.Vendor).Select(r => new VendorNeed(r.Item, inv.CountAll(r.Item) + r.Need)).ToList();
        if (vendor.Count > 0)
            steps.Add(_ => this.Track(new VendorTask(vendor)));

        // 3) マップごとに：戦闘 → 同じマップで採れる素材の採集
        var combat = raw.Where(r => r.Routes[0] == Route.Combat).ToDictionary(r => r.Item, r => r.Need);
        var gather = raw.Where(r => r.Routes[0] == Route.Gather).ToList();
        var gatheredInMap = new HashSet<uint>();
        if (combat.Count > 0)
        {
            var combatJob = CombatJobPicker.Pick();
            if (combatJob == null)
                return this.Fail("戦闘に使えるジョブ（ギアセットのある戦闘ジョブ）がありません");

            // 紫貨の収集品の素材など、開始時の点検に入っていなかった戦闘の素材もあるので、周回を始める前にもう一度確かめる
            // （以前は買い物や採集を済ませた後、戦闘の作業の始めで止まっていた）
            if (!ctx.Rotation.IsLoaded)
                return this.Fail($"戦闘で集める素材（{string.Join("、", combat.Select(kv => $"{CraftPlanner.ItemName(kv.Key)}×{kv.Value}"))}）がありますが、RotationSolverReborn が読み込まれていません");

            var combatPlan = CombatPlanner.Plan(ctx.Data.Sources!, combat, out var unreachable);
            foreach (var id in unreachable)
            {
                // RoutesFor で外しているので通常は来ない。来たら戦闘をあきらめて次の周回で別の手段にする
                ctx.Log.Warn("戦闘", $"{CraftPlanner.ItemName(id)} を落とすモンスターは、行けるエリア（野外・解放済みのエーテライトあり）にいません");
                this.Exclude(id, Route.Combat);
            }

            foreach (var (terr, needs, spots) in combatPlan)
            {
                Vector3? firstSpot = spots.Count > 0 ? MapCoords.ToWorld(terr, spots[0].X, spots[0].Y) : null;
                steps.Add(_ => new EquipJobTask(combatJob.Value.ClassJob));
                steps.Add(_ => new TeleportTask(terr, firstSpot));
                steps.Add(_ => this.Track(new CombatTask(terr, needs, spots, TimeSpan.FromMinutes(25))));

                // 同じ採集品が複数のマップで採れても、割り当てるのは最初のマップだけ
                // （複数のマップに同じ不足数で入れると、その数だけ余計に採る）
                var here = gather.Where(g => !gatheredInMap.Contains(g.Item) && ctx.Gbr.GatherableTerritories(g.Item)?.Contains(terr) == true).ToList();
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

        // 4) 残りの採集（シャード含む）。GBR が場所とジョブを選ぶ
        var rest = gather.Where(g => !gatheredInMap.Contains(g.Item)).ToList();
        if (rest.Count > 0)
        {
            steps.Add(_ => this.Track(new GatherTask(
                rest.Select(g => new GatherNeed(g.Item, inv.CountAll(g.Item) + g.Need)), null, "採掘・園芸", TimeSpan.FromMinutes(90))));
        }

        // 4.5) 採集→精選（霊砂など。収集品を GBR に採らせて精選で得る）
        foreach (var r in raw.Where(r => r.Routes[0] == Route.Reduce))
        {
            var reduceNeed = new ReduceNeed(r.Item, inv.CountAll(r.Item) + r.Need, ReduceTask.UsableSources(ctx.Data.Sources!, r.Item));
            steps.Add(_ => this.Track(new ReduceTask(reduceNeed)));
        }

        // 5) 釣り（GBR に一任）。GBR が釣れない設定なら、別の手段に黙って切り替えずに止める。
        //    周回を始める前に、同意・AutoHook・GBR の UseAutoHook をそろって確かめる（作業の始めでももう一度確かめる）
        var fish = raw.Where(r => r.Routes[0] == Route.Fish).ToList();
        if (fish.Count > 0)
        {
            var missing = RequiredCapabilities.Fishing(ctx.Gbr.ReadAutoGatherBool("FishDataCollection"), ctx.AutoHook.IsLoaded, ctx.Gbr.ReadAutoGatherBool("UseAutoHook"));
            if (missing.Count > 0)
            {
                return this.Fail(
                    $"釣りで集める素材（{string.Join("、", fish.Select(f => $"{CraftPlanner.ItemName(f.Item)}×{f.Need}"))}）がありますが、釣りを始められません：{string.Join(" / ", missing)}");
            }

            steps.Add(_ => this.Track(new GatherTask(
                fish.Select(f => new GatherNeed(f.Item, inv.CountAll(f.Item) + f.Need)), null, "釣り", TimeSpan.FromMinutes(90), Route.Fish)));
        }

        this.child = new SequenceTask($"素材集め {this.rounds.AcquireRounds}周目", steps);
        return TaskResult.Running;
    }

    /// <summary>本来の手段ではなくマーケットに回った理由（確認窓の文言用）。</summary>
    private string WhyMarket(TaskContext ctx, uint item)
    {
        if (this.excluded.TryGetValue(item, out var bad) && bad.Count > 0)
            return $"{string.Join("・", bad.Select(Ui.MainWindow.RouteName))} で集めきれませんでした";

        var sources = ctx.Data.Sources!;
        var natural = PlanBuilder.ChooseRoutes(sources, item);
        var blockers = PlanBuilder.RouteBlockers(sources.Get(item), natural, FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete, AreaAccess.UnlockedNow(), GatherAbilities.Usable);
        if (blockers.Count > 0)
            return string.Join(" / ", blockers.Select(b => $"{Ui.MainWindow.RouteName(b.Route)}は使えません：{b.Reason}"));
        if (sources.Get(item).CanReduce)
            return $"精選が使えません：{(Unlocks.IsUnlocked(Unlocks.Reduction) ? "元の収集品を採れる採集職のレベルが足りないか、収集品採集が使えない" : "精選が未解放")}";
        return $"ほかの手段（{string.Join("・", natural.Where(r => r != Route.MarketBoard).Select(Ui.MainWindow.RouteName))}）が使えません";
    }

    // 使える入手手段（計画の表示と同じ判定：PlanBuilder.AvailableRoutes）。
    // 戦闘に使えるジョブが無ければ、戦闘は手段から外す（次の手段がマーケットなら、買う前に確認窓を出す）。
    // 以前は戦闘が第一の手段の素材があると、そこで止まっていた
    private List<Route> RoutesFor(TaskContext ctx, uint item)
    {
        var routes = PlanBuilder.AvailableRoutes(ctx.Data.Sources!, item, this.excluded);
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
                list.Add(new MarketNeed([mid], count - owned, CraftPlanner.ItemName(mid), count));
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
                        this.Exclude(id, Route.Combat);
                    break;
                case GatherTask g:
                    // 採集と釣りのどちらで失敗したかは、作業の種類で決める（品目の性質で決めると、両方で取れる品で取り違える）
                    foreach (var id in g.Unfinished)
                        this.Exclude(id, g.Route);
                    break;
                case ReduceTask rt:
                    foreach (var id in rt.Unfinished)
                        this.Exclude(id, Route.Reduce);
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

        // 収集品の納品に要るクエスト（職人の新たなお仕事）。納品が要るときだけ、未完了の前提（同じ区分のもの）ごと進める
        // （以前は紫貨が足りていて納品しないときも進め、前提も進めなかった）
        // 選んだ窓口の画面を開くのにこのクエストが要るときも、納品の要らないときでも進める
        var windowNeedsQuest = town.Value.Scrip.UnlockQuest != 0 && town.Value.Scrip.UnlockQuest == b.RequiredQuest;
        if ((this.NeedsDelivery() || windowNeedsQuest) && b.RequiredQuest != 0 && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(b.RequiredQuest))
        {
            var chain = Unlocks.ChainToRun(b.RequiredQuest, out var blockedBy);
            if (blockedBy != null)
                return this.Fail($"紫貨を稼ぐ収集品の納品にはクエスト「{Unlocks.QuestName(b.RequiredQuest)}」が要りますが、進められません：{blockedBy}");
            foreach (var id in chain)
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
                var plan = c.Data.Planner!.Build([new QuestItemReq(b.CollectableItemId, make + inv.CountAll(b.CollectableItemId), false, string.Empty)], inv, PlanBuilder.IsBookUnlocked);
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
                return new SequenceTask("収集品の製作", plan.Crafts.Select(pc => (Func<TaskContext, AutoTask?>)(cc => this.NextCraft(cc, pc))));
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

    /// <summary>
    /// 製作の列が終わったら、HQ 指定の品が HQ にならなかった回数を品目ごとに数える。
    /// 上限に届いた品があれば、何を見直せばよいか（必要な品・レシピ・装備の数値・Artisan の設定・NQ になった回数）を出して止める。
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
            // 前提が未達で飛ばしたクエストがあれば、終わりに改めて知らせる。
            // 結果は「前提未達のため一部除外して完了」になる
            var blockedLines = plan.BlockedSummary();
            this.HasExclusions = blockedLines.Count > 0;
            foreach (var line in blockedLines)
            {
                ctx.Log.Warn("クエスト", $"前提のクエストが未完了のため進めていません：{line}");
                Svc.Chat.Print($"[AutoJobQuest] 前提のクエストが未完了のため進めていません：{line}");
            }

            this.stage = Stage.Done;
            return TaskResult.Running;
        }

        // 納品物がそろっていないクエストがあれば、製作からやり直す
        if (plan.Craft.Crafts.Count > 0)
        {
            this.stage = Stage.Craft;
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
        var next = plan.RemainingQuests[0];
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

        // 1本ずつ進め、終わるたびに計画を立て直す（Questionable が Artisan の既製リストで
        // 手持ちの材料を使うことがあるので、次のクエストの納品物が残っているかを毎回確かめてから始める）
        this.child = new QuestTask(next);
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------

    private TaskResult AfterChild(TaskContext ctx)
    {
        switch (this.stage)
        {
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
                if (this.TallyHqFailures(ctx) is { } hqStop)
                    return this.Fail(hqStop);
                break;

            // Craft・Quests は同じ段で立て直す（足りない品があれば作り直し、残りが無ければ次へ）
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // 中断の時計を止めたままにしない（次の実行の作業時間を狂わせない）
        WorkClock.Resume();

        // 1つが例外で落ちても残りの後始末を必ず行う（以前は子の後始末が落ちると、
        // RSR・GBR・TextAdvance が頼んだままになった）。子は先に外しておく（2回後始末しない）
        var c = this.child;
        this.child = null;
        Safe(ctx, "作業の後始末", () => c?.Cleanup(ctx));

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
