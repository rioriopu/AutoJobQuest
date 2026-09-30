using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AutoJobQuest.Data;

/// <summary>ギアセットに登録された品1つ。</summary>
/// <param name="Gearset">ギアセットの番号。</param>
/// <param name="ItemId">品（HQ の +1,000,000 は外したもの）。</param>
/// <param name="Hq">HQ か。</param>
/// <param name="Materia">付いているマテリア（種類と等級）。</param>
public sealed record GearsetPiece(int Gearset, uint ItemId, bool Hq, IReadOnlyList<MateriaRef> Materia);

/// <summary>
/// 利用者のギアセットに登録された品を、納品物の手持ちとして数えない・渡さない・マテリアを付けない。
///
/// 【以前】所持数はカバンとアーマリーを数え、除くのは装備中の品だけだった。そのため、ほかの職のギアセットに入っている装備・道具
/// （例：鍛冶 Lv30 チョコボハチェット(HQ)＝園芸師の道具、甲冑 Lv35 スチールフライパン(HQ)＝調理師の道具）を「手持ち」と数えて作らずに渡し、
/// マテリア付きの装備（例：木工 Lv20 のランスに竜騎士のマテリア付きランス）を「装着済み」とみなして渡しえた（納品物 155 品目のうち 89 品目が装備品）。
/// 【いま】
///  ・マテリアの無い同じ品は物として区別できないので、数で守る：ギアセット1つが使う数（同じ品が複数のギアセットにあれば、その最大）から、
///    いま装備している数を引いた分を、手持ちから引いて数える（自分で作った同じ品は数えるので、作り直しを繰り返さない）。
///  ・マテリア付きの品は、付いたマテリアの種類と等級がギアセットの品と一致したら利用者の品とみなし、渡さない・装着済みに数えない。
/// ギアセットはゲームから読む（RaptureGearsetModule。GearsetItem の ItemId は HQ なら +1,000,000、マテリアは Materia の行と等級）。
/// </summary>
public static class GearsetGuard
{
    /// <summary>いまのギアセットに登録された品（全部のギアセットの分。読めなければ空）。</summary>
    public static unsafe List<GearsetPiece> Read()
    {
        var list = new List<GearsetPiece>();
        try
        {
            var m = RaptureGearsetModule.Instance();
            if (m == null)
                return list;
            for (var i = 0; i < 100; i++)
            {
                if (!m->IsValidGearset(i))
                    continue;
                var g = m->GetGearset(i);
                if (g == null)
                    continue;
                for (var k = 0; k < g->Items.Length; k++)
                {
                    var gi = g->Items[k];
                    if (gi.ItemId == 0)
                        continue;
                    var materia = new List<MateriaRef>();
                    for (var j = 0; j < 5; j++)
                    {
                        if (gi.Materia[j] != 0)
                            materia.Add(new MateriaRef(gi.Materia[j], gi.MateriaGrades[j]));
                    }

                    list.Add(new GearsetPiece(i, gi.ItemId % 1_000_000, gi.ItemId >= 1_000_000, materia));
                }
            }
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("所持", $"ギアセットを読めませんでした（{ex.Message}）。ギアセットの品を守る数えは使いません");
        }

        return list;
    }

    /// <summary>ギアセット1つが使う数（同じ品が複数のギアセットにあれば、その最大）。</summary>
    public static Dictionary<(uint ItemId, bool Hq), int> ProtectedCounts(IEnumerable<GearsetPiece> pieces)
        => pieces.GroupBy(p => (p.ItemId, p.Hq))
            .ToDictionary(g => g.Key, g => g.GroupBy(p => p.Gearset).Max(x => x.Count()));

    /// <summary>
    /// マテリア付きの品が、ギアセットの品（同じ品・品質・マテリアの種類と等級）と一致するか（利用者の品）。
    /// マテリアの無い品は区別できないので false（数で守る：<see cref="ProtectedCounts"/>）。
    /// </summary>
    public static bool IsProtectedMelded(uint itemId, bool hq, IReadOnlyList<MateriaRef> materia, IEnumerable<GearsetPiece> pieces)
    {
        if (materia.Count == 0)
            return false;
        var mine = materia.OrderBy(x => x.Id).ThenBy(x => x.Grade).ToList();
        return pieces.Any(p => p.ItemId == itemId && p.Hq == hq && p.Materia.Count == mine.Count
                               && p.Materia.OrderBy(x => x.Id).ThenBy(x => x.Grade).SequenceEqual(mine));
    }

    /// <summary>
    /// そのギアセットの品が、いま全部装備されているか（持っていない品は見ない：売った・捨てた品で着替え直しが止まらないように）。
    /// Questionable が推奨装備に着替えた後、ギアセットに着替え直したかを確かめるのに使う。
    /// </summary>
    public static bool GearsetEquipped(int gearset)
    {
        if (Automation.GameMemory.Test is { } test)
            return test.GearsetEquipped(gearset);
        var equipped = EquippedCounts();
        var owned = Inventory.Snapshot(subtractGearsets: false);
        return AllEquipped(Read().Where(p => p.Gearset == gearset).Select(p => (p.ItemId, p.Hq)),
            k => equipped.GetValueOrDefault(k), k => k.Hq ? owned.CountHq(k.ItemId) : owned.CountNq(k.ItemId));
    }

    /// <summary>
    /// ギアセットの品（品, HQ）が全部装備されているか（ゲームを起動せずに試せるように分けた）。
    /// 装備していない品でも、鞄・アーマリーチェストに無ければ（売った・捨てた）見ない。
    /// </summary>
    public static bool AllEquipped(IEnumerable<(uint ItemId, bool Hq)> pieces, Func<(uint ItemId, bool Hq), int> equipped, Func<(uint ItemId, bool Hq), int> owned)
        => pieces.All(k => equipped(k) > 0 || owned(k) <= 0);

    /// <summary>いま装備している品の数（HQ の区別あり）。</summary>

    public static unsafe Dictionary<(uint ItemId, bool Hq), int> EquippedCounts()
    {
        var d = new Dictionary<(uint, bool), int>();
        var im = InventoryManager.Instance();
        var c = im == null ? null : im->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null || !c->IsLoaded)
            return d;
        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s == null || s->ItemId == 0)
                continue;
            var key = (s->ItemId, (s->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
            d[key] = d.GetValueOrDefault(key) + 1;
        }

        return d;
    }
}
