using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutoJobQuest.Core;
using AutoJobQuest.Data;

namespace AutoJobQuest.Planning;

/// <summary>点検1項目の重さ。</summary>
public enum Severity
{
    Ok,

    /// <summary>動作保証外（続けることはできる）。</summary>
    Warn,

    /// <summary>このままでは動かない（開始しない）。</summary>
    Error,
}

public sealed record PreflightItem(Severity Severity, string Text);

/// <summary>
/// 開始前の点検。フレームワークのスレッドから呼ぶ。
///
///  ・クラフター・ギャザラーのどれかが Lv60 未満なら「動作を保証しない」旨を出す。
///  ・製作装備はショップで買える Lv60 装備（ノーマル品）以上が前提。下回るジョブがあれば同じく警告する。
///  ・AutoRetainer のマルチモードは使わない（点検しない）。
/// </summary>
public static class Preflight
{
    /// <summary>画面に常に出す前提の文言。</summary>
    public const string Premise =
        "前提：クラフター8職・ギャザラー3職がすべて Lv60 以上であること。"
        + "製作装備は、ショップで購入できる Lv60 装備（ノーマル品）以上を着けていること"
        + "（主道具・副道具・頭・胴・手・脚・足）。満たしていない場合、動作は保証しません。";

    /// <summary>必須プラグイン（InternalName, 表示名, 用途）。</summary>
    public static readonly (string Internal, string Display, string Why)[] RequiredPlugins =
    [
        ("Artisan", "Artisan", "製作"),
        ("GatherBuddyReborn", "GatherBuddyReborn", "採集・釣り・NPC購入"),
        ("vnavmesh", "vnavmesh", "移動"),
        ("AutoHook", "AutoHook", "釣り"),
        ("Lifestream", "Lifestream", "テレポ・宿屋"),
        ("Questionable", "Questionable", "クエストの受注・報告"),
        ("RotationSolver", "RotationSolverReborn", "戦闘"),
        ("TextAdvance", "TextAdvance", "会話送り・納品"),
    ];

    public static List<PreflightItem> Run(TaskContext ctx, JobQuestPlan? plan)
    {
        var list = new List<PreflightItem>();

        // 1) プラグイン
        var installed = Svc.PluginInterface.InstalledPlugins.ToList();
        foreach (var (internalName, display, why) in RequiredPlugins)
        {
            var p = installed.FirstOrDefault(x => x.InternalName == internalName);
            if (p == null || !p.IsLoaded)
                list.Add(new PreflightItem(Severity.Error, $"{display} が読み込まれていません（{why}に使います）"));
        }

        // GBR の NPC 購入は Allagan Tools か Allagan Item Search が要る
        if (!installed.Any(x => x.IsLoaded && x.InternalName is "InventoryTools" or "AllaganItemSearch"))
            list.Add(new PreflightItem(Severity.Warn, "Allagan Tools（または Allagan Item Search）が無いため、GBR の NPC 購入が使えません。NPC で買える素材は別の手段で集めます"));

        // 2) ジョブの並び（定数の前提）
        if (Jobs.VerifyLayout() is { } layoutProblem)
            list.Add(new PreflightItem(Severity.Error, layoutProblem));

        // 3) レベル
        var low = Jobs.Crafters.Concat(Jobs.Gatherers)
            .Select(j => (Job: j, Level: Jobs.Level(j)))
            .Where(x => x.Level < 60)
            .ToList();
        if (low.Count > 0)
        {
            var names = string.Join("・", low.Select(x => $"{Jobs.Name(x.Job)} Lv{x.Level}"));
            list.Add(new PreflightItem(Severity.Warn, $"Lv60 未満のジョブがあります（{names}）。動作は保証しません"));
        }

        // 4) 装備（基準値はゲームデータから計算）・ギアセット
        if (ctx.Data.GearBaselines is { } baselines)
        {
            foreach (var job in Jobs.Crafters)
            {
                var gear = GearCheck.ReadGearset(job);
                if (gear.GearsetIndex < 0)
                {
                    list.Add(new PreflightItem(Severity.Error, $"{Jobs.Name(job)} のギアセットがありません（Artisan が着替えで止まります）"));
                    continue;
                }

                var (bCr, bCo) = baselines.GetValueOrDefault(job);
                if (gear.Craftsmanship < bCr || gear.Control < bCo)
                {
                    list.Add(new PreflightItem(Severity.Warn,
                        $"{Jobs.Name(job)} の装備が基準（ショップの Lv60 ノーマル品）を下回っています："
                        + $"作業精度 {gear.Craftsmanship}/{bCr}、加工精度 {gear.Control}/{bCo}。動作は保証しません"));
                }
            }
        }

        foreach (var job in Jobs.Gatherers)
        {
            if (GearCheck.FindGearset(job) < 0)
                list.Add(new PreflightItem(Severity.Warn, $"{Jobs.Name(job)} のギアセットがありません（GBR が採集・釣りで止まります）"));
        }

        var combat = CombatJobPicker.Pick();
        if (combat == null)
            list.Add(new PreflightItem(Severity.Warn, "ギアセットのある戦闘ジョブが見つかりません（戦闘で集める素材が集められません）"));
        else
            list.Add(new PreflightItem(Severity.Ok, $"戦闘に使うジョブ：{Jobs.Name(combat.Value.ClassJob)} Lv{combat.Value.Level}（ギアセット {combat.Value.Gearset + 1}）"));

        // 5) GBR の設定（読むだけ）
        if (ctx.Gbr.ReadAutoGatherBool("UseNavigation") == false)
            list.Add(new PreflightItem(Severity.Error, "GBR の「Use vnavmesh Navigation」が OFF です。採集で移動できません"));
        if (ctx.Gbr.ReadAutoGatherBool("DoGathering") == false)
            list.Add(new PreflightItem(Severity.Error, "GBR の「Enable Gathering Window Interaction」が OFF です。採集できません"));
        if (ctx.Gbr.ReadAutoTurnInCollectables() == true)
            list.Add(new PreflightItem(Severity.Warn, "GBR の収集品の自動納品が ON です。採った収集品を途中で納品しに行くことがあります"));

        var needsFish = plan?.Shortfalls.Any(x => x.Route == Route.Fish) ?? false;
        if (needsFish)
        {
            if (ctx.Gbr.ReadAutoGatherBool("FishDataCollection") != true)
                list.Add(new PreflightItem(Severity.Warn,
                    "GBR の「Opt-in to fishing data collection」が OFF のため、GBR は釣りをしません。"
                    + "これは釣果を GBR の外部サーバーへ送ることへの同意です（ON にするかは利用者の判断です。こちらからは変えません）。"
                    + "OFF のままなら、釣りの素材は集めずに止まります"));
            if (ctx.Gbr.ReadAutoGatherBool("UseAutoHook") == false)
                list.Add(new PreflightItem(Severity.Warn, "GBR の UseAutoHook が OFF のため、釣りが始まりません"));
        }

        // 6) Artisan の簡易製作（設定ファイルを読むだけ）
        var quick = ReadArtisanBool("QuickSynthMode");
        if (quick == true && (plan?.Craft.Crafts.Any(x => x.WantHq) ?? false))
            list.Add(new PreflightItem(Severity.Warn, "Artisan の「Use Quick Synthesis where possible」が ON です。HQ 指定の納品物が NQ になります"));

        // 7) 秘伝書
        if (plan != null && plan.Craft.LockedBySecretBook.Count > 0)
            list.Add(new PreflightItem(Severity.Warn, "秘伝書が未読のレシピがあります。紫貨を稼いで秘伝書を交換・使用してから製作します"));

        if (list.All(x => x.Severity == Severity.Ok))
            list.Add(new PreflightItem(Severity.Ok, "問題は見つかりませんでした"));

        return list;
    }

    /// <summary>Artisan の設定ファイル（pluginConfigs\Artisan.json）の真偽値を読む。読めなければ null。</summary>
    public static bool? ReadArtisanBool(string name)
    {
        try
        {
            var dir = Svc.PluginInterface.ConfigDirectory.Parent;
            if (dir == null)
                return null;
            var path = Path.Combine(dir.FullName, "Artisan.json");
            if (!File.Exists(path))
                return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>戦闘に使うジョブを選ぶ（戦闘ジョブのうち最もレベルが高いもの）。</summary>
public static class CombatJobPicker
{
    public static (uint ClassJob, int Level, int Gearset)? Pick()
    {
        // ギアセットのあるもの。同じレベルならジョブ（JobIndex あり）をクラスより優先する
        var candidates = Jobs.CombatJobs()
            .Select(cj => (Row: cj, Level: Jobs.Level(cj.RowId), Gearset: GearCheck.FindGearset(cj.RowId)))
            .Where(x => x.Gearset >= 0 && x.Level > 0)
            .OrderByDescending(x => x.Level)
            .ThenByDescending(x => x.Row.JobIndex > 0)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var best = candidates[0];
        return (best.Row.RowId, best.Level, best.Gearset);
    }
}
