using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 製作1件（レシピ×回数）。
/// <paramref name="Reserve"/> が true のものは「納品用の取り置き」：HQ 指定で納品する中間素材を、それを材料に使う
/// 親の製作がすべて終わった後に作る分（親の製作で Artisan が HQ を材料に使っても、
/// 納品用の HQ が減らないようにする）。回数は作る直前に手持ちから数え直す（<see cref="ReserveCrafts"/>）。
/// <paramref name="HqTarget"/> はその品の HQ 指定の納品の数（計画全体）、<paramref name="TurnInTarget"/> は
/// その品を直接納品する数（HQ 指定と品質を問わない分の合計。材料として使う分は含まない）。
/// </summary>
public sealed record PlannedCraft(
    uint RecipeId,
    uint ItemId,
    uint ClassJobId,
    int RecipeLevel,
    int Crafts,
    int Yield,
    bool WantHq,
    int Depth,
    uint SecretRecipeBookId,
    bool Reserve = false,
    int HqTarget = 0,
    int TurnInTarget = 0)
{
    /// <summary>
    /// 取り置きの分を、いまの手持ちで数え直した製作回数（取り置きでなければ計画の回数のまま）。
    /// 親の製作がすべて終わった後なので、この後に材料として使われることは無い。
    /// </summary>
    public int ReserveCrafts(IInventoryView inv)
    {
        if (!this.Reserve)
            return this.Crafts;
        var needHq = Math.Max(0, this.HqTarget - inv.CountHq(this.ItemId));
        var needAll = Math.Max(0, this.TurnInTarget - inv.CountHq(this.ItemId) - inv.CountNq(this.ItemId));
        var need = Math.Max(needHq, needAll);
        return (need + Math.Max(1, this.Yield) - 1) / Math.Max(1, this.Yield);
    }
}

/// <summary>製作計画の結果。</summary>
public sealed class CraftPlan
{
    public Dictionary<uint, (int Any, int Hq, int Parent)> StockDemand { get; } = [];
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

    /// <summary>
    /// レシピはあるが、作れる職がいないので作らない品（品 → 理由。製作しない素材として扱い、マーケットボードで買う）。
    /// </summary>
    public Dictionary<uint, string> NotCraftable { get; } = [];
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
///
/// 【HQ 指定の中間素材】HQ で納品する品が、別の納品物の材料にもなるとき、
/// 親の製作で Artisan がどちらの品質を材料に使うかに頼らない。回数を2つに分ける：
///  ・材料の分：親の製作が使う数だけ、親より先に作る（手持ちで足りれば作らない）。
///  ・取り置きの分（Reserve）：親の製作がすべて終わった後に、納品の数（HQ の数と、品質を問わない数）まで作る。
///    Artisan の CraftItem は、親の製作の材料に手持ちの HQ を先に使う（推定・強い：材料欄ごとに NQ の合図を101回 → HQ の合図を
///    101回送り、最後に HQ を押し切る。Artisan の CraftingList.cs の SetIngredients・issue #28/#107 の報告）。
///    なので材料を集める量は「親が手持ちの HQ を先に使う」ふつうの場合で見積もる（多めに作る＝手作業で作った参照リストと同じ考え）。
///    作る回数は作る直前に手持ちで数え直す（もし NQ が先に使われていれば、その分は作らない）。親の製作がどちらの品質を
///    使っても、後から作った取り置きは親に使われないので壊れない。
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

    /// <summary>
    /// そのアイテムを作るレシピ。作れないなら null。
    /// <paramref name="ability"/> を渡すと、作れる職のレシピの中から選ぶ（null なら職を問わない）。
    /// </summary>
    public Recipe? Pick(uint itemId, CraftAbility? ability = null)
    {
        if (!this.byItem.TryGetValue(itemId, out var list))
            return null;

        return list
            .Where(x => ability == null || ability.Can(x))
            .OrderBy(x => x.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 999)
            .ThenBy(x => x.RowId)
            .Cast<Recipe?>()
            .FirstOrDefault();
    }

    /// <summary>その品のレシピを、どの職も作れない理由（職ごと。例：「鍛冶師 Lv45・レシピ Lv58」）。</summary>
    public string WhyNotCraftable(uint itemId, CraftAbility ability)
        => this.byItem.TryGetValue(itemId, out var list)
            ? string.Join("／", list
                .GroupBy(x => Jobs.CraftTypeToClassJob(x.CraftType.RowId))
                .OrderBy(g => g.Key)
                .Select(g => $"{Jobs.Name(g.Key)} {ability.WhyNot(g.Key, g.Min(CraftAbility.RecipeLevel)) ?? "作れる"}"))
            : "レシピが無い";

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
    /// <param name="ability">
    /// どの職がどのレシピを作れるか。作れる職がいない品は製作しない素材として扱い（マーケットボードで買う）、
    /// その材料も集めない。null なら職を問わず作れるものとする（ゲームデータだけで見積もる、別スレッドの計算用）。
    /// </param>
    public CraftPlan Build(IEnumerable<QuestItemReq> targets, IInventoryView inv, Func<uint, bool> isBookUnlocked, CraftAbility? ability = null)
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

            var r = this.Pick(item, ability);
            if (r != null)
            {
                recipeOf[item] = r.Value;
                foreach (var (ing, _) in Ingredients(r.Value))
                    Assign(ing);
            }
            else if (ability != null && this.IsCraftable(item))
            {
                plan.NotCraftable[item] = this.WhyNotCraftable(item, ability);
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

        var craftsOf = new Dictionary<uint, int>();   // recipeId -> 回数（材料の分・分けない品はその品の全部）
        var reserveOf = new Dictionary<uint, int>();  // recipeId -> 取り置きの分の回数（親の製作の後に作る）
        var parentDemand = new Dictionary<uint, int>();  // 品 -> 親の製作が材料として使う数
        var turnInAny = new Dictionary<uint, int>(anyDemand);  // 品 -> 直接納品する数（品質を問わない分。材料の分を足す前に写す）
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

                plan.StockDemand[item] = (anyDemand.GetValueOrDefault(item), hqDemand.GetValueOrDefault(item), parentDemand.GetValueOrDefault(item));
                var r = recipeOf[item];
                var yield = Math.Max(1, (int)r.AmountResult);
                var (crafts, reserve) = SplitCrafts(
                    hqDemand.GetValueOrDefault(item), turnInAny.GetValueOrDefault(item), parentDemand.GetValueOrDefault(item),
                    inv.CountHq(item), inv.CountNq(item), yield, NetNeed(item, anyDemand, hqDemand, inv));
                if (crafts + reserve <= 0)
                    continue;

                if (crafts > 0)
                    craftsOf[r.RowId] = crafts;
                if (reserve > 0)
                    reserveOf[r.RowId] = reserve;

                foreach (var (ing, amount) in Ingredients(r))
                {
                    anyDemand[ing] = anyDemand.GetValueOrDefault(ing) + (crafts + reserve) * amount;
                    parentDemand[ing] = parentDemand.GetValueOrDefault(ing) + (crafts + reserve) * amount;
                }
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

            plan.StockDemand[item] = (anyDemand.GetValueOrDefault(item), hqDemand.GetValueOrDefault(item), parentDemand.GetValueOrDefault(item));
            plan.RawTotal[item] = total;
            var shortfall = NetNeed(item, anyDemand, hqDemand, inv);
            if (shortfall > 0)
                plan.RawShortfall[item] = shortfall;
        }

        // 4) 並べる（Artisan の SortList と同じ：深さ→難易度→ジョブ→RowId）。
        // 取り置きの分は、材料の分のすべての後に、深い（親に近い）ものから並べる
        // （取り置きの品が別の取り置きの品の材料になるときも、材料にする側を先に作り終える）
        var allRecipes = craftsOf.Keys.Union(reserveOf.Keys).ToList();
        var depthOf = new Dictionary<uint, int>();
        foreach (var rid in allRecipes)
        {
            var r = this.recipes.GetRow(rid);
            var max = 0;
            foreach (var (ing, _) in Ingredients(r))
            {
                var d = 0;
                CountDepth(ing, allRecipes, ref d, 0);
                if (d > max)
                    max = d;
            }

            depthOf[rid] = max;
        }

        var hqItems = hqDemand.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet();
        var ordered = craftsOf.Keys
            .OrderBy(x => depthOf[x])
            .ThenBy(x => Difficulty(this.recipes.GetRow(x)))
            .ThenBy(x => this.recipes.GetRow(x).CraftType.RowId)
            .ThenBy(x => x)
            .Select(x => (Rid: x, Reserve: false))
            .Concat(reserveOf.Keys
                .OrderByDescending(x => depthOf[x])
                .ThenBy(x => Difficulty(this.recipes.GetRow(x)))
                .ThenBy(x => this.recipes.GetRow(x).CraftType.RowId)
                .ThenBy(x => x)
                .Select(x => (Rid: x, Reserve: true)));
        foreach (var (rid, isReserve) in ordered)
        {
            var r = this.recipes.GetRow(rid);
            var item = r.ItemResult.RowId;

            // HQ 指定：取り置きの分があれば、HQ を狙うのは取り置きの分だけ（材料の分は親に使われる）
            var wantHq = hqItems.Contains(item) && (isReserve || !reserveOf.ContainsKey(rid));
            var pc = new PlannedCraft(
                rid,
                item,
                Jobs.CraftTypeToClassJob(r.CraftType.RowId),
                r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0,
                isReserve ? reserveOf[rid] : craftsOf[rid],
                Math.Max(1, (int)r.AmountResult),
                wantHq,
                depthOf[rid],
                r.SecretRecipeBook.RowId,
                isReserve,
                hqDemand.GetValueOrDefault(item),
                turnInAny.GetValueOrDefault(item) + hqDemand.GetValueOrDefault(item));

            plan.Crafts.Add(pc);

            // 同じレシピが材料の分と取り置きの分の2つに分かれても、秘伝書・マイスターの注意は1回だけ出す
            if (isReserve && craftsOf.ContainsKey(rid))
                continue;

            if (pc.SecretRecipeBookId != 0 && !isBookUnlocked(pc.SecretRecipeBookId))
                plan.LockedBySecretBook.Add(pc);

            if (r.IsSpecializationRequired)
                plan.Problems.Add($"{ItemName(pc.ItemId)} はマイスター専用レシピです（自動では作れません）");
        }

        return plan;
    }

    /// <summary>
    /// 1品の製作回数を「材料の分」と「取り置きの分」に分ける。分けるのは、HQ 指定の納品があり、
    /// しかも親の製作がその品を材料に使うときだけ。それ以外は今までどおり（<paramref name="netNeed"/> から回数を出す）。
    ///
    /// 分けるとき、親の製作は手持ちの HQ から先に使う（Artisan の CraftItem のふつうの動き：推定・強い）と見て数える：
    ///  ・材料の分 ＝ 親が使う数 − 手持ち（品質を問わない）。親の製作に材料が足りるだけ作る。
    ///  ・親の後に残る HQ ＝ 手持ちの HQ − 親が使う数（親が HQ を先に使うとき）。
    ///  ・取り置きの分 ＝ max（HQ の納品 − 残る HQ、直接の納品の合計 − 親の後に残る数）。
    /// </summary>
    /// <param name="hq">HQ 指定の納品の数。</param>
    /// <param name="turnIn">品質を問わない直接の納品の数（材料として使う分は含まない）。</param>
    /// <param name="parent">親の製作が材料として使う数。</param>
    /// <param name="ownedHq">手持ちの HQ。</param>
    /// <param name="ownedNq">手持ちの NQ。</param>
    /// <param name="yield">1回でできる数。</param>
    /// <param name="netNeed">分けないときの「まだ要る数」。</param>
    public static (int Crafts, int Reserve) SplitCrafts(int hq, int turnIn, int parent, int ownedHq, int ownedNq, int yield, int netNeed)
    {
        yield = Math.Max(1, yield);
        if (hq <= 0 || parent <= 0)
            return (netNeed <= 0 ? 0 : (netNeed + yield - 1) / yield, 0);

        var ownedAll = ownedHq + ownedNq;
        var forParents = Math.Max(0, parent - ownedAll);
        var crafts = (forParents + yield - 1) / yield;
        var leftAll = ownedAll + crafts * yield - parent;
        var leftHq = Math.Max(0, ownedHq - parent);
        var needReserve = Math.Max(Math.Max(0, hq - leftHq), Math.Max(0, turnIn + hq - leftAll));
        return (crafts, (needReserve + yield - 1) / yield);
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

    private void CountDepth(uint item, IReadOnlyCollection<uint> recipeIds, ref int depth, int guard)
    {
        if (guard > 20)
            return;

        foreach (var rid in recipeIds)
        {
            var r = this.recipes.GetRow(rid);
            if (r.ItemResult.RowId != item)
                continue;

            depth++;
            foreach (var (sub, _) in Ingredients(r))
                this.CountDepth(sub, recipeIds, ref depth, guard + 1);
            return;
        }
    }

    private static int Difficulty(Recipe r)
        => (r.RecipeLevelTable.ValueNullable?.Difficulty ?? 0) * r.DifficultyFactor / 100;

    /// <summary>アイテム名（クライアント言語）。</summary>
    /// <summary>
    /// 品の名前。200万番台はクエスト専用品（EventItem シート）から引く（以前は Item シートだけを引き、「#2001682」のように出た）。
    /// </summary>
    public static string ItemName(uint itemId)
    {
        if (itemId >= 2_000_000)
            return Svc.Data.GetExcelSheet<EventItem>().TryGetRow(itemId, out var ev) && ev.Singular.ExtractText() is { Length: > 0 } evName
                ? evName
                : $"#{itemId}";
        return Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"#{itemId}";
    }
}
