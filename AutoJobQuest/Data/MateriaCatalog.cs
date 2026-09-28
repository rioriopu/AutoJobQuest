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
    /// 「種類不問」のときに付けるマテリア（設定 <see cref="Configuration.AnyMateriaItemId"/>。既定は剛柔のマテリア）を、
    /// その納品物に付けられるか確かめて返す。付けられなければ null と理由。
    /// </summary>
    public static uint? ResolveAny(uint configured, uint targetItemId, out string? problem)
    {
        problem = null;
        var items = Svc.Data.GetExcelSheet<Item>();
        if (configured == 0 || !items.TryGetRow(configured, out var materia))
        {
            problem = $"設定の「任意のマテリアに使う品」（{configured}）がアイテムとして見つかりません";
            return null;
        }

        if (!IsMateria(configured))
        {
            problem = $"設定の「任意のマテリアに使う品」{materia.Name.ExtractText()} はマテリアではありません";
            return null;
        }

        if (items.TryGetRow(targetItemId, out var target) && materia.LevelItem.RowId > target.LevelItem.RowId)
        {
            problem = $"{materia.Name.ExtractText()}（アイテムLv{materia.LevelItem.RowId}）は {target.Name.ExtractText()}（アイテムLv{target.LevelItem.RowId}）に付けられません";
            return null;
        }

        return configured;
    }
}
