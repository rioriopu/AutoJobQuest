using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoJobQuest.Data;

/// <summary>Questionable の経路の「Craft」手順1件。</summary>
/// <param name="Sequence">クエストの段。</param>
/// <param name="ItemId">作る品（無ければ null：在庫に関係なく Artisan の既製リストが動く）。</param>
/// <param name="ItemCount">持っていれば飛ばす数。</param>
/// <param name="Hq">HQ で数えるか（ItemQuality が HQ）。</param>
/// <param name="SkipIfHeld">「持っていれば飛ばす」条件（StepIf.Item）が付いているか。</param>
public sealed record QuestionableCraftStep(int Sequence, uint? ItemId, int ItemCount, bool Hq, bool SkipIfHeld);

/// <summary>
/// Questionable の経路の手順1件（どの種類の手順でも）。Questionable の Craft 手順を飛ばして残りをこちらで行うときと、
/// 同じ段で複数の相手に渡すクエストの残りの相手へこちらで渡しに行くときに使う。
/// </summary>
/// <param name="Sequence">クエストの段。</param>
/// <param name="Index">段の中の手順の番号（Questionable の GetCurrentStepData().Step と同じ数え方：0 始まり）。</param>
/// <param name="Type">手順の種類（InteractionType の文字列：Interact・CompleteQuest・Craft・PurchaseItem・WalkTo・WaitForManualProgress 等）。</param>
/// <param name="DataId">相手（NPC 等）の ID。無ければ null。</param>
/// <param name="Territory">手順のエリア。</param>
/// <param name="Position">手順の位置。無ければ null。</param>
/// <param name="ItemId">手順の品（Craft・PurchaseItem 等）。無ければ null。</param>
/// <param name="BaitId">釣りの手順（Fish）で使う餌（ItemsToGather の FishingOptions.BaitId）。無ければ null。
/// 導入版の経路データの釣りの手順31件すべてにあり、Questionable が AutoHook に渡すプリセットの「強制する餌」と一致する
/// （経路のプリセット・Questionable 内蔵のプリセット・プリセットの自動生成のいずれも：全件ほどいて確かめた）。</param>
public sealed record QuestionableStep(int Sequence, int Index, string Type, uint? DataId, uint Territory, System.Numerics.Vector3? Position, uint? ItemId, string? Comment = null, int? ItemCount = null, uint? BaitId = null);

/// <summary>
/// Questionable の経路データ（pluginConfigs\Questionable\PathData\bundle.zip）から、ジョブクエの「Craft」手順を読む
/// （120本の Craft 手順は計174）。読むだけで、Questionable には何も頼まない。
///
/// Questionable の Craft 手順は、手順の最初に「持っていれば飛ばす」（StepIf.Item）を見て、所持数（所持品＋アーマリー、装備中は数えない）が
/// ItemCount 以上なら Artisan を呼ばずに飛ばす。足りなければ Artisan の既製リスト（クエスト番号のリスト。在庫を見ない）を動かす
/// （Questionable の SkipCondition.cs・Craft.cs）。そのため：
///  ・納品物の Craft 手順は、こちらが先に作っておけば飛ばされる。
///  ・中間素材の Craft 手順（調理 Lv5〜50 の食塩・小麦粉・重曹など）は、納品物を作るのに使い切ると残らず、既製リストが動いて
///    追加製作（材料が無ければ Questionable が NPC から買い足す）になる。→ 計画で、その数も手元に残す。
///  ・ItemId の無い Craft 手順（木工 Lv1〜25 の6本・調理 Lv53〜60 の4本）は、在庫に関係なく既製リストが動く。防げないので、
///    始める前に名前を出して確かめてもらう（防げるとは言わない）。
/// 経路データが読めないときは null（点検はせず、記録に残す）。
/// </summary>
public static class QuestionablePaths
{
    private static readonly object Gate = new();
    private static string? loadedPath;
    private static DateTime loadedStamp;
    private static Dictionary<ushort, List<QuestionableCraftStep>> cache = [];
    private static Dictionary<ushort, List<QuestionableStep>> stepCache = [];
    private static Dictionary<ushort, HashSet<uint>> gatherCache = [];
    private static Dictionary<ushort, string> entries = [];

    /// <summary>経路データの場所。</summary>
    public static string BundlePath
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty, "Questionable", "PathData", "bundle.zip");

    /// <summary>そのクエスト（Questionable の番号＝行 ID − 65536）の Craft 手順。経路データが読めない・経路が無ければ null。</summary>
    public static IReadOnlyList<QuestionableCraftStep>? CraftSteps(ushort shortId) => CraftSteps(BundlePath, shortId);

    /// <summary>そのクエストの全手順（段・手順の順）。経路データが読めない・経路が無ければ null。</summary>
    public static IReadOnlyList<QuestionableStep>? Steps(ushort shortId) => Steps(BundlePath, shortId);

    /// <summary>
    /// そのクエストの経路で、Questionable 自身が採集・釣りで採る品（手順の ItemsToGather）。経路が無ければ空。
    /// 採集職の納品物は、こちらが先に集めきれなくても、受注後に Questionable が採る。
    /// </summary>
    public static IReadOnlySet<uint> GatheredItems(ushort shortId) => GatheredItems(BundlePath, shortId);

    /// <summary>上と同じ。経路データの場所を渡す（試験用）。</summary>
    public static IReadOnlySet<uint> GatheredItems(string bundlePath, ushort shortId)
    {
        lock (Gate)
        {
            if (CraftSteps(bundlePath, shortId) == null)
                return new HashSet<uint>();
            return gatherCache.TryGetValue(shortId, out var set) ? set : new HashSet<uint>();
        }
    }

    /// <summary>上と同じ。経路データの場所を渡す（試験用）。</summary>
    public static IReadOnlyList<QuestionableStep>? Steps(string bundlePath, ushort shortId)
    {
        lock (Gate)
        {
            if (CraftSteps(bundlePath, shortId) == null)
                return null;
            return stepCache.TryGetValue(shortId, out var list) ? list : null;
        }
    }

    /// <summary>上と同じ。経路データの場所を渡す（試験用）。</summary>
    public static IReadOnlyList<QuestionableCraftStep>? CraftSteps(string bundlePath, ushort shortId)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(bundlePath))
                    return null;

                var stamp = File.GetLastWriteTimeUtc(bundlePath);
                if (loadedPath != bundlePath || stamp != loadedStamp)
                {
                    using var zip = ZipFile.OpenRead(bundlePath);
                    var map = new Dictionary<ushort, string>();
                    foreach (var e in zip.Entries)
                    {
                        var m = Regex.Match(e.FullName, @"^QuestPaths/.*/(\d+)_[^/]*\.json$");
                        if (m.Success && ushort.TryParse(m.Groups[1].Value, out var id) && !map.ContainsKey(id))
                            map[id] = e.FullName;
                    }

                    entries = map;
                    cache = [];
                    stepCache = [];
                    gatherCache = [];
                    loadedPath = bundlePath;
                    loadedStamp = stamp;
                }

                if (cache.TryGetValue(shortId, out var cached))
                    return cached;
                if (!entries.TryGetValue(shortId, out var name))
                    return null;

                using var z = ZipFile.OpenRead(bundlePath);
                using var stream = z.GetEntry(name)!.Open();
                using var doc = JsonDocument.Parse(stream);
                var list = new List<QuestionableCraftStep>();
                var all = new List<QuestionableStep>();
                var gathered = new HashSet<uint>();
                if (doc.RootElement.TryGetProperty("QuestSequence", out var seqs))
                {
                    foreach (var seq in seqs.EnumerateArray())
                    {
                        var sequence = seq.TryGetProperty("Sequence", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : -1;
                        if (!seq.TryGetProperty("Steps", out var steps))
                            continue;
                        var index = 0;
                        foreach (var st in steps.EnumerateArray())
                        {
                            all.Add(ReadStep(sequence, index++, st));

                            // 採集・釣りの手順で Questionable 自身が採る品（ItemsToGather）
                            if (st.TryGetProperty("ItemsToGather", out var tg) && tg.ValueKind == JsonValueKind.Array)
                                foreach (var g in tg.EnumerateArray())
                                    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("ItemId", out var gi) && gi.ValueKind == JsonValueKind.Number)
                                        gathered.Add(gi.GetUInt32());
                            if (!st.TryGetProperty("InteractionType", out var it) || it.GetString() != "Craft")
                                continue;
                            uint? item = st.TryGetProperty("ItemId", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetUInt32() : null;
                            var count = st.TryGetProperty("ItemCount", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : 0;
                            var hq = st.TryGetProperty("ItemQuality", out var qv) && qv.ValueKind == JsonValueKind.String && qv.GetString() == "HQ";
                            var skip = st.TryGetProperty("SkipConditions", out var sc) && sc.ValueKind == JsonValueKind.Object
                                       && sc.TryGetProperty("StepIf", out var si) && si.ValueKind == JsonValueKind.Object
                                       && si.TryGetProperty("Item", out var sitem) && sitem.ValueKind == JsonValueKind.Object;
                            list.Add(new QuestionableCraftStep(sequence, item, count, hq, skip));
                        }
                    }
                }

                cache[shortId] = list;
                stepCache[shortId] = all;
                gatherCache[shortId] = gathered;
                return list;
            }
            catch (Exception ex)
            {
                Core.DebugLog.Current?.Line("データ", $"Questionable の経路データを読めませんでした（{ex.GetType().Name}: {ex.Message}）");
                return null;
            }
        }
    }

    private static QuestionableStep ReadStep(int sequence, int index, JsonElement st)
    {
        var type = st.TryGetProperty("InteractionType", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() ?? string.Empty : string.Empty;
        uint? data = st.TryGetProperty("DataId", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetUInt32() : null;
        var terr = st.TryGetProperty("TerritoryId", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetUInt32() : 0u;
        uint? item = st.TryGetProperty("ItemId", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetUInt32() : null;
        System.Numerics.Vector3? pos = null;
        if (st.TryGetProperty("Position", out var pv) && pv.ValueKind == JsonValueKind.Object
            && pv.TryGetProperty("X", out var x) && pv.TryGetProperty("Y", out var y) && pv.TryGetProperty("Z", out var z))
            pos = new System.Numerics.Vector3(x.GetSingle(), y.GetSingle(), z.GetSingle());
        var comment = st.TryGetProperty("Comment", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
        int? count = st.TryGetProperty("ItemCount", out var cnt) && cnt.ValueKind == JsonValueKind.Number ? cnt.GetInt32() : null;

        // 釣りの手順の餌（採る品のうち、最初に餌の指定があるもの）
        uint? bait = null;
        if (st.TryGetProperty("ItemsToGather", out var tg) && tg.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in tg.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("FishingOptions", out var fo) && fo.ValueKind == JsonValueKind.Object
                    && fo.TryGetProperty("BaitId", out var bv) && bv.ValueKind == JsonValueKind.Number && bv.GetUInt32() > 0)
                {
                    bait = bv.GetUInt32();
                    break;
                }
            }
        }

        return new QuestionableStep(sequence, index, type, data, terr, pos, item, comment, count, bait);
    }

    /// <summary>
    /// その段で「作る品の指定の無い Craft 手順」の番号（無ければ null）。この手順は在庫に関係なく Artisan の既製リストを動かす
    /// （Questionable の Craft.cs：ItemId の有無に関係なく最初に既製リストを呼ぶ。止められるのは手順の前の「持っていれば飛ばす」だけで、
    /// ItemId が無いと必ず無効。Questionable の設定・IPC・コマンドで飛ばす手段は無い）。
    /// </summary>
    public static int? PremadeCraftIndex(IReadOnlyList<QuestionableStep> steps, int sequence)
        => steps.FirstOrDefault(s => s.Sequence == sequence && s.Type == "Craft" && s.ItemId == null) is { } c ? c.Index : null;
}
