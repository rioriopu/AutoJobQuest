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
public sealed class JobQuestFlow : AutoTask
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
    // 周回の回数（素材集めと製作で別々に数える。共有すると製作の作り直しが素材集めの上限を食う）。
    // どちらも「進まなかった周回」を数える：製作が進んだ（残りの製作回数が減った）ら両方 0 に戻す
    // （途中で打ち切って立て直す形にしたので、全体の回数で数えると正常な多レシピの計画でも上限に当たる）
    private int acquireRound;
    private int craftRound;
    private int lastCraftRemaining = int.MaxValue;

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

    public JobQuestFlow(bool[] selected)
    {
        this.selected = selected;
    }

    public override string Name => "ジョブクエ自動化";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        MarketBoardTask.SpentThisRun = 0;
        Unlocks.GaveUp.Clear();
        ctx.Data.EnsureBuilding();
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        // 子の作業が動いていればそれを進める
        if (this.child != null)
        {
            var r = this.child.Step(ctx);
            this.Status = $"{this.child.Name}: {this.child.Status}";
            if (r == TaskResult.Running)
                return TaskResult.Running;

            this.child.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.child.FailReason : null;
            this.child = null;
            if (failed != null)
                return this.Fail(failed);

            return this.AfterChild(ctx);
        }

        switch (this.stage)
        {
            case Stage.WaitData:
                if (ctx.Data.BuildError != null)
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

    // ------------------------------------------------------------------
    // ② 事前点検

    private TaskResult RunPreflight(TaskContext ctx)
    {
        var plan = this.Plan(ctx);
        if (plan.NothingToDo)
        {
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

        // 紫貨のための収集品は、中間素材まで別の職で作る（例：シーダーロングボウ＝木工・鍛冶・裁縫）。
        // その職のギアセットが無ければ、素材を集める前に止める（以前は開始時の点検が
        // 収集品そのものの職しか見ていなかったので、中間素材の段で止まりえた）
        var extra = this.ExtraTargets().ToList();
        if (extra.Count > 0)
        {
            var plan = this.Plan(ctx);
            var craftAll = ctx.Data.Planner!.Build(plan.RemainingQuests.SelectMany(q => q.Items).Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked);
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
        this.collectablesNeeded = b.RewardLow > 0 ? (int)Math.Ceiling(scripNeed / (double)b.RewardLow) : 0;

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
            : this.books.Offers.Values
                .Where(o => !ExchangeBooksTask.IsLearned(o.TomeId) && Inventory.CountNow(o.BookItemId) == 0)
                .Sum(o => (int)o.Price);

    /// <summary>計画で使う職のうち、ギアセットの無いもの（「職（品）」の並び）。全部あれば null。</summary>
    private static string? MissingGearsets(CraftPlan plan)
    {
        var missing = plan.Crafts
            .GroupBy(c => c.ClassJobId)
            .Where(g => GearCheck.FindGearset(g.Key) < 0)
            .Select(g => $"{Jobs.Name(g.Key)}（{string.Join("・", g.Select(c => CraftPlanner.ItemName(c.ItemId)).Distinct())}）")
            .ToList();
        return missing.Count == 0 ? null : string.Join("、", missing);
    }

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
            craftAll = ctx.Data.Planner!.Build(plan.RemainingQuests.SelectMany(q => q.Items).Concat(extra), Inventory.Snapshot(), PlanBuilder.IsBookUnlocked);

        var raw = craftAll.RawShortfall
            .Select(kv => (Item: kv.Key, Need: kv.Value, Routes: this.RoutesFor(ctx, kv.Key)))
            .ToList();

        var marketMateria = MateriaMarketNeeds(ctx.Config, plan);

        // 採集・戦闘・NPC 購入で集めきれず、次の手段がマーケットになった品目は、買う前に利用者に確かめる
        // （時間切れや拒否で、聞かずにギルを使う手段へ切り替えない）
        // 精選で集めるはずだった品（霊砂など）が、精選を使えずマーケットに回る場合も同じく確かめる（霊砂は精選で得る）
        var sourcesIdx = ctx.Data.Sources!;
        var switched = raw
            .Where(r => r.Routes.Count > 0 && r.Routes[0] == Route.MarketBoard
                        && ((this.excluded.TryGetValue(r.Item, out var bad) && bad.Count > 0) || sourcesIdx.Get(r.Item).CanReduce)
                        && !this.marketSwitchApproved.Contains(r.Item))
            .ToList();
        if (switched.Count > 0)
        {
            this.pendingSwitch = switched.Select(r => r.Item).ToList();
            var lines = switched.Select(r =>
                this.excluded.TryGetValue(r.Item, out var bad) && bad.Count > 0
                    ? $"・{CraftPlanner.ItemName(r.Item)}×{r.Need}（{string.Join("・", bad.Select(Ui.MainWindow.RouteName))} で集めきれませんでした）"
                    : $"・{CraftPlanner.ItemName(r.Item)}×{r.Need}（精選が使えません：{(Unlocks.IsUnlocked(Unlocks.Reduction) ? "元の収集品を採れる採集職のレベルが足りない" : "精選が未解放")}）");
            this.confirmTicket = ctx.Confirm.Ask(
                "マーケット購入への切り替えの確認",
                "次の素材は、予定の手段では集めきれませんでした。\n\n" + string.Join("\n", lines)
                + "\n\nマーケットボードで買ってよいですか？「はい」で買います（1回の購入額が基準を超えるときは、あらためて確認します）。「いいえ」で自動動作を止めます。");
            ctx.Log.Warn("素材", "集めきれなかった素材をマーケットで買うか、確認を出しました：" + string.Join(" ", lines));
            this.stage = Stage.WaitSwitchAnswer;
            return TaskResult.Running;
        }

        if (raw.Count == 0 && marketMateria.Count == 0)
        {
            this.stage = this.booksDone ? Stage.Craft : Stage.Books;
            return TaskResult.Running;
        }

        if (this.acquireRound++ >= ctx.Config.MaxRetryRounds + 2)
        {
            var left = string.Join("、", raw.Select(r => $"{CraftPlanner.ItemName(r.Item)}×{r.Need}").Concat(marketMateria.Select(m => m.Label)));
            return this.Fail($"何度集めても足りない素材があります：{left}");
        }

        ctx.Log.Write("素材", $"{this.acquireRound}周目：{raw.Count} 品目{(marketMateria.Count > 0 ? $"＋マテリア {marketMateria.Count} 件" : string.Empty)}を集めます");

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

        // 5) 釣り（GBR に一任）。GBR が釣れない設定なら、別の手段に黙って切り替えずに止める
        var fish = raw.Where(r => r.Routes[0] == Route.Fish).ToList();
        if (fish.Count > 0)
        {
            var optIn = ctx.Gbr.ReadAutoGatherBool("FishDataCollection");
            if (optIn != true)
            {
                return this.Fail(
                    $"釣りで集める素材（{string.Join("、", fish.Select(f => $"{CraftPlanner.ItemName(f.Item)}×{f.Need}"))}）がありますが、"
                    + (optIn == false
                        ? "GBR の「Opt-in to fishing data collection」が OFF のため GBR は釣りをしません（こちらからは変えません）"
                        : "GBR の「Opt-in to fishing data collection」の設定を読めませんでした（GBR の版が変わった可能性。記録の IPC 欄を見てください）"));
            }

            steps.Add(_ => this.Track(new GatherTask(
                fish.Select(f => new GatherNeed(f.Item, inv.CountAll(f.Item) + f.Need)), null, "釣り", TimeSpan.FromMinutes(90), Route.Fish)));
        }

        this.child = new SequenceTask($"素材集め {this.acquireRound}周目", steps);
        return TaskResult.Running;
    }

    // 使える入手手段（計画の表示と同じ判定：PlanBuilder.AvailableRoutes）
    private List<Route> RoutesFor(TaskContext ctx, uint item)
        => PlanBuilder.AvailableRoutes(ctx.Data.Sources!, item, this.excluded);

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

        var town = this.books.ChooseTown();
        if (town == null)
            return this.Fail("収集品納品窓口とスクリップ取引窓口のある街に、解放済みのエーテライトがありません");

        var steps = new List<Func<TaskContext, AutoTask?>>();
        var b = this.books;

        if (b.RequiredQuest != 0 && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(b.RequiredQuest))
        {
            var qname = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(b.RequiredQuest, out var q) ? q.Name.ExtractText() : b.RequiredQuest.ToString();
            steps.Add(_ => new RunQuestTask(b.RequiredQuest, qname));
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
                    // 黙って飛ばすと、後の交換で「紫貨が足りません」という別の理由で止まり、原因が分からなくなる
                    return new StopTask($"紫貨のための収集品（{CraftPlanner.ItemName(b.CollectableItemId)}）の素材が足りません："
                                        + string.Join("、", plan.RawShortfall.Select(x => $"{CraftPlanner.ItemName(x.Key)}×{x.Value}")));
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
            return this.Fail($"秘伝書が未読のため作れない品があります：{string.Join("、", plan.Craft.LockedBySecretBook.Select(c => CraftPlanner.ItemName(c.ItemId)))}");

        // 製作が進んだか（残りの製作回数が前の周回より減ったか）。進んでいれば周回の数を 0 に戻す。
        // 進まない周回（HQ ができない等）が MaxRetryRounds+4 回（既定 7 回）続いたら止める。
        // 素材集めの周回も、製作が進んだら数え直す（HQ の作り直しで材料を集め直すのは「集めきれない」ではないため）
        var remaining = plan.Craft.Crafts.Sum(c => c.Crafts);
        if (remaining < this.lastCraftRemaining)
        {
            this.craftRound = 0;
            this.acquireRound = 0;
        }
        else if (++this.craftRound >= ctx.Config.MaxRetryRounds + 4)
        {
            return this.Fail($"何度作っても納品物がそろいません（HQ ができない等。残りの製作 {remaining} 回のまま {this.craftRound} 周進みませんでした）"
                             + (this.craftCut != null ? $"。直前の打ち切り：{this.craftCut}" : string.Empty));
        }

        this.lastCraftRemaining = remaining;

        if (MissingGearsets(plan.Craft) is { } missing)
            return this.Fail($"製作に使う職のギアセットがありません：{missing}");

        var steps = new List<Func<TaskContext, AutoTask?>> { _ => new GoToInnTask() };
        this.craftCut = null;
        foreach (var c in plan.Craft.Crafts)
            steps.Add(cc => this.NextCraft(cc, c));

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

        var inv = Inventory.Snapshot();
        var recipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().GetRow(c.RecipeId);
        var lacking = CraftPlanner.Ingredients(recipe)
            .Where(x => inv.CountAll(x.Item) < x.Amount * c.Crafts)
            .Select(x => $"{CraftPlanner.ItemName(x.Item)} {inv.CountAll(x.Item)}/{x.Amount * c.Crafts}")
            .ToList();
        if (lacking.Count > 0)
        {
            this.craftCut = $"{CraftPlanner.ItemName(c.ItemId)} の材料が計画より少ない（{string.Join("、", lacking)}）";
            ctx.Log.Warn("製作", $"{this.craftCut}ので、ここで製作の列を打ち切って計画を立て直します");
            return null;
        }

        return new CraftOneTask(c);
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

                if (++this.booksRound >= 3)
                    return this.Fail($"秘伝書の段を {this.booksRound} 回やり直しても、読めていない秘伝書が残っています：{string.Join("、", left.Select(o => CraftPlanner.ItemName(o.BookItemId)))}");

                ctx.Log.Warn("秘伝書", $"まだ読めていない秘伝書があるので、素材集めからやり直します：{string.Join("、", left.Select(o => CraftPlanner.ItemName(o.BookItemId)))}");
                this.RecomputeBookNeeds(ctx);
                this.stage = Stage.Acquire;
                break;
            }
            case Stage.Meld:
                this.stage = Stage.Quests;
                break;

            // Craft・Quests は同じ段で立て直す（足りない品があれば作り直し、残りが無ければ次へ）
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.child != null)
        {
            this.child.Cleanup(ctx);
            this.child = null;
        }

        // 念のため、他プラグインへ頼んでいたことを全部戻す
        ctx.Rotation.ClearOwnPriorities();
        ctx.Rotation.ReleaseHenched();
        ctx.Gbr.RestoreIfIdle(ctx.GatherBuddy.IsAutoGatherEnabled(), ctx.Gbr.VendorIsBusy());
        ctx.TextAdvance.ReleaseControl();
    }
}
