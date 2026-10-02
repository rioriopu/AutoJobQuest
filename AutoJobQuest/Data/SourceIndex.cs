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
/// <param name="UpHours">出ている時（エオルゼア時間の 0〜23 時を1ビットずつ。いつも出ている点は <see cref="AllHours"/>）。</param>
public sealed record GatherSpot(uint Territory, int GatheringLevel, bool Timed, bool Mining, bool Hidden = false, uint UpHours = GatherSpot.AllHours)
{
    /// <summary>いつも出ている（24 時間ぶんのビットがすべて立っている）。</summary>
    public const uint AllHours = 0xFFFFFF;
}

/// <summary>
/// ギルショップを開く NPC 1人と、その NPC が現れる条件（ゲームデータの Story：ストーリーの進み具合で NPC を出し入れする表）。
/// 友好部族を解放していないと現れない NPC（高地ドラヴァニアのアキンド）は購入先に含めない。
/// ゲームデータとキャラクター情報から、行くかを判断する。アキンド（ENpc 1016804）は Story 1703969 の段 150 以降にだけ現れ、
/// 段 150 は「名なしのグナース族」（67791：グナース族の解放）の完了（外部の資料とも一致）。
/// </summary>
/// <param name="Npc">ENpc の番号。</param>
/// <param name="Gate">現れるのに要るクエスト（空なら条件なし）。</param>
/// <param name="GateAll">Gate のすべてが要るか（false ならどれか1つ。StoryDefine.CompletedQuestOperator＝1 がすべて）。</param>
/// <param name="Never">条件を読み取れない・途中で消える（使えないとみなす）。</param>
public sealed record ShopNpc(uint Npc, uint[] Gate, bool GateAll, bool Never)
{
    public static readonly ShopNpc[] None = [];

    /// <summary>このキャラクターの前に現れているか。</summary>
    public bool Visible(Func<uint, bool> isComplete)
        => !this.Never && (this.Gate.Length == 0 || (this.GateAll ? this.Gate.All(isComplete) : this.Gate.Any(isComplete)));
}

/// <summary>その品を売るギルショップの1件（店の条件）。</summary>
/// <param name="Quests">買うのに要るクエスト（店の GilShop.Quest と品の GilShopItem.QuestRequired。空なら条件なし）。</param>
/// <param name="Unknown">確かめられない条件（アチーブメント）が付いているか（付いていれば、その店では買えないとみなす）。</param>
/// <param name="Shop">ギルショップ（GilShop の行）。</param>
/// <param name="Npcs">その店を直接開く NPC（ENpcData に店がある NPC）と、その NPC が現れる条件。分からなければ空。</param>
public sealed record VendorOffer(uint[] Quests, bool Unknown, uint Shop = 0, ShopNpc[]? Npcs = null)
{
    /// <summary>店と品の条件（クエスト・アチーブメント）を満たすか。</summary>
    public bool Conditions(Func<uint, bool> isComplete) => !this.Unknown && this.Quests.All(isComplete);

    /// <summary>
    /// このキャラクターがこの店で買えるか：店と品の条件を満たし、店を開く NPC の誰かが現れている。
    /// 店を開く NPC が分からない店は条件だけで見る（GBR は独自の対応表と Allagan Tools の対応も使うので、買うときに NPC ごとに確かめ直す：VendorAccess）。
    /// </summary>
    public bool Usable(Func<uint, bool> isComplete)
        => this.Conditions(isComplete) && (this.Npcs is not { Length: > 0 } || this.Npcs.Any(n => n.Visible(isComplete)));
}

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

    // クエスト専用として外した出現位置（名前の番号ごと。利用者が戻したエリアのものは SpawnsOf で足す）
    private readonly Dictionary<uint, List<MobSpot>> eventOnlySpots = [];

    /// <summary>
    /// そのモンスターの出現位置。クエスト専用として外した出現位置のうち、利用者がデバッグタブで戻したエリアのもの（HuntPrefs.IsRestored）も足す
    /// （ゲームのデータでは普段の敵と見分けきれないことがあるので、利用者が戻せるようにする）。
    /// </summary>
    public IReadOnlyList<MobSpot> SpawnsOf(uint bnpcNameId)
    {
        var list = this.spawns.TryGetValue(bnpcNameId, out var l) ? l : null;
        if (this.eventOnlySpots.TryGetValue(bnpcNameId, out var removed))
        {
            var back = removed.Where(s => HuntPrefs.IsRestored(s.Territory, bnpcNameId)).ToList();
            if (back.Count > 0)
                return [.. list ?? [], .. back];
        }

        return list ?? [];
    }

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

        // 出ている時（GBR の BitfieldUptime と同じ読み方：GatherBuddy.GameData の Node.Base.cs・BitfieldUptime.cs）。
        // 時刻は HHMM（400＝4:00）。未知の採集場所は始まり〜終わり、伝説・時限は始まり＋長さ（200＝2時間。160 は 2時間として扱う）を最大3組
        static uint Hours(ushort start, ushort end)
        {
            if (start == end || start > 2400 || end > 2400)
                return GatherSpot.AllHours;
            int s = start / 100, e = end / 100;
            if (e < s)
                e += 24;
            var mask = 0u;
            for (var i = s; i < e; i++)
                mask |= 1u << (i % 24);
            return mask;
        }

        uint UpHours(uint point)
        {
            if (!transient.TryGetRow(point, out var t))
                return GatherSpot.AllHours;
            if (t.GatheringRarePopTimeTable.RowId == 0)
                return Hours(t.EphemeralStartTime, t.EphemeralEndTime);
            var table = t.GatheringRarePopTimeTable.Value;
            var mask = 0u;
            for (var i = 0; i < 3; i++)
            {
                var duration = table.Duration[i];
                if (duration == 0)
                    continue;
                var start = table.StartTime[i];
                mask |= Hours(start, (ushort)((start + (duration == 160 ? 200 : duration)) % 2400));
            }

            return mask == 0 ? GatherSpot.AllHours : mask;
        }

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
            var up = UpHours(p.RowId);
            var timed = up != GatherSpot.AllHours;
            var gis = gb.Item.Select(x => x.RowId).Where(x => x != 0).Concat(extra.GetValueOrDefault(p.RowId) ?? []).Distinct();
            foreach (var gi in gis)
            {
                if (!gitems.TryGetRow(gi, out var g))
                    continue;

                var item = g.Item.RowId;
                if (item == 0 || item >= 1_000_000)
                    continue;

                var spot = new GatherSpot(terr, gb.GatheringLevel, timed, mining, g.IsHidden, up);
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

    // ENpcData の値の上位 16bit（イベントハンドラの種別：FFXIVClientStructs の EventHandlerContent。Shop＝0x0004・Story＝0x001A）
    private const uint HandlerGilShop = 0x0004;
    private const uint HandlerStory = 0x001A;

    // NPC → 現れる条件（null＝条件なし）。店を開く NPC は作るときに、ほかの NPC（GBR が独自の対応表で挙げる NPC）は初めて聞かれたときに計算して覚える
    // （Story を持つ NPC は 6,000 人を超え、全員を先に計算すると読み込みが十数秒延びた）
    private readonly Dictionary<uint, ShopNpc?> npcGates = [];

    // Story → 聞き手の NPC → 現れる段の範囲（Story ごとに1回だけ作る）
    private readonly Dictionary<uint, Dictionary<uint, List<(int Begin, int End)>>> storyListeners = [];
    private readonly object gateGate = new();

    /// <summary>その NPC の現れる条件（Story の聞き手でなければ null＝いつも現れる）。どのスレッドから呼んでもよい。</summary>
    public ShopNpc? NpcGate(uint npc)
    {
        lock (this.gateGate)
        {
            if (this.npcGates.TryGetValue(npc, out var known))
                return known;
            var gate = Svc.Data.GetExcelSheet<ENpcBase>().TryGetRow(npc, out var row) ? this.GateOf(row) : null;
            this.npcGates[npc] = gate;
            return gate;
        }
    }

    /// <summary>その NPC が、このキャラクターの前に現れているか（条件の無い NPC は true）。</summary>
    public bool NpcVisible(uint npc, Func<uint, bool> isComplete) => this.NpcGate(npc)?.Visible(isComplete) ?? true;

    private void BuildVendors()
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        var shops = Svc.Data.GetExcelSheet<GilShop>();
        var shopNpcs = this.BuildShopNpcs(shops);
        foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>())
        {
            var shopQuest = shops.TryGetRow(shop.RowId, out var gs) ? gs.Quest.RowId : 0;
            var npcs = shopNpcs.GetValueOrDefault(shop.RowId) ?? ShopNpc.None;
            foreach (var row in shop)
            {
                var id = row.Item.RowId;
                if (id == 0)
                    continue;

                var s = this.Get(id);
                s.Vendor = true;
                var quests = row.QuestRequired.Select(q => q.RowId).Append(shopQuest).Where(q => q != 0).Distinct().ToArray();
                // StateRequired は普通の店の品の大半にも 100 が入っていて条件ではない（ゲームデータで数えた：12,617件）ので見ない
                s.VendorOffers.Add(new VendorOffer(quests, row.AchievementRequired.RowId != 0, shop.RowId, npcs));
                if (items.TryGetRow(id, out var it) && it.PriceMid > 0 && (s.VendorPrice == 0 || it.PriceMid < s.VendorPrice))
                    s.VendorPrice = it.PriceMid;
            }
        }
    }

    /// <summary>
    /// ギルショップ → その店を直接開く NPC（ENpcData に店の番号がある NPC：GBR の VendorShopResolver と同じ見方）と、
    /// NPC が現れる条件（ENpcData の Story：<see cref="StoryGate"/>）。条件は <see cref="npcGates"/> にも入れる（GBR が独自の対応表で挙げる NPC にも使う）。
    /// ゲームデータの調べ：ギルショップを直接開く NPC 487 人のうち、Story の聞き手は 47 人。どの人も「あるクエストの完了」で現れ、途中で消える人はいない。
    /// </summary>
    private Dictionary<uint, ShopNpc[]> BuildShopNpcs(Lumina.Excel.ExcelSheet<GilShop> shops)
    {
        var map = new Dictionary<uint, List<ShopNpc>>();
        foreach (var npc in Svc.Data.GetExcelSheet<ENpcBase>())
        {
            List<uint>? gil = null;
            foreach (var d in npc.ENpcData)
            {
                var v = d.RowId;
                if (v != 0 && v >> 16 == HandlerGilShop && shops.TryGetRow(v, out _))
                    (gil ??= []).Add(v);
            }

            if (gil == null)
                continue;
            ShopNpc? gate;
            lock (this.gateGate)
            {
                gate = this.GateOf(npc);
                this.npcGates[npc.RowId] = gate;
            }

            var entry = gate ?? new ShopNpc(npc.RowId, [], false, false);
            foreach (var shop in gil.Distinct())
            {
                if (!map.TryGetValue(shop, out var list))
                    map[shop] = list = [];
                list.Add(entry);
            }
        }

        return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>
    /// その Story で NPC が現れる条件（聞き手でなければ null）。ゲームデータの読み方（ゲームの中では未確認の推定を含む）：
    ///  ・StoryListener のうち、その NPC の行の SequenceBegin（現れる段）〜 SequenceEnd（65535＝ずっと）。ずっと現れる行が無ければ「途中で消える」とみなし使わない。
    ///  ・現れる段が 1 以下なら条件なし。それより後なら、その段以上で「クエストの完了だけ」で決まる最初の StoryDefine の CompletedQuest が条件
    ///    （CompletedQuestOperator＝1 はすべて、2 はどれか）。受注中だけで現れる段は、完了まで「現れない」側に倒す。見つからなければ使わない。
    /// 注意：Lumina の入れ子の構造体を FirstOrDefault の空振りで受けると、既定値のまま欄を読んだ時点で落ちる（調べで実際に起きた）ので、ToList して数で見る。
    /// </summary>
    private ShopNpc? StoryGate(uint npc, Story story)
    {
        if (!this.storyListeners.TryGetValue(story.RowId, out var index))
        {
            index = [];
            foreach (var l in story.StoryListener)
            {
                var id = l.Listener.RowId;
                if (id == 0)
                    continue;
                if (!index.TryGetValue(id, out var r))
                    index[id] = r = [];
                r.Add((l.SequenceBegin, l.SequenceEnd));
            }

            this.storyListeners[story.RowId] = index;
        }

        if (!index.TryGetValue(npc, out var ranges) || ranges.Count == 0)
            return null;
        var open = ranges.Where(r => r.End == ushort.MaxValue).ToList();
        if (open.Count == 0)
            return new ShopNpc(npc, [], false, true);
        var begin = open.Min(r => r.Begin);
        if (begin <= 1)
            return null;

        var defs = story.StoryDefine
            .Where(x => x.Sequence >= begin && x.CompletedQuest.Any(q => q.RowId != 0) && x.AcceptedQuest.All(q => q.RowId == 0))
            .OrderBy(x => x.Sequence)
            .ToList();
        if (defs.Count == 0)
            return new ShopNpc(npc, [], false, true);
        var d = defs[0];
        return new ShopNpc(npc, d.CompletedQuest.Select(q => q.RowId).Where(q => q != 0).Distinct().ToArray(), d.CompletedQuestOperator == 1, false);
    }

    /// <summary>その NPC の ENpcData にある Story のうち、聞き手になっている最初の Story から決めた条件（無ければ null）。gateGate を握って呼ぶ。</summary>
    private ShopNpc? GateOf(ENpcBase npc)
    {
        var stories = Svc.Data.GetExcelSheet<Story>();
        foreach (var d in npc.ENpcData)
        {
            var v = d.RowId;
            if (v == 0 || v >> 16 != HandlerStory || !stories.TryGetRow(v, out var story))
                continue;
            if (this.StoryGate(npc.RowId, story) is { } gate)
                return gate;
        }

        return null;
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

    /// <summary>
    /// FATE でしか出ない敵（FATE のボス）を、素材を落とす敵から外す（不具合の例：東ザナラーンのエルダー・ロングホーンは普段いない
    /// ＝FATE「長角の古老『エルダー・ロングホーン』」のボス。素材集めの戦闘は FATE の敵を狙わない作りなので、出現点に入れても倒せず、
    /// 見つからないまま見回り続けた）。見分け方：ゲームデータの FATE の名前の「」の中と同じ名前で、出現点が2か所以下
    /// （普通の敵がたまたま同じ名前でも、出現点が多いので外れない。エルダー・ロングホーンの出現点は1か所）。
    /// </summary>
    private void ExcludeFateBosses()
    {
        var bosses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Svc.Data.GetExcelSheet<Fate>())
        {
            var name = f.Name.ExtractText();
            var a = name.IndexOf('「');
            var b = name.LastIndexOf('」');
            if (a >= 0 && b > a + 1)
                bosses.Add(name[(a + 1)..b]);
        }

        if (bosses.Count == 0)
            return;

        var names = Svc.Data.GetExcelSheet<BNpcName>();
        var removed = new HashSet<string>();
        foreach (var s in this.map.Values)
        {
            s.DropMobs.RemoveAll(m =>
            {
                if (!names.TryGetRow(m, out var row) || row.Singular.ExtractText() is not { Length: > 0 } n || !bosses.Contains(n))
                    return false;
                if (this.spawns.TryGetValue(m, out var list) && list.Count > 2)
                    return false;
                removed.Add(n);
                return true;
            });
        }

        if (removed.Count > 0)
            Core.DebugLog.Current?.Line("データ", $"FATE でしか出ない敵（FATE のボス）を、素材を落とす敵から外しました：{removed.Count} 種（{string.Join("・", removed.Take(10))}{(removed.Count > 10 ? " など" : string.Empty)}）");
        this.FateBosses = removed;
    }

    /// <summary>素材を落とす敵から外した FATE のボスの名前（調べ用）。</summary>
    public IReadOnlySet<string> FateBosses { get; private set; } = new HashSet<string>();

    /// <summary>クエスト専用の敵として外した出現位置（調べ用：名前の番号・エリア・地図座標・理由。EventOnlySpawns）。</summary>
    public IReadOnlyList<(uint NameId, uint Territory, float MapX, float MapY, string Why)> EventOnlySpawnsRemoved { get; private set; } = [];

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

            // クエスト専用の敵の出現位置は入れない（EventOnlySpawns）。
            // 読めなければ外さずに続ける（以前と同じ。普段いない敵は、戦闘で見かけないまま回ったら回らなくなる）
            EventOnlySpawns? eventOnly = null;
            Dictionary<MobSpawnPosition, string> questOnly = new(ReferenceEqualityComparer.Instance);
            try
            {
                var built = EventOnlySpawns.Build();
                questOnly = built.QuestOnly(spawnRows);
                eventOnly = built; // 判定まで済んでから（途中で失敗したら、外した件数の記録を出さない）
            }
            catch (Exception ex)
            {
                this.notes.Add($"クエストの敵の置き場所を読めませんでした（外さずに続けます）: {ex.GetType().Name}: {ex.Message}");
            }

            var removedEvent = new List<(uint, uint, float, float, string)>();
            foreach (var m in spawnRows)
            {
                if (m.BNpcNameId == 0 || m.TerritoryTypeId == 0)
                    continue;
                if (questOnly.TryGetValue(m, out var quest))
                {
                    removedEvent.Add((m.BNpcNameId, m.TerritoryTypeId, m.Position.X, m.Position.Y, $"クエスト「{quest}」の敵"));
                    if (!this.eventOnlySpots.TryGetValue(m.BNpcNameId, out var gone))
                        this.eventOnlySpots[m.BNpcNameId] = gone = [];
                    gone.Add(new MobSpot(m.BNpcNameId, m.TerritoryTypeId, m.Position.X, m.Position.Y));
                    continue;
                }

                if (!this.spawns.TryGetValue(m.BNpcNameId, out var list))
                    this.spawns[m.BNpcNameId] = list = [];

                // 位置の1・2番目は地図座標（X, Y）。3番目は高さだが値が当てにならない行があるので使わない
                list.Add(new MobSpot(m.BNpcNameId, m.TerritoryTypeId, m.Position.X, m.Position.Y));
            }

            if (failed1.Count + failed2.Count > 0)
                this.notes.Add($"戦闘ドロップのデータで読めない行がありました（ドロップ {failed1.Count} 行 / 出現位置 {failed2.Count} 行）");

            this.EventOnlySpawnsRemoved = removedEvent;
            if (eventOnly != null)
                Core.DebugLog.Current?.Line("データ", $"クエスト専用の敵の出現位置を外しました：{removedEvent.Count} 行"
                                                    + $"（クエストの台本の敵の置き場所 {eventOnly.QuestPlaces} か所から。討伐手帳に載る敵は外さない。デバッグタブで戻せる）");

            this.ExcludeFateBosses();
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
