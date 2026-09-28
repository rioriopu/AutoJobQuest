using System;
using System.Collections.Generic;
using System.Text;
using AutoJobQuest.Core;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// ショップ・マーケットボード・会話の選択肢など、自動操作に関わる画面の開閉と中身を記録する
/// （不具合のとき細かく説明しなくても分かるように）。
///
///  ・開いたとき：画面の名前と AtkValues（自動操作で読む値の置き場）を全部書く。確認文・選択肢は本文も書く。
///  ・中身が変わったとき：同じ画面は3秒に1回まで書く。
///  ・閉じたとき：名前だけ書く。
/// こちらが送った操作（コールバック・ボタン）は GameUi 側で記録する。
/// 記録するのは実行中だけ（設定「画面を常に記録」を ON にすると実行していなくても書く）。
/// </summary>
public sealed unsafe class AddonRecorder : IDisposable
{
    /// <summary>記録する画面。</summary>
    public static readonly string[] Watched =
    [
        "Shop", "ShopExchangeItem", "ShopExchangeItemDialog", "ShopExchangeCurrency", "ShopExchangeCurrencyDialog",
        "InclusionShop", "FreeShop", "GrandCompanyExchange", "CollectablesShop",
        "ItemSearch", "ItemSearchResult", "ItemHistory",
        "SelectYesno", "SelectString", "SelectIconString", "InputNumeric", "Request", "ContextIconMenu",
        "Talk", "JournalAccept", "JournalResult",
        "MateriaAttach", "MateriaAttachDialog",
        "RecipeNote", "Synthesis", "SynthesisSimple",
        "Gathering", "GatheringMasterpiece", "PurifyItemSelector", "PurifyResult",
    ];

    private readonly Func<bool> shouldRecord;
    private readonly Dictionary<string, DateTime> lastRefresh = [];

    public AddonRecorder(Func<bool> shouldRecord)
    {
        this.shouldRecord = shouldRecord;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Watched, this.OnSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, Watched, this.OnRefresh);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Watched, this.OnFinalize);
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, Watched, this.OnSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, Watched, this.OnRefresh);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, Watched, this.OnFinalize);
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        if (!this.shouldRecord())
            return;
        try
        {
            DebugLog.Current?.Block("画面", $"開いた: {args.AddonName}", Describe((AtkUnitBase*)args.Addon.Address));
        }
        catch (Exception ex)
        {
            DebugLog.Current?.Line("画面", $"{args.AddonName} を記録できませんでした: {ex.Message}");
        }
    }

    private void OnRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.shouldRecord())
            return;

        var now = DateTime.UtcNow;
        if (this.lastRefresh.TryGetValue(args.AddonName, out var last) && now - last < TimeSpan.FromSeconds(3))
            return;
        this.lastRefresh[args.AddonName] = now;

        try
        {
            DebugLog.Current?.Block("画面", $"更新: {args.AddonName}", Describe((AtkUnitBase*)args.Addon.Address));
        }
        catch (Exception ex)
        {
            DebugLog.Current?.Line("画面", $"{args.AddonName} を記録できませんでした: {ex.Message}");
        }
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        if (!this.shouldRecord())
            return;
        DebugLog.Current?.Line("画面", $"閉じた: {args.AddonName}");
    }

    /// <summary>画面の中身を文字にする（本文・選択肢・AtkValues）。</summary>
    public static string Describe(AtkUnitBase* addon)
    {
        if (addon == null)
            return "（画面が無い）";

        var sb = new StringBuilder();
        var name = addon->NameString;
        sb.AppendLine($"名前={name} 表示={addon->IsVisible} 準備完了={addon->IsReady} 値の数={addon->AtkValuesCount}");

        if (name == "SelectYesno")
        {
            var y = (AddonSelectYesno*)addon;
            if (y->PromptText != null)
                sb.AppendLine($"本文: {y->PromptText->NodeText}");
        }

        if (name is "SelectString" or "SelectIconString")
        {
            var entries = GameUi.MenuEntries(out _);
            if (entries != null)
                sb.AppendLine("選択肢: " + string.Join(" / ", entries));
        }

        sb.Append(DumpValues(addon, 800));
        return sb.ToString();
    }

    /// <summary>AtkValues を「[番号] 型=値」で並べる（0 や空の値は省く）。</summary>
    public static string DumpValues(AtkUnitBase* addon, int max)
    {
        var sb = new StringBuilder();
        var count = Math.Min((int)addon->AtkValuesCount, max);
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            var v = addon->AtkValues[i];
            string? text = (v.Type & AtkValueType.TypeMask) switch
            {
                AtkValueType.Int => v.Int == 0 ? null : $"I={v.Int}",
                AtkValueType.UInt => v.UInt == 0 ? null : $"U={v.UInt}",
                AtkValueType.Bool => v.Byte == 0 ? null : "B=true",
                AtkValueType.Float => v.Float == 0 ? null : $"F={v.Float}",
                AtkValueType.String or AtkValueType.ConstString => v.String.Value == null ? null : StringValue(v),
                _ => null,
            };
            if (text == null)
                continue;

            sb.Append($"[{i}]{text}  ");
            if (++shown % 8 == 0)
                sb.AppendLine();
        }

        if (addon->AtkValuesCount > max)
            sb.AppendLine().Append($"（{max} 番以降は省略。全部で {addon->AtkValuesCount} 個）");
        return sb.ToString();
    }

    private static string? StringValue(AtkValue v)
    {
        var s = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
        if (string.IsNullOrEmpty(s))
            return null;
        if (s.Length > 80)
            s = s[..80] + "…";
        return $"S=「{s.Replace("\n", " ")}」";
    }
}
