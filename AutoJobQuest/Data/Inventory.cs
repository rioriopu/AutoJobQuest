using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoJobQuest.Data;

/// <summary>
/// 所持数を読む。フレームワークのスレッドから呼ぶこと。
///
/// 【どこを数えるか】
///  ・カバン（Inventory1〜4）
///  ・クリスタル欄（Crystals＝2001）… シャード・クリスタル・クラスターはカバンではなくここに入る。
///  ・アーマリーチェスト … 製作した装備が入ることがある。納品（NPC への受け渡し）でも拾われる
///    （Questionable の所持判定も checkArmory: true で数えている：Craft.cs GetOwnedItemCount）。
/// 装備中の品は数えない（納品で外されると困るため）。リテイナー・チョコボかばんも数えない。
/// ギアセットに登録された品は、ギアセット1つが使う数（装備中の分を除く）だけ手持ちから引く（利用者の装備を納品物と数えない：GearsetGuard）。
/// </summary>
public sealed unsafe class Inventory : IInventoryView
{
    /// <summary>数える欄。</summary>
    public static readonly InventoryType[] Containers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    /// <summary>カバンだけ（空き枠を数えるとき用）。</summary>
    public static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly Dictionary<uint, int> nq = [];
    private readonly Dictionary<uint, int> hq = [];

    /// <summary>
    /// 検証の仕組み用：設定すると、ゲームの持ち物の代わりにこれを読む（（品, HQ か）→ 数）。本番では null のまま
    /// （本番の作業〔CraftOneTask 等〕を偽物の持ち物で通しで動かすため）。
    /// </summary>
    public static Func<IReadOnlyDictionary<(uint Item, bool Hq), int>>? TestSource { get; set; }

    /// <summary>検証の仕組み用：偽物の持ち物のときの鞄の空き枠の数（<see cref="TestSource"/> と組で使う）。</summary>
    public static Func<int> TestFreeBagSlots { get; set; } = () => 100;

    /// <summary>
    /// 実行の始めに手持ち（鞄・アーマリー）にあったギアセットの品の数（品, HQ か）→ 数。設定されていれば、守る数はこれを上限にする
    /// （利用者の分がリテイナーなど手持ちの外にあると、実行の途中で作った・引き出した同じ品を利用者の分として引いてしまい、
    /// 「1つも増えませんでした」で止まっていた）。実行の間だけ設定する（JobQuestFlow）。
    /// </summary>
    public static Dictionary<(uint Item, bool Hq), int>? GearsetKeepCap { get; set; }

    /// <summary>守る数：ギアセットが使う数 − 装備中の数。実行の始めの手持ちの数（<paramref name="capAtStart"/>）があれば、それを上限にする。</summary>
    public static int ProtectedKeep(int used, int equipped, int? capAtStart)
        => Math.Max(0, capAtStart is { } cap ? Math.Min(used - equipped, cap) : used - equipped);

    /// <summary>いまの手持ちにある、ギアセットの守る分（品, HQ か）→ 数（実行の始めに <see cref="GearsetKeepCap"/> を決めるため）。</summary>
    public static Dictionary<(uint Item, bool Hq), int> GearsetKeepInBags()
    {
        var result = new Dictionary<(uint Item, bool Hq), int>();
        if (TestSource != null)
            return result;
        var pieces = GearsetGuard.Read();
        if (pieces.Count == 0)
            return result;
        var equipped = GearsetGuard.EquippedCounts();
        var raw = Snapshot(subtractGearsets: false);
        foreach (var ((item, isHq), n) in GearsetGuard.ProtectedCounts(pieces))
        {
            var inBags = Math.Min(n - equipped.GetValueOrDefault((item, isHq)), isHq ? raw.CountHq(item) : raw.CountNq(item));
            if (inBags > 0)
                result[(item, isHq)] = inBags;
        }

        return result;
    }

    /// <summary>
    /// いまの所持数を写し取る。
    /// <paramref name="subtractGearsets"/> を false にすると、ギアセットの品を引かない生の数（受け取りの前後の増減を見るとき用）。
    /// </summary>
    public static Inventory Snapshot(bool subtractGearsets = true)
    {
        var inv = new Inventory();
        if (TestSource is { } source)
        {
            foreach (var ((item, isHq), n) in source())
            {
                var d = isHq ? inv.hq : inv.nq;
                d[item] = d.GetValueOrDefault(item) + n;
            }

            return inv;
        }

        var im = InventoryManager.Instance();
        if (im == null)
            return inv;

        foreach (var type in Containers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;

            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    continue;

                // 収集品は通常品として数えない（納品にも材料にも使えないため）
                if ((slot->Flags & InventoryItem.ItemFlags.Collectable) != 0)
                    continue;

                var dict = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? inv.hq : inv.nq;
                dict[slot->ItemId] = dict.GetValueOrDefault(slot->ItemId) + slot->Quantity;
            }
        }

        // ギアセットの品を引く（以前はアーマリーにある利用者の装備・道具を納品物の手持ちと数えた）。
        // 装備中の分はもともと数えていないので、引くのは「ギアセットが使う数 − 装備中の数」だけ
        if (!subtractGearsets)
            return inv;
        var pieces = GearsetGuard.Read();
        if (pieces.Count > 0)
        {
            var equipped = GearsetGuard.EquippedCounts();
            var cap = GearsetKeepCap;
            foreach (var ((item, isHq), n) in GearsetGuard.ProtectedCounts(pieces))
            {
                var keep = ProtectedKeep(n, equipped.GetValueOrDefault((item, isHq)), cap == null ? null : cap.GetValueOrDefault((item, isHq)));
                if (keep <= 0)
                    continue;
                var dict = isHq ? inv.hq : inv.nq;
                if (dict.TryGetValue(item, out var have))
                    dict[item] = Math.Max(0, have - keep);
            }
        }

        return inv;
    }

    /// <summary>
    /// ギアセットが使う数のうち、装備中と手持ち（生の数）で足りていない分（品, HQ か）→ 数。
    /// リテイナーに預けてある利用者の装備を、引き出しの対象に数えないために使う。
    /// </summary>
    public static Dictionary<(uint Item, bool Hq), int> GearsetKeepOutsideBags()
    {
        var result = new Dictionary<(uint Item, bool Hq), int>();
        if (TestSource != null)
            return result;
        var pieces = GearsetGuard.Read();
        if (pieces.Count == 0)
            return result;
        var equipped = GearsetGuard.EquippedCounts();
        var raw = Snapshot(subtractGearsets: false);
        foreach (var ((item, isHq), n) in GearsetGuard.ProtectedCounts(pieces))
        {
            var rest = n - equipped.GetValueOrDefault((item, isHq)) - (isHq ? raw.CountHq(item) : raw.CountNq(item));
            if (rest > 0)
                result[(item, isHq)] = rest;
        }

        return result;
    }

    public int CountNq(uint itemId) => this.nq.GetValueOrDefault(itemId);

    public int CountHq(uint itemId) => this.hq.GetValueOrDefault(itemId);

    public int CountAll(uint itemId) => this.CountNq(itemId) + this.CountHq(itemId);

    /// <summary>その場で1品目だけ数える（写しを取らずに）。</summary>
    public static int CountNow(uint itemId, bool hqOnly = false)
    {
        var snap = Snapshot();
        return hqOnly ? snap.CountHq(itemId) : snap.CountAll(itemId);
    }

    /// <summary>カバンの空き枠の数。</summary>
    public static int FreeBagSlots()
    {
        if (TestSource != null)
            return TestFreeBagSlots();
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;

        var free = 0;
        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;

            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>
    /// クリスタル欄に入る品か（ファイアシャード＝アイテム 2 と同じ FilterGroup。SourceIndex.BuildCrystals と同じ判定で、番号の範囲を決め打ちしない）。
    /// </summary>
    public static bool IsCrystalItem(uint itemId)
    {
        var items = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        return items.TryGetRow(2, out var shard) && items.TryGetRow(itemId, out var row) && row.FilterGroup == shard.FilterGroup;
    }

    /// <summary>クリスタル欄にあるその品の数（上限を超えて引き出さないため）。</summary>
    public static int CrystalCount(uint itemId)
    {
        if (TestSource is { } source)
            return source().Where(kv => kv.Key.Item == itemId).Sum(kv => kv.Value);
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var c = im->GetInventoryContainer(InventoryType.Crystals);
        if (c == null)
            return 0;
        var n = 0;
        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s != null && s->ItemId == itemId)
                n += s->Quantity;
        }

        return n;
    }

    /// <summary>
    /// 鞄（Inventory1〜4）の、同じ品・同じ品質の山にまだ積める数（新しい枠を使わずに入る数）。
    /// 収集品の山には通常品を積めないので数えない。
    /// </summary>
    public static int StackRoom(uint itemId, bool hq, int stackSize)
    {
        if (TestSource != null)
            return 0; // 検証の仕組み：同じ品の山には積まず、新しい枠を使うとみる
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var room = 0;
        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId != itemId || (s->Flags & InventoryItem.ItemFlags.Collectable) != 0
                    || ((s->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != hq)
                    continue;
                room += Math.Max(0, stackSize - s->Quantity);
            }
        }

        return room;
    }

    /// <summary>
    /// 指定のマテリアが付いた品を持っているか。
    /// materiaItemId が null なら「何かマテリアが付いていればよい」。
    /// </summary>
    public static bool HasMelded(uint itemId, bool hqOnly, uint? materiaItemId)
    {
        if (TestSource != null)
            return false;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;

        // マテリアのアイテム ID → (Materia 行, 等級)
        (uint Row, int Grade)? want = null;
        if (materiaItemId is { } mid)
            want = MateriaCatalog.Find(mid);

        // 利用者のギアセットの品（付いたマテリアまで一致する品）は「装着済み」に数えない
        var pieces = GearsetGuard.Read();

        foreach (var type in Containers)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;

            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId)
                    continue;
                if (hqOnly && (slot->Flags & InventoryItem.ItemFlags.HighQuality) == 0)
                    continue;
                if (GearsetGuard.IsProtectedMelded(itemId, (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0, MateriaOf(slot), pieces))
                    continue;

                for (byte m = 0; m < 5; m++)
                {
                    var row = slot->Materia[m];
                    if (row == 0)
                        continue;
                    if (want == null)
                        return true;
                    if (row == want.Value.Row && slot->MateriaGrades[m] == want.Value.Grade)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>その枠の品に付いたマテリア（種類と等級）。</summary>
    public static List<Automation.MateriaRef> MateriaOf(InventoryItem* slot)
    {
        var list = new List<Automation.MateriaRef>();
        for (var m = 0; m < 5; m++)
        {
            if (slot->Materia[m] != 0)
                list.Add(new Automation.MateriaRef(slot->Materia[m], slot->MateriaGrades[m]));
        }

        return list;
    }

    /// <summary>
    /// 収集品の数（収集価値が minCollectability 以上のもの）。カバンの枠を直接数える
    /// （GetInventoryItemCount では収集品は 0 が返る）。
    /// </summary>
    public static int CountCollectables(uint itemId, int minCollectability)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;

        var n = 0;
        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId != itemId || (s->Flags & InventoryItem.ItemFlags.Collectable) == 0)
                    continue;
                if (s->SpiritbondOrCollectability >= minCollectability)
                    n += Math.Max(1, s->Quantity);
            }
        }

        return n;
    }

    /// <summary>
    /// カバン（Inventory1〜4）にある収集品を、元のアイテムごとに数える（収集価値は問わない）。
    /// 納品画面の右の一覧（node 31）の行数と突き合わせるのに使う。
    /// </summary>
    public static Dictionary<uint, int> HeldCollectables()
    {
        var totals = new Dictionary<uint, int>();
        var im = InventoryManager.Instance();
        if (im == null)
            return totals;

        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !s->IsCollectable())
                    continue;
                var id = s->GetBaseItemId();
                totals[id] = totals.GetValueOrDefault(id) + s->Quantity;
            }
        }

        return totals;
    }

    /// <summary>その収集品の手持ち1個ずつの収集価値。</summary>
    public static List<int> Collectabilities(uint itemId)
    {
        var values = new List<int>();
        var im = InventoryManager.Instance();
        if (im == null)
            return values;

        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && s->IsCollectable() && s->GetBaseItemId() == itemId)
                    values.Add(s->GetCollectability());
            }
        }

        return values;
    }

    /// <summary>
    /// 特殊通貨（スクリップなど）の番号からアイテム ID を引き、所持数を返す。
    /// アイテムに直せなければ itemId=0 で 0 を返す（呼び出し側は「0 個」と区別して扱うこと）。
    /// </summary>
    public static int CountSpecialCurrency(byte specialId, out uint itemId)
    {
        itemId = SpecialCurrency.ItemId(specialId);
        var im = InventoryManager.Instance();
        if (itemId == 0 || im == null)
            return 0;

        return im->GetInventoryItemCount(itemId, false, false, false) + im->GetInventoryItemCount(itemId, true, false, false);
    }

    /// <summary>ギルの所持額。</summary>
    public static long Gil()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetGil();
    }
}
