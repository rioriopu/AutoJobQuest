using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace AutoJobQuest.Data;

/// <summary>AutoHook の魚のデータ1件（導入版の Data\FishData\fish_list.json）。</summary>
/// <param name="ItemId">品。</param>
/// <param name="IsSpearFish">刺突漁の魚か。</param>
/// <param name="Size">大きさ（AutoHook の SpearfishSize：1＝小・2＝中・3＝大。ゲームの刺突漁の画面の値と同じ）。</param>
/// <param name="Speed">速さ（AutoHook の SpearfishSpeed：100〜600 の50刻み。ゲームの刺突漁の画面の値と同じ）。</param>
/// <param name="Predators">前提の魚（品, 数）。</param>
public sealed record AutoHookFish(uint ItemId, bool IsSpearFish, int Size, int Speed, IReadOnlyList<(uint ItemId, int Quantity)> Predators)
{
    /// <summary>釣れる天気（ゲームデータ Weather の行。空ならいつでも）。例：雨乞魚（4905）は 7・15（導入版 6.0.2.3）。</summary>
    public IReadOnlyList<uint> Weathers { get; init; } = [];

    /// <summary>
    /// 釣れる時間帯（エオルゼア時間の始まりの時と、続く時間。AutoHook の Spawn・Duration。17.5 時・0.5 時間のような小数もある）。
    /// 無ければ null（いつでも）。例：フルムーンサーディン（4898）は 18 時から 12 時間＝18:00〜06:00（導入版 6.0.2.3）。
    /// </summary>
    public (double Start, double Hours)? Window { get; init; }
}

/// <summary>
/// AutoHook のファイル（魚のデータ・設定）を読み、AutoHook に渡すプリセットの文字列を作る
/// （漁師 Lv68 の刺突漁。調べは導入版 6.0.2.3 を逆コンパイルして確かめた）。
///
/// 【AutoHook が突く魚の決め方】刺突の画面の魚の「大きさ・速さ」と、プリセットの項目の魚（品）の「大きさ・速さ」
/// （AutoHook の魚のデータから引く）が一致したら突く（GigComponent.FindGigForFish → FindGigForPool）。魚の品では照らさない。
/// そのため、データの大きさ・速さがゲームと合わない魚は、選んでも突かない（大方士はデータが「小・2」で、ゲームの速さは100〜600）。
///
/// 【プリセットの文字列】刺突漁は "AHSF1_"＋（JSON を GZip で縮めて Base64）。竿のプリセットは "AH11_"＋（JSON を Brotli で縮めて Base64）。
/// どちらも AutoHook の IPC「ImportAndSelectPreset」で取り込ませる（Configuration.ImportPreset が前置詞で見分ける）。
/// </summary>
public static class AutoHookData
{
    /// <summary>XIVLauncher のフォルダ（pluginConfigs の1つ上）。</summary>
    private static string? LauncherDirectory
        => Svc.PluginInterface.ConfigDirectory.Parent?.Parent?.FullName;

    /// <summary>AutoHook の設定ファイル（pluginConfigs\AutoHook.json）。</summary>
    public static string? ConfigPath
        => Svc.PluginInterface.ConfigDirectory.Parent?.FullName is { } dir ? Path.Combine(dir, "AutoHook.json") : null;

    /// <summary>
    /// 読み込まれている AutoHook の魚のデータの場所（installedPlugins\AutoHook\&lt;版&gt;\Data\FishData\fish_list.json）。
    /// 読み込まれている版のフォルダに無ければ、ある版のうち一番新しいもの。見つからなければ null。
    /// </summary>
    public static string? FishListPath(Version? loaded)
    {
        if (LauncherDirectory is not { } root)
            return null;
        var baseDir = Path.Combine(root, "installedPlugins", "AutoHook");
        if (!Directory.Exists(baseDir))
            return null;
        if (loaded != null)
        {
            var exact = Path.Combine(baseDir, loaded.ToString(), "Data", "FishData", "fish_list.json");
            if (File.Exists(exact))
                return exact;
        }

        return Directory.GetDirectories(baseDir)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(x => x.Version != null && File.Exists(Path.Combine(x.Dir, "Data", "FishData", "fish_list.json")))
            .OrderByDescending(x => x.Version)
            .Select(x => Path.Combine(x.Dir, "Data", "FishData", "fish_list.json"))
            .FirstOrDefault();
    }

    /// <summary>魚のデータを読む（品 → 魚）。</summary>
    public static Dictionary<uint, AutoHookFish> LoadFishList(string path)
    {
        var result = new Dictionary<uint, AutoHookFish>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            if (!f.TryGetProperty("ItemId", out var idv) || idv.ValueKind != JsonValueKind.Number || idv.GetInt64() <= 0)
                continue;
            var id = (uint)idv.GetInt64();
            var predators = new List<(uint, int)>();
            if (f.TryGetProperty("Predators", out var pv) && pv.ValueKind == JsonValueKind.Array)
                foreach (var p in pv.EnumerateArray())
                    if (p.TryGetProperty("ItemId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.GetInt64() > 0)
                        predators.Add(((uint)pid.GetInt64(), p.TryGetProperty("Quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 0));
            result.TryAdd(id, new AutoHookFish(
                id,
                f.TryGetProperty("IsSpearFish", out var sv) && sv.ValueKind == JsonValueKind.True,
                f.TryGetProperty("Size", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetInt32() : 0,
                f.TryGetProperty("Speed", out var spv) && spv.ValueKind == JsonValueKind.Number ? spv.GetInt32() : 0,
                predators)
            {
                Weathers = f.TryGetProperty("Weathers", out var wv) && wv.ValueKind == JsonValueKind.Array
                    ? wv.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.Number && w.GetInt64() > 0).Select(w => (uint)w.GetInt64()).ToList()
                    : [],
                Window = f.TryGetProperty("Spawn", out var st) && st.ValueKind == JsonValueKind.Number
                         && f.TryGetProperty("Duration", out var du) && du.ValueKind == JsonValueKind.Number
                         && du.GetDouble() is > 0 and < 24
                    ? (st.GetDouble() % 24, du.GetDouble())
                    : null,
            });
        }

        return result;
    }

    /// <summary>ゲームの刺突の画面に出うる速さか（100〜600 の50刻み。ClientStructs の AddonSpearFishing.FishInfo の注記）。</summary>
    public static bool IsGameSpeed(int speed) => speed is >= 100 and <= 600 && speed % 50 == 0;

    /// <summary>ゲームの刺突の画面に出うる大きさか（1＝小・2＝中・3＝大）。</summary>
    public static bool IsGameSize(int size) => size is >= 1 and <= 3;

    /// <summary>大きさ・速さの呼び名（記録用。AutoHook の画面の英語の呼び名と同じ並び）。</summary>
    public static string Describe(int size, int speed)
        => $"{size switch { 1 => "小", 2 => "中", 3 => "大", _ => $"大きさ{size}" }}・速さ{speed}";

    /// <summary>
    /// 刺突漁のプリセットの文字列（"AHSF1_"）。項目ごとに品と漁場（刺突漁の表 SpearfishingNotebook の行。0 はどの漁場でも）を入れる。
    /// AutoHook は取り込むと、その場で選び、刺突の自動（AutoGig）を ON にする（SpearFishingPresets.AddNewPreset）。
    /// </summary>
    public static string SpearfishPresetString(string presetName, IEnumerable<(uint ItemId, uint Notebook)> gigs, int hitboxSize = 25)
    {
        var json = new JObject
        {
            ["PresetName"] = presetName,
            ["HitboxSize"] = hitboxSize,
            ["Gigs"] = new JArray(gigs.Select(g => new JObject
            {
                ["Enabled"] = true,
                ["ItemId"] = (int)g.ItemId,
                ["SpearfishingNotebookId"] = g.Notebook,
            })),
        };
        return "AHSF1_" + Convert.ToBase64String(Compress(Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)), brotli: false));
    }

    /// <summary>
    /// 竿のプリセットの文字列（"AH11_"。名前だけの、中身は AutoHook の既定の値のもの）。
    /// GBR は「Use existing AutoHook presets」が ON のとき、魚の番号と同じ名前の竿のプリセットがあれば、刺突漁でも自分のプリセットを作らない
    /// （GBR の AutoGather.AutoHook.cs の FindAutoHookPreset。刺突漁では竿のプリセットは使われない）。その「名前の札」に使う。
    /// </summary>
    public static string RodPresetString(string presetName)
    {
        var json = new JObject { ["PresetName"] = presetName };
        return "AH11_" + Convert.ToBase64String(Compress(Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)), brotli: true));
    }

    /// <summary>プリセットの文字列を JSON に戻す（記録・試験用）。</summary>
    public static string? DecodePresetString(string preset)
    {
        var brotli = preset.StartsWith("AH11_", StringComparison.Ordinal);
        var cut = preset.IndexOf('_');
        if (cut < 0)
            return null;
        using var input = new MemoryStream(Convert.FromBase64String(preset[(cut + 1)..]));
        using Stream z = brotli ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] Compress(byte[] bytes, bool brotli)
    {
        using var ms = new MemoryStream();
        using (Stream z = brotli ? new BrotliStream(ms, CompressionLevel.SmallestSize) : new GZipStream(ms, CompressionMode.Compress))
            z.Write(bytes, 0, bytes.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// AutoHook の設定の、竿のプリセットの名前の一覧・いま選ばれている竿のプリセットの名前・刺突漁のプリセットの名前の一覧・刺突の自動の ON/OFF
    /// （読めなければ null）。
    /// </summary>
    public static (List<string> RodPresets, string? SelectedRodPreset, List<string> GigPresets, bool? AutoGigEnabled)? ReadConfig(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var root = JObject.Parse(File.ReadAllText(path));
            var hooks = root["HookPresets"] as JObject;
            var rods = (hooks?["CustomPresets"] as JArray)?.OfType<JObject>().ToList() ?? [];
            var selectedGuid = hooks?["SelectedGuid"]?.ToString();
            var selected = rods.FirstOrDefault(p => !string.IsNullOrEmpty(selectedGuid) && p["UniqueId"]?.ToString() == selectedGuid)?["PresetName"]?.ToString();
            var gigConfig = root["AutoGigConfig"] as JObject;
            var gigs = (gigConfig?["Presets"] as JArray)?.OfType<JObject>().Select(p => p["PresetName"]?.ToString() ?? string.Empty).ToList() ?? [];
            var autoGig = gigConfig?["AutoGigEnabled"]?.Type == JTokenType.Boolean ? (bool?)gigConfig["AutoGigEnabled"]!.Value<bool>() : null;
            return (rods.Select(p => p["PresetName"]?.ToString() ?? string.Empty).ToList(), selected, gigs, autoGig);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("刺突漁", $"AutoHook の設定を読めませんでした（{ex.GetType().Name}: {ex.Message}）");
            return null;
        }
    }
}
