using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>製作1件（レシピ×回数）。</summary>
public sealed record PlannedCraft(
    uint RecipeId,
    uint ItemId,
    uint ClassJobId,
    int RecipeLevel,
    int Crafts,
    int Yield,
    bool WantHq,
    int Depth,
    uint SecretRecipeBookId);

/// <summary>製作計画の結果。</summary>
public sealed class CraftPlan
{
    /// <summary>作る順（下位の中間素材から）。</summary>
    public List<PlannedCraft> Crafts { get; } = [];

    /// <summary>製作しない素材の、いま足りない数（必要数 − 所持数）。</summary>
    public Dictionary<uint, int> RawShortfall { get; } = [];

    /// <summary>製作しない素材の必要数（所持数を引く前）。</summary>
    public Dictionary<uint, int> RawTotal { get; } = [];

    /// <summary>計画上の問題（レシピが見つからない等）。</summary>
    public List<string> Problems { get; } = [];

    /// <summary>秘伝書が要るのにまだ読んでいないレシピ。</summary>
    public List<PlannedCraft> LockedBySecretBook { get; } = [];
}

/// <summary>所持数の見え方。計画を所持数から切り離して確かめられるようにするための窓口。</summary>
public interface IInventoryView
{
    int CountNq(uint itemId);

    int CountHq(uint itemId);
}

/// <summary>
/// 納品物から製作リストと末端素材を計算する（解析ツール jqa の Gen.cs を移植）。
///
/// 【レシピの選び方】レシピLvが最小、同じLvなら RowId が最小（Number==0 は除く）。
/// 手作業で作った参照リスト（木工〜革細工）と全件突き合わせ済みの規則。
///
/// 【回数の出し方】需要を全部合算してから、所持数を引き、出来高で割って切り上げる。
/// Artisan の「親ごとに切り上げ」より余りが出ない。親→子の順（トポロジカル順）に流す。
///
/// 【HQ】HQ 指定の納品物は HQ の所持だけを数える。NQ で持っていても作り直す。
/// </summary>
public sealed class CraftPlanner
{
    private readonly Dictionary<uint, List<Recipe>> byItem = [];
    private readonly Lumina.Excel.ExcelSheet<Recipe> recipes;

    public CraftPlanner()
    {
        this.recipes = Svc.Data.GetExcelSheet<Recipe>();
        foreach (var r in this.recipes)
        {
            if (r.Number == 0 || r.ItemResult.RowId == 0)
                continue;

            if (!this.byItem.TryGetValue(r.ItemResult.RowId, out var list))
                this.byItem[r.ItemResult.RowId] = list = [];
            list.Add(r);
        }
    }

    /// <summary>そのアイテムを作るレシピ。作れないなら null。</summary>
    public Recipe? Pick(uint itemId)
    {
        if (!this.byItem.TryGetValue(itemId, out var list))
            return null;

        return list
            .OrderBy(x => x.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 999)
            .ThenBy(x => x.RowId)
            .First();
    }

    public bool IsCraftable(uint itemId) => this.byItem.ContainsKey(itemId);

    /// <summary>レシピの材料（アイテム, 個数）。</summary>
    public static IEnumerable<(uint Item, int Amount)> Ingredients(Recipe r)
    {
        for (var i = 0; i < r.Ingredient.Count; i++)
        {
            var it = r.Ingredient[i].RowId;
            var n = r.AmountIngredient[i];
            if (it != 0 && n > 0)
                yield return (it, n);
        }
    }

    /// <summary>
    /// 計画を立てる。
    /// </summary>
    /// <param name="targets">納品物（同じ品目が複数あってもよい。合算する）。</param>
    /// <param name="inv">所持数。</param>
    /// <param name="isBookUnlocked">秘伝書を読んだか（SecretRecipeBook の行 ID → 読了）。</param>
    public CraftPlan Build(IEnumerable<QuestItemReq> targets, IInventoryView inv, Func<uint, bool> isBookUnlocked)
    {
        var plan = new CraftPlan();

        var anyDemand = new Dictionary<uint, int>();
        var hqDemand = new Dictionary<uint, int>();
        foreach (var t in targets)
        {
            if (t.Hq)
                hqDemand[t.ItemId] = hqDemand.GetValueOrDefault(t.ItemId) + t.Count;
            else
                anyDemand[t.ItemId] = anyDemand.GetValueOrDefault(t.ItemId) + t.Count;
        }

        // 1) どの品をどのレシピで作るかを決める（納品物から材料へ辿る）
        var recipeOf = new Dictionary<uint, Recipe>();
        var visiting = new HashSet<uint>();
        void Assign(uint item)
        {
            if (recipeOf.ContainsKey(item) || !visiting.Add(item))
                return;

            var r = this.Pick(item);
            if (r != null)
            {
                recipeOf[item] = r.Value;
                foreach (var (ing, _) in Ingredients(r.Value))
                    Assign(ing);
            }

            visiting.Remove(item);
        }

        foreach (var item in anyDemand.Keys.Concat(hqDemand.Keys).Distinct().ToList())
            Assign(item);

        // 2) 親→子の順に需要を流す
        var parents = new Dictionary<uint, HashSet<uint>>();
        foreach (var (item, r) in recipeOf)
        {
            foreach (var (ing, _) in Ingredients(r))
            {
                if (!recipeOf.ContainsKey(ing))
                    continue;
                if (!parents.TryGetValue(ing, out var set))
                    parents[ing] = set = [];
                set.Add(item);
            }
        }

        var craftsOf = new Dictionary<uint, int>();  // recipeId -> 回数
        var done = new HashSet<uint>();
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var item in recipeOf.Keys.ToList())
            {
                if (done.Contains(item))
                    continue;
                if (parents.TryGetValue(item, out var ps) && ps.Any(p => !done.Contains(p)))
                    continue;

                done.Add(item);
                progress = true;

                var need = NetNeed(item, anyDemand, hqDemand, inv);
                if (need <= 0)
                    continue;

                var r = recipeOf[item];
                var yield = Math.Max(1, (int)r.AmountResult);
                var crafts = (need + yield - 1) / yield;
                craftsOf[r.RowId] = crafts;

                foreach (var (ing, amount) in Ingredients(r))
                    anyDemand[ing] = anyDemand.GetValueOrDefault(ing) + crafts * amount;
            }
        }

        if (done.Count != recipeOf.Count)
            plan.Problems.Add("レシピの材料が循環していて、順番を決められない品があります（計画から外しました）");

        // 3) 製作しない素材
        foreach (var item in anyDemand.Keys.Concat(hqDemand.Keys).Distinct())
        {
            if (recipeOf.ContainsKey(item))
                continue;

            var total = anyDemand.GetValueOrDefault(item) + hqDemand.GetValueOrDefault(item);
            if (total <= 0)
                continue;

            plan.RawTotal[item] = total;
            var shortfall = NetNeed(item, anyDemand, hqDemand, inv);
            if (shortfall > 0)
                plan.RawShortfall[item] = shortfall;
        }

        // 4) 並べる（Artisan の SortList と同じ：深さ→難易度→ジョブ→RowId）
        var depthOf = new Dictionary<uint, int>();
        foreach (var rid in craftsOf.Keys)
        {
            var r = this.recipes.GetRow(rid);
            var max = 0;
            foreach (var (ing, _) in Ingredients(r))
            {
                var d = 0;
                CountDepth(ing, craftsOf, ref d, 0);
                if (d > max)
                    max = d;
            }

            depthOf[rid] = max;
        }

        var hqItems = hqDemand.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet();
        foreach (var rid in craftsOf.Keys
                     .OrderBy(x => depthOf[x])
                     .ThenBy(x => Difficulty(this.recipes.GetRow(x)))
                     .ThenBy(x => this.recipes.GetRow(x).CraftType.RowId)
                     .ThenBy(x => x))
        {
            var r = this.recipes.GetRow(rid);
            var pc = new PlannedCraft(
                rid,
                r.ItemResult.RowId,
                Jobs.CraftTypeToClassJob(r.CraftType.RowId),
                r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0,
                craftsOf[rid],
                Math.Max(1, (int)r.AmountResult),
                hqItems.Contains(r.ItemResult.RowId),
                depthOf[rid],
                r.SecretRecipeBook.RowId);

            plan.Crafts.Add(pc);

            if (pc.SecretRecipeBookId != 0 && !isBookUnlocked(pc.SecretRecipeBookId))
                plan.LockedBySecretBook.Add(pc);

            if (r.IsSpecializationRequired)
                plan.Problems.Add($"{ItemName(pc.ItemId)} はマイスター専用レシピです（自動では作れません）");
        }

        return plan;
    }

    /// <summary>需要から所持数を引いた「まだ要る数」。HQ 指定分は HQ の所持だけで満たす。</summary>
    private static int NetNeed(uint item, Dictionary<uint, int> anyDemand, Dictionary<uint, int> hqDemand, IInventoryView inv)
    {
        var hq = hqDemand.GetValueOrDefault(item);
        var total = anyDemand.GetValueOrDefault(item) + hq;
        if (total <= 0)
            return 0;

        var ownedHq = inv.CountHq(item);
        var ownedAll = ownedHq + inv.CountNq(item);

        var netHq = Math.Max(0, hq - ownedHq);
        var netAll = Math.Max(0, total - ownedAll);
        return Math.Max(netHq, netAll);
    }

    private void CountDepth(uint item, Dictionary<uint, int> craftsOf, ref int depth, int guard)
    {
        if (guard > 20)
            return;

        foreach (var rid in craftsOf.Keys)
        {
            var r = this.recipes.GetRow(rid);
            if (r.ItemResult.RowId != item)
                continue;

            depth++;
            foreach (var (sub, _) in Ingredients(r))
                this.CountDepth(sub, craftsOf, ref depth, guard + 1);
            return;
        }
    }

    private static int Difficulty(Recipe r)
        => (r.RecipeLevelTable.ValueNullable?.Difficulty ?? 0) * r.DifficultyFactor / 100;

    /// <summary>アイテム名（クライアント言語）。</summary>
    public static string ItemName(uint itemId)
        => Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"#{itemId}";
}
