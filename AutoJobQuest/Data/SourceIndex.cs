using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace AutoJobQuest.Data;

/// <summary>採集できる場所1件。</summary>
/// <param name="Territory">エリア（TerritoryType の行）。</param>
/// <param name="GatheringLevel">採集点のレベル。</param>
/// <param name="Timed">時間限定の採集点にしか無いか。</param>
/// <param name="Mining">採掘（true）か園芸（false）か。</param>
public sealed record GatherSpot(uint Territory, int GatheringLevel, bool Timed, bool Mining);

/// <summary>モンスターの出現位置1件。</summary>
/// <param name="BNpcNameId">モンスターの名前 ID（BNpcName の行）。</param>
/// <param name="Territory">エリア。</param>
/// <param name="MapX">地図座標 X（1〜42 程度）。</param>
/// <param name="MapY">地図座標 Y。</param>
public sealed record MobSpot(uint BNpcNameId, uint Territory, float MapX, float MapY);

/// <summary>クリスタル類の段階。</summary>
public enum CrystalTier
{
    None,
    Shard,
    Crystal,
    Cluster,
}

/// <summary>1品目の入手元。</summary>
public sealed class ItemSources
{
    public uint ItemId { get; init; }

    public CrystalTier Crystal { get; set; }

    /// <summary>ギルショップで売っているか。</summary>
    public bool Vendor { get; set; }

    /// <summary>そのギルショップの値段（最安）。0 なら不明。</summary>
    public uint VendorPrice { get; set; }

    public List<GatherSpot> Gather { get; } = [];

    public bool Fish { get; set; }

    public bool Spearfish { get; set; }

    /// <summary>落とすモンスター（名前 ID）。</summary>
    public List<uint> DropMobs { get; } = [];

    /// <summary>マーケットボードに出せる品か（検索分類があり、取引不可でない）。</summary>
    public bool Marketable { get; set; }

    public bool CanGather => this.Gather.Count > 0;

    public bool CanGatherUntimed => this.Gather.Any(x => !x.Timed);

    /// <summary>
    /// 精選するとこの品が出る、採掘・園芸で採れる品（精選の元。採集点のレベルの低い順）。
    /// 例：微光の霊砂 ← ファイアグラベル・赤玉土など（霊砂は収集品を採って精選で得る）。
    /// </summary>
    public List<uint> ReducedFrom { get; } = [];

    public bool CanReduce => this.ReducedFrom.Count > 0;
}

/// <summary>
/// 素材の入手元をゲームデータから引く（解析ツール jqa の Classify.cs を移植）。
///
///  ・採集   … GatheringPointBase（種類）→ GatheringItem → Item。時間限定は GatheringPointTransient。
///  ・釣り   … FishParameter.Item、銛は SpearfishingItem.Item。
///  ・NPC販売 … GilShopItem。
///  ・戦闘   … ゲームデータには無い。LuminaSupplemental 同梱の MobDrop.csv / MobSpawn.csv を使う
///             （Artisan・GBR も同じデータを使っている）。
///  ・クリスタル類 … FilterGroup がクリスタル欄のもの。アイテムLv が最小の段がシャード、
///             次がクリスタル、その次がクラスター（2〜7 / 8〜13 / 14〜19 の 1 / 25 / 50）。
///
/// 重いので起動時には作らない（計画のときに別スレッドで作る）。
/// </summary>
public sealed class SourceIndex
{
    private readonly Dictionary<uint, ItemSources> map = [];
    private readonly Dictionary<uint, List<MobSpot>> spawns = [];

    public IReadOnlyList<string> Notes => this.notes;

    private readonly List<string> notes = [];

    /// <summary>クリスタル類の FilterGroup（ファイアシャード＝アイテム 2 の値から読む）。</summary>
    private byte crystalFilterGroup;

    public static SourceIndex Build()
    {
        var idx = new SourceIndex();
        idx.BuildCrystals();
        idx.BuildGathering();
        idx.BuildFishing();
        idx.BuildVendors();
        idx.BuildMarket();
        idx.BuildCombat();
        idx.BuildReduction();
        return idx;
    }

    /// <summary>
    /// 精選（Aetherial Reduction）の対応：どの品を精選すると何が出るか。ゲームデータには無いので、
    /// LuminaSupplemental 同梱の ItemSupplement.csv（種類 Reduction）を使う（Allagan Tools も同じデータ：
    /// AllaganLib ItemInfoCache.cs:1657）。元の品は、採掘・園芸で採れるもの（GBR に採らせられるもの）だけを残す。
    /// 採集より後に作ること（CanGather を見るため）。
    /// </summary>
    private void BuildReduction()
    {
        try
        {
            var rows = CsvLoader.LoadResource<ItemSupplement>(CsvLoader.ItemSupplementResourceName, true, out var failed, out _);
            foreach (var r in rows)
            {
                if (r.ItemSupplementSource != ItemSupplementSource.Reduction || r.ItemId == 0 || r.SourceItemId == 0)
                    continue;
                var src = this.Get(r.SourceItemId);
                if (!src.CanGather)
                    continue;
                var dst = this.Get(r.ItemId);
                if (!dst.ReducedFrom.Contains(r.SourceItemId))
                    dst.ReducedFrom.Add(r.SourceItemId);
            }

            // 採集点のレベルの低い順（採れる見込みの高い順）
            foreach (var s in this.map.Values.Where(x => x.ReducedFrom.Count > 1))
                s.ReducedFrom.Sort((a, b) => this.Get(a).Gather.Min(g => g.GatheringLevel).CompareTo(this.Get(b).Gather.Min(g => g.GatheringLevel)));

            if (failed.Count > 0)
                this.notes.Add($"精選のデータで読めない行がありました（{failed.Count} 行）");
        }
        catch (Exception ex)
        {
            this.notes.Add($"精選のデータを読めませんでした: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>入手元。データに無い品でも空の情報を返す（null にはしない）。</summary>
    public ItemSources Get(uint itemId)
    {
        if (!this.map.TryGetValue(itemId, out var s))
            this.map[itemId] = s = new ItemSources { ItemId = itemId };
        return s;
    }

    /// <summary>そのモンスターの出現位置。</summary>
    public IReadOnlyList<MobSpot> SpawnsOf(uint bnpcNameId)
        => this.spawns.TryGetValue(bnpcNameId, out var l) ? l : [];

    private void BuildCrystals()
    {
        var items = Svc.Data.GetExcelSheet<Item>();

        // ファイアシャード（2）はクリスタル欄に入る品の代表。FilterGroup をそこから読む（数値を決め打ちしない）
        if (!items.TryGetRow(2, out var fireShard))
        {
            this.notes.Add("アイテム 2（ファイアシャード）が読めません。クリスタル類の判定ができません");
            return;
        }

        this.crystalFilterGroup = fireShard.FilterGroup;

        var crystals = items.Where(x => x.FilterGroup == this.crystalFilterGroup && x.RowId != 0).ToList();
        var levels = crystals.Select(x => x.LevelItem.RowId).Distinct().OrderBy(x => x).ToList();
        foreach (var c in crystals)
        {
            var tier = levels.IndexOf(c.LevelItem.RowId) switch
            {
                0 => CrystalTier.Shard,
                1 => CrystalTier.Crystal,
                _ => CrystalTier.Cluster,
            };
            this.Get(c.RowId).Crystal = tier;
        }
    }

    private void BuildGathering()
    {
        var transient = Svc.Data.GetExcelSheet<GatheringPointTransient>();
        var points = Svc.Data.GetExcelSheet<GatheringPoint>();
        var gitems = Svc.Data.GetExcelSheet<GatheringItem>();

        // GatheringPointBase ごとに「どのエリアにあるか」「全部の採集点が時間限定か」
        var baseTerritories = new Dictionary<uint, HashSet<uint>>();
        var baseTimed = new Dictionary<uint, bool>();
        foreach (var p in points)
        {
            var b = p.GatheringPointBase.RowId;
            if (b == 0)
                continue;

            var timed = false;
            if (transient.TryGetRow(p.RowId, out var t))
            {
                timed = t.GatheringRarePopTimeTable.RowId != 0
                        || (t.EphemeralStartTime != 65535 && t.EphemeralStartTime != t.EphemeralEndTime);
            }

            baseTimed[b] = baseTimed.TryGetValue(b, out var prev) ? prev && timed : timed;

            if (p.TerritoryType.RowId != 0)
            {
                if (!baseTerritories.TryGetValue(b, out var set))
                    baseTerritories[b] = set = [];
                set.Add(p.TerritoryType.RowId);
            }
        }

        foreach (var gb in Svc.Data.GetExcelSheet<GatheringPointBase>())
        {
            // 0 採掘 / 1 砕岩 / 2 伐採 / 3 草刈り。それ以外（銛など）はここでは扱わない
            var type = gb.GatheringType.RowId;
            if (type > 3)
                continue;

            var mining = type <= 1;
            var timed = baseTimed.GetValueOrDefault(gb.RowId, true);
            var terrs = baseTerritories.GetValueOrDefault(gb.RowId);
            if (terrs == null || terrs.Count == 0)
                continue;

            foreach (var gi in gb.Item)
            {
                if (gi.RowId == 0 || !gitems.TryGetRow(gi.RowId, out var g))
                    continue;

                var item = g.Item.RowId;
                if (item == 0 || item >= 1_000_000)
                    continue;

                var s = this.Get(item);
                foreach (var terr in terrs)
                    s.Gather.Add(new GatherSpot(terr, gb.GatheringLevel, timed, mining));
            }
        }
    }

    private void BuildFishing()
    {
        foreach (var f in Svc.Data.GetExcelSheet<FishParameter>())
            if (f.Item.RowId is > 0 and < 1_000_000)
                this.Get(f.Item.RowId).Fish = true;

        foreach (var f in Svc.Data.GetExcelSheet<SpearfishingItem>())
            if (f.Item.RowId != 0)
                this.Get(f.Item.RowId).Spearfish = true;
    }

    private void BuildVendors()
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var row in shop)
            {
                var id = row.Item.RowId;
                if (id == 0)
                    continue;

                var s = this.Get(id);
                s.Vendor = true;
                if (items.TryGetRow(id, out var it) && it.PriceMid > 0 && (s.VendorPrice == 0 || it.PriceMid < s.VendorPrice))
                    s.VendorPrice = it.PriceMid;
            }
        }
    }

    private void BuildMarket()
    {
        foreach (var it in Svc.Data.GetExcelSheet<Item>())
        {
            if (it.RowId == 0 || it.ItemSearchCategory.RowId == 0 || it.IsUntradable)
                continue;

            this.Get(it.RowId).Marketable = true;
        }
    }

    private void BuildCombat()
    {
        try
        {
            var drops = CsvLoader.LoadResource<MobDrop>(CsvLoader.MobDropResourceName, true, out var failed1, out _);
            foreach (var d in drops)
            {
                if (d.ItemId == 0 || d.BNpcNameId == 0)
                    continue;
                var s = this.Get(d.ItemId);
                if (!s.DropMobs.Contains(d.BNpcNameId))
                    s.DropMobs.Add(d.BNpcNameId);
            }

            var spawnRows = CsvLoader.LoadResource<MobSpawnPosition>(CsvLoader.MobSpawnResourceName, true, out var failed2, out _);
            foreach (var m in spawnRows)
            {
                if (m.BNpcNameId == 0 || m.TerritoryTypeId == 0)
                    continue;
                if (!this.spawns.TryGetValue(m.BNpcNameId, out var list))
                    this.spawns[m.BNpcNameId] = list = [];

                // 位置の1・2番目は地図座標（X, Y）。3番目は高さだが値が当てにならない行があるので使わない
                list.Add(new MobSpot(m.BNpcNameId, m.TerritoryTypeId, m.Position.X, m.Position.Y));
            }

            if (failed1.Count + failed2.Count > 0)
                this.notes.Add($"戦闘ドロップのデータで読めない行がありました（ドロップ {failed1.Count} 行 / 出現位置 {failed2.Count} 行）");
        }
        catch (Exception ex)
        {
            this.notes.Add($"戦闘ドロップのデータを読めませんでした: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>地図座標とワールド座標の変換。</summary>
public static class MapCoords
{
    /// <summary>
    /// 地図座標（1〜42 程度）をワールド座標へ。
    /// Dalamud の MapUtil.ConvertWorldCoordXZToMapCoord（ゲームのコードから取った式）
    ///   c = 0.02 × offset + 2048 / SizeFactor + 0.02 × value + 1
    /// の逆算：value = 50 × (c − 1 − 2048 / SizeFactor) − offset。
    /// 確認：リムサ下甲板層の MB（ワールド X −223.8、SizeFactor 200）→ 地図 6.8。
    /// 高さは分からないので 0 を入れる。使う側で vnavmesh に地面の点を問い合わせること。
    /// </summary>
    public static Vector3 ToWorld(uint territory, float mapX, float mapY)
    {
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        if (!terr.TryGetRow(territory, out var t) || !t.Map.IsValid)
            return Vector3.Zero;

        var map = t.Map.Value;
        var scale = map.SizeFactor == 0 ? 100f : map.SizeFactor;
        float Conv(float c, short offset) => (50f * (c - 1f - (2048f / scale))) - offset;
        return new Vector3(Conv(mapX, map.OffsetX), 0, Conv(mapY, map.OffsetY));
    }
}
