using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AutoJobQuest.Data;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Planning;

/// <summary>刺突漁の漁場1つ（刺突漁の表 SpearfishingNotebook の行）。</summary>
/// <param name="NotebookId">刺突漁の表の行（AutoHook のプリセットの項目の「漁場」に入れる番号）。</param>
/// <param name="Territory">エリア。</param>
/// <param name="Shadow">魚影（IsShadowNode）か。</param>
/// <param name="GatheringPointBase">採集点の種類（GatheringPointBase の行）。</param>
/// <param name="Items">獲れる魚（品）。</param>
/// <param name="Map">地図の上の位置（刺突漁の表の X・Y。近さを比べるだけに使う）。</param>
public sealed record SpearfishPool(uint NotebookId, uint Territory, bool Shadow, uint GatheringPointBase, IReadOnlyList<uint> Items, Vector2 Map);

/// <summary>
/// 刺突漁で、魚影にだけ出る魚を獲る計画（漁師 Lv68「減少を食い止めろ」の大方士）。
///  ・前提の魚（Predator）を親の漁場で獲ると魚影が出る。GBR の目標は「その魚影に出る、同じ前提の魚を持つ別の魚」（GbrTarget）にする。
///    GBR は目標の魚が魚影の魚なら、親の漁場では前提の魚を、魚影が出れば魚影へ向かう（GBR の AutoGather.AutoHook.cs・ActiveItemList.cs）。
///  ・どの魚を突くかは、こちらが作る AutoHook のプリセットで決める：親の漁場＝前提の魚、魚影＝欲しい魚の大きさ・速さの魚（Proxy）。
///    欲しい魚の AutoHook のデータがゲームの値と合わない（大方士は「小・2」）ので、同じ大きさ・速さの別の魚を「代わりの札」にする。
///  ・欲しい魚の大きさ・速さ：AutoHook のデータがゲームの値なら、それ。無ければ Questionable の経路の注記（「(Average and Slow)」）。
///    実際に魚影で見た値が違えば、見た値に合わせ直す（<see cref="SpearfishPlanner.Calibrate"/>）。
/// </summary>
public sealed record SpearfishPlan(
    uint Wanted,
    int Count,
    uint Predator,
    uint GbrTarget,
    SpearfishPool Parent,
    SpearfishPool Shadow,
    (int Size, int Speed)? Stats,
    string StatsFrom,
    uint? Proxy,
    IReadOnlyList<(uint Item, int Size, int Speed)> ShadowRegulars)
{
    /// <summary>AutoHook のプリセットの項目（品, 漁場）。親の漁場で前提の魚、魚影で代わりの札（無ければ魚影は空）。</summary>
    public List<(uint ItemId, uint Notebook)> Gigs()
    {
        var list = new List<(uint, uint)> { (this.Predator, this.Parent.NotebookId) };
        if (this.Proxy is { } proxy)
            list.Add((proxy, this.Shadow.NotebookId));
        return list;
    }
}

/// <summary>刺突漁の計画の決め方（ゲームデータと AutoHook のデータから。ゲームを起動せずに試せる）。</summary>
public static class SpearfishPlanner
{
    /// <summary>そのエリアの刺突漁の漁場（ゲームデータから）。</summary>
    public static List<SpearfishPool> Pools(uint territory)
    {
        var bases = Svc.Data.GetExcelSheet<GatheringPointBase>();
        var spear = Svc.Data.GetExcelSheet<SpearfishingItem>();
        var list = new List<SpearfishPool>();
        foreach (var nb in Svc.Data.GetExcelSheet<SpearfishingNotebook>())
        {
            if (nb.TerritoryType.RowId != territory || nb.GatheringPointBase.RowId == 0 || !bases.TryGetRow(nb.GatheringPointBase.RowId, out var gpb))
                continue;
            var items = new List<uint>();
            foreach (var it in gpb.Item)
                if (it.RowId != 0 && spear.TryGetRow(it.RowId, out var si) && si.Item.RowId != 0)
                    items.Add(si.Item.RowId);
            list.Add(new SpearfishPool(nb.RowId, territory, nb.IsShadowNode, gpb.RowId, items, new Vector2(nb.X, nb.Y)));
        }

        return list;
    }

    /// <summary>
    /// 世界の座標（X・Z）を、刺突漁の表の座標（X・Y）に直す。刺突漁の表の座標は地図の印と同じ形
    /// （世界＝（表 − 1024）÷（SizeFactor÷100）− Offset：エーテライトの地図の印と同じ換算。AetherytePlaces）。エリアの地図が読めなければ null。
    /// </summary>
    public static Vector2? ToNotebookCoordinates(uint territory, Vector3 world)
    {
        if (!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) || t.Map.ValueNullable is not { } map || map.SizeFactor == 0)
            return null;
        var scale = map.SizeFactor / 100f;
        return new Vector2(((world.X + map.OffsetX) * scale) + 1024f, ((world.Z + map.OffsetY) * scale) + 1024f);
    }

    /// <summary>
    /// Questionable の経路の注記から、その魚の大きさ・速さを読む（例：「Catch 3x dafangshi (Average and Slow)」）。
    /// 魚の英語名（<paramref name="englishName"/>）の後ろの括弧を読む。読めなければ null。
    /// </summary>
    public static (int Size, int Speed)? ParseNote(string? note, string englishName)
    {
        if (string.IsNullOrEmpty(note) || string.IsNullOrEmpty(englishName))
            return null;
        var m = Regex.Match(note, Regex.Escape(englishName) + @"\s*\((Small|Average|Large)\s+and\s+([A-Za-z ]+?)\)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var size = m.Groups[1].Value.ToLowerInvariant() switch { "small" => 1, "average" => 2, _ => 3 };
        var speed = m.Groups[2].Value.Trim().ToLowerInvariant() switch
        {
            "super slow" => 100,
            "extremely slow" => 150,
            "very slow" => 200,
            "slow" => 250,
            "average" => 300,
            "fast" => 350,
            "very fast" => 400,
            "extremely fast" => 450,
            "super fast" => 500,
            "hyper fast" => 550,
            "mega fast" or "lyn fast" => 600,
            _ => 0,
        };
        return speed == 0 ? null : (size, speed);
    }

    /// <summary>
    /// 計画を立てる。立てられなければ null と理由。
    /// </summary>
    /// <param name="wanted">欲しい魚。</param>
    /// <param name="count">要る数。</param>
    /// <param name="territory">そのクエストの手順のエリア（Questionable の経路の手で行う手順のエリア）。</param>
    /// <param name="near">手順の位置（同じエリアに漁場が複数あるとき、近いほうを選ぶ。無ければ null）。</param>
    /// <param name="fish">AutoHook の魚のデータ。</param>
    /// <param name="note">Questionable の経路の注記（手で行う手順の Comment）。</param>
    /// <param name="englishName">欲しい魚の英語名（注記を読むため）。</param>
    public static SpearfishPlan? Build(uint wanted, int count, uint territory, Vector2? near, IReadOnlyDictionary<uint, AutoHookFish> fish,
        IReadOnlyList<SpearfishPool> pools, string? note, string englishName, out string? why)
    {
        why = null;
        if (!fish.TryGetValue(wanted, out var w))
        {
            why = "AutoHook の魚のデータに、欲しい魚がありません";
            return null;
        }

        if (w.Predators.Count == 0)
        {
            why = "AutoHook の魚のデータで、欲しい魚に前提の魚がありません（魚影の魚ではない）";
            return null;
        }

        var predator = w.Predators[0].ItemId;
        var parents = pools.Where(p => !p.Shadow && p.Items.Contains(predator)).ToList();
        if (parents.Count == 0)
        {
            why = $"エリア {territory} に、前提の魚（{CraftPlanner.ItemName(predator)}）の獲れる漁場がありません";
            return null;
        }

        var parent = near is { } n ? parents.OrderBy(p => Vector2.Distance(p.Map, n)).First() : parents[0];

        // GBR の目標：欲しい魚と同じ前提の魚を持つ、魚影に出る別の魚（番号の小さい順に、魚影の漁場があるもの）
        var predatorSet = w.Predators.Select(p => p.ItemId).OrderBy(x => x).ToList();
        SpearfishPool? shadow = null;
        uint target = 0;
        foreach (var f in fish.Values.Where(f => f.IsSpearFish && f.ItemId != wanted && f.Predators.Select(p => p.ItemId).OrderBy(x => x).SequenceEqual(predatorSet)).OrderBy(f => f.ItemId))
        {
            var s = pools.Where(p => p.Shadow && p.Items.Contains(f.ItemId)).OrderBy(p => Vector2.Distance(p.Map, parent.Map)).FirstOrDefault();
            if (s == null)
                continue;
            shadow = s;
            target = f.ItemId;
            break;
        }

        if (shadow == null)
        {
            why = "欲しい魚と同じ前提の魚を持つ、魚影に出る魚が見つかりません（GBR に魚影へ向かわせる目標が作れません）";
            return null;
        }

        var regulars = shadow.Items
            .Select(i => fish.TryGetValue(i, out var f) ? f : null)
            .Where(f => f != null && AutoHookData.IsGameSize(f.Size) && AutoHookData.IsGameSpeed(f.Speed))
            .Select(f => (f!.ItemId, f.Size, f.Speed))
            .ToList();

        (int Size, int Speed)? stats = null;
        var from = "不明（魚影で見た値で決める）";
        if (AutoHookData.IsGameSize(w.Size) && AutoHookData.IsGameSpeed(w.Speed))
        {
            stats = (w.Size, w.Speed);
            from = "AutoHook の魚のデータ";
        }
        else if (ParseNote(note, englishName) is { } parsed)
        {
            stats = parsed;
            from = $"Questionable の経路の注記（AutoHook のデータは {AutoHookData.Describe(w.Size, w.Speed)} で、ゲームの値ではない）";
        }

        var proxy = stats is { } st ? ProxyFor(st, wanted, fish, shadow.Items.Concat(parent.Items).ToHashSet()) : null;
        return new SpearfishPlan(wanted, count, predator, target, parent, shadow, stats, from, proxy, regulars);
    }

    /// <summary>
    /// その大きさ・速さの「代わりの札」にする魚。欲しい魚そのもののデータが合っていれば、それを使う。
    /// 無ければ、同じ大きさ・速さの刺突漁の魚のうち、この漁場（親・魚影）にいないもの（番号の小さい順）。見つからなければ null。
    /// </summary>
    public static uint? ProxyFor((int Size, int Speed) stats, uint wanted, IReadOnlyDictionary<uint, AutoHookFish> fish, IReadOnlySet<uint> avoid)
    {
        if (fish.TryGetValue(wanted, out var w) && w.Size == stats.Size && w.Speed == stats.Speed)
            return wanted;
        return fish.Values
            .Where(f => f.IsSpearFish && f.Size == stats.Size && f.Speed == stats.Speed && !avoid.Contains(f.ItemId))
            .OrderBy(f => f.ItemId)
            .Select(f => (uint?)f.ItemId)
            .FirstOrDefault();
    }

    /// <summary>
    /// 魚影で見た魚の大きさ・速さから、欲しい魚の値を決め直す。魚影にいるいつもの魚のどれとも違う値の魚がいれば、それが欲しい魚とみる。
    /// 今の値と同じ・見分けられない（いつもの魚と同じ値しか見ていない）なら null（変えない）。
    /// </summary>
    public static (int Size, int Speed)? Calibrate(IEnumerable<(int Size, int Speed)> observed, IReadOnlyCollection<(uint Item, int Size, int Speed)> regulars, (int Size, int Speed)? current)
    {
        foreach (var o in observed)
        {
            if (!AutoHookData.IsGameSize(o.Size) || !AutoHookData.IsGameSpeed(o.Speed))
                continue;
            if (regulars.Any(r => r.Size == o.Size && r.Speed == o.Speed))
                continue;
            if (current is { } c && c.Size == o.Size && c.Speed == o.Speed)
                return null;
            return o;
        }

        return null;
    }
}
