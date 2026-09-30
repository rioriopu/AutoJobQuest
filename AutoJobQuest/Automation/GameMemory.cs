using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// 検証の仕組み用：ゲームのメモリの偽物（受注・納品の作業と呼び鈴の引き出しを、偽物のゲームで通しで動かすため）。
/// 本番では使わない。
/// </summary>
public interface IGameMemoryTest
{
    bool IsQuestComplete(uint questRowId);

    bool IsQuestAccepted(uint questRowId);

    byte QuestSequence(uint questRowId);

    bool JournalFull { get; }

    bool? TodoChecked(uint questRowId, byte todo);

    uint CurrentClassJob { get; }

    int JobLevel(uint classJobId);

    int FindGearset(uint classJobId);

    bool GearsetEquipped(int gearset);

    int EquipGearset(int gearset);

    bool IsBookUnlocked(uint secretRecipeBookId);

    bool RetainersReady { get; }

    IReadOnlyList<(ulong Id, string Name, bool Available)> Retainers { get; }

    ulong LastSelectedRetainer { get; }

    int RetainerListIndex(nint list, string name);

    void SelectRetainer(nint list, int index);

    bool RetainerInventoryActive { get; }

    void HideRetainerInventory();

    List<RetainerStockTask.RetainerSlot>? RetainerSlots();

    void OpenRetainerItemMenu(InventoryType container, int slot);

    bool ContextMenuTargets(InventoryType container, int slot);

    List<string> ContextMenuLabels();

    bool ContextMenuItemDisabled(int index);

    List<(int Size, int Speed, bool Inverse)>? SpearfishingFish();

    uint TargetBaseId { get; }

    bool? AetherCurrentsComplete(uint territory);

    bool HasStatus(uint statusId);
}

/// <summary>
/// ゲームのメモリ（クエストの進み・ギアセット・リテイナー・右クリックのメニュー）を読む・動かす所を1か所に集めたもの。
/// 本番ではゲームのメモリを読む（中身は、もとは各作業に書いてあった処理そのまま）。検証の仕組みでは <see cref="Test"/> の偽物を使う。
/// ゲームの画面は <see cref="GameUi.TestBackend"/>、持ち物の数は <see cref="Data.Inventory.TestSource"/> で差し替える。
/// </summary>
public static unsafe class GameMemory
{
    /// <summary>検証の仕組み用：設定すると、ゲームのメモリの代わりにこれを使う。本番では null のまま。</summary>
    public static IGameMemoryTest? Test { get; set; }

    // ---- クエスト ----

    public static bool IsQuestComplete(uint questRowId)
        => Test is { } t ? t.IsQuestComplete(questRowId) : QuestManager.IsQuestComplete(questRowId);

    public static byte QuestSequence(uint questRowId)
        => Test is { } t ? t.QuestSequence(questRowId) : QuestManager.GetQuestSequence(questRowId);

    /// <summary>受注済みか。クエストの情報を読めなければ null。</summary>
    public static bool? QuestAccepted(uint questRowId)
    {
        if (Test is { } t)
            return t.IsQuestAccepted(questRowId);
        var qm = QuestManager.Instance();
        return qm == null ? null : qm->IsQuestAccepted(questRowId);
    }

    /// <summary>ジャーナルが上限まで埋まっているか（読めなければ false）。</summary>
    public static bool JournalFull
    {
        get
        {
            if (Test is { } t)
                return t.JournalFull;
            var qm = QuestManager.Instance();
            return qm != null && qm->NumAcceptedQuests >= qm->NormalQuests.Length;
        }
    }

    // ---- ギアセット ----

    /// <summary>そのギアセットに着替える（ゲームの戻り値。0 なら受け付けた、それ以外は断られた：EquipJobTask）。</summary>
    public static int EquipGearset(int gearset)
        => Test is { } t ? t.EquipGearset(gearset) : RaptureGearsetModule.Instance()->EquipGearset(gearset);

    // ---- リテイナー ----

    public static bool RetainersReady
    {
        get
        {
            if (Test is { } t)
                return t.RetainersReady;
            var m = RetainerManager.Instance();
            return m != null && m->IsReady;
        }
    }

    /// <summary>リテイナー（一覧の並び。ID が 0 の枠は除く）。</summary>
    public static List<(ulong Id, string Name, bool Available)> Retainers()
    {
        if (Test is { } t)
            return [.. t.Retainers];
        var list = new List<(ulong, string, bool)>();
        var m = RetainerManager.Instance();
        if (m == null)
            return list;
        for (uint i = 0; i < m->Retainers.Length; i++)
        {
            var r = m->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0)
                list.Add((r->RetainerId, r->NameString, r->Available));
        }

        return list;
    }

    /// <summary>最後に呼んだリテイナー。読めなければ null。</summary>
    public static ulong? LastSelectedRetainer
    {
        get
        {
            if (Test is { } t)
                return t.LastSelectedRetainer;
            var m = RetainerManager.Instance();
            return m == null ? null : m->LastSelectedRetainerId;
        }
    }

    /// <summary>
    /// 一覧の窓での番号（名前で探す。並びを RetainerManager の並びと決めつけない。選べない人は -1）。
    /// 一覧の値は 3 番目から1人10個ずつ（名前・…・選べるか＝8番目）：ECommons の ReaderRetainerList と同じ。
    /// </summary>
    public static int RetainerListIndex(AtkUnitBase* list, string name)
    {
        if (Test is { } t)
            return t.RetainerListIndex((nint)list, name);
        for (var i = 0; i < 10; i++)
        {
            var at = 3 + (i * 10);
            if (at + 8 >= list->AtkValuesCount)
                break;
            var v = list->AtkValues[at];
            if (v.Type == 0)
                break;
            if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) || v.String.Value == null)
                continue;
            var shown = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
            if (shown != name)
                continue;
            var active = list->AtkValues[at + 8];
            return active.Type == AtkValueType.Bool && active.Byte == 0 ? -1 : i;
        }

        return -1;
    }

    /// <summary>一覧でリテイナーを選ぶ（ECommons の AddonMaster.RetainerList.Entry.Select と同じ：2, 一覧の番号, 型なし, 型なし）。</summary>
    public static void SelectRetainer(AtkUnitBase* list, int index)
    {
        if (Test is { } t)
        {
            t.SelectRetainer((nint)list, index);
            return;
        }

        var args = stackalloc AtkValue[4];
        for (var n = 0; n < 4; n++)
            args[n] = default;
        args[0].SetInt(2);
        args[1].SetUInt((uint)index);
        list->FireCallback(4, args, true);
    }

    /// <summary>リテイナーの持ち物（AgentRetainer）が開いているか。</summary>
    public static bool RetainerInventoryActive
    {
        get
        {
            if (Test is { } t)
                return t.RetainerInventoryActive;
            var agent = AgentRetainer.Instance();
            return agent != null && agent->IsAgentActive();
        }
    }

    /// <summary>リテイナーの持ち物を閉じる（AgentRetainer.Hide）。</summary>
    public static void HideRetainerInventory()
    {
        if (Test is { } t)
        {
            t.HideRetainerInventory();
            return;
        }

        var agent = AgentRetainer.Instance();
        if (agent != null)
            agent->Hide();
    }

    /// <summary>
    /// 開いているリテイナーの持ち物。通常の7ページが読めていなければ null（まだ読み込み中）。
    /// クリスタルの欄は読み込み済みと報告しないことがあるので、読めた枠だけを数える。
    /// 収集品・マテリアの付いた品・リンクだけの枠は数えない。
    /// </summary>
    public static List<RetainerStockTask.RetainerSlot>? RetainerSlots()
    {
        if (Test is { } t)
            return t.RetainerSlots();
        var result = new List<RetainerStockTask.RetainerSlot>();
        var im = InventoryManager.Instance();
        if (im == null)
            return null;
        foreach (var page in RetainerStockTask.Pages)
        {
            var c = im->GetInventoryContainer(page);
            if (c == null || c->Size <= 0)
            {
                if (page == InventoryType.RetainerCrystals)
                    continue;
                return null;
            }

            if (!c->IsLoaded && page != InventoryType.RetainerCrystals)
                return null;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || s->Quantity <= 0 || s->IsSymbolic || s->IsCollectable() || s->GetMateriaCount() > 0)
                    continue;
                result.Add(new(page, i, s->ItemId, (s->Flags & InventoryItem.ItemFlags.HighQuality) != 0, s->Quantity));
            }
        }

        return result;
    }

    /// <summary>リテイナーの持ち物の枠の右クリックのメニューを開く。</summary>
    public static void OpenRetainerItemMenu(InventoryType container, int slot)
    {
        if (Test is { } t)
        {
            t.OpenRetainerItemMenu(container, slot);
            return;
        }

        AgentInventoryContext.Instance()->OpenForItemSlot(container, slot, 0, AgentRetainer.Instance()->GetAddonId());
    }

    /// <summary>右クリックのメニューの対象が、その枠（リテイナーの持ち物の窓から開いたもの）か。</summary>
    public static bool ContextMenuTargets(InventoryType container, int slot)
    {
        if (Test is { } t)
            return t.ContextMenuTargets(container, slot);
        var context = AgentInventoryContext.Instance();
        return context != null && context->TargetInventoryId == container && context->TargetInventorySlotId == slot
               && context->OwnerAddonId == AgentRetainer.Instance()->GetAddonId();
    }

    /// <summary>右クリックのメニューの項目の文字。</summary>
    public static List<string> ContextMenuLabels()
    {
        if (Test is { } t)
            return t.ContextMenuLabels();
        var labels = new List<string>();
        var context = AgentInventoryContext.Instance();
        foreach (var value in context->EventParams)
            if (value.Type == AtkValueType.String && value.String.Value != null)
                labels.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
        return labels;
    }

    /// <summary>右クリックのメニューのその項目が選べない状態か。</summary>
    public static bool ContextMenuItemDisabled(int index)
        => Test is { } t ? t.ContextMenuItemDisabled(index) : AgentInventoryContext.Instance()->IsContextItemDisabled(index);

    // ---- 刺突漁 ----

    /// <summary>
    /// 刺突の画面に出ている魚（大きさ・速さ・向き。下の段から）。画面が開いていなければ null。
    /// 値はゲームの画面の値（ClientStructs の AddonSpearFishing.FishInfo：大きさ 1〜3、速さ 100〜600 の50刻み）。
    /// </summary>
    public static List<(int Size, int Speed, bool Inverse)>? SpearfishingFish()
    {
        if (Test is { } t)
            return t.SpearfishingFish();
        var addon = (AddonSpearFishing*)GameUi.Addon("SpearFishing");
        if (addon == null)
            return null;
        var list = new List<(int, int, bool)>();
        foreach (var f in addon->Fish)
            if (f.Available)
                list.Add(((int)f.Size, f.Speed, f.InverseDirection));
        return list;
    }

    /// <summary>いまターゲットしている物の BaseId（採集点なら GatheringPoint の行）。無ければ 0。</summary>
    public static uint TargetBaseId => Test is { } t ? t.TargetBaseId : Svc.Targets.Target?.BaseId ?? 0;

    /// <summary>
    /// そのエリアの風脈がすべて開放済みか（飛べる・潜れる。GBR の ShouldFly と同じ判定）。風脈の無いエリア・読めなければ null。
    /// </summary>
    public static bool? AetherCurrentsComplete(uint territory)
    {
        if (Test is { } t)
            return t.AetherCurrentsComplete(territory);
        if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territory, out var row) || row.AetherCurrentCompFlgSet.RowId == 0)
            return null;
        var ps = PlayerState.Instance();
        return ps == null ? null : ps->IsAetherCurrentZoneComplete(row.AetherCurrentCompFlgSet.RowId);
    }

    /// <summary>自分にその状態（Status の行）が付いているか。</summary>
    public static bool HasStatus(uint statusId)
        => Test is { } t ? t.HasStatus(statusId) : Svc.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == statusId) == true;
}
