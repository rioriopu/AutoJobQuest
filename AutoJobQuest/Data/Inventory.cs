using System;
using System.Collections.Generic;
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

    /// <summary>いまの所持数を写し取る。</summary>
    public static Inventory Snapshot()
    {
        var inv = new Inventory();
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
        var pieces = GearsetGuard.Read();
        if (pieces.Count > 0)
        {
            var equipped = GearsetGuard.EquippedCounts();
            foreach (var ((item, isHq), n) in GearsetGuard.ProtectedCounts(pieces))
            {
                var keep = n - equipped.GetValueOrDefault((item, isHq));
                if (keep <= 0)
                    continue;
                var dict = isHq ? inv.hq : inv.nq;
                if (dict.TryGetValue(item, out var have))
                    dict[item] = Math.Max(0, have - keep);
            }
        }

        return inv;
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
    /// 指定のマテリアが付いた品を持っているか。
    /// materiaItemId が null なら「何かマテリアが付いていればよい」。
    /// </summary>
    public static bool HasMelded(uint itemId, bool hqOnly, uint? materiaItemId)
    {
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
