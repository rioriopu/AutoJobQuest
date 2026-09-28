using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// マテリアの対応表。
///
/// 【等級と列の関係】Materia.Item[n] の n が 0 始まりの等級（0=マテリア, 1=マテリラ, 2=マテリダ…）。
/// 装備に付いたマテリアは InventoryItem.Materia[i]（Materia の行）と MateriaGrades[i]（同じ 0 始まりの等級）で表れる。
/// 耐性系（行 26〜33）は Item[2] にだけ値があるので、「最初に値のある列を等級I」と数えてはいけない
/// （ゲームデータで実測）。
///
/// 【付けられる条件】マテリアのアイテムLv ≤ 装備のアイテムLv。
/// マテリア=15、マテリラ=30、マテリダ=45（ゲームデータ実測）。Lv20 の納品物（アイテムLv17〜20）には
/// 「〜のマテリア」しか付かない。Questionable の経路データの注記「Materia I (not II+)」とも一致する。
/// </summary>
public static class MateriaCatalog
{
    private static Dictionary<uint, (uint Row, int Grade)>? byItem;

    private static Dictionary<uint, (uint Row, int Grade)> Map
    {
        get
        {
            if (byItem != null)
                return byItem;

            var map = new Dictionary<uint, (uint, int)>();
            foreach (var m in Svc.Data.GetExcelSheet<Materia>())
            {
                for (var g = 0; g < m.Item.Count; g++)
                {
                    var it = m.Item[g].RowId;
                    if (it != 0)
                        map[it] = (m.RowId, g);
                }
            }

            byItem = map;
            return map;
        }
    }

    /// <summary>マテリアのアイテム ID から (Materia 行, 0 始まりの等級)。マテリアでなければ null。</summary>
    public static (uint Row, int Grade)? Find(uint materiaItemId)
        => Map.TryGetValue(materiaItemId, out var v) ? v : null;

    public static bool IsMateria(uint itemId) => Map.ContainsKey(itemId);

    /// <summary>
    /// 「種類不問」のときに候補にするマテリア（アイテム ID）。
    ///
    /// 条件:
    ///  ・対象装備のアイテムLv 以下のアイテムLv（付けられる等級）
    ///  ・効果値が 0 でない（剛力など効果値 0 の旧マテリアは今は付けても意味がなく、出品もまず無い）
    ///  ・対象装備が持っている能力値のマテリアを先に並べる（確実に付けられるものから試す）
    /// 同じ種類なら等級の低い（安い）ほうだけを候補にする。
    /// </summary>
    public static List<uint> CandidatesFor(uint targetItemId)
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        if (!items.TryGetRow(targetItemId, out var target))
            return [];

        var targetLevel = target.LevelItem.RowId;
        var ownParams = new HashSet<uint>();
        foreach (var bp in target.BaseParam)
            if (bp.RowId != 0)
                ownParams.Add(bp.RowId);

        var preferred = new List<uint>();
        var others = new List<uint>();
        foreach (var m in Svc.Data.GetExcelSheet<Materia>())
        {
            if (m.BaseParam.RowId == 0)
                continue;

            for (var g = 0; g < m.Item.Count; g++)
            {
                var it = m.Item[g].RowId;
                if (it == 0 || m.Value[g] <= 0)
                    continue;
                if (!items.TryGetRow(it, out var mi) || mi.LevelItem.RowId > targetLevel)
                    continue;

                (ownParams.Contains(m.BaseParam.RowId) ? preferred : others).Add(it);
                break; // 同じ種類は一番低い等級だけ
            }
        }

        return preferred.Concat(others).ToList();
    }
}
