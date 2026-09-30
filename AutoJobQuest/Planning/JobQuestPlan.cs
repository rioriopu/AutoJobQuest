using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoJobQuest.Planning;

/// <summary>素材をどうやって手に入れるか。</summary>
public enum Route
{
    /// <summary>入手手段が見つからない。</summary>
    Unknown,

    /// <summary>マーケットボードで買う（クリスタル・クラスター・マテリア・霊砂・デミマテリラなど）。</summary>
    MarketBoard,

    /// <summary>NPC から買う（GBR の購入機能。ギルの店だけ）。</summary>
    Vendor,

    /// <summary>採掘・園芸（GBR の自動採集）。シャードもここ。</summary>
    Gather,

    /// <summary>釣り（GBR の自動採集 ＋ AutoHook）。</summary>
    Fish,

    /// <summary>モンスターを倒して集める。</summary>
    Combat,

    /// <summary>
    /// 収集品を GBR に採らせ、こちらで精選して得る（霊砂など）。
    /// マーケットより先に試す。精選が未解放・採集職のレベル不足なら候補から外す。
    /// </summary>
    Reduce,
}

/// <summary>製作しない素材1品目の必要数と入手手段。</summary>
public sealed class RawNeed
{
    public required uint ItemId { get; init; }

    public required int Total { get; init; }

    public required int Shortfall { get; init; }

    public required Route Route { get; init; }

    /// <summary>その次に試す手段（第一の手段が失敗したとき）。</summary>
    public List<Route> Fallbacks { get; init; } = [];

    public string Name => CraftPlanner.ItemName(this.ItemId);
}

/// <summary>マテリアの用意1件。</summary>
public sealed class MateriaNeed
{
    public required JobQuest Quest { get; init; }

    public required uint TargetItemId { get; init; }

    public required bool TargetHq { get; init; }

    /// <summary>指定のマテリア（null なら種類不問）。</summary>
    public uint? MateriaItemId { get; init; }

    /// <summary>もう付いた品を持っているか。</summary>
    public bool AlreadyMelded { get; init; }
}

/// <summary>
/// 進められない理由の種類（受注の NPC に話しかけて確かめるのは、前提の連鎖で止まる <see cref="Chain"/> だけ。
/// ほかの種類は、話しかけずに手前で止める）。
/// </summary>
public enum BlockKind
{
    /// <summary>前提のクエストの連鎖（前提の欄・受注の場面の判定）で止まる。受注の NPC に断られる。</summary>
    Chain,

    /// <summary>その職が未解放。</summary>
    JobLocked,

    /// <summary>受注・手順・採集点のエリアへ行けない（受注はできても、手順の途中で進めない）。</summary>
    Area,

    /// <summary>要る秘伝書を交換する店が未解放。</summary>
    Book,

    /// <summary>納品物を取る能力（刺突漁）が未解放。</summary>
    Ability,

    /// <summary>前のジョブクエが進められない（その後のジョブクエも受けられない）。</summary>
    Previous,
}

/// <summary>前提のクエストが未完了で、自動では進められないジョブクエ。</summary>
/// <param name="Quest">進められないジョブクエ。</param>
/// <param name="BlockingQuest">止めている前提のクエスト（メインクエスト等）。</param>
/// <param name="Reason">理由（画面・記録用）。</param>
/// <param name="Kind">理由の種類。</param>
public sealed record BlockedQuest(JobQuest Quest, uint BlockingQuest, string Reason, BlockKind Kind = BlockKind.Chain);

/// <summary>
/// 前提の点検に使う材料（ゲームから読む。ゲームを起動せずに試すときは偽物を渡す）。
/// null の項目は確かめない（前提のクエストだけを見る昔の点検と同じになる）。
/// </summary>
/// <param name="IsComplete">クエストが完了済みか。</param>
/// <param name="AreaReachable">エリアへ行けるか（入口のエーテライトが無いエリアは null＝判断しない）。</param>
/// <param name="JobLevel">職のレベル（0 なら未解放）。</param>
/// <param name="BookShopBlocker">そのジョブクエが、交換できない秘伝書のせいで進められないなら、止めているクエストと理由。</param>
/// <param name="QuestGate">
/// そのほかの、そのジョブクエの手前で止まる理由（止めているクエスト・理由・種類）。刺突漁の未解放と、採集点のエリアに入れない（メインクエスト）
/// （以前は開始そのものを止めていた）。
/// </param>
public sealed record PrereqContext(
    Func<uint, bool> IsComplete,
    Func<uint, bool?>? AreaReachable = null,
    Func<uint, int>? JobLevel = null,
    Func<uint, (uint Quest, string Reason)?>? BookShopBlocker = null,
    Func<JobQuest, (uint Quest, string Reason, BlockKind Kind)?>? QuestGate = null)
{
    /// <summary>前提のクエストだけを見る（昔の点検と同じ）。</summary>
    public static PrereqContext ChainOnly(Func<uint, bool> isComplete) => new(isComplete);

    /// <summary>ゲームから読む（フレームワークのスレッドから呼ぶ）。</summary>
    public static PrereqContext FromGame(GameDataCache data)
    {
        var unlocked = AreaAccess.UnlockedNow();
        return new PrereqContext(
            QuestManager.IsQuestComplete,
            terr => AreaAccess.Reachable(terr, unlocked),
            j => Jobs.Level(j),
            q => FindBookShopBlocker(data, q, QuestManager.IsQuestComplete, PlanBuilder.IsBookUnlocked),
            q => QuestGateOf(data.Sources, q, QuestManager.IsQuestComplete, terr => AreaAccess.Reachable(terr, unlocked), GatherAbilities.Usable));
    }

    /// <summary>
    /// 刺突漁の未解放・採集点のエリアに入れない（メインクエスト）で、そのジョブクエの手前で止まる理由（無ければ null。
    /// <see cref="MainQuestGate.StopNote"/> と同じ判定：画面の案内と、実際に止まる場所を食い違わせない）。
    /// </summary>
    public static (uint Quest, string Reason, BlockKind Kind)? QuestGateOf(SourceIndex? sources, JobQuest q, Func<uint, bool> isComplete,
        Func<uint, bool?> areaReachable, Func<uint, bool?> abilityUsable)
    {
        if (sources == null)
            return null;
        // 刺突漁の解放クエストは、自動で進められるなら止めない（そのジョブクエの前にこちらで進める）
        if (MainQuestGate.GigQuestMissing([q], sources, abilityUsable) is var gig and not 0 && !isComplete(gig) && !PlanBuilder.GigUnlockRunnable(q, gig, isComplete))
            return (gig, $"銛でしか取れない納品物があり、刺突漁が使えません（解放のクエスト「{Unlocks.QuestName(gig)}」の前提が未完了で、自動で進められません）", BlockKind.Ability);
        if (Jobs.IsGatherer(q.ClassJobId) && MainQuestGate.Missing([q], isComplete, areaReachable, sources) is { Count: > 0 } missing)
        {
            var m = MainQuestGate.MostAdvanced(missing);
            return (m, $"納品物の採集点のエリアに入れません（メインクエスト「{Unlocks.QuestName(m)}」が未完了）", BlockKind.Area);
        }

        return null;
    }

    /// <summary>
    /// そのジョブクエに要る秘伝書のうち未読のものが、交換の店の解放クエストが未完了で手に入らないなら、そのクエストと理由
    /// （ゲームデータ：紫貨の秘伝書の店は「一流の道具」が未完了だと使えない。以前は確かめず、紫貨を稼いだ後で止まりえた）。
    /// </summary>
    public static (uint Quest, string Reason)? FindBookShopBlocker(GameDataCache data, uint questId, Func<uint, bool> isComplete, Func<uint, bool> isBookUnlocked)
    {
        if (data.QuestBooks is not { } qb || data.Books is not { } books || !qb.TryGetValue(questId, out var tomes))
            return null;

        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SecretRecipeBook>();
        foreach (var tome in tomes.Where(t => !isBookUnlocked(t)))
        {
            if (!sheet.TryGetRow(tome, out var row) || !books.Offers.TryGetValue(row.Item.RowId, out var offer) || offer.RequiredQuests is not { } req)
                continue;
            var missing = req.FirstOrDefault(r => !isComplete(r));
            if (missing != 0)
                return (missing, $"秘伝書「{CraftPlanner.ItemName(row.Item.RowId)}」を交換する店の解放クエスト「{Unlocks.QuestName(missing)}」が未完了です");
        }

        return null;
    }
}

/// <summary>選んだジョブの、残りのジョブクエ全部についての計画。</summary>
public sealed class JobQuestPlan
{
    /// <summary>残りのジョブクエのうち、進められるもの（前提が進められないものは <see cref="Blocked"/> へ分ける）。</summary>
    public List<JobQuest> RemainingQuests { get; } = [];

    /// <summary>
    /// 作る・そろえる納品物（残りのクエストの納品物から、クエストの今の段でもう要らない分を除いたもの：<see cref="Automation.QuestItemStage"/>）。
    /// 製作の計画・素材集めは、これを基にする（RemainingQuests の Items をそのまま使わない）。
    /// </summary>
    public List<QuestItemReq> Targets { get; } = [];
    public List<QuestItemReq> RetainerTargets { get; } = [];

    /// <summary>クエストの今の段（納品物がもう要らない・手持ちで進めるものだけ。画面・記録用）。</summary>
    public Dictionary<uint, Automation.QuestItemStage.Stage> ItemStages { get; } = [];

    /// <summary>途中まで渡したクエストの、まだ要る納品物（マテリア装着の判断に使う）。</summary>
    public Dictionary<uint, List<QuestItemReq>> PartialNeeds { get; } = [];

    /// <summary>
    /// 残りのジョブクエのうち、前提のクエスト（メインクエスト等）が未完了で進められないもの。
    /// 計画（素材・製作・装着）には入れない（進められないクエストの素材を集めない）。
    /// 開始時に「どのクエストが未達なので動作保証しない」と注意を出す。
    /// </summary>
    public List<BlockedQuest> Blocked { get; } = [];

    public CraftPlan Craft { get; set; } = new();

    public List<RawNeed> Raw { get; } = [];

    public List<MateriaNeed> Materia { get; } = [];

    public List<string> Warnings { get; } = [];

    public DateTime CreatedAt { get; } = DateTime.Now;

    public IEnumerable<RawNeed> Shortfalls => this.Raw.Where(x => x.Shortfall > 0);

    /// <summary>進められるジョブクエが残っていない（前提が未達で進められないものは含めない）。</summary>
    public bool NothingToDo => this.RemainingQuests.Count == 0;

    /// <summary>
    /// 進められないジョブクエを、止めている前提ごとにまとめた文（無ければ空）。職ごとに Lv の範囲と本数を出す。
    /// 例：「希望の灯火」（第七星暦ストーリー）が未完了 → 木工師 Lv53〜60（4本）・鍛冶師 Lv53〜60（4本）
    /// </summary>
    public List<string> BlockedSummary() => SummarizeBlocked(this.Blocked);

    /// <summary><see cref="BlockedSummary"/> の本体（計画を立てずに、進められないクエストだけ調べたときにも使う）。</summary>
    public static List<string> SummarizeBlocked(IEnumerable<BlockedQuest> blocked)
        => blocked
            .GroupBy(b => b.BlockingQuest)
            .Select(g =>
            {
                var head = g.Key != 0
                    ? $"「{Unlocks.QuestName(g.Key)}」{GenreOf(g.Key)}が未完了"
                    : g.First().Reason;
                var jobs = g.GroupBy(b => b.Quest.ClassJobId)
                    .Select(j =>
                    {
                        var min = j.Min(b => b.Quest.Level);
                        var max = j.Max(b => b.Quest.Level);
                        return $"{Jobs.Name(j.Key)} Lv{min}{(max != min ? $"〜{max}" : string.Empty)}（{j.Count()}本）";
                    });
                return $"{head} → {string.Join("・", jobs)}";
            })
            .ToList();

    private static string GenreOf(uint questId)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(questId, out var q)
           && q.JournalGenre.ValueNullable is { } g
            ? $"（{g.Name.ExtractText()}）"
            : string.Empty;
}

/// <summary>
/// 計画を立てる。フレームワークのスレッドから呼ぶこと（所持数とクエストの完了状態を読むため）。
/// </summary>
public static class PlanBuilder
{
    /// <summary>
    /// 選ばれたジョブの残りのジョブクエについて計画を立てる。
    /// </summary>
    /// <param name="data">ゲームデータの表（作り終わっていること）。</param>
    /// <param name="selected">木工→調理の順の選択。</param>
    /// <param name="excludedRoutes">前の周回で失敗した手段（品目 → 手段）。次は別の手段にする。</param>
    /// <param name="onlyQuests">
    /// 今の区切りのジョブクエ（鞄があふれないよう、職ごと・前から何本かずつに区切って進める。JobQuestFlow の NextBatch）。
    /// null なら選んだ職の残り全部。進められないクエスト（Blocked）は区切りに関係なく全部を出す。
    /// </param>
    public static unsafe JobQuestPlan Build(GameDataCache data, bool[] selected, IReadOnlyDictionary<uint, HashSet<Route>>? excludedRoutes = null,
        IReadOnlySet<uint>? onlyQuests = null)
    {
        var plan = new JobQuestPlan();
        if (!data.IsReady)
        {
            plan.Warnings.Add("ゲームデータを読み込み中です");
            return plan;
        }

        var jobs = new HashSet<uint>();
        for (var i = 0; i < selected.Length && i < Jobs.QuestJobs.Length; i++)
            if (selected[i])
                jobs.Add(Jobs.QuestJobs[i]);

        // 残りのジョブクエ。前提のクエスト（メインクエスト等）が未完了で自動では進められないものは分けて、計画に入れない
        // （開始時に「未達なので動作保証しない」と注意を出し、進められる分だけ進める。
        //   進められないクエストの素材まで集めると、ギルと時間が無駄になるため）
        var (runnable, blocked) = SplitQuests(data.Quests!.Quests, jobs, PrereqContext.FromGame(data));
        plan.Blocked.AddRange(blocked);
        plan.RemainingQuests.AddRange(onlyQuests == null ? runnable : runnable.Where(q => onlyQuests.Contains(q.RowId)));

        var inv = Inventory.Snapshot();

        // 0) 途中の段で納品物を渡すクエスト（120本中47本）は、今の段によっては納品物がもう要らない・手持ちで足りる
        //    （以前は、渡した後に止めて再開すると、同じ品をもう一度作っていた）
        //    同じ段で複数の相手に渡すクエストは、相手ごとの渡し済み（日誌の✓）を見て、残りの相手の品を用意する（QuestItemNeeds）
        foreach (var q in plan.RemainingQuests)
        {
            var needs = Automation.QuestItemNeeds.Decide(q.Items, QuestManager.GetQuestSequence(q.RowId), q.FirstItemSeq, q.LastItemSeq, q.Handovers,
                todo => QuestTodo.IsChecked(q.RowId, todo), (item, hq) => hq ? inv.CountHq(item) : inv.CountAll(item));
            var stage = needs.Stage;
            if (stage != Automation.QuestItemStage.Stage.All)
            {
                plan.ItemStages[q.RowId] = stage;
                plan.PartialNeeds[q.RowId] = needs.Needed;
            }

            // 受注した後にしか手に入らない納品物（Lv61〜70 の取引できない品：クエストがくれる材料・その材料から作る品・受注後に採る品）は
            // 用意しない（受注後に Questionable が作る・採る）。取引できる魚・鉱石などは先に用意できる。
            // 品質不問で2個以上を渡す製作品は HQ で用意する（NQ と HQ の山が分かれると納品窓で渡せない）
            // 採集職の納品物で、こちら（GBR）で集めきれなかった品（採集・釣りの手段を外した品）は、受注後に Questionable が自分で採るので、
            // 先に集める対象から外す（以前は集めにくい品が1つあると、周回の上限で実行全体が止まった）。
            // 漁師のジョブクエで Questionable が自分で釣る魚は、最初から任せる（エサは Questionable が用意する）
            var questGathers = Jobs.IsGatherer(q.ClassJobId) ? QuestionablePaths.GatheredItems(q.ShortId) : new HashSet<uint>();
            var prepare = needs.Needed.Where(n => !q.AfterAcceptItems.Contains(n.ItemId) && !LeaveToQuestionable(q.ClassJobId, n.ItemId, questGathers, excludedRoutes))
                .Select(n => PlanAsHq(data.Planner!, n))
                .Select(n => PlanSingleQuality(data.Planner!, n, inv)).ToList();
            plan.RetainerTargets.AddRange(prepare);
            plan.Targets.AddRange(prepare);

            // 受注後に作る品のクリスタル（クエストはくれない）。まだ渡す前で、作った品も足りていなければ、
            // 作る回数＋作り直しの予備の分を用意する（HQ にならなかったときの作り直し：QuestCraftTask）
            if (stage == Automation.QuestItemStage.Stage.All)
                foreach (var qc in q.QuestCrafts)
                {
                    var held = qc.Hq ? inv.CountHq(qc.ItemId) : inv.CountAll(qc.ItemId);
                    if (held >= qc.Count)
                        continue;
                    var crafts = (int)Math.Ceiling((qc.Count - held) * (double)qc.Crafts / Math.Max(1, qc.Count)) + QuestCraftSpare;
                    foreach (var (item, amount) in qc.Prepared)
                    {
                        var req = new QuestItemReq(item, amount * crafts, false, $"受注後に作る {CraftPlanner.ItemName(qc.ItemId)} の材料（予備 {QuestCraftSpare} 回分を含む）");
                        plan.Targets.Add(req);
                        plan.RetainerTargets.Add(req);
                    }
                }

            // Questionable の「Craft」手順のうち、納品物ではない品（中間素材）の手順は、手元に無いと Artisan の既製リストが動いて
            // 追加製作・材料の買い足しになる。その数も手元に残るよう作る（まだ手順の前のクエストだけ）
            if (stage == Automation.QuestItemStage.Stage.All)
            {
                var holds = QuestionableHolds(q, QuestionablePaths.CraftSteps(q.ShortId)).ToList();
                plan.Targets.AddRange(holds);
                plan.RetainerTargets.AddRange(holds);
            }
        }

        // 1) 製作計画（納品物 → 中間素材 → 末端素材）
        // 作れる職がいない中間素材は作らずに買う
        plan.Craft = data.Planner!.Build(plan.Targets, inv, IsBookUnlocked, CraftAbility.FromGame());
        plan.Warnings.AddRange(plan.Craft.Problems);
        if (plan.Craft.NotCraftable.Count > 0)
            plan.Warnings.Add(CraftAbility.Summary(plan.Craft.NotCraftable));

        // 2) 末端素材の入手手段
        var unlockedAetherytes = AreaAccess.UnlockedNow();
        foreach (var (item, total) in plan.Craft.RawTotal.OrderBy(x => x.Key))
        {
            var shortfall = plan.Craft.RawShortfall.GetValueOrDefault(item);
            var routes = AvailableRoutes(data.Sources!, item, excludedRoutes);
            var first = routes.Count > 0 ? routes[0] : Route.Unknown;
            plan.Raw.Add(new RawNeed
            {
                ItemId = item,
                Total = total,
                Shortfall = shortfall,
                Route = first,
                Fallbacks = routes.Skip(1).ToList(),
            });

            if (shortfall > 0 && first == Route.Unknown)
                plan.Warnings.Add($"{CraftPlanner.ItemName(item)} ×{shortfall} の入手手段が見つかりません");

            // 前提が未達で使えない手段は、理由を出す（前提の未達で詰まらないよう確かめる）
            if (shortfall > 0)
            {
                foreach (var (route, why) in RouteBlockers(data.Sources!.Get(item), ChooseRoutes(data.Sources!, item), QuestManager.IsQuestComplete, unlockedAetherytes, GatherAbilities.Usable, Jobs.Level))
                    plan.Warnings.Add($"{CraftPlanner.ItemName(item)} ×{shortfall}：{Ui.MainWindow.RouteName(route)}は使えません（{why}）。{(first == Route.Unknown ? "ほかの手段もありません" : $"{Ui.MainWindow.RouteName(first)}で集めます")}");
            }
        }

        // 3) マテリア装着
        foreach (var q in plan.RemainingQuests)
        {
            if (q.Materia is not { } m)
                continue;

            var hq = q.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;

            // 納品物を渡し終えたクエストは装着も要らない。途中まで渡したクエストは、付ける品がまだ要る品に入っているときだけ
            var stage = plan.ItemStages.GetValueOrDefault(q.RowId, Automation.QuestItemStage.Stage.All);
            if (stage == Automation.QuestItemStage.Stage.None
                || (stage == Automation.QuestItemStage.Stage.HeldOnly && !plan.PartialNeeds.GetValueOrDefault(q.RowId, []).Any(n => n.ItemId == m.TargetItemId)))
                continue;
            plan.Materia.Add(new MateriaNeed
            {
                Quest = q,
                TargetItemId = m.TargetItemId,
                TargetHq = hq,
                MateriaItemId = m.MateriaItemId,
                AlreadyMelded = Inventory.HasMelded(m.TargetItemId, hq, m.MateriaItemId),
            });
        }

        // 4) 秘伝書
        foreach (var locked in plan.Craft.LockedBySecretBook.GroupBy(x => x.SecretRecipeBookId))
        {
            var names = string.Join("・", locked.Select(x => CraftPlanner.ItemName(x.ItemId)));
            plan.Warnings.Add($"秘伝書（SecretRecipeBook {locked.Key}）が未読のため作れません: {names}");
        }

        return plan;
    }

    /// <summary>
    /// 受注後に作る品（Lv61〜70 の製作職）の、作り直しの予備の回数（そのクリスタルを先に用意する）。
    /// 設定の QuestCraftRetryRounds（受注後に作る品が HQ にならなかったときに作り直す回数の上限）から Configuration が入れる。
    /// </summary>
    public static int QuestCraftSpare { get; set; } = 10;

    /// <summary>
    /// 品質不問で2個以上を渡す、重ねられて HQ のある製作品は、HQ で用意する。
    /// 計画は品質不問の納品を NQ と HQ の合計で数えるが、納品窓は「1つの欄に1つの山から N 個」しか受け付けない。
    /// 手持ちやリテイナーの NQ に製作でできた HQ が混ざると、どちらの山も N 個に届かず渡せない（Lv60 までで9件：木工 Lv10 のアッシュ材×12 など）。
    /// HQ で N 個そろえておけば、納品窓は NQ の山が足りなければ HQ の山を使える。
    /// </summary>
    public static QuestItemReq PlanAsHq(CraftPlanner planner, QuestItemReq n)
    {
        if (n.Hq || n.Count < 2 || !planner.IsCraftable(n.ItemId))
            return n;
        if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(n.ItemId, out var item) || item.StackSize <= 1 || !item.CanBeHq)
            return n;
        return n with { Hq = true, Evidence = n.Evidence + "（品質不問だが、NQ と HQ の山が分かれると納品窓で渡せないので HQ で用意する）" };
    }

    /// <summary>
    /// 刺突漁を解放するクエスト（「「刺突漁」で魚を狙え」）を、そのジョブクエの前に自動で進められるか。
    /// 解放クエストの前提が、完了済みか、そのジョブクエの前提の連鎖（同じ実行で先に進める、同じ区分のクエスト）に入っていれば進められる。
    /// 解放クエスト 68458 の前提は「大海原に泳ぎ出せ！」（メインクエスト。開始条件の「いざ山岳地帯へ」の連鎖に入る）と
    /// 「網元代行シシプ」（漁師 Lv1 のクラスクエスト。漁師のジョブクエの連鎖に入る）：ゲームデータで確認。
    /// </summary>
    public static bool GigUnlockRunnable(JobQuest q, uint gig, Func<uint, bool> isComplete)
    {
        var before = Unlocks.ChainCore(q.RowId, isComplete, out var questBlocked, out _);
        if (questBlocked != null)
            return false;
        var willBeDone = before.ToHashSet();
        Unlocks.ChainCore(gig, id => isComplete(id) || willBeDone.Contains(id), out var blocked, out _);
        return blocked == null;
    }

    /// <summary>
    /// 採集職の納品物を、先に集めずに Questionable に任せるか（Questionable が経路の採集・釣りの手順で自分で採る品だけ）。
    ///  ・漁師のジョブクエの魚は、最初から任せる（漁師ジョブクエは Questionable が釣り餌を全部用意するので、
    ///    万能ルアーを買う必要も持つ必要も無い。以前は先に GBR で釣っていたので、GBR 用のエサ＝万能ルアーが要った）。
    ///  ・採掘・園芸の品は、こちら（GBR）で集めきれなかった（採集・釣りの手段を外した）ときだけ任せる。
    /// </summary>
    public static bool LeaveToQuestionable(uint classJobId, uint item, IReadOnlySet<uint> questGathers, IReadOnlyDictionary<uint, HashSet<Route>>? excludedRoutes)
    {
        if (!questGathers.Contains(item))
            return false;
        if (classJobId == Automation.SpearfishTask.Fisher)
            return true;
        return excludedRoutes != null && excludedRoutes.TryGetValue(item, out var bad) && (bad.Contains(Route.Gather) || bad.Contains(Route.Fish));
    }

    /// <summary>
    /// 品質不問で2個以上を渡す、重ねられて HQ のある「作れない品」（採集品・魚）は、1つの品質で N 個そろうように数える
    /// （釣り・採集で HQ が混ざると、NQ と HQ の山がどちらも N 個に届かず、納品窓で渡せない。
    /// 計画は NQ と HQ の合計で数えるので「足りている」と判断し、再開しても同じ所で止まり続けた。作れる品は <see cref="PlanAsHq"/> で HQ にそろえる）。
    /// どちらかの品質で N 個あればそのまま。無ければ、手持ちの HQ の数を上乗せして、集め足す品（GBR の採集・NPC の品は NQ）で NQ の山が N 個になるようにする。
    /// </summary>
    public static QuestItemReq PlanSingleQuality(CraftPlanner planner, QuestItemReq n, IInventoryView inv)
    {
        if (n.Hq || n.Count < 2 || planner.IsCraftable(n.ItemId))
            return n;
        if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(n.ItemId, out var item) || item.StackSize <= 1 || !item.CanBeHq)
            return n;
        var nq = inv.CountNq(n.ItemId);
        var hq = inv.CountHq(n.ItemId);
        if (hq == 0 || Math.Max(nq, hq) >= n.Count)
            return n;
        return n with
        {
            Count = n.Count + hq,
            Evidence = n.Evidence + $"（品質不問だが NQ {nq}・HQ {hq} に分かれ、どちらの山も {n.Count} 個に届かず納品窓で渡せないので、NQ で {n.Count} 個そろうよう HQ の {hq} 個を上乗せする）",
        };
    }

    /// <summary>
    /// Questionable の Craft 手順のうち、納品物ではない品で「持っていれば飛ばす」条件が付いたもの（手元に残す数）。
    /// 納品物の手順は、納品物をそろえる計画で満たされるので足さない。ItemId の無い手順は満たしようがないので足さない（事前点検で知らせる）。
    /// </summary>
    public static IEnumerable<QuestItemReq> QuestionableHolds(JobQuest q, IReadOnlyList<QuestionableCraftStep>? steps)
    {
        if (steps == null)
            yield break;
        var delivered = q.Items.Select(i => i.ItemId).ToHashSet();
        foreach (var g in steps.Where(s => s.ItemId is { } id && s.SkipIfHeld && s.ItemCount > 0 && !delivered.Contains(id)).GroupBy(s => (s.ItemId!.Value, s.Hq)))
            yield return new QuestItemReq(g.Key.Value, g.Max(s => s.ItemCount), g.Key.Hq, "Questionable の製作の手順（持っていれば飛ばす）");
    }

    /// <summary>
    /// 選んだ職の未完了のジョブクエを「進められる」と「前提が未達で進められない」に分ける（計画の最初の段。
    /// 完了済みかは外から渡す：ゲームを起動せずに、完了済みの組み合わせを仮定して試せるように）。
    /// 進められないクエストの納品物は計画（素材・製作・装着）に入れない。
    /// </summary>
    public static (List<JobQuest> Runnable, List<BlockedQuest> Blocked) SplitQuests(IEnumerable<JobQuest> all, IReadOnlySet<uint> jobs, Func<uint, bool> isComplete)
        => SplitQuests(all, jobs, PrereqContext.ChainOnly(isComplete));

    /// <summary>上と同じ。前提の点検の材料をまとめて渡す（エリア・秘伝書の店・職の解放も見る）。</summary>
    public static (List<JobQuest> Runnable, List<BlockedQuest> Blocked) SplitQuests(IEnumerable<JobQuest> all, IReadOnlySet<uint> jobs, PrereqContext prereq)
    {
        var candidates = all.Where(q => jobs.Contains(q.ClassJobId) && !prereq.IsComplete(q.RowId)).ToList();
        var blocked = FindBlocked(candidates, prereq);
        var blockedIds = blocked.Select(b => b.Quest.RowId).ToHashSet();
        return (candidates.Where(q => !blockedIds.Contains(q.RowId)).ToList(), blocked);
    }

    /// <summary>
    /// 未完了のジョブクエのうち、前提のクエストが自動で進められない（区分の違う前提＝メインクエスト等が未完了）もの。
    /// 「完了済みか」は外から渡す（ゲームを起動せずに試せるように）。
    /// </summary>
    public static List<BlockedQuest> FindBlocked(IEnumerable<JobQuest> quests, Func<uint, bool> isComplete)
        => FindBlocked(quests, PrereqContext.ChainOnly(isComplete));

    /// <summary>
    /// 進められないジョブクエ（関連クエストの未達で進められない・詰まることがないよう、前提を必ず確かめる）。
    /// 見る順（最初に当たった理由を出す）：
    ///  1) その職が未解放（レベル 0＝ギルドに加入していない）。以前は前提のたどり方で、ジャーナルに出ない「〇〇師になるには？」の
    ///     クエストに当たり、理由の文が分かりにくかった
    ///  2) 前提のクエスト（区分の違う前提＝メインクエスト等が未完了）
    ///  3) 受注・手順の場所のエリアへ行けない（入口のエーテライトが未解放。イシュガルドなど：AreaAccess）。
    ///     ジョブクエ本体と、これから進める前提のクエストの両方を見る
    ///  4) 納品物を作るのに要る秘伝書が未読で、その交換の店の解放クエストが未完了（「一流の道具」など）
    /// </summary>
    public static List<BlockedQuest> FindBlocked(IEnumerable<JobQuest> quests, PrereqContext prereq)
    {
        var list = new List<BlockedQuest>();
        var chains = new Dictionary<uint, List<uint>>();
        var targets = quests.ToList();
        foreach (var q in targets)
        {
            if (prereq.IsComplete(q.RowId))
                continue;

            if (prereq.JobLevel?.Invoke(q.ClassJobId) is 0)
            {
                list.Add(new BlockedQuest(q, 0, $"{Jobs.Name(q.ClassJobId)}が未解放です（ギルドに加入していません）", BlockKind.JobLocked));
                continue;
            }

            var chain = Unlocks.ChainCore(q.RowId, prereq.IsComplete, out var blocked, out var blocker);
            if (blocked != null)
            {
                list.Add(new BlockedQuest(q, blocker, blocked, BlockKind.Chain));
                continue;
            }

            chains[q.RowId] = chain;
            if (prereq.AreaReachable != null && FirstUnreachable(chain, prereq.AreaReachable) is { } area)
            {
                // 文言は「交感すればよい」か「メインクエスト等で解放する」かで分ける（AreaAccess.UnreachableHint）
                list.Add(new BlockedQuest(q, 0,
                    $"「{AreaAccess.Name(area.Territory)}」へ行けません（{(area.Quest == q.RowId ? "このクエスト" : $"前提のクエスト「{Unlocks.QuestName(area.Quest)}」")}の場所です。"
                    + $"{AreaAccess.UnreachableHint(area.Territory, prereq.IsComplete)}）", BlockKind.Area));
                continue;
            }

            if (prereq.BookShopBlocker?.Invoke(q.RowId) is { } book)
            {
                list.Add(new BlockedQuest(q, book.Quest, book.Reason, BlockKind.Book));
                continue;
            }

            if (prereq.QuestGate?.Invoke(q) is { } gate)
                list.Add(new BlockedQuest(q, gate.Quest, gate.Reason, gate.Kind));
        }

        // 前のジョブクエが進められなければ、その後のジョブクエも受けられない（受けられないジョブクエの直前まで進めて止める）。
        // 前提の連鎖でメインクエストに止められるものは、連鎖をたどると同じメインクエストに当たるので上で分かる。
        // エリア・秘伝書の店・刺突漁などで止まるものは連鎖に出ないので、ここで後ろへ伝える（以前は後のジョブクエを計画に入れ、受注で詰まりえた）
        var first = new Dictionary<uint, BlockedQuest>();
        foreach (var b in list)
            first.TryAdd(b.Quest.RowId, b);
        foreach (var q in targets)
        {
            if (first.ContainsKey(q.RowId) || !chains.TryGetValue(q.RowId, out var chain))
                continue;
            var before = chain.FirstOrDefault(id => id != q.RowId && first.ContainsKey(id));
            if (before == 0)
                continue;
            var origin = first[before];
            list.Add(new BlockedQuest(q, origin.BlockingQuest, $"前のジョブクエ「{Unlocks.QuestName(before)}」が進められません（{origin.Reason}）", BlockKind.Previous));
        }

        return list;
    }

    /// <summary>
    /// 受注の NPC に話しかけて確かめるジョブクエの候補。職ごとに、進められない最初の1本
    /// （レベルの低い順）が「前提の連鎖でメインクエストに止められる」ものだけ。職の並び（木工→…→漁師）の順。
    /// エリア・秘伝書の店・刺突漁などで止まる職は、話しかけずに手前で止める。
    /// </summary>
    public static List<BlockedQuest> KnockCandidates(IEnumerable<BlockedQuest> blocked, Func<uint, bool> isMainScenario)
        => blocked
            .GroupBy(b => b.Quest.ClassJobId)
            .OrderBy(g => Array.IndexOf(Jobs.QuestJobs, g.Key))
            .Select(g => g.OrderBy(b => b.Quest.Level).ThenBy(b => b.Quest.RowId).First())
            .Where(b => b.Kind == BlockKind.Chain && b.BlockingQuest != 0 && isMainScenario(b.BlockingQuest))
            .ToList();

    /// <summary>進めるクエストの列のうち、行けないエリアを場所に持つ最初のもの（無ければ null）。</summary>
    private static (uint Quest, uint Territory)? FirstUnreachable(IEnumerable<uint> questIds, Func<uint, bool?> reachable)
    {
        foreach (var id in questIds)
        {
            foreach (var terr in AreaAccess.QuestTerritories(id).OrderBy(t => t))
            {
                if (reachable(terr) == false)
                    return (id, terr);
            }
        }

        return null;
    }

    /// <summary>
    /// 選んだジョブの、進められないジョブクエだけを調べる（計画を立てずに。ジョブのチェック欄の下に出す用）。
    /// ゲームデータの読み込み前は空。フレームワークのスレッドから呼ぶ。
    /// </summary>
    public static List<BlockedQuest> FindBlocked(GameDataCache data, bool[] selected)
    {
        if (data.Quests == null)
            return [];
        var jobs = new HashSet<uint>();
        for (var i = 0; i < selected.Length && i < Jobs.QuestJobs.Length; i++)
            if (selected[i])
                jobs.Add(Jobs.QuestJobs[i]);
        return FindBlocked(data.Quests.Quests.Where(q => jobs.Contains(q.ClassJobId)), PrereqContext.FromGame(data));
    }

    /// <summary>
    /// 入手手段の優先順を決める。
    ///
    ///  ・シャードは足りない分を GBR で採る。クリスタル・クラスターは足りない分をマーケットボードで買う。
    ///  ・NPC で買える素材は買う（魚も含む）。
    ///  ・マテリア・デミマテリラ・霊砂はマーケットボードで買う。
    /// それ以外は 採集 → 釣り → 戦闘 → マーケットボード の順に試す。
    /// </summary>
    /// <summary>
    /// 実際に使える入手手段（優先順）。計画の表示と実行の両方がこれを使う（表示と動きが食い違わないように）。
    ///  ・前の周回で失敗した手段を外す
    ///  ・NPC 購入：GBR の購入機能は Allagan Tools か Allagan Item Search が無いと使えないので外す
    ///  ・戦闘：落とすモンスターが行けるエリア（野外・解放済みのエーテライトあり）に出なければ外す
    ///  （使えない手段を試して失敗するまで周回を1回無駄にし、行けないエリアではテレポの段で全体が止まるため）
    /// </summary>
    public static List<Route> AvailableRoutes(SourceIndex sources, uint itemId, IReadOnlyDictionary<uint, HashSet<Route>>? excludedRoutes)
    {
        var routes = ChooseRoutes(sources, itemId);
        if (excludedRoutes != null && excludedRoutes.TryGetValue(itemId, out var bad))
            routes = routes.Where(r => !bad.Contains(r)).ToList();
        if (routes.Contains(Route.Vendor) && !Svc.PluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName is "InventoryTools" or "AllaganItemSearch"))
            routes.Remove(Route.Vendor);
        if (routes.Contains(Route.Combat) && !Automation.CombatPlanner.HasReachableSpawn(sources, itemId))
            routes.Remove(Route.Combat);
        // 精選：解放済みか、実行の最初に解放できる見込みがあり、元の収集品を採れる採集職のレベルがあるときだけ
        if (routes.Contains(Route.Reduce) && (!Automation.ReduceTask.Usable() || Automation.ReduceTask.UsableSources(sources, itemId).Count == 0))
            routes.Remove(Route.Reduce);

        // 前提の解放（前提の未達で詰まらないよう、手段を使う前に確かめる。ゲームデータの調査で分かったもの）
        foreach (var (route, _) in RouteBlockers(sources.Get(itemId), routes, QuestManager.IsQuestComplete, AreaAccess.UnlockedNow(), GatherAbilities.Usable, Jobs.Level))
            routes.Remove(route);
        return routes;
    }

    /// <summary>
    /// 前提が未達で使えない入手手段と、その理由（ゲームを起動せずに試せるように、状態は外から渡す）。
    ///  ・NPC 購入：売っている店がすべて、未完了のクエスト（友好部族など）か確かめられない条件付き
    ///  ・採掘・園芸：行ける採集点が無い（採集点のエリアの入口のエーテライトが未解放。GBR は解放済みのエーテライトから向かう）、
    ///    または隠しの品しか無く「眼力」が使えない（未解放かレベル不足）
    ///  ・釣り：竿では釣れず銛でしか取れない品で、刺突漁が使えない
    ///  ・精選：元の収集品を採る「収集品採集」が使えない（クエスト「職人の新たなお仕事」が未完了など）
    ///  ・採掘・園芸・釣り：採集職のレベルが、採集点・魚のレベルに届かない（未解放を含む。
    ///    選ばなかった職はレベルの制限なし。届かなければ採らずにマーケットボードで買う）。<paramref name="jobLevel"/> が null なら見ない
    /// 能力が使えるかを確かめられない（null）ときは外さない（決め打ちの番号がゲームデータと合わない等。記録に残している）。
    /// </summary>
    public static List<(Route Route, string Reason)> RouteBlockers(ItemSources s, IReadOnlyCollection<Route> routes, Func<uint, bool> isComplete,
        IReadOnlySet<uint> unlockedAetherytes, Func<uint, bool?> abilityUsable, Func<uint, int>? jobLevel = null)
    {
        var list = new List<(Route, string)>();

        if (routes.Contains(Route.Vendor) && s.VendorOffers.Count > 0 && !s.VendorOffers.Any(o => !o.Unknown && o.Quests.All(isComplete)))
        {
            var q = s.VendorOffers.SelectMany(o => o.Quests).FirstOrDefault(q => !isComplete(q));
            list.Add((Route.Vendor, q != 0
                ? $"売っている店に、クエスト「{Unlocks.QuestName(q)}」の完了が要ります"
                : "売っている店に、確かめられない条件（アチーブメント等）が付いています"));
        }

        if (routes.Contains(Route.Gather) && s.Gather.Count > 0)
        {
            var reachable = s.Gather.Where(g => AreaAccess.Reachable(g.Territory, unlockedAetherytes) != false).ToList();
            if (reachable.Count == 0)
            {
                list.Add((Route.Gather, $"採集点のあるエリア（{string.Join("・", s.Gather.Select(g => AreaAccess.Name(g.Territory)).Distinct())}）のエーテライトが未解放です"));
            }
            else
            {
                // 採集点のレベルに届く採集職（採掘点なら採掘師、園芸点なら園芸師）がいるか
                var leveled = jobLevel == null ? reachable : reachable.Where(g => jobLevel(GathererOf(g)) >= Math.Max(1, g.GatheringLevel)).ToList();
                if (leveled.Count == 0)
                {
                    var need = reachable.GroupBy(GathererOf).OrderBy(g => g.Key)
                        .Select(g => $"{Jobs.Name(g.Key)} {LevelText(jobLevel!(g.Key))}・採集点 Lv{g.Min(x => x.GatheringLevel)}");
                    list.Add((Route.Gather, $"採集点のレベルに届く採集職がいません（{string.Join("／", need)}）"));
                }
                else if (leveled.All(g => g.Hidden))
                {
                    // 隠しの品：採掘なら採掘師の、園芸なら園芸師の眼力が要る
                    var usable = leveled.Any(g => abilityUsable(g.Mining ? GatherAbilities.MinerLuck : GatherAbilities.BotanistLuck) != false);
                    if (!usable)
                    {
                        var need = leveled.Select(g => g.Mining ? GatherAbilities.MinerLuck : GatherAbilities.BotanistLuck).Distinct().Select(GatherAbilities.Requirement);
                        list.Add((Route.Gather, $"隠し（HIDDEN）の採集物で、{string.Join("か", need)}が要ります"));
                    }
                }
            }
        }

        if (routes.Contains(Route.Fish) && (s.Fish || s.Spearfish) && jobLevel != null && jobLevel(Fisher) is var fisher && (fisher <= 0 || fisher < s.FishLevel))
            list.Add((Route.Fish, $"漁師のレベルが魚のレベルに届きません（漁師 {LevelText(fisher)}・魚 Lv{s.FishLevel}）"));
        else if (routes.Contains(Route.Fish) && !s.Fish && s.Spearfish && abilityUsable(GatherAbilities.Gig) == false)
            list.Add((Route.Fish, $"銛でしか取れない魚で、刺突漁{GatherAbilities.Requirement(GatherAbilities.Gig)}が要ります"));

        return list;
    }

    /// <summary>採掘師・園芸師・漁師（ClassJob の行）。</summary>
    private const uint Miner = 16, Botanist = 17, Fisher = 18;

    private static uint GathererOf(GatherSpot g) => g.Mining ? Miner : Botanist;

    private static string LevelText(int level) => level <= 0 ? "未解放" : $"Lv{level}";

    public static List<Route> ChooseRoutes(SourceIndex sources, uint itemId)
    {
        var s = sources.Get(itemId);
        var list = new List<Route>();

        switch (s.Crystal)
        {
            case CrystalTier.Shard:
                list.Add(Route.Gather);
                if (s.Marketable)
                    list.Add(Route.MarketBoard);
                return list;
            case CrystalTier.Crystal:
            case CrystalTier.Cluster:
                list.Add(Route.MarketBoard);
                if (s.CanGather)
                    list.Add(Route.Gather);
                return list;
        }

        if (s.Vendor)
            list.Add(Route.Vendor);
        if (s.CanGather)
            list.Add(Route.Gather);
        if (s.Fish || s.Spearfish)
            list.Add(Route.Fish);
        if (s.DropMobs.Any(m => sources.SpawnsOf(m).Count > 0))
            list.Add(Route.Combat);
        if (s.CanReduce)
            list.Add(Route.Reduce);
        if (s.Marketable)
            list.Add(Route.MarketBoard);

        return list;
    }

    /// <summary>秘伝書を読んだか。</summary>
    public static unsafe bool IsBookUnlocked(uint secretRecipeBookId)
    {
        if (Automation.GameMemory.Test is { } test)
            return test.IsBookUnlocked(secretRecipeBookId);
        try
        {
            var ps = PlayerState.Instance();
            return ps != null && ps->IsSecretRecipeBookUnlocked(secretRecipeBookId);
        }
        catch
        {
            // 関数が見つからない（パッチ直後など）ときは「未読」とみなす（秘伝書の流れで確かめ直す）
            return false;
        }
    }
}
