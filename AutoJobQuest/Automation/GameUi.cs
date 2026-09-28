using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// ゲーム画面（アドオン）とキャラクターの状態を見る小物。
/// </summary>
public static unsafe class GameUi
{
    /// <summary>
    /// ショップ・マーケットボード系の画面の名前。
    /// これらが開いている間は、移動・テレポなどの介入を一切しない。
    /// </summary>
    public static readonly string[] ShopAddons =
    [
        "Shop", "ShopExchangeItem", "ShopExchangeItemDialog", "ShopExchangeCurrency", "ShopExchangeCurrencyDialog",
        "InclusionShop", "FreeShop", "GrandCompanyExchange", "CollectablesShop",
        "ItemSearch", "ItemSearchResult", "ItemHistory", "RetainerSell", "RetainerSellList",
        "SelectYesno", "SelectString", "SelectIconString", "InputNumeric", "Request",
    ];

    /// <summary>アドオンを名前で探す。表示中でなければ null。</summary>
    public static AtkUnitBase* Addon(string name)
    {
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null)
            return null;
        var a = mgr->GetAddonByName(name);
        return a != null && a->IsVisible ? a : null;
    }

    /// <summary>アドオンが表示されていて、操作できる状態か。</summary>
    public static bool IsReady(string name, out AtkUnitBase* addon)
    {
        addon = Addon(name);
        return addon != null && addon->IsReady && addon->IsFullyLoaded();
    }

    public static bool IsVisible(string name) => Addon(name) != null;

    /// <summary>ショップ・マーケットボード・会話の選択肢など、介入してはいけない画面が開いているか。</summary>
    public static bool IsShopOrMarketOpen()
    {
        foreach (var n in ShopAddons)
            if (IsVisible(n))
                return true;
        return false;
    }

    /// <summary>
    /// アドオンのコールバックを撃つ（ECommons の Callback.Fire と同じ中身：AtkValue を並べて FireCallback）。
    /// 引数は int / uint / bool / string のどれか。
    /// </summary>
    public static void Fire(AtkUnitBase* addon, bool updateState, params object[] values)
    {
        if (addon == null)
        {
            Core.DebugLog.Current?.Line("操作", $"コールバック送信先の画面がありません（値: {string.Join(", ", values)}）");
            return;
        }

        // 送った操作を記録する（不具合のとき、どの画面に何を送ったかを追えるように）
        Core.DebugLog.Current?.Line("操作", $"コールバック送信: {addon->NameString}（{string.Join(", ", System.Linq.Enumerable.Select(values, v => $"{v?.GetType().Name}:{v}"))}）更新={updateState}");

        var atk = stackalloc AtkValue[values.Length];
        var strings = new System.Collections.Generic.List<nint>();
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case int v:
                        atk[i].Type = AtkValueType.Int;
                        atk[i].Int = v;
                        break;
                    case uint v:
                        atk[i].Type = AtkValueType.UInt;
                        atk[i].UInt = v;
                        break;
                    case bool v:
                        atk[i].Type = AtkValueType.Bool;
                        atk[i].Byte = (byte)(v ? 1 : 0);
                        break;
                    case string s:
                        var bytes = System.Text.Encoding.UTF8.GetBytes(s + '\0');
                        var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
                        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, p, bytes.Length);
                        strings.Add(p);
                        atk[i].Type = AtkValueType.String;
                        atk[i].String = (byte*)p;
                        break;
                    default:
                        throw new ArgumentException($"未対応の型: {values[i]?.GetType().Name}");
                }
            }

            addon->FireCallback((uint)values.Length, atk, updateState);
        }
        finally
        {
            foreach (var p in strings)
                System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>自分のキャラクターが自由に動ける状態か（会話・カットシーン・エリア移動・詠唱中でない）。</summary>
    public static bool PlayerFree()
    {
        if (!Me.Available)
            return false;

        var c = Svc.Condition;
        return !(c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                 || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent]
                 || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene]
                 || c[ConditionFlag.Casting] || c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30]
                 || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39]
                 || c[ConditionFlag.Jumping] || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction]
                 || c[ConditionFlag.PreparingToCraft] || c[ConditionFlag.Gathering] || c[ConditionFlag.Fishing]
                 || c[ConditionFlag.MeldingMateria] || c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71]
                 || c[ConditionFlag.MountOrOrnamentTransition]);
    }

    public static bool InCombat => Svc.Condition[ConditionFlag.InCombat];

    public static bool BetweenAreas => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];

    public static bool Mounted => Svc.Condition[ConditionFlag.Mounted];

    /// <summary>
    /// オブジェクトに話しかける。ターゲットが違えば、まずターゲットだけして false を返す（次の呼び出しで話しかける）。
    /// 視線判定なし（checkLineOfSight: false）で話しかける
    /// （同じフレームでターゲットと話しかけを行うと効かないことがある）。
    /// 呼び出し側は1秒程度の間隔を置いて繰り返し呼ぶこと。
    /// </summary>
    public static bool Interact(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;

        if (Svc.Targets.Target?.Address != obj.Address)
        {
            Svc.Targets.Target = obj;
            Core.DebugLog.Current?.Line("操作", $"ターゲット: {obj.Name.TextValue}（BaseId {obj.BaseId}、距離 {System.Numerics.Vector3.Distance(obj.Position, Me.Position):0.0}m）");
            return false;
        }

        var go = (GameObject*)obj.Address;
        var ok = ts->InteractWithObject(go, false) != 0;
        Core.DebugLog.Current?.Line("操作", $"話しかけ: {obj.Name.TextValue}（BaseId {obj.BaseId}、距離 {System.Numerics.Vector3.Distance(obj.Position, Me.Position):0.0}m）→ {(ok ? "受け付け" : "受け付けられず")}");
        return ok;
    }

    /// <summary>
    /// 会話ウィンドウ（Talk）を1つ進める（ECommons AddonMaster.Talk.Click と同じ：MouseDown→MouseClick→MouseUp）。
    /// </summary>
    public static bool AdvanceTalk()
    {
        if (!IsReady("Talk", out var addon))
            return false;

        var evt = stackalloc AtkEvent[1];
        evt[0] = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &AtkStage.Instance()->AtkEventTarget,
            State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = stackalloc AtkEventData[1];
        for (var i = 0; i < sizeof(AtkEventData); i++)
            ((byte*)data)[i] = 0;

        Core.DebugLog.Current?.Line("操作", "会話を進めます（Talk）");
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
        return true;
    }

    /// <summary>
    /// 確認窓（SelectYesno）の「はい」をボタンとして押す。ボタンが無効・非表示なら押さない
    /// （FireCallbackInt(0) はゲームが押させない場面でも通してしまうため使わない）。
    /// </summary>
    public static bool ClickYes(AtkUnitBase* addon)
    {
        if (addon == null)
            return false;
        var y = (AddonSelectYesno*)addon;
        return ClickComponentButton(addon, y->YesButton, "はい");
    }

    /// <summary>
    /// ボタンを押す（ECommons の ClickAddonButton と同じ：ボタンのノードに登録済みの先頭イベントを流す）。
    /// ボタンが無い・押せない状態なら false（押せないボタンを強制的に押すことはしない）。
    /// </summary>
    public static bool ClickButton(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null)
            return false;
        return ClickComponentButton(addon, addon->GetComponentButtonById(nodeId), $"ノード{nodeId}");
    }

    private static bool ClickComponentButton(AtkUnitBase* addon, AtkComponentButton* button, string label)
    {
        if (button == null || !button->IsEnabled || button->AtkComponentBase.OwnerNode == null
            || !button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
        {
            Core.DebugLog.Current?.Line("操作", $"ボタンを押せません: {addon->NameString} {label}（{(button == null ? "無い" : "押せない状態か非表示")}）");
            return false;
        }

        var evt = button->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event;
        if (evt == null)
        {
            Core.DebugLog.Current?.Line("操作", $"ボタンを押せません: {addon->NameString} {label}（イベントが無い）");
            return false;
        }

        Core.DebugLog.Current?.Line("操作", $"ボタン押下: {addon->NameString} {label}");
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    /// <summary>AtkValues の整数（Int・UInt のどちらでも）。読めなければ null。</summary>
    public static long? AtkInt(AtkUnitBase* addon, int index)
    {
        if (addon == null || index < 0 || index >= addon->AtkValuesCount)
            return null;
        var v = addon->AtkValues[index];
        return v.Type switch
        {
            AtkValueType.Int => v.Int,
            AtkValueType.UInt => v.UInt,
            _ => null,
        };
    }

    /// <summary>選択肢メニュー（SelectString / SelectIconString）の項目。開いていなければ null。</summary>
    public static List<string>? MenuEntries(out AtkUnitBase* addon)
    {
        addon = null;
        if (IsReady("SelectString", out var s))
        {
            addon = s;
            return ReadPopup(&((AddonSelectString*)s)->PopupMenu.PopupMenu);
        }

        if (IsReady("SelectIconString", out var i))
        {
            addon = i;
            return ReadPopup(&((AddonSelectIconString*)i)->PopupMenu.PopupMenu);
        }

        return null;
    }

    private static List<string> ReadPopup(PopupMenu* menu)
    {
        var list = new List<string>();
        for (var i = 0; i < menu->EntryCount; i++)
        {
            var p = menu->EntryNames[i].Value;
            list.Add(p == null ? string.Empty : Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)p).TextValue);
        }

        return list;
    }

    /// <summary>空白（半角・全角）を除いて比べるための正規化。</summary>
    public static string Normalize(string s) => s.Replace(" ", string.Empty).Replace("　", string.Empty).Trim();

    /// <summary>SelectYesno の本文。開いていなければ null。</summary>
    public static string? YesnoText(out AtkUnitBase* addon)
    {
        addon = null;
        if (!IsReady("SelectYesno", out var a))
            return null;
        addon = a;
        var y = (AddonSelectYesno*)a;
        return y->PromptText == null ? string.Empty : y->PromptText->NodeText.ToString();
    }

    /// <summary>一般アクション（GeneralAction）を使う。</summary>
    public static bool UseGeneralAction(uint id)
    {
        var ok = ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);
        Core.DebugLog.Current?.Line("操作", $"一般アクション {id} を使用 → {(ok ? "受け付け" : "拒否")}");
        return ok;
    }
}
