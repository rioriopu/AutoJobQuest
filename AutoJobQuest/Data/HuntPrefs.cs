using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace AutoJobQuest.Data;

/// <summary>
/// 素材集めの戦闘の、利用者の決めごと（デバッグタブの「モンスターを倒して素材を集める」の各素材の行で選ぶ。本番の素材集めの戦闘にも効く）。
///  ・狙わない敵（素材×エリア×敵の名前の番号）：普段いない敵・その素材を落とさない敵・行かないエリア。
///    出現のデータ（LuminaSupplemental の MobSpawn）には、FATE の取り巻きや普段いない敵も入っていて、ゲームデータだけでは見分けられない
///    （出現点が1か所の敵には、本当にいる敵〔フリーズドラゴン〕もいない敵もいる。FATE の場所からの距離も重なる）。
///    素材ごとに持つ（例：マイトリングはダイアマイトウェブを落とさない。同じ敵でも別の素材は落としうる）。
///  ・後回しのエリア（素材×エリア）：ほかに行けるエリアで見つからなくなったときだけ行く
///    （例：アンフィプテレの粗皮はアジス・ラーで集め、見つからなくなったらドラヴァニア雲海へ）。
///  ・自動で外した敵を戻す（エリア×敵）：クエスト専用の敵としてゲームのデータで外した敵（EventOnlySpawns）が、実は普段からいるとき。
///  ・狩り場（素材×エリア×ワールド座標）：その素材の敵は、データの出現点の代わりにここを回って探す
///    （例：北部森林のベーンマイトは決まった場所に固まっているので、そこへ飛んで探す）。
/// 置き場所はこのプラグインの設定フォルダの hunt_prefs.json（2つのゲームで共有：5秒ごとに読み直し、変えるときは読み直してから書く）。
/// 敵の番号・座標はコードに書かず、利用者の選んだものだけを持つ。
/// </summary>
public static class HuntPrefs
{
    /// <summary>ファイルの中身。</summary>
    public sealed class Store
    {
        /// <summary>狙わない敵（「素材:エリア:敵の名前の番号」）。</summary>
        public List<string> Skips { get; set; } = [];

        /// <summary>狩り場。</summary>
        public List<Spot> Spots { get; set; } = [];

        /// <summary>後回しのエリア（「素材:エリア」）：ほかに行けるエリアで見つからなくなったときだけ行く。</summary>
        public List<string> Later { get; set; } = [];

        /// <summary>クエスト専用として自動で外した敵のうち、戻すもの（「エリア:敵の名前の番号」。EventOnlySpawns）。</summary>
        public List<string> Restored { get; set; } = [];
    }

    /// <summary>狩り場（ワールド座標）。</summary>
    public sealed record Spot(uint Item, uint Territory, float X, float Y, float Z);

    /// <summary>検証の仕組み・調べ道具用：設定すると、ファイルの代わりにこの場所を使う。</summary>
    public static string? TestPath { get; set; }

    private static Store? cache;
    private static HashSet<string> cachedSkips = new(StringComparer.Ordinal); // 画面が毎フレーム引くので、引きやすい形でも持つ
    private static HashSet<string> cachedLater = new(StringComparer.Ordinal);
    private static HashSet<string> cachedRestored = new(StringComparer.Ordinal);
    private static DateTime cachedAt = DateTime.MinValue;

    private static string? FilePath
    {
        get
        {
            if (TestPath != null)
                return TestPath;
            try
            {
                return Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "hunt_prefs.json");
            }
            catch
            {
                return null; // 検証の仕組み（Dalamud の無いところ）では読まない
            }
        }
    }

    private static string Key(uint itemId, uint territory, uint nameId) => $"{itemId}:{territory}:{nameId}";

    private static Store Read(bool fresh)
    {
        if (!fresh && cache != null && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(5))
            return cache;
        var store = new Store();
        try
        {
            if (FilePath is { } p && File.Exists(p))
                store = JsonSerializer.Deserialize<Store>(File.ReadAllText(p)) ?? new Store();
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"素材集めの戦闘の決めごと（hunt_prefs.json）を読めませんでした（{ex.GetType().Name}: {ex.Message}）。全部の敵を狙い、狩り場は使いません");
        }

        Remember(store);
        return store;
    }

    private static void Remember(Store store)
    {
        cache = store;
        cachedSkips = store.Skips.ToHashSet(StringComparer.Ordinal);
        cachedLater = store.Later.ToHashSet(StringComparer.Ordinal);
        cachedRestored = store.Restored.ToHashSet(StringComparer.Ordinal);
        cachedAt = DateTime.UtcNow;
    }

    private static void Write(Store store)
    {
        try
        {
            store.Skips = store.Skips.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            store.Later = store.Later.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            store.Restored = store.Restored.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (FilePath is { } p)
                File.WriteAllText(p, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"素材集めの戦闘の決めごと（hunt_prefs.json）を保存できませんでした（{ex.GetType().Name}: {ex.Message}）");
        }

        Remember(store);
    }

    /// <summary>その素材を集めるときに、そのエリアのその敵を狙わないか。</summary>
    public static bool IsSkipped(uint itemId, uint territory, uint nameId)
    {
        Read(false);
        return cachedSkips.Contains(Key(itemId, territory, nameId));
    }

    /// <summary>狙う・狙わないを変えて保存する（読み直してから書く）。</summary>
    public static void SetSkipped(uint itemId, uint territory, IEnumerable<uint> nameIds, bool skip)
    {
        var store = Read(true);
        foreach (var id in nameIds)
        {
            store.Skips.Remove(Key(itemId, territory, id));
            if (skip)
                store.Skips.Add(Key(itemId, territory, id));
        }

        Write(store);
    }

    /// <summary>その素材を集めるときに、そのエリアは後回しか。</summary>
    public static bool IsLater(uint itemId, uint territory)
    {
        Read(false);
        return cachedLater.Contains($"{itemId}:{territory}");
    }

    /// <summary>後回しにする・しないを変えて保存する（読み直してから書く）。</summary>
    public static void SetLater(uint itemId, uint territory, bool later)
    {
        var store = Read(true);
        store.Later.Remove($"{itemId}:{territory}");
        if (later)
            store.Later.Add($"{itemId}:{territory}");
        Write(store);
    }

    /// <summary>クエスト専用として自動で外した、そのエリアのその敵を、利用者が戻したか。</summary>
    public static bool IsRestored(uint territory, uint nameId)
    {
        Read(false);
        return cachedRestored.Contains($"{territory}:{nameId}");
    }

    /// <summary>自動で外した敵を戻す・戻さないを変えて保存する（読み直してから書く）。</summary>
    public static void SetRestored(uint territory, uint nameId, bool restored)
    {
        var store = Read(true);
        store.Restored.Remove($"{territory}:{nameId}");
        if (restored)
            store.Restored.Add($"{territory}:{nameId}");
        Write(store);
    }

    /// <summary>その素材のそのエリアの狩り場（無ければ空）。</summary>
    public static List<Spot> Spots(uint itemId, uint territory)
        => Read(false).Spots.Where(s => s.Item == itemId && s.Territory == territory).ToList();

    /// <summary>狩り場を足す（読み直してから書く）。</summary>
    public static void AddSpot(uint itemId, uint territory, Vector3 at)
    {
        var store = Read(true);
        store.Spots.Add(new Spot(itemId, territory, MathF.Round(at.X, 1), MathF.Round(at.Y, 1), MathF.Round(at.Z, 1)));
        Write(store);
    }

    /// <summary>その素材のそのエリアの狩り場を全部消す（読み直してから書く）。</summary>
    public static void ClearSpots(uint itemId, uint territory)
    {
        var store = Read(true);
        store.Spots.RemoveAll(s => s.Item == itemId && s.Territory == territory);
        Write(store);
    }
}
