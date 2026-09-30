using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// エーテライト・エーテルネットの中継点の位置。
///
/// Aetheryte 表の Level[0] が指す行は、いまの Level シートにほとんど無い（本体108行すべてと、中継点131行のうち103行：実測）。
/// 以前は Level だけで位置を引いていたので、中継点しか無いエリア（旧市街・ザル回廊・上甲板層など）へのエーテルネットの移動が必ず止まった。
/// そこで、地図の印（MapMarker）から求める。本体は DataType 3 で DataKey＝エーテライトの行、中継点は DataType 4 で DataKey＝AethernetName。
/// 地図の画素の座標を、地図の SizeFactor と Offset で世界の X・Z に直す。同じ印が広域の地図にも載るので、エーテライトのエリアの地図を優先する
/// （導入版の Questionable の QuestGameData.BuildAetherytePositions と同じ式。誤差は数m で、最寄りを選ぶ・近くのオブジェクトと照らすには足りる）。
/// 地図の印からは高さが分からない。
/// </summary>
public static class AetherytePlaces
{
    private static readonly object Gate = new();
    private static Dictionary<uint, Vector2>? byMarker;

    /// <summary>
    /// そのエーテライト・中継点の位置（水平の X・Z と、分かれば高さ）。求められなければ null。
    /// Level の行があればそれ（高さつき）、無ければ地図の印から（高さなし）。
    /// </summary>
    public static (Vector2 Flat, float? Height)? Of(uint aetheryteId)
    {
        if (!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var a))
            return null;
        var lv = a.Level.FirstOrDefault(l => l.RowId != 0);
        if (lv.RowId != 0 && Svc.Data.GetExcelSheet<Level>().TryGetRow(lv.RowId, out var row))
            return (new Vector2(row.X, row.Z), row.Y);
        return Markers().TryGetValue(aetheryteId, out var flat) ? (flat, null) : null;
    }

    /// <summary>地図の印から求めた水平の位置だけ（Level を見ない。変換の式を Level のある行と照らして確かめるため）。無ければ null。</summary>
    public static Vector2? MarkerPosition(uint aetheryteId) => Markers().TryGetValue(aetheryteId, out var flat) ? flat : null;

    /// <summary>水平の距離（高さは見ない。地図の印からは高さが分からないため）。求められなければ null。</summary>
    public static float? FlatDistance(uint aetheryteId, Vector3 position)
        => Of(aetheryteId) is { } p ? Vector2.Distance(p.Flat, new Vector2(position.X, position.Z)) : null;

    /// <summary>そのエーテライト・中継点の表示名（本体は PlaceName、中継点は AethernetName）。</summary>
    public static string Name(uint aetheryteId)
    {
        if (!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var a))
            return aetheryteId.ToString();
        var name = a.IsAetheryte ? a.PlaceName.ValueNullable?.Name.ExtractText() : a.AethernetName.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(name))
            name = a.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? aetheryteId.ToString() : name;
    }

    private static Dictionary<uint, Vector2> Markers()
    {
        lock (Gate)
        {
            if (byMarker != null)
                return byMarker;

            var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();
            var byAethernetName = new Dictionary<uint, uint>();
            foreach (var a in aetherytes)
                if (a.AethernetName.RowId != 0)
                    byAethernetName[a.AethernetName.RowId] = a.RowId;

            var markers = Svc.Data.GetSubrowExcelSheet<MapMarker>();
            var result = new Dictionary<uint, Vector2>();
            foreach (var map in Svc.Data.GetExcelSheet<Map>())
            {
                if (map.MapMarkerRange == 0 || map.SizeFactor == 0 || !markers.TryGetRow(map.MapMarkerRange, out var group))
                    continue;
                foreach (var marker in group)
                {
                    uint id = marker.DataType switch
                    {
                        3 => marker.DataKey.RowId,
                        4 when byAethernetName.TryGetValue(marker.DataKey.RowId, out var shard) => shard,
                        _ => 0,
                    };
                    if (id == 0)
                        continue;

                    var scale = map.SizeFactor / 100f;
                    var world = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);

                    // 広域の地図にも同じ印が載る。エーテライトのエリアの地図を優先する
                    var preferred = map.TerritoryType.RowId != 0 && aetherytes.TryGetRow(id, out var owner) && map.TerritoryType.RowId == owner.Territory.RowId;
                    if (preferred || !result.ContainsKey(id))
                        result[id] = world;
                }
            }

            byMarker = result;
            return result;
        }
    }
}
