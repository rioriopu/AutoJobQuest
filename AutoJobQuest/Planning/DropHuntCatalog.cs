using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Data;

namespace AutoJobQuest.Planning;

/// <summary>倒しに行くモンスター（エリアごと。名前が同じで番号の違うモンスターは1つにまとめる）。</summary>
/// <param name="NameIds">モンスターの名前（BNpcName の行。同じ名前の別の番号も含む）。</param>
/// <param name="Name">モンスターの名前（画面用）。</param>
/// <param name="Territory">エリア（TerritoryType の行）。</param>
/// <param name="TerritoryName">エリアの名前（画面用）。</param>
/// <param name="Spots">そのエリアの出現点（地図座標。LuminaSupplemental の MobSpawn）と、その点のモンスター。</param>
public sealed record DropHuntMob(List<uint> NameIds, string Name, uint Territory, string TerritoryName, List<(Vector2 Spot, uint Mob)> Spots);

/// <summary>モンスターのドロップで集める素材1つ。</summary>
/// <param name="ItemId">品。</param>
/// <param name="ItemName">品の名前。</param>
/// <param name="Needed">全ジョブクエで要る数（在庫 0 から全部作るとき）。</param>
/// <param name="NeededBy">要るジョブクエ（「革細工師 Lv45×4」など）。</param>
/// <param name="FirstRoute">本来の最初の入手手段（NPC 購入が先の品もある）。</param>
/// <param name="FirstLevel">要るジョブクエのいちばん低いレベル（並べる順）。</param>
/// <param name="Mobs">落とすモンスター（エリアごと）。</param>
public sealed record DropHuntEntry(uint ItemId, string ItemName, int Needed, string NeededBy, Route FirstRoute, int FirstLevel, List<DropHuntMob> Mobs);

/// <summary>
/// デバッグ：モンスターのドロップで集める素材の一覧（各素材を落とすモンスターを倒しに行くデバッグ）。
/// 全ジョブクエ（製作8職＋採集3職・Lv1〜70）を在庫 0 から全部作るときの末端の素材のうち、入手手段に戦闘がある品（落とすモンスターの
/// 出現点が分かっている品）。重いので、画面の外（作業用のスレッド）で作る。NPC で買える品は、画面で今のキャラクターが買えるかを見て外す
/// （<see cref="BuyableFromNpc"/>。店にクエストの条件があり未完了なら、本番でも討伐で集めるので残す）。
/// </summary>
public static class DropHuntCatalog
{
    /// <summary>
    /// 今のキャラクターが NPC から買えるか（条件なしの店か、要るクエストをすべて終えた店が1つでもある）。買えるなら討伐は要らないので、
    /// デバッグの一覧から外す。計画の判定（PlanBuilder.RouteBlockers の NPC 購入）と同じ見方。
    /// </summary>
    public static bool BuyableFromNpc(SourceIndex sources, uint itemId, System.Func<uint, bool> isComplete)
        => sources.Get(itemId).VendorOffers.Any(o => !o.Unknown && o.Quests.All(isComplete));

    /// <summary>NPC の店で買うのに要る、まだ終えていないクエスト（画面の説明用）。</summary>
    public static string VendorNeeds(SourceIndex sources, uint itemId, System.Func<uint, bool> isComplete)
    {
        var offers = sources.Get(itemId).VendorOffers;
        var quests = offers.SelectMany(o => o.Quests).Where(q => !isComplete(q)).Distinct().Select(q => $"「{Unlocks.QuestName(q)}」").ToList();
        return quests.Count > 0 ? $"クエスト{string.Join("か", quests)}の完了" : "確かめられない条件（アチーブメント等）";
    }

    public static List<DropHuntEntry> Build(GameDataCache data)
    {
        // 製作の計画の部品は自分専用に作る（作業用のスレッドで使うので、画面の計画と同じ部品を同時に使わない。GameDataCache も毎回新しく作る）
        var sources = data.Sources!;
        var planner = new CraftPlanner();
        var needed = new Dictionary<uint, int>();
        var neededBy = new Dictionary<uint, List<(int Level, string Text)>>();
        foreach (var q in data.Quests!.Quests)
        {
            var targets = q.Items.Where(i => !q.AfterAcceptItems.Contains(i.ItemId)).Select(i => PlanBuilder.PlanAsHq(planner, i));
            foreach (var (item, count) in planner.Build(targets, new GameDataCache.EmptyInventory(), _ => true).RawTotal)
            {
                needed[item] = needed.GetValueOrDefault(item) + count;
                if (!neededBy.TryGetValue(item, out var list))
                    neededBy[item] = list = [];
                list.Add((q.Level, $"{Jobs.Name(q.ClassJobId)} Lv{q.Level}×{count}"));
            }
        }

        var names = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>();
        var result = new List<DropHuntEntry>();
        foreach (var (item, count) in needed)
        {
            var routes = PlanBuilder.ChooseRoutes(sources, item);
            if (!routes.Contains(Route.Combat))
                continue;

            // 名前とエリアが同じものは1つにまとめる（ゲームデータでは別の番号でも、画面では区別できないため）
            var mobs = sources.Get(item).DropMobs
                .SelectMany(mob => sources.SpawnsOf(mob).Select(s => (Mob: mob, Name: names.TryGetRow(mob, out var row) ? row.Singular.ExtractText() : $"モンスター {mob}", Spot: s)))
                .GroupBy(x => (x.Name, x.Spot.Territory))
                .Select(g => new DropHuntMob(g.Select(x => x.Mob).Distinct().ToList(), g.Key.Name, g.Key.Territory, Automation.TeleportTask.TerritoryName(g.Key.Territory),
                    g.Select(x => (new Vector2(x.Spot.MapX, x.Spot.MapY), x.Mob)).ToList()))
                .ToList();

            if (mobs.Count == 0)
                continue;
            var by = neededBy[item].OrderBy(x => x.Level).ToList();
            result.Add(new DropHuntEntry(item, CraftPlanner.ItemName(item), count, string.Join("・", by.Select(x => x.Text)), routes[0], by[0].Level,
                mobs.OrderBy(m => m.Name).ThenBy(m => m.TerritoryName).ToList()));
        }

        // 本来の最初の手段が戦闘の品（実際にモンスターを倒して集める品）を先に、NPC 購入などが先の品を後に
        return result.OrderBy(e => e.FirstRoute == Route.Combat ? 0 : 1).ThenBy(e => e.FirstLevel).ThenBy(e => e.ItemName).ToList();
    }
}
