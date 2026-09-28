using System;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 納品物にマテリアを1個付ける（自動装着）。
///
/// 手順は Automaton の GettingTooAttached.cs から「外す」部分を除いて切り出したもの:
///   開く（GeneralAction 12）→ 分類「所持品」→ アイテムを選ぶ → マテリアを選ぶ → 確認ダイアログの「装着する」
///   → 装着中の旗が下りる → 「対象のマテリア数 +1 AND カバンのマテリア −1」で確認 → 自分で開いたときだけ閉じる。
///
/// 補足:
///  ・装着はその装備の修理職（Item.ClassJobRepair）でないとできないので、先に着替える。
///  ・成功率が 100% 未満なら押さずに「戻る」を押して中止する（通常の装着の1穴目は 100%）。
///  ・YesAlready など他のプラグインが先に確認を押しても、結果の確認で成功とみなす。
///  ・AgentMateriaAttach の Materia プロパティは誤りがあるので MateriaSorted を使う。一覧は ItemCount / MateriaCount まで回す。
/// </summary>
public sealed unsafe class MeldTask : AutoTask
{
    private enum MeldStep
    {
        Prepare, Equip, Open, WaitOpen, SelectCategory, WaitCategory, SelectItem, WaitItemLoaded,
        SelectMateria, WaitDialog, CheckDialog, WaitMeldEnd, Verify, Close,
    }

    private const uint GeneralActionMateriaMelding = 12; // GeneralAction 12 = マテリア装着（ゲームデータで確認）
    private const string DialogAddonName = "MateriaAttachDialog";
    private const uint MeldButtonNodeId = 35;   // ECommons / YesAlready / SimpleTweaks の3つで一致
    private const uint ReturnButtonNodeId = 36;  // ECommons

    private readonly MateriaNeed need;
    private MeldStep step = MeldStep.Prepare;
    private EquipJobTask? equip;

    private InventoryType targetType;
    private int targetSlot;
    private uint materiaItemId;
    private uint targetLevelItem;
    private bool openedByUs;
    private byte materiaCountBefore;
    private long materiaStockBefore;
    private uint targetItemIdBefore;

    public MeldTask(MateriaNeed need)
    {
        this.need = need;
    }

    public override string Name => $"マテリア装着: {CraftPlanner.ItemName(this.need.TargetItemId)}";

    private static AgentMateriaAttach* Agent => AgentMateriaAttach.Instance();

    private InventoryItem* LiveTarget => InventoryManager.Instance()->GetInventorySlot(this.targetType, this.targetSlot);

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.step)
        {
            case MeldStep.Prepare:
                return this.Prepare(ctx);

            case MeldStep.Equip:
            {
                var r = this.equip!.Step(ctx);
                this.Status = this.equip.Status;
                if (r == TaskResult.Running)
                    return TaskResult.Running;
                this.equip.Cleanup(ctx);
                if (r == TaskResult.Failed)
                    return this.Fail(this.equip.FailReason ?? "着替えに失敗しました");
                this.Next(MeldStep.Open);
                return TaskResult.Running;
            }

            case MeldStep.Open:
            {
                var target = this.LiveTarget;
                if (target == null || target->ItemId == 0)
                    return this.Fail("対象のアイテムが見つかりません");
                if (!GameUi.PlayerFree())
                    return TaskResult.Running;

                this.targetItemIdBefore = target->ItemId;
                this.materiaCountBefore = target->GetMateriaCount();
                this.materiaStockBefore = CountInBags(this.materiaItemId);
                if (this.materiaStockBefore <= 0)
                    return this.Fail($"カバンに {CraftPlanner.ItemName(this.materiaItemId)} がありません");

                if (Agent->IsAgentActive())
                {
                    this.Next(MeldStep.SelectCategory);
                    return TaskResult.Running;
                }

                if (!GameUi.UseGeneralAction(GeneralActionMateriaMelding))
                    return this.Fail("マテリア装着の画面を開けませんでした");
                this.openedByUs = true;
                this.Next(MeldStep.WaitOpen);
                return TaskResult.Running;
            }

            case MeldStep.WaitOpen:
                if (Agent->IsAgentActive())
                    this.Next(MeldStep.SelectCategory);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("マテリア装着の画面が開きません");
                return TaskResult.Running;

            case MeldStep.SelectCategory:
                if (Agent->UpdateState != 0)
                {
                    if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("装着画面の読み込みが終わりません");
                    return TaskResult.Running;
                }

                if (Agent->Category != AgentMateriaAttach.FilterCategory.Inventory)
                    SendAgentEvent(0, 0, (int)AgentMateriaAttach.FilterCategory.Inventory);
                this.Next(MeldStep.WaitCategory);
                return TaskResult.Running;

            case MeldStep.WaitCategory:
                if (Agent->UpdateState == 0 && Agent->Category == AgentMateriaAttach.FilterCategory.Inventory)
                    this.Next(MeldStep.SelectItem);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("分類が「所持品」に切り替わりません");
                return TaskResult.Running;

            case MeldStep.SelectItem:
            {
                var live = this.LiveTarget;
                var data = Agent->Data;
                for (var i = 0; i < Agent->ItemCount; i++)
                {
                    var entry = data->ItemsSorted[i].Value;
                    if (entry == null)
                        continue;
                    if (entry->Item == live)
                    {
                        SendAgentEvent(0, 1, i, 1, 0);
                        this.Next(MeldStep.WaitItemLoaded);
                        return TaskResult.Running;
                    }
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("対象のアイテムが装着画面の一覧に出ません");
                return TaskResult.Running;
            }

            case MeldStep.WaitItemLoaded:
                if (Agent->UpdateState == 0)
                    this.Next(MeldStep.SelectMateria);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("アイテムを選んだあとの読み込みが終わりません");
                return TaskResult.Running;

            case MeldStep.SelectMateria:
            {
                var data = Agent->Data;
                for (var i = 0; i < Agent->MateriaCount; i++)
                {
                    var entry = data->MateriaSorted[i].Value;
                    if (entry == null || entry->Item == null)
                        continue;
                    if (entry->Item->ItemId != this.materiaItemId)
                        continue;
                    if (entry->ItemLevel > this.targetLevelItem)
                        return this.Fail($"このマテリアはアイテムレベルが足りず付けられません（{entry->ItemLevel} > {this.targetLevelItem}）");

                    SendAgentEvent(0, 2, i, 1, 0);
                    this.Next(MeldStep.WaitDialog);
                    return TaskResult.Running;
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("指定のマテリアが右の一覧に出ません（等級・アイテムレベルの制限の可能性）");
                return TaskResult.Running;
            }

            case MeldStep.WaitDialog:
                if (Svc.Condition[ConditionFlag.MeldingMateria] && IsDialogReady(out _))
                    this.Next(MeldStep.CheckDialog);
                else if (this.MateriaCountIncreased())
                    this.Next(MeldStep.Verify); // 他のプラグインが先に押した
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("装着の確認画面が出ません");
                return TaskResult.Running;

            case MeldStep.CheckDialog:
            {
                if (!IsDialogReady(out var dialog))
                {
                    if (!Svc.Condition[ConditionFlag.MeldingMateria])
                        this.Next(MeldStep.Verify);
                    else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("確認画面が消えました");
                    return TaskResult.Running;
                }

                var rate = dialog->TypedAtkValues->SuccessRate.Int;
                if (rate < 100)
                {
                    PressButton(dialog, ReturnButtonNodeId);
                    return this.Fail($"成功率が {rate}% のため中止しました（100% でないとマテリアを失う恐れがあります）");
                }

                var meld = dialog->GetComponentButtonById(MeldButtonNodeId);
                if (meld == null || !meld->IsEnabled)
                {
                    if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("「装着する」が押せる状態になりません");
                    return TaskResult.Running;
                }

                PressMeld(dialog);
                this.Next(MeldStep.WaitMeldEnd);
                return TaskResult.Running;
            }

            case MeldStep.WaitMeldEnd:
                if (!Svc.Condition[ConditionFlag.MeldingMateria])
                    this.Next(MeldStep.Verify);
                else if (this.TimedOut(TimeSpan.FromSeconds(15)))
                    return this.Fail("装着が終わりません");
                return TaskResult.Running;

            case MeldStep.Verify:
            {
                var increased = this.MateriaCountIncreased();
                var decreased = CountInBags(this.materiaItemId) == this.materiaStockBefore - 1;
                if (increased && decreased)
                {
                    ctx.Log.Write("装着", $"{CraftPlanner.ItemName(this.need.TargetItemId)} に {CraftPlanner.ItemName(this.materiaItemId)} を付けました");
                    this.Next(MeldStep.Close);
                    return TaskResult.Running;
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail($"装着できたか確かめられません（付いた={increased} / 減った={decreased}）");
                return TaskResult.Running;
            }

            case MeldStep.Close:
                if (this.openedByUs && Agent->IsAgentActive())
                    Agent->Hide();
                this.openedByUs = false;
                return TaskResult.Done;
        }

        return TaskResult.Running;
    }

    private TaskResult Prepare(TaskContext ctx)
    {
        if (Inventory.HasMelded(this.need.TargetItemId, this.need.TargetHq, this.need.MateriaItemId))
            return TaskResult.Done;

        var items = Svc.Data.GetExcelSheet<Item>();
        if (!items.TryGetRow(this.need.TargetItemId, out var target))
            return this.Fail("アイテムのデータが読めません");
        this.targetLevelItem = target.LevelItem.RowId;

        // 使うマテリア：指定品か、種類不問なら「付けられる候補のうちカバンにあるもの」
        if (this.need.MateriaItemId is { } mid)
        {
            this.materiaItemId = mid;
        }
        else
        {
            var candidates = MateriaCatalog.CandidatesFor(this.need.TargetItemId);
            this.materiaItemId = candidates.FirstOrDefault(c => CountInBags(c) > 0);
            if (this.materiaItemId == 0)
                return this.Fail("付けられるマテリアがカバンにありません");
        }

        // 付ける対象のスロット（カバン内・HQ 指定なら HQ・まだ穴が空いているもの）
        if (!this.FindTargetSlot(target.MateriaSlotCount))
            return this.Fail($"{CraftPlanner.ItemName(this.need.TargetItemId)}{(this.need.TargetHq ? "（HQ）" : string.Empty)} がカバンにありません（アーマリーチェストにある場合はカバンに移してください）");

        // 装着はその装備の修理職で行う
        var repair = target.ClassJobRepair.RowId;
        if (repair != 0 && Jobs.CurrentClassJob != repair)
        {
            this.equip = new EquipJobTask(repair);
            this.Next(MeldStep.Equip);
            return TaskResult.Running;
        }

        this.Next(MeldStep.Open);
        return TaskResult.Running;
    }

    private bool FindTargetSlot(byte slotCount)
    {
        var im = InventoryManager.Instance();
        foreach (var type in Inventory.Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId != this.need.TargetItemId)
                    continue;
                if (this.need.TargetHq && (s->Flags & InventoryItem.ItemFlags.HighQuality) == 0)
                    continue;
                if (s->GetMateriaCount() >= Math.Max((byte)1, slotCount))
                    continue;

                this.targetType = type;
                this.targetSlot = i;
                return true;
            }
        }

        return false;
    }

    private void Next(MeldStep s)
    {
        this.step = s;
        this.NextPhase(s.ToString());
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.equip?.Cleanup(ctx);
        if (this.openedByUs && Agent != null && Agent->IsAgentActive())
            Agent->Hide();
        this.openedByUs = false;
    }

    private bool MateriaCountIncreased()
    {
        var live = this.LiveTarget;
        return live != null && live->ItemId == this.targetItemIdBefore && live->GetMateriaCount() == this.materiaCountBefore + 1;
    }

    /// <summary>GettingTooAttached.cs:146-154 と同じ。値はすべて AtkValueType.Int。</summary>
    private static void SendAgentEvent(ulong eventKind, params int[] values)
    {
        var ret = new AtkValue();
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i].Type = AtkValueType.Int;
            atkValues[i].Int = values[i];
        }

        Agent->ReceiveEvent(&ret, atkValues, (uint)values.Length, eventKind);
    }

    /// <summary>clib AddonMateriaAttachDialog.cs と同じ（ButtonClick / eventParam 0）。</summary>
    private static void PressMeld(AddonMateriaAttachDialog* dialog)
    {
        var addon = (AtkUnitBase*)dialog;
        var evt = new AtkEvent { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt, &data);
    }

    /// <summary>ECommons ClickAddonButton と同じ：ノードに登録済みの先頭イベントを流す。</summary>
    private static void PressButton(AddonMateriaAttachDialog* dialog, uint nodeId)
    {
        var addon = (AtkUnitBase*)dialog;
        var button = addon->GetComponentButtonById(nodeId);
        if (button == null || !button->IsEnabled)
            return;
        var res = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = res.AtkEventManager.Event;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
    }

    private static bool IsDialogReady(out AddonMateriaAttachDialog* dialog)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(DialogAddonName);
        dialog = (AddonMateriaAttachDialog*)addon;
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded();
    }

    private static long CountInBags(uint itemId)
    {
        long total = 0;
        var im = InventoryManager.Instance();
        foreach (var type in Inventory.Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    total += slot->Quantity;
            }
        }

        return total;
    }
}
