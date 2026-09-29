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

/// <summary>前提のクエストが未完了で、自動では進められないジョブクエ。</summary>
/// <param name="Quest">進められないジョブクエ。</param>
/// <param name="BlockingQuest">止めている前提のクエスト（メインクエスト等）。</param>
/// <param name="Reason">理由（画面・記録用）。</param>
public sealed record BlockedQuest(JobQuest Quest, uint BlockingQuest, string Reason);

/// <summary>選んだジョブの、残りのジョブクエ全部についての計画。</summary>
public sealed class JobQuestPlan
{
    /// <summary>残りのジョブクエのうち、進められるもの（前提が進められないものは <see cref="Blocked"/> へ分ける）。</summary>
    public List<JobQuest> RemainingQuests { get; } = [];

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
    public static unsafe JobQuestPlan Build(GameDataCache data, bool[] selected, IReadOnlyDictionary<uint, HashSet<Route>>? excludedRoutes = null)
    {
        var plan = new JobQuestPlan();
        if (!data.IsReady)
        {
            plan.Warnings.Add("ゲームデータを読み込み中です");
            return plan;
        }

        var jobs = new HashSet<uint>();
        for (var i = 0; i < selected.Length && i < Jobs.Crafters.Length; i++)
            if (selected[i])
                jobs.Add(Jobs.Crafters[i]);

        // 残りのジョブクエ。前提のクエスト（メインクエスト等）が未完了で自動では進められないものは分けて、計画に入れない
        // （開始時に「未達なので動作保証しない」と注意を出し、進められる分だけ進める。
        //   進められないクエストの素材まで集めると、ギルと時間が無駄になるため）
        var (runnable, blocked) = SplitQuests(data.Quests!.Quests, jobs, QuestManager.IsQuestComplete);
        plan.Blocked.AddRange(blocked);
        plan.RemainingQuests.AddRange(runnable);

        var inv = Inventory.Snapshot();

        // 1) 製作計画（納品物 → 中間素材 → 末端素材）
        var targets = plan.RemainingQuests.SelectMany(q => q.Items).ToList();
        plan.Craft = data.Planner!.Build(targets, inv, IsBookUnlocked);
        plan.Warnings.AddRange(plan.Craft.Problems);

        // 2) 末端素材の入手手段
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
        }

        // 3) マテリア装着
        foreach (var q in plan.RemainingQuests)
        {
            if (q.Materia is not { } m)
                continue;

            var hq = q.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;
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
    /// 選んだ職の未完了のジョブクエを「進められる」と「前提が未達で進められない」に分ける（計画の最初の段。
    /// 完了済みかは外から渡す：ゲームを起動せずに、完了済みの組み合わせを仮定して試せるように）。
    /// 進められないクエストの納品物は計画（素材・製作・装着）に入れない。
    /// </summary>
    public static (List<JobQuest> Runnable, List<BlockedQuest> Blocked) SplitQuests(IEnumerable<JobQuest> all, IReadOnlySet<uint> jobs, Func<uint, bool> isComplete)
    {
        var candidates = all.Where(q => jobs.Contains(q.ClassJobId) && !isComplete(q.RowId)).ToList();
        var blocked = FindBlocked(candidates, isComplete);
        var blockedIds = blocked.Select(b => b.Quest.RowId).ToHashSet();
        return (candidates.Where(q => !blockedIds.Contains(q.RowId)).ToList(), blocked);
    }

    /// <summary>
    /// 未完了のジョブクエのうち、前提のクエストが自動で進められない（区分の違う前提＝メインクエスト等が未完了）もの。
    /// 「完了済みか」は外から渡す（ゲームを起動せずに試せるように）。
    /// </summary>
    public static List<BlockedQuest> FindBlocked(IEnumerable<JobQuest> quests, Func<uint, bool> isComplete)
    {
        var list = new List<BlockedQuest>();
        foreach (var q in quests)
        {
            if (isComplete(q.RowId))
                continue;
            Unlocks.ChainCore(q.RowId, isComplete, out var blocked, out var blocker);
            if (blocked != null)
                list.Add(new BlockedQuest(q, blocker, blocked));
        }

        return list;
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
        for (var i = 0; i < selected.Length && i < Jobs.Crafters.Length; i++)
            if (selected[i])
                jobs.Add(Jobs.Crafters[i]);
        return FindBlocked(data.Quests.Quests.Where(q => jobs.Contains(q.ClassJobId)), QuestManager.IsQuestComplete);
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
        return routes;
    }

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
