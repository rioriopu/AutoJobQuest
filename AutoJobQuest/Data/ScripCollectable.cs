using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 紫貨を稼ぐために作って納品する収集品を、ジョブに合わせて選ぶ（秘伝書の3巻を1回だけ手に入れるのに、
/// 木工師なら『収集用のシーダーロングボウ』×3、錬金術師なら
/// 『収集用のアルケオーニスグリモア』×3。どれも収集品取引のレベル50の納品。不具合の例：錬金術師だけ Lv100 で
/// 木工師 Lv1 のキャラクターが「入手手段が残っていない素材があります：収集用のシーダーロングボウ×3」で止まった）。
///
/// 品の番号は決め打ちしない。職ごとの指定（<see cref="Configuration.ScripCollectableByJob"/>：既定は調理師だけ
/// 「収集用のソーム・アル・オ・マロン」）があればその品、無ければ設定の品（<see cref="Configuration.ScripCollectableItemId"/>）を物差しにして、
/// 同じ窓口（CollectablesShop）の同じ段（品の LevelMin・LevelMax）で、報酬の通貨が同じ品を、その職のタブ（ShopItems[職−8]）から引く
/// （ゲームデータの実測：収集品納品 3866626 の Lv50〜50 の段は製作8職とも1品ずつあり、報酬は紫貨 45/49/54・収集価値 110 以上で同じ。
/// 木工＝シーダーロングボウ 30970、錬金＝アルケオーニスグリモア 31060。どの品のレシピにも、要るクエスト・秘伝書は無い）。
/// 職ごとの指定の品も、設定の品と同じ窓口・同じ通貨の、その職のタブの品でなければ使わない（写しの表 BookData.ForCollectable が作れないため）。
/// </summary>
public static class ScripCollectable
{
    // （設定の品, 職）→ その窓口のその職のタブの品のうち、設定の品と同じ通貨のもの（品, LevelMin, LevelMax）と、設定の品の段。
    // ゲームデータは動かないので、1回引いたら覚えておく
    private static readonly ConcurrentDictionary<(uint Configured, uint ClassJob), (List<(uint Item, int Min, int Max)> Tab, int Min, int Max)> Tabs = new();

    /// <summary>
    /// その職の、設定の品と同じ窓口・同じ段・同じ報酬の通貨の収集品（無ければ 0）。設定の品がその職の品なら、それ自身を返す。
    /// </summary>
    public static uint SameTierFor(uint configured, uint classJobId)
    {
        var (tab, min, max) = TabOf(configured, classJobId);
        return tab.FirstOrDefault(x => x.Min == min && x.Max == max).Item;
    }

    /// <summary>
    /// その職で作る品。職ごとの指定があり、設定の品と同じ窓口・同じ通貨の、その職のタブの品ならそれ。無ければ <see cref="SameTierFor"/>。
    /// </summary>
    public static uint ItemFor(uint configured, IReadOnlyDictionary<uint, uint>? byJob, uint classJobId)
    {
        if (byJob != null && byJob.TryGetValue(classJobId, out var pick) && pick != 0 && TabOf(configured, classJobId).Tab.Any(x => x.Item == pick))
            return pick;
        return SameTierFor(configured, classJobId);
    }

    private static (List<(uint Item, int Min, int Max)> Tab, int Min, int Max) TabOf(uint configured, uint classJobId)
        => Tabs.GetOrAdd((configured, classJobId), k => Lookup(k.Configured, k.ClassJob));

    private static (List<(uint Item, int Min, int Max)> Tab, int Min, int Max) Lookup(uint configured, uint classJobId)
    {
        var none = (new List<(uint, int, int)>(), 0, 0);
        if (!Jobs.Crafters.Contains(classJobId))
            return none;

        // 設定の品の行（CollectablesShopItem の行）と、段（レベルの幅）・報酬の通貨
        var items = Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>();
        uint row = 0;
        int min = 0, max = 0, currency = -1;
        foreach (var r in items)
        {
            for (var i = 0; i < r.Count && row == 0; i++)
            {
                var it = r[i];
                if (it.Item.RowId != configured || it.CollectablesShopRewardScrip.ValueNullable is not { } reward)
                    continue;
                row = r.RowId;
                min = it.LevelMin;
                max = it.LevelMax;
                currency = reward.Currency;
            }

            if (row != 0)
                break;
        }

        if (row == 0)
            return none;

        // その行を持つ窓口の、その職のタブ（ShopItems[職−8]：BookData.ResolveCollectable と同じ読み方）の品のうち、同じ通貨のもの
        var slot = (int)classJobId - 8;
        foreach (var shop in Svc.Data.GetExcelSheet<CollectablesShop>())
        {
            var slots = shop.ShopItems;
            if (!Enumerable.Range(0, slots.Count).Any(s => slots[s].RowId == row))
                continue;
            if (slot < 0 || slot >= slots.Count || !items.TryGetRow(slots[slot].RowId, out var target))
                continue;
            var tab = target
                .Where(it => it.Item.RowId != 0 && it.CollectablesShopRewardScrip.ValueNullable?.Currency == currency)
                .Select(it => (it.Item.RowId, (int)it.LevelMin, (int)it.LevelMax))
                .ToList();
            return (tab, min, max);
        }

        return none;
    }

    /// <summary>
    /// 紫貨を稼ぐ収集品を選ぶ。選ぶ順：
    ///  1) 秘伝書の要る職（<paramref name="preferredJobs"/> の順）の品（<see cref="ItemFor"/>）で、作れるもの（ジョブに合わせる）
    ///  2) 設定の品（作れるなら）
    ///  3) ほかの製作職の品で、作れるもの
    /// どれも作れなければ設定の品を返す（秘伝書の下準備で、作れる職がいない理由を出して止まる：JobQuestFlow.WaitBookData）。
    /// </summary>
    /// <param name="canCraft">その品を、いまのキャラクターの職で作れるか（レベル・ギアセット・装備。試すときに差し替える）。</param>
    public static uint Choose(uint configured, IReadOnlyDictionary<uint, uint>? byJob, IEnumerable<uint> preferredJobs, Func<uint, bool> canCraft)
    {
        foreach (var job in preferredJobs.Distinct())
        {
            var item = ItemFor(configured, byJob, job);
            if (item != 0 && canCraft(item))
                return item;
        }

        if (canCraft(configured))
            return configured;

        foreach (var job in Jobs.Crafters)
        {
            var item = ItemFor(configured, byJob, job);
            if (item != 0 && canCraft(item))
                return item;
        }

        return configured;
    }

    /// <summary>今のキャラクターの状態で選ぶ（フレームワークのスレッドから呼ぶ。レベルとギアセットをその時点で写し取る）。</summary>
    public static uint ChooseFromGame(Configuration cfg, IEnumerable<uint> preferredJobs, CraftPlanner? planner)
    {
        if (planner == null)
            return cfg.ScripCollectableItemId;
        var ability = CraftAbility.FromGame();
        return Choose(cfg.ScripCollectableItemId, cfg.ScripCollectableByJob, preferredJobs, item => planner.Pick(item, ability) != null);
    }

    /// <summary>
    /// どの製作職も、紫貨の収集品を作れない理由（職ごと。例：「木工師（収集用のシーダーロングボウ）Lv1・レシピ Lv50／錬金術師（…）ギアセットが無い」）。
    /// </summary>
    public static string WhyNone(Configuration cfg, CraftPlanner planner, CraftAbility ability)
        => string.Join("／", Jobs.Crafters
            .Select(job => (Job: job, Item: ItemFor(cfg.ScripCollectableItemId, cfg.ScripCollectableByJob, job)))
            .Where(x => x.Item != 0)
            .Select(x => $"{Jobs.Name(x.Job)}（{CraftPlanner.ItemName(x.Item)}）{ability.WhyNot(x.Job, planner.Pick(x.Item) is { } r ? CraftAbility.RecipeLevel(r) : 999) ?? "作れる"}"));

    /// <summary>収集品だけの素材のうち、戦闘でも集める品の説明（記録・点検用。例：「ディープアイの涙だけ」。無ければ「しない」）。</summary>
    public static string CombatItemsText(Configuration cfg)
        => cfg.ScripCollectableCombatItems.Count == 0
            ? "しない"
            : $"{string.Join("・", cfg.ScripCollectableCombatItems.Select(CraftPlanner.ItemName))}だけ";

    /// <summary>記録・点検に出す説明（例：「収集用のアルケオーニスグリモア（錬金術師の品。設定の『収集用のシーダーロングボウ』と同じ段の品を、ジョブに合わせて選びました）」）。</summary>
    public static string Describe(uint item, Configuration cfg, CraftPlanner? planner)
    {
        var job = planner?.Pick(item) is { } r ? Jobs.Name(Jobs.CraftTypeToClassJob(r.CraftType.RowId)) : "?";
        if (item == cfg.ScripCollectableItemId)
            return $"{CraftPlanner.ItemName(item)}（{job}の品。設定の品）";
        if (cfg.ScripCollectableByJob.ContainsValue(item))
            return $"{CraftPlanner.ItemName(item)}（{job}の品。職ごとの指定の品を、ジョブに合わせて選びました）";
        return $"{CraftPlanner.ItemName(item)}（{job}の品。設定の「{CraftPlanner.ItemName(cfg.ScripCollectableItemId)}」と同じ段の品を、ジョブに合わせて選びました）";
    }
}
