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
    // 周回の回数（素材集めと製作で別々に数える。共有すると製作の作り直しが素材集めの上限を食う）
    private int acquireRound;
    private int craftRound;

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
    private List<BookOffer> booksToBuy = [];
    private int collectablesNeeded;
    private bool booksDone;

    public JobQuestFlow(bool[] selected)
    {
        this.selected = selected;
    }

    public override string Name => "ジョブクエ自動化";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        MarketBoardTask.SpentThisRun = 0;
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
                this.stage = Stage.BookPrep;
                return TaskResult.Running;
            }

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

        this.stage = Stage.BookPrep;
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
        var bookItems = tomes.Select(t => sheet.TryGetRow(t, out var r) ? r.Item.RowId : 0).Where(x => x != 0).ToList();
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

        var offers = this.books.Offers.Values.Where(o => !ExchangeBooksTask.IsLearned(o.TomeId)).ToList();
        this.booksToBuy = offers.Where(o => Inventory.CountNow(o.BookItemId) == 0).ToList();

        var price = this.booksToBuy.Sum(o => (int)o.Price);
        var scrips = this.booksToBuy.Count > 0 ? Inventory.CountSpecialCurrency(this.booksToBuy[0].SpecialCurrencyId, out _) : 0;
        var scripNeed = Math.Max(0, price - scrips);
        var held = Inventory.CountCollectables(this.books.CollectableItemId, this.books.MinCollectability);

        // 1個あたりの報酬は、確実に足りるよう「低」で見積もる（シーダーロングボウは 45。最高評価なら 54）。
        // 持っていたい総数で覚える（手持ちは計画を立てるたびに1回だけ引く。二重に引かない）
        this.collectablesNeeded = this.books.RewardLow > 0
            ? (int)Math.Ceiling(scripNeed / (double)this.books.RewardLow)
            : 0;

        ctx.Log.Write("秘伝書",
            $"交換 {this.booksToBuy.Count} 冊（紫貨 {price}）、所持 {scrips}、不足 {scripNeed}。"
            + $"{CraftPlanner.ItemName(this.books.CollectableItemId)} が {this.collectablesNeeded} 個要ります"
            + $"（1個 {this.books.RewardLow}〜{this.books.RewardHigh}、手持ちの収集品 {held} 個 → 作るのは {Math.Max(0, this.collectablesNeeded - held)} 個）");

        this.stage = Stage.Acquire;
        return TaskResult.Running;
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
        var switched = raw
            .Where(r => r.Routes.Count > 0 && r.Routes[0] == Route.MarketBoard
                        && this.excluded.TryGetValue(r.Item, out var bad) && bad.Count > 0
                        && !this.marketSwitchApproved.Contains(r.Item))
            .ToList();
        if (switched.Count > 0)
        {
            this.pendingSwitch = switched.Select(r => r.Item).ToList();
            var lines = switched.Select(r =>
                $"・{CraftPlanner.ItemName(r.Item)}×{r.Need}（{string.Join("・", this.excluded[r.Item].Select(Ui.MainWindow.RouteName))} で集めきれませんでした）");
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

        // 2) NPC 購入
        var vendor = raw.Where(r => r.Routes[0] == Route.Vendor).Select(r => new VendorNeed(r.Item, r.Need)).ToList();
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
                        here.Select(g => new GatherNeed(g.Item, g.Need)), terr,
                        $"{TeleportTask.TerritoryName(terr)} で採集", TimeSpan.FromMinutes(40))));
                }
            }
        }

        // 4) 残りの採集（シャード含む）。GBR が場所とジョブを選ぶ
        var rest = gather.Where(g => !gatheredInMap.Contains(g.Item)).ToList();
        if (rest.Count > 0)
        {
            steps.Add(_ => this.Track(new GatherTask(
                rest.Select(g => new GatherNeed(g.Item, g.Need)), null, "採掘・園芸", TimeSpan.FromMinutes(90))));
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
                fish.Select(f => new GatherNeed(f.Item, f.Need)), null, "釣り", TimeSpan.FromMinutes(90), Route.Fish)));
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

                return new SequenceTask("収集品の製作", plan.Crafts.Select(pc => (Func<TaskContext, AutoTask?>)(_ => new CraftOneTask(pc))));
            });
        }

        steps.Add(_ => new DeliverCollectablesTask(b, town.Value.Collect));
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

        if (this.craftRound++ > ctx.Config.MaxRetryRounds + 4)
            return this.Fail("何度作っても納品物がそろいません（HQ ができない等）");

        var steps = new List<Func<TaskContext, AutoTask?>> { _ => new GoToInnTask() };
        foreach (var c in plan.Craft.Crafts)
            steps.Add(_ => new CraftOneTask(c));

        this.child = new SequenceTask("グリダニアの宿屋で製作", steps);
        return TaskResult.Running;
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

        // 1本ずつ進め、終わるたびに計画を立て直す（Questionable が Artisan の既製リストで
        // 手持ちの材料を使うことがあるので、次のクエストの納品物が残っているかを毎回確かめてから始める）
        this.child = new QuestTask(plan.RemainingQuests[0]);
        return TaskResult.Running;
    }

    // ------------------------------------------------------------------

    private TaskResult AfterChild(TaskContext ctx)
    {
        switch (this.stage)
        {
            case Stage.Acquire:
                // 失敗した手段を記録し、立て直す（足りていれば StartAcquire の中で次の段へ進む）
                this.CollectFailures(ctx);
                break;
            case Stage.Books:
                this.booksDone = true;
                this.stage = Stage.Craft;
                break;
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
