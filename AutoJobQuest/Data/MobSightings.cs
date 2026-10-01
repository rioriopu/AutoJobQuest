using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace AutoJobQuest.Data;

/// <summary>
/// 戦闘で実際に見かけた目当ての敵の位置（このプラグインが自分で記録し、次から出現点の候補に足す）。
/// 討伐を自動化する他のツールにある討伐手帳の機能の考え方を、データを使わずに独自に作ったもの：
/// フィールドの敵の湧く場所はゲームのデータに入っていない（クルザス西部高地〔397〕の配置ファイル6本で、戦闘する敵の置き場所は0件だった）。
/// 出現点の元データ（LuminaSupplemental の MobSpawn）は地図の座標だけで、敵によっては1か所しかない（フリーズドラゴン）。
/// 記録するのは、こちらが狙う敵だけ（地域×名前ごとに、互いに <see cref="Apart"/> m 以上離れた位置を <see cref="MaxPerMob"/> か所まで）。
/// 置き場所はこのプラグインの設定フォルダの mob_sightings.json。
/// </summary>
public static class MobSightings
{
    /// <summary>同じ場所とみなす距離（これより近い位置は記録しない）。</summary>
    public const float Apart = 20f;

    /// <summary>地域×名前ごとに記録する位置の上限。</summary>
    public const int MaxPerMob = 20;

    private static Dictionary<string, List<float[]>>? data;
    private static bool dirty;
    private static DateTime savedAt = DateTime.MinValue;

    /// <summary>検証の仕組み用：設定すると、ファイルの代わりにこの場所を使う。</summary>
    public static string? TestPath { get; set; }

    private static string FilePath => TestPath ?? Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "mob_sightings.json");

    private static string Key(uint territory, uint nameId) => $"{territory}:{nameId}";

    private static Dictionary<string, List<float[]>> Data
    {
        get
        {
            if (data != null)
                return data;
            try
            {
                data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch
            {
                data = [];
            }

            return data;
        }
    }

    /// <summary>見かけた位置を記録する（近い位置が既にあれば記録しない）。記録したら true。</summary>
    public static bool Record(uint territory, uint nameId, Vector3 position)
    {
        var key = Key(territory, nameId);
        if (!Data.TryGetValue(key, out var list))
            Data[key] = list = [];
        if (list.Count >= MaxPerMob || list.Any(p => Vector3.Distance(new Vector3(p[0], p[1], p[2]), position) < Apart))
            return false;
        list.Add([position.X, position.Y, position.Z]);
        dirty = true;
        return true;
    }

    /// <summary>その地域で、指定の名前の敵を見かけた位置。</summary>
    public static List<Vector3> Get(uint territory, IEnumerable<uint> nameIds)
        => nameIds.SelectMany(n => Data.TryGetValue(Key(territory, n), out var list) ? list : [])
                  .Select(p => new Vector3(p[0], p[1], p[2]))
                  .ToList();

    /// <summary>変わっていれば保存する（force でなければ30秒に1回まで）。</summary>
    public static void Save(bool force = false)
    {
        if (!dirty || (!force && DateTime.UtcNow - savedAt < TimeSpan.FromSeconds(30)))
            return;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data));
            dirty = false;
            savedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"見かけた敵の位置を保存できませんでした：{ex.Message}");
        }
    }
}
