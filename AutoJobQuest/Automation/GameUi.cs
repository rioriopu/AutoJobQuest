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
            return;

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

    /// <summary>オブジェクトに話しかける（ターゲットしてインタラクト）。</summary>
    public static bool Interact(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;

        var go = (GameObject*)obj.Address;
        Svc.Targets.Target = obj;
        return ts->InteractWithObject(go, true) != 0;
    }

    /// <summary>
    /// ボタンを押す（ECommons の ClickAddonButton と同じ：ボタンのノードに登録済みの先頭イベントを流す）。
    /// ボタンが無い・押せない状態なら false（押せないボタンを強制的に押すことはしない）。
    /// </summary>
    public static bool ClickButton(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null)
            return false;
        var button = addon->GetComponentButtonById(nodeId);
        if (button == null || !button->IsEnabled || button->AtkComponentBase.OwnerNode == null)
            return false;
        var res = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = res.AtkEventManager.Event;
        if (evt == null)
            return false;
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
        => ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);
}
