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
/// 検証の仕組み用：ゲームの画面の偽物（受注・納品の作業と呼び鈴の引き出しを、偽物のゲームで通しで動かすため）。
/// 画面は番号（アドレスの代わり。中身は読まない）で表す。本番では使わない。
/// </summary>
public interface IGameUiTestBackend
{
    /// <summary>その名前の画面が出ていれば番号、出ていなければ 0。</summary>
    nint Addon(string name);

    /// <summary>その名前の画面が操作できる状態か。</summary>
    bool IsReady(string name);

    void Fire(nint addon, bool updateState, object[] values);

    /// <summary>選択肢（SelectString・SelectIconString）の項目と、その画面の番号。出ていなければ null。</summary>
    List<string>? MenuEntries(out nint addon);

    bool AdvanceTalk();

    bool PlayerFree();

    bool Interact(Dalamud.Game.ClientState.Objects.Types.IGameObject obj);

    void Close(nint addon);
}

/// <summary>
/// ゲーム画面（アドオン）とキャラクターの状態を見る小物。
/// </summary>
public static unsafe class GameUi
{
    /// <summary>
    /// 検証の仕組み用：設定すると、ゲームの画面の代わりにこれを使う。本番では null のまま（今までと同じ処理を通る）。
    /// </summary>
    public static IGameUiTestBackend? TestBackend { get; set; }

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
        if (TestBackend is { } test)
            return (AtkUnitBase*)test.Addon(name);
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
        if (TestBackend is { } test)
            return addon != null && test.IsReady(name);
        return addon != null && addon->IsReady && addon->IsFullyLoaded();
    }

    /// <summary>画面を閉じる（Close(true)）。</summary>
    public static void Close(AtkUnitBase* addon)
    {
        if (TestBackend is { } test)
        {
            test.Close((nint)addon);
            return;
        }

        if (addon != null)
            addon->Close(true);
    }

    public static bool IsVisible(string name) => Addon(name) != null;

    /// <summary>ショップ・マーケットボード・会話の選択肢など、介入してはいけない画面が開いているか。</summary>
    public static bool IsShopOrMarketOpen() => OpenBlockingAddon() != null;

    /// <summary>開いている「介入してはいけない画面」の名前（最初の1つ。無ければ null）。</summary>
    public static string? OpenBlockingAddon()
    {
        foreach (var n in ShopAddons)
            if (IsVisible(n))
                return n;
        return null;
    }

    /// <summary>画面の名前を、利用者に分かる言葉にする（案内用）。</summary>
    public static string WindowLabel(string addon) => addon switch
    {
        "SelectYesno" => "確認（はい／いいえ）",
        "SelectString" or "SelectIconString" => "選択肢",
        "InputNumeric" => "数の入力",
        "Request" => "クエストの納品窓",
        "ItemSearch" or "ItemSearchResult" or "ItemHistory" => "マーケットボード",
        "RetainerSell" or "RetainerSellList" => "リテイナーの出品",
        "Shop" or "FreeShop" => "ショップ",
        "InclusionShop" or "ShopExchangeItem" or "ShopExchangeItemDialog" or "ShopExchangeCurrency" or "ShopExchangeCurrencyDialog" => "アイテム交換",
        "GrandCompanyExchange" => "軍票の交換",
        "CollectablesShop" => "収集品の納品",
        _ => addon,
    } + $"（{addon}）";

    /// <summary>
    /// アドオンのコールバックを撃つ（ECommons の Callback.Fire と同じ中身：AtkValue を並べて FireCallback）。
    /// 引数は int / uint / bool / string のどれか。
    /// </summary>
    public static void Fire(AtkUnitBase* addon, bool updateState, params object[] values)
    {
        if (TestBackend is { } test)
        {
            test.Fire((nint)addon, updateState, values);
            return;
        }

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
        if (TestBackend is { } test)
            return test.PlayerFree();
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
    /// 既定は、Questionable・TextAdvance と同じく視線判定なし（checkLineOfSight: false）。
    /// 視線判定ありは、経路の途中で早めに話しかけてみるとき（壁越しに話しかけない）だけに使う。
    /// 受け付けたかは、Questionable と同じく「戻り値が 0 より大きく、7 でない」で見る（Questionable の GameFunctions は
    /// 7 を失敗として扱う。以前は 0 以外を成功とみなした）。戻り値は記録に出す（実機の確認のため）。
    /// 呼び出し側は1秒程度の間隔を置いて繰り返し呼ぶこと。
    /// </summary>
    public static bool Interact(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, bool checkLineOfSight = false)
    {
        if (TestBackend is { } test)
            return test.Interact(obj);
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
        var result = (long)ts->InteractWithObject(go, checkLineOfSight);
        var ok = result > 0 && result != 7;
        Core.DebugLog.Current?.Line("操作", $"話しかけ: {obj.Name.TextValue}（BaseId {obj.BaseId}、距離 {System.Numerics.Vector3.Distance(obj.Position, Me.Position):0.0}m、"
                                          + $"視線判定{(checkLineOfSight ? "あり" : "なし")}）→ {(ok ? "受け付け" : "受け付けられず")}（戻り値 {result}）");
        return ok;
    }

    /// <summary>
    /// 会話ウィンドウ（Talk）を1つ進める（ECommons AddonMaster.Talk.Click と同じ：MouseDown→MouseClick→MouseUp）。
    /// </summary>
    public static bool AdvanceTalk()
    {
        if (TestBackend is { } test)
            return test.AdvanceTalk();
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
        if (TestBackend is { } test)
        {
            var entries = test.MenuEntries(out var handle);
            addon = (AtkUnitBase*)handle;
            return entries;
        }

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

    /// <summary>
    /// 画面に出ている文字を全部集める（AtkValues の文字列と、文字のノード。部品の中も4段までたどる）。
    /// 置き場所（番号）を決め打ちせずに「この画面にこの名前が出ているか」を確かめるのに使う。
    /// 読めない値は飛ばす（ここは裏付け用で、読めなくても例外は出さない）。
    /// </summary>
    public static List<string> AllTexts(AtkUnitBase* addon)
    {
        var result = new List<string>();
        if (addon == null)
            return result;

        try
        {
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                var v = addon->AtkValues[i];
                if ((v.Type & AtkValueType.TypeMask) is not (AtkValueType.String or AtkValueType.ConstString) || v.String.Value == null)
                    continue;
                var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
                if (!string.IsNullOrEmpty(text))
                    result.Add(text);
            }
        }
        catch
        {
            // 読めない値は飛ばす
        }

        try
        {
            CollectNodeTexts(addon->UldManager.NodeList, addon->UldManager.NodeListCount, result, 0);
        }
        catch
        {
            // 同上
        }

        return result;
    }

    private static void CollectNodeTexts(AtkResNode** nodes, int count, List<string> result, int depth)
    {
        if (nodes == null || depth > 4)
            return;

        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            if (node == null)
                continue;

            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = Dalamud.Game.Text.SeStringHandling.SeString.Parse(textNode->NodeText.AsSpan().ToArray()).TextValue;
                if (!string.IsNullOrEmpty(text))
                    result.Add(text);
                continue;
            }

            // 部品（コンポーネント）のノードの中にも文字がある
            if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    CollectNodeTexts(component->UldManager.NodeList, component->UldManager.NodeListCount, result, depth + 1);
            }
        }
    }

    /// <summary>
    /// SelectYesno の本文（表示される文字だけ）。開いていなければ null。
    /// Utf8String.ToString は SeString の制御（色・品名の差し込み等）の生のバイトまで文字にしてしまい、
    /// 品名との照合が外れるので、SeString として解析して TextValue を使う。
    /// </summary>
    public static string? YesnoText(out AtkUnitBase* addon)
    {
        addon = null;
        if (!IsReady("SelectYesno", out var a))
            return null;
        addon = a;
        var y = (AddonSelectYesno*)a;
        if (y->PromptText == null)
            return string.Empty;
        try
        {
            return Dalamud.Game.Text.SeStringHandling.SeString.Parse(y->PromptText->NodeText.AsSpan().ToArray()).TextValue;
        }
        catch
        {
            return y->PromptText->NodeText.ToString();
        }
    }

    /// <summary>その一般アクションが今使えるか（ActionManager.GetActionStatus。0＝使える、それ以外＝使えない理由の番号）。</summary>
    public static uint GeneralActionStatus(uint id) => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, id);

    /// <summary>その行動（Action シート）が今使えるか（ActionManager.GetActionStatus。0＝使える、それ以外＝使えない理由の LogMessage の行番号）。</summary>
    public static uint ActionStatus(uint id) => ActionManager.Instance()->GetActionStatus(ActionType.Action, id);

    /// <summary>行動の後の硬直中か（ActionManager.AnimationLock が 0 より大きい。ECommons の Player.IsAnimationLocked と同じ）。</summary>
    public static bool AnimationLocked => ActionManager.Instance()->AnimationLock > 0;

    /// <summary>一般アクション（GeneralAction）を使う。</summary>

    public static bool UseGeneralAction(uint id)
    {
        var ok = ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);
        Core.DebugLog.Current?.Line("操作", $"一般アクション {id} を使用 → {(ok ? "受け付け" : "拒否")}");
        return ok;
    }
}
