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
/// <param name="Hidden">隠し（HIDDEN）の品か（採集職の「眼力」で確実に出せる。無くても採集点を回るうちに運で出る）。</param>
public sealed record GatherSpot(uint Territory, int GatheringLevel, bool Timed, bool Mining, bool Hidden = false);

/// <summary>その品を売るギルショップの1件（店の条件）。</summary>
/// <param name="Quests">買うのに要るクエスト（店の GilShop.Quest と品の GilShopItem.QuestRequired。空なら条件なし）。</param>
/// <param name="Unknown">確かめられない条件（アチーブメント）が付いているか（付いていれば、その店では買えないとみなす）。</param>
public sealed record VendorOffer(uint[] Quests, bool Unknown);

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

    /// <summary>
    /// 売っているギルショップごとの条件（ゲームデータの調査：サンレモン・硬銀砂など11種は、売っている店すべてに
    /// 友好部族クエストの条件が付いている。GBR は確かめないので、未完了なら NPC 購入の手段から外す）。
    /// </summary>
    public List<VendorOffer> VendorOffers { get; } = [];

    /// <summary>そのギルショップの値段（最安）。0 なら不明。</summary>
    public uint VendorPrice { get; set; }

    public List<GatherSpot> Gather { get; } = [];

    public bool Fish { get; set; }

    public bool Spearfish { get; set; }

    /// <summary>
    /// 釣り・銛で取るときの品のレベル（FishParameter・SpearfishingItem の GatheringItemLevel。行が複数なら低いほう。0 は不明）。
    /// 漁師のレベルがこれに届かなければ、釣りの手段は使わない。
    /// </summary>
    public int FishLevel { get; set; }

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

                // GBR が精選するのは収集品だけ（収集品でない品はデータに載っていても精選の元にしない。
                // 例：暁光の霊砂の元の候補に、収集品でないスペアミントが入っていた）
                if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(r.SourceItemId, out var srcItem) || !srcItem.IsCollectable)
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

    /// <summary>
    /// 採掘・園芸で採れる場所。
    ///  ・採集点（GatheringPoint）ごとに、その点の種類（GatheringPointBase.Item）の品と、点ごとに追加される品
    ///    （GatheringItemPoint：行＝採集品、子行＝採集点）の両方を拾う。以前は前者だけで、チタン鉱などの本当の採集点
    ///    （低地ドラヴァニア・Lv55・隠し）が見えず、実体の無いエリアの点で「採れる」と数えていた。
    ///  ・実在する野外のエリア（TerritoryType.TerritoryIntendedUse＝1）だけを使う。エリア 1 などの実体の無い採集点や、
    ///    ディアデムなどの特別なエリアは除く（GBR もこれらを除いている）。
    ///  ・隠し（GatheringItem.IsHidden）の品は、採集職の「眼力」で確実に出せる。無くても運で出るので採集から外さない
    ///    （眼力の有無は PlanBuilder.HiddenGather で知らせる）。
    ///  ・時間限定は採集点ごと（GatheringPointTransient）。
    /// </summary>
    private void BuildGathering()
    {
        var transient = Svc.Data.GetExcelSheet<GatheringPointTransient>();
        var points = Svc.Data.GetExcelSheet<GatheringPoint>();
        var bases = Svc.Data.GetExcelSheet<GatheringPointBase>();
        var gitems = Svc.Data.GetExcelSheet<GatheringItem>();
        var terrs = Svc.Data.GetExcelSheet<TerritoryType>();

        bool Timed(uint point)
            => transient.TryGetRow(point, out var t)
               && (t.GatheringRarePopTimeTable.RowId != 0 || (t.EphemeralStartTime != 65535 && t.EphemeralStartTime != t.EphemeralEndTime));

        bool RealField(uint terr)
            => terr > 1 && terrs.TryGetRow(terr, out var t) && t.TerritoryIntendedUse.RowId == 1;

        // 点ごとに追加される品（GatheringItemPoint：行の番号＝GatheringItem、子行の GatheringPoint＝採集点）
        var extra = new Dictionary<uint, List<uint>>();
        foreach (var row in Svc.Data.GetSubrowExcelSheet<GatheringItemPoint>())
        {
            foreach (var sub in row)
            {
                var point = sub.GatheringPoint.RowId;
                if (point == 0)
                    continue;
                if (!extra.TryGetValue(point, out var list))
                    extra[point] = list = [];
                list.Add(sub.RowId);
            }
        }

        var skipped = 0;
        foreach (var p in points)
        {
            var baseId = p.GatheringPointBase.RowId;
            if (baseId == 0 || !bases.TryGetRow(baseId, out var gb))
                continue;

            // 0 採掘 / 1 砕岩 / 2 伐採 / 3 草刈り。それ以外（銛など）はここでは扱わない
            var type = gb.GatheringType.RowId;
            if (type > 3)
                continue;

            var terr = p.TerritoryType.RowId;
            if (!RealField(terr))
            {
                skipped++;
                continue;
            }

            var mining = type <= 1;
            var timed = Timed(p.RowId);
            var gis = gb.Item.Select(x => x.RowId).Where(x => x != 0).Concat(extra.GetValueOrDefault(p.RowId) ?? []).Distinct();
            foreach (var gi in gis)
            {
                if (!gitems.TryGetRow(gi, out var g))
                    continue;

                var item = g.Item.RowId;
                if (item == 0 || item >= 1_000_000)
                    continue;

                var spot = new GatherSpot(terr, gb.GatheringLevel, timed, mining, g.IsHidden);
                var s = this.Get(item);
                if (!s.Gather.Contains(spot))
                    s.Gather.Add(spot);
            }
        }

        if (skipped > 0)
            Core.DebugLog.Current?.Line("データ", $"採集点のうち、実在する野外のエリアでないもの {skipped} 件を除きました");
    }

    private void BuildFishing()
    {
        foreach (var f in Svc.Data.GetExcelSheet<FishParameter>())
            if (f.Item.RowId is > 0 and < 1_000_000)
            {
                var s = this.Get(f.Item.RowId);
                s.Fish = true;
                s.FishLevel = LowerLevel(s.FishLevel, f.GatheringItemLevel.ValueNullable?.GatheringItemLevel ?? 0);
            }

        foreach (var f in Svc.Data.GetExcelSheet<SpearfishingItem>())
            if (f.Item.RowId != 0)
            {
                var s = this.Get(f.Item.RowId);
                s.Spearfish = true;
                s.FishLevel = LowerLevel(s.FishLevel, f.GatheringItemLevel.ValueNullable?.GatheringItemLevel ?? 0);
            }

        // 0（不明）は比べない
        static int LowerLevel(int now, int level) => level <= 0 ? now : now <= 0 ? level : Math.Min(now, level);
    }

    private void BuildVendors()
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        var shops = Svc.Data.GetExcelSheet<GilShop>();
        foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>())
        {
            var shopQuest = shops.TryGetRow(shop.RowId, out var gs) ? gs.Quest.RowId : 0;
            foreach (var row in shop)
            {
                var id = row.Item.RowId;
                if (id == 0)
                    continue;

                var s = this.Get(id);
                s.Vendor = true;
                var quests = row.QuestRequired.Select(q => q.RowId).Append(shopQuest).Where(q => q != 0).Distinct().ToArray();
                // StateRequired は普通の店の品の大半にも 100 が入っていて条件ではない（ゲームデータで数えた：12,617件）ので見ない
                s.VendorOffers.Add(new VendorOffer(quests, row.AchievementRequired.RowId != 0));
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

    /// <summary>ワールド座標（X・Z）を地図座標へ（<see cref="ToWorld"/> の逆。c = 0.02 × (value + offset) + 2048 / SizeFactor + 1）。</summary>
    public static Vector2 ToMap(uint territory, float worldX, float worldZ)
    {
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        if (!terr.TryGetRow(territory, out var t) || !t.Map.IsValid)
            return Vector2.Zero;

        var map = t.Map.Value;
        var scale = map.SizeFactor == 0 ? 100f : map.SizeFactor;
        float Conv(float v, short offset) => (0.02f * (v + offset)) + (2048f / scale) + 1f;
        return new Vector2(Conv(worldX, map.OffsetX), Conv(worldZ, map.OffsetY));
    }
}
