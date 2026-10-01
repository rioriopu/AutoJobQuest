using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoJobQuest.Data;

/// <summary>
/// 素材集めの戦闘で狙わない敵（エリア×敵の名前の番号。利用者が画面で選ぶ：デバッグタブの「モンスターを倒して素材を集める」の各素材の行のチェック）。
/// 例：アルドゴートの粗皮は東ザナラーンのミオトラグス・ナニーとビリーだけ（ほかの敵は探してもいない）、
/// アンテロープの角はアンテロープ・スタッグだけ。出現のデータ（LuminaSupplemental の MobSpawn）には、FATE の取り巻きや普段いない敵も入っていて、
/// ゲームデータだけでは見分けられない（出現点が1か所の敵には、本当にいる敵〔フリーズドラゴン〕もいない敵もいる。FATE の場所からの距離も重なる）。
/// 置き場所はこのプラグインの設定フォルダの mob_skips.json（2つのゲームで共有：5秒ごとに読み直し、変えるときは読み直してから書く）。
/// 敵の番号はコードに書かず、利用者の選んだものだけを持つ。
/// </summary>
public static class MobSkips
{
    /// <summary>検証の仕組み用：設定すると、ファイルの代わりにこの場所を使う。</summary>
    public static string? TestPath { get; set; }

    private static HashSet<string>? cache;
    private static DateTime cachedAt = DateTime.MinValue;

    private static string? FilePath
    {
        get
        {
            if (TestPath != null)
                return TestPath;
            try
            {
                return Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "mob_skips.json");
            }
            catch
            {
                return null; // 検証の仕組み（Dalamud の無いところ）では読まない
            }
        }
    }

    private static string Key(uint territory, uint nameId) => $"{territory}:{nameId}";

    private static HashSet<string> Read(bool fresh)
    {
        if (!fresh && cache != null && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(5))
            return cache;
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (FilePath is { } p && File.Exists(p))
                set = (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p)) ?? []).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"狙わない敵の設定（mob_skips.json）を読めませんでした（{ex.GetType().Name}: {ex.Message}）。全部の敵を狙います");
        }

        cache = set;
        cachedAt = DateTime.UtcNow;
        return set;
    }

    /// <summary>そのエリアのその敵を狙わないか。</summary>
    public static bool IsSkipped(uint territory, uint nameId) => Read(false).Contains(Key(territory, nameId));

    /// <summary>狙う・狙わないを変えて保存する（読み直してから書く）。</summary>
    public static void Set(uint territory, IEnumerable<uint> nameIds, bool skip)
    {
        var set = Read(true);
        foreach (var id in nameIds)
        {
            if (skip)
                set.Add(Key(territory, id));
            else
                set.Remove(Key(territory, id));
        }

        try
        {
            if (FilePath is { } p)
                File.WriteAllText(p, JsonSerializer.Serialize(set.OrderBy(x => x, StringComparer.Ordinal).ToList()));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"狙わない敵の設定（mob_skips.json）を保存できませんでした（{ex.GetType().Name}: {ex.Message}）");
        }

        cache = set;
        cachedAt = DateTime.UtcNow;
    }
}
