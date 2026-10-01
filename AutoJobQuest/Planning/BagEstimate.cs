using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;

namespace AutoJobQuest.Planning;

/// <summary>
/// 区切り（職ごと・前から何本か）を終えるまでに、鞄に新しく要る枠の見積もり（全11職を選ぶと鞄があふれる）。
///
/// 【調査（在庫0から計画して、並びどおりに作ったときに同時に持つ品を数えた）】
///  ・11職をまとめて進めると、製作中に 221〜289 枠（HQ/NQ に分かれた最悪 395）。鞄は 140 枠なので必ずあふれる。
///  ・職ごとに区切ると、1職の製作中の最大は 31（彫金）〜70（調理）枠。報酬を含めても 81 枠。
///  ・クリスタル類は鞄ではなく専用の欄に入る（上限 9,999。全職でも1種類の最大 558）。数えない。
///
/// 見積もりは、計画の製作の並びどおりに「集めた素材が減り、作った品が増える」のをたどって、途中の最大の枠を出す（調査と同じ数え方）。
/// 全部を同時に持つとした単純な足し算は2倍以上多く見積もり（1職で 116〜169 枠）、ほとんどの職を必要以上に細かく区切るため使わない。
/// 多めに見る所：HQ を狙う品は NQ と HQ の山に分かれうるので1枠足す／報酬は全部鞄に入るとみなす／秘伝書が要れば <see cref="BooksAllowance"/> を足す。
/// </summary>
public static class BagEstimate
{
    /// <summary>秘伝書の段（紫貨のための収集品は1個1枠：最大16個と中間素材）の分として足す枠。</summary>
    public const int BooksAllowance = 20;

    /// <param name="craft">区切りの製作計画（並びは作る順）。</param>
    /// <param name="rewardSlots">クエストの報酬で鞄に入りうる枠（<see cref="RewardSlots"/>）。</param>
    /// <param name="needsBooks">秘伝書の段が要るか。</param>
    /// <param name="stackSize">品のスタック数。</param>
    /// <param name="isCrystal">クリスタル欄に入る品か（数えない）。</param>
    /// <param name="ingredients">レシピ1回分の材料。</param>
    /// <param name="owned">
    /// 鞄にいまある数（null なら数えない＝以前の控えめな見積もり）。製作で鞄の品を使い、数が減ってスタックが空けば、その枠を空いたとして数える
    /// （不具合の例：錬金術師で、開始時は見積もり 113 枠・空き 127 枠だったが、リテイナーから素材と中間素材を引き出すと空きが 84 枠に減り、
    /// 見積もりは 98 枠までしか減らず、続きから始められなくなった。引き出した品は製作で使うと空くのに、使う前の分として数え続けていた）。
    /// </param>
    public static int Slots(CraftPlan craft, int rewardSlots, bool needsBooks, Func<uint, int> stackSize, Func<uint, bool> isCrystal,
        Func<uint, IEnumerable<(uint Item, int Amount)>> ingredients, Func<uint, int>? owned = null)
    {
        // 鞄にある品のうち、製作で使った後の数と、使い切って空いた枠
        var stock = new Dictionary<uint, int>();
        var freed = 0;
        int SlotsOf(uint item, int n) => n <= 0 ? 0 : (n + Math.Max(1, stackSize(item)) - 1) / Math.Max(1, stackSize(item));

        // 新しく持つ品（集める素材の不足分と、作った品）。手持ちの在庫は、もとから鞄の枠を使っている（空き枠の数に入っている）
        var held = new Dictionary<uint, int>();
        var split = new HashSet<uint>(craft.Crafts.Where(c => c.WantHq).Select(c => c.ItemId));
        foreach (var (item, n) in craft.RawShortfall)
            held[item] = held.GetValueOrDefault(item) + Math.Max(0, n);

        int Count()
        {
            var slots = 0;
            foreach (var (item, n) in held)
            {
                if (n <= 0 || isCrystal(item))
                    continue;
                var stack = Math.Max(1, stackSize(item));
                slots += ((n + stack - 1) / stack) + (split.Contains(item) ? 1 : 0);
            }

            return slots;
        }

        var peak = Count();
        foreach (var c in craft.Crafts)
        {
            foreach (var (item, amount) in ingredients(c.RecipeId))
            {
                // 新しく持った分から使い、足りない分は鞄にある分から使う（鞄の分は、スタックが空けばその枠を空いたとして数える）
                var use = amount * c.Crafts;
                if (held.TryGetValue(item, out var have))
                {
                    var fromNew = Math.Min(have, use);
                    held[item] = have - fromNew;
                    use -= fromNew;
                }

                if (use > 0 && owned != null && !isCrystal(item))
                {
                    var before = stock.TryGetValue(item, out var cur) ? cur : Math.Max(0, owned(item));
                    var after = Math.Max(0, before - use);
                    freed += SlotsOf(item, before) - SlotsOf(item, after);
                    stock[item] = after;
                }
            }

            held[c.ItemId] = held.GetValueOrDefault(c.ItemId) + (c.Crafts * Math.Max(1, c.Yield));
            peak = Math.Max(peak, Count() - freed);
        }

        return peak + rewardSlots + (needsBooks ? BooksAllowance : 0);
    }

    /// <summary>ゲームデータのレシピで数える版（鞄にある品を使い切って空く枠は数えない：控えめな見積もり）。</summary>
    public static int Slots(CraftPlan craft, int rewardSlots, bool needsBooks)
        => Slots(craft, rewardSlots, needsBooks, StackSize, Inventory.IsCrystalItem, RecipeIngredients);

    /// <summary>レシピ1回分の材料（ゲームデータ）。</summary>
    public static IEnumerable<(uint Item, int Amount)> RecipeIngredients(uint recipeId)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().TryGetRow(recipeId, out var r) ? CraftPlanner.Ingredients(r).ToList() : [];

    /// <summary>クエストの報酬で鞄に入りうる枠（固定の報酬の品の数＋選べる報酬があれば1。ゲームデータの Quest.Reward・OptionalItemReward から）。</summary>
    public static int RewardSlots(IEnumerable<JobQuest> quests)
    {
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var slots = 0;
        foreach (var q in quests)
        {
            if (!sheet.TryGetRow(q.RowId, out var row))
                continue;
            slots += row.Reward.Count(r => r.RowId != 0 && !Inventory.IsCrystalItem(r.RowId));
            if (row.OptionalItemReward.Any(r => r.RowId != 0))
                slots++;
        }

        return slots;
    }

    /// <summary>その品のスタック数（ゲームデータ。読めなければ 1）。</summary>
    public static int StackSize(uint item)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(item, out var row) ? (int)Math.Max(1u, row.StackSize) : 1;

    /// <summary>計画の鞄の見積もり（製作の途中の最大＋報酬＋秘伝書の分）。</summary>
    public static int ForPlan(JobQuestPlan plan)
    {
        // 鞄にある品を製作で使い切って空く枠も数える（いまの鞄の数は Inventory から。引き出した後に、続きから始められなくなったため：Slots の owned の説明）
        var bag = Inventory.Snapshot();
        return Slots(plan.Craft, RewardSlots(plan.RemainingQuests), plan.Craft.LockedBySecretBook.Count > 0, StackSize, Inventory.IsCrystalItem, RecipeIngredients, bag.CountAll);
    }

    /// <summary>
    /// 鞄の空きの不足（枠）。使える空き＝空き − 残しておく空き。足りていれば 0（足りなければ開始できない）。
    /// </summary>
    public static int Shortage(int need, int freeSlots, int keepFree)
        => Math.Max(0, need - (freeSlots - keepFree));

    /// <summary>鞄の空きが足りないときの文。</summary>
    public static string ShortageText(int need, int freeSlots, int keepFree)
        => $"鞄の空きが {Shortage(need, freeSlots, keepFree)} 枠足りません（選んだ職のジョブクエに要る見積もり {need} 枠・使える空き {Math.Max(0, freeSlots - keepFree)} 枠"
           + $"〔空き {freeSlots} 枠から、残しておく空き {keepFree} 枠を除く〕）。選ぶ職を減らすか、鞄を空けてください。残しておく空きは設定タブで変えられます";
}
