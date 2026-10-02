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
/// 品の番号は決め打ちしない。設定の品（<see cref="Configuration.ScripCollectableItemId"/>）を物差しにして、同じ窓口（CollectablesShop）の
/// 同じ段（品の LevelMin・LevelMax）で、報酬の通貨が同じ品を、その職のタブ（ShopItems[職−8]）から引く
/// （ゲームデータの実測：収集品納品 3866626 の Lv50〜50 の段は製作8職とも1品ずつあり、報酬は紫貨 45/49/54・収集価値 110 以上で同じ。
/// 木工＝シーダーロングボウ 30970、錬金＝アルケオーニスグリモア 31060。どの品のレシピにも、要るクエスト・秘伝書は無い）。
/// </summary>
public static class ScripCollectable
{
    // （設定の品, 職）→ 同じ段の品（無ければ 0）。ゲームデータは動かないので、1回引いたら覚えておく
    private static readonly ConcurrentDictionary<(uint Configured, uint ClassJob), uint> SameTier = new();

    /// <summary>
    /// その職の、設定の品と同じ窓口・同じ段・同じ報酬の通貨の収集品（無ければ 0）。設定の品がその職の品なら、それ自身を返す。
    /// </summary>
    public static uint SameTierFor(uint configured, uint classJobId)
        => SameTier.GetOrAdd((configured, classJobId), k => Lookup(k.Configured, k.ClassJob));

    private static uint Lookup(uint configured, uint classJobId)
    {
        if (!Jobs.Crafters.Contains(classJobId))
            return 0;

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
            return 0;

        // その行を持つ窓口の、その職のタブ（ShopItems[職−8]：BookData.ResolveCollectable と同じ読み方）から、同じ段・同じ通貨の品
        var slot = (int)classJobId - 8;
        foreach (var shop in Svc.Data.GetExcelSheet<CollectablesShop>())
        {
            var slots = shop.ShopItems;
            if (!Enumerable.Range(0, slots.Count).Any(s => slots[s].RowId == row))
                continue;
            if (slot < 0 || slot >= slots.Count || !items.TryGetRow(slots[slot].RowId, out var target))
                continue;
            foreach (var it in target)
            {
                if (it.Item.RowId != 0 && it.LevelMin == min && it.LevelMax == max
                    && it.CollectablesShopRewardScrip.ValueNullable?.Currency == currency)
                    return it.Item.RowId;
            }
        }

        return 0;
    }

    /// <summary>
    /// 紫貨を稼ぐ収集品を選ぶ。選ぶ順：
    ///  1) 秘伝書の要る職（<paramref name="preferredJobs"/> の順）の、同じ段の品で、作れるもの（ジョブに合わせる）
    ///  2) 設定の品（作れるなら）
    ///  3) ほかの製作職の、同じ段の品で、作れるもの
    /// どれも作れなければ設定の品を返す（秘伝書の下準備で、作れる職がいない理由を出して止まる：JobQuestFlow.WaitBookData）。
    /// </summary>
    /// <param name="canCraft">その品を、いまのキャラクターの職で作れるか（レベル・ギアセット・装備。試すときに差し替える）。</param>
    public static uint Choose(uint configured, IEnumerable<uint> preferredJobs, Func<uint, bool> canCraft)
    {
        foreach (var job in preferredJobs.Distinct())
        {
            var item = SameTierFor(configured, job);
            if (item != 0 && canCraft(item))
                return item;
        }

        if (canCraft(configured))
            return configured;

        foreach (var job in Jobs.Crafters)
        {
            var item = SameTierFor(configured, job);
            if (item != 0 && canCraft(item))
                return item;
        }

        return configured;
    }

    /// <summary>今のキャラクターの状態で選ぶ（フレームワークのスレッドから呼ぶ。レベルとギアセットをその時点で写し取る）。</summary>
    public static uint ChooseFromGame(uint configured, IEnumerable<uint> preferredJobs, CraftPlanner? planner)
    {
        if (planner == null)
            return configured;
        var ability = CraftAbility.FromGame();
        return Choose(configured, preferredJobs, item => planner.Pick(item, ability) != null);
    }

    /// <summary>
    /// どの製作職も、設定の品と同じ段の品を作れない理由（職ごと。例：「木工師 Lv1・レシピ Lv50／錬金術師 ギアセットが無い」）。
    /// </summary>
    public static string WhyNone(uint configured, CraftPlanner planner, CraftAbility ability)
        => string.Join("／", Jobs.Crafters
            .Select(job => (Job: job, Item: SameTierFor(configured, job)))
            .Where(x => x.Item != 0)
            .Select(x => $"{Jobs.Name(x.Job)}（{CraftPlanner.ItemName(x.Item)}）{ability.WhyNot(x.Job, planner.Pick(x.Item) is { } r ? CraftAbility.RecipeLevel(r) : 999) ?? "作れる"}"));

    /// <summary>記録・点検に出す説明（例：「収集用のアルケオーニスグリモア（錬金術師の品。設定の『収集用のシーダーロングボウ』と同じ収集品納品の Lv50 の段）」）。</summary>
    public static string Describe(uint item, uint configured, CraftPlanner? planner)
    {
        var job = planner?.Pick(item) is { } r ? Jobs.Name(Jobs.CraftTypeToClassJob(r.CraftType.RowId)) : "?";
        return item == configured
            ? $"{CraftPlanner.ItemName(item)}（{job}の品。設定の品）"
            : $"{CraftPlanner.ItemName(item)}（{job}の品。設定の「{CraftPlanner.ItemName(configured)}」と同じ段の品を、ジョブに合わせて選びました）";
    }
}
