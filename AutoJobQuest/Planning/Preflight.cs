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
        // 戦闘・釣りの素材が今の計画に無ければ、RSR・AutoHook が無くても止めない（使わない物で止めない）。
        // ほかの手段が失敗して戦闘・釣りに回ったときは、その作業の開始時に理由を出して止まる
        bool Uses(Route r) => plan == null || plan.Shortfalls.Any(x => x.Route == r || x.Fallbacks.Contains(r));
        foreach (var (internalName, display, why) in RequiredPlugins)
        {
            var p = installed.FirstOrDefault(x => x.InternalName == internalName);
            if (p != null && p.IsLoaded)
                continue;

            var optional = (internalName == "RotationSolver" && !Uses(Route.Combat)) || (internalName == "AutoHook" && !Uses(Route.Fish));
            list.Add(optional
                ? new PreflightItem(Severity.Warn, $"{display} が読み込まれていません（{why}に使います。今の計画では使いませんが、ほかの手段で集めきれず{why}に回ったときに止まります）")
                : new PreflightItem(Severity.Error, $"{display} が読み込まれていません（{why}に使います）"));
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
            // ギアセットが要るのは、今の計画で製作に使うクラフターだけ（選んだジョブ＋中間素材を作るジョブ＋紫貨の収集品を作るジョブ）。
            // 使わないクラフターのギアセットが無いだけで止めない
            var used = new HashSet<uint>(Jobs.Crafters);
            if (plan != null)
            {
                used = plan.RemainingQuests.Select(q => q.ClassJobId).Concat(plan.Craft.Crafts.Select(c => c.ClassJobId)).ToHashSet();
                if (plan.Craft.LockedBySecretBook.Count > 0 && ctx.Data.Planner?.Pick(ctx.Config.ScripCollectableItemId) is { } collectRecipe)
                    used.Add(Jobs.CraftTypeToClassJob(collectRecipe.CraftType.RowId));
            }

            foreach (var job in Jobs.Crafters.Where(used.Contains))
            {
                var gear = GearCheck.ReadGearset(job);
                if (gear.GearsetIndex < 0)
                {
                    list.Add(new PreflightItem(Severity.Error, $"{Jobs.Name(job)} のギアセットがありません（Artisan が着替えで止まります）"));
                    continue;
                }

                var (bCr, bCo) = baselines.GetValueOrDefault(job);
                if (bCr == 0 || bCo == 0)
                {
                    // 基準をゲームデータから計算できなかった（ショップの品が見つからない等）。比べても意味が無いので、そう記録する
                    list.Add(new PreflightItem(Severity.Warn, $"{Jobs.Name(job)} の装備の基準をゲームデータから計算できませんでした（装備の確認を飛ばします）"));
                    continue;
                }

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
            // 釣りは GBR に一任する。GBR が釣れない設定なら、別の手段に黙って切り替えず始める前に止める
            var fishItems = string.Join("、", plan!.Shortfalls.Where(x => x.Route == Route.Fish).Select(x => $"{x.Name}×{x.Shortfall}"));
            if (ctx.Gbr.ReadAutoGatherBool("FishDataCollection") != true)
                list.Add(new PreflightItem(Severity.Error,
                    $"釣りで集める素材があります（{fishItems}）が、GBR の「Opt-in to fishing data collection」が OFF のため GBR は釣りをしません。"
                    + "これは釣果を GBR の外部サーバーへ送ることへの同意なので、こちらからは変えません。"
                    + "GBR の設定画面の検索欄に「fishing data」と入れると項目が出ます。ON にしてからもう一度始めてください"));
            if (ctx.Gbr.ReadAutoGatherBool("UseAutoHook") == false)
                list.Add(new PreflightItem(Severity.Error, $"釣りで集める素材があります（{fishItems}）が、GBR の UseAutoHook が OFF のため釣りが始まりません"));
        }

        // 5.5) 任意のマテリア（既定は剛柔のマテリア）が、付ける納品物に付けられるか
        if (plan != null)
        {
            foreach (var m in plan.Materia.Where(m => !m.AlreadyMelded && m.MateriaItemId == null))
            {
                if (MateriaCatalog.ResolveAny(ctx.Config.AnyMateriaItemId, m.TargetItemId, out var problem) == null)
                    list.Add(new PreflightItem(Severity.Error, $"{m.Quest}：{problem}"));
            }

            // マテリアを付ける品がアーマリーチェストにだけある（装着はカバンの品しか探さない。持っているので作り直しもしない）。
            // 装着の段まで進んでから止まらないよう、ここで知らせる
            foreach (var m in plan.Materia.Where(m => !m.AlreadyMelded))
            {
                var owned = m.TargetHq ? Inventory.CountNow(m.TargetItemId, hqOnly: true) : Inventory.CountNow(m.TargetItemId);
                if (owned > 0 && !Automation.MeldTask.InBags(m.TargetItemId, m.TargetHq))
                    list.Add(new PreflightItem(Severity.Error,
                        $"{m.Quest}：マテリアを付ける {CraftPlanner.ItemName(m.TargetItemId)}{(m.TargetHq ? "（HQ）" : string.Empty)} がアーマリーチェストにあります。カバンに移してから始めてください"));
            }

            // 前提のクエストが自動で進められない（メインクエスト等が未完了）ジョブクエ。
            // 止めずに「どのクエストが未達なので動作保証しない」と注意を出す（確認窓で続けるか決める）。
            // 続けた場合、そのクエストは計画に入れない（素材も集めない）。進められる分だけ進める
            foreach (var line in plan.BlockedSummary())
                list.Add(new PreflightItem(Severity.Warn,
                    $"前提のクエストが未完了のため、次のジョブクエは進められません（動作保証外。続けた場合、これらは飛ばし、素材も集めません）：{line}"));
        }

        // RSR がこちらを使う前から動いている（利用者が使っている）とき。
        // 戦闘では Henched に切り替え、終わったら使う前のモードに戻す（モードは RSR の内部から読む：RsrStateReader）。
        // モードを読めない場合だけ、終わったら Off になるので確認窓で本人に決めてもらう
        if (plan != null && (plan.Shortfalls.Any(x => x.Route == Route.Combat || x.Fallbacks.Contains(Route.Combat))) && ctx.Rotation.IsActive() == true)
        {
            var mode = ctx.Rotation.CurrentModeName();
            list.Add(mode != null
                ? new PreflightItem(Severity.Ok, $"RotationSolverReborn は今 {mode} で動いています。戦闘の間だけ Henched に切り替え、終わったら {mode} に戻します")
                : new PreflightItem(Severity.Warn, $"RotationSolverReborn が動いていますが、今のモードを読めません（{Ipc.RsrStateReader.LastError}）。戦闘の後は Off になります"));
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
