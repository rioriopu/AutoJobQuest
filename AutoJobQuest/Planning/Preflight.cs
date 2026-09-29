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

        // 1.2) 結果の分からないマーケットの購入が残っている。自動では買い直さず、利用者に確かめてもらう。
        // 「はい」で続けると控えを消す（JobQuestFlow の事前点検の答え）。買えていなければ、次の購入で不足分を買う
        if (ctx.Config.PendingPurchase is { } pending)
            list.Add(new PreflightItem(Severity.Warn,
                $"前回のマーケット購入の結果が確かめられていません：{pending.Describe()}。"
                + "マーケットボードの「取引履歴」（またはギルと所持品）で、買えたかどうかを確かめてください。"
                + "確かめたうえで続けるなら「はい」（控えを消します。買えていなければ、足りない分をあらためて買います）"));

        // 1.5) 画面の開閉の知らせ（AddonLifecycle）を受け取れないと、自分が開いた選択肢・確認窓を見分けられず、会話や交換が進まない
        if (!ctx.Ownership.Registered)
            list.Add(new PreflightItem(Severity.Error, "画面の開閉の知らせを受け取る仕組みを登録できませんでした（自分が開いた画面を見分けられません）。プラグインを読み込み直してください"));

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

            // Questionable の手順に、在庫に関係なく Artisan の既製リストが動く「Craft」手順（作る品の指定が無い）があるクエスト
            // （木工 Lv1〜25 の6本・調理 Lv53〜60 の4本）。こちらからは止められないので、
            // 名前を出して、続けるか利用者に決めてもらう（追加製作を防げるとは言わない）
            var premadeAlways = plan.RemainingQuests
                .Where(q => QuestionablePaths.CraftSteps(q.ShortId) is { } steps && steps.Any(s => s.ItemId == null))
                .Select(q => $"{Jobs.Name(q.ClassJobId)} {q}")
                .ToList();
            if (premadeAlways.Count > 0)
                list.Add(new PreflightItem(Severity.Warn,
                    $"次のクエストは、Questionable の手順で Artisan の既製リストが必ず動きます（在庫に関係なく、手持ちの材料で追加製作します。"
                    + $"材料が無ければ Questionable が NPC から買い足します。こちらからは止められません）：{string.Join("、", premadeAlways)}"));

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
                ? new PreflightItem(Severity.Ok, $"RotationSolverReborn は今 {mode} で動いています。戦闘の間だけ Henched に切り替え、終わったら {mode} に戻します（途中で RSR が Off や別のモードになっていたら、そのままにします）")
                : new PreflightItem(Severity.Warn, $"RotationSolverReborn が動いていますが、今のモードを読めません（{Ipc.RsrStateReader.LastError}）。戦闘の後は Off になります"));
        }

        // 5.8) RSR の設定（戦闘で集める素材があるときだけ。RSR の内部を読むだけ）
        if (plan != null && Uses(Route.Combat) && installed.Any(x => x.InternalName == Ipc.RsrStateReader.InternalName && x.IsLoaded))
            list.AddRange(RsrSettings(ctx));

        // 5.85) vnavmesh の「詰まったら止める」「止めた後に探し直す」が両方 ON（設定ファイルを読むだけ）。
        // 詰まったときに vnavmesh が自分で経路を探し直して歩き出す（vnavmesh の FollowPath.OnStuck → AsyncMoveRequest）。
        // こちらが移動を止めた後でも、その探索が終わると遅れて歩き出すので、話しかけ・採集の位置合わせがずれる
        if (ReadPluginConfigBool("vnavmesh.json", "Payload", "StopOnStuck") == true && ReadPluginConfigBool("vnavmesh.json", "Payload", "RetryOnStuck") == true)
            list.Add(new PreflightItem(Severity.Warn,
                "vnavmesh の「Stop pathing when stuck」と「Retry pathing after stop」が両方 ON です。詰まったときに vnavmesh が自分で経路を探し直すので、"
                + "こちらが移動を止めた後に遅れて歩き出すことがあります（NPC への話しかけ・採集の位置合わせがずれます）。どちらかを OFF にすることを勧めます"));

        // 5.9) Artisan がすでに動いている（リスト実行中・連続製作中）。こちらの製作と取り合うので始めない。
        // 止めるのは Artisan の側の操作になるので、こちらからは止めない。
        // Questionable が動かしたリストが前回から残っている場合がある
        if (ctx.Artisan.IsLoaded && (ctx.Artisan.IsListRunning() == true || ctx.Artisan.IsEndurance() == true))
            list.Add(new PreflightItem(Severity.Error, "Artisan が動いています（リスト実行中か連続製作中）。Artisan の画面で止めてから始めてください"));

        // （以前の 5.95・5.96 は外した）
        //  ・「連続製作が終わったら製作の構えを解く」（ExitCraftStanceEndurance）が OFF でも、製作の列の最後にこちらで構えを解く
        //    （ExitCraftStanceTask。Artisan の TaskExitCraft と同じやり方）ので、注意は要らない。
        //  ・「NQ ができたら／失敗したら連続製作をやめる」（EnduranceStopNQ・EnduranceStopFail）が ON でも、止まったところから
        //    同じ作業の中で残りを頼み直す（CraftResume）ので、進み方は変わらない。これらは Artisan の連続製作（CraftItem が内部で使う）
        //    にだけ効く設定で、Crafting List には効かない（Artisan の EnduranceCraftWatcher.cs）。

        // 5.97) Questionable の設定（pluginConfigs\Questionable.json を読むだけ）。
        // 止める条件に対象のクエストが入っている・完了を止める設定が ON だと、クエストの途中で Questionable が止まり、やり直しても進まない
        if (plan != null && plan.RemainingQuests.Count > 0)
            list.AddRange(QuestionableSettings(plan));

        // 5.98) ジャーナルの受注数（受注中のクエストが上限だと、新しいクエストを受けられず Questionable が受注を待ち続ける）
        unsafe
        {
            var qm = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.Instance();
            if (qm != null && plan != null && plan.RemainingQuests.Count > 0)
            {
                var accepted = qm->NumAcceptedQuests;
                var cap = qm->NormalQuests.Length;
                if (accepted >= cap)
                    list.Add(new PreflightItem(Severity.Error, $"受注中のクエストが {accepted} 本で上限です。新しいクエストを受けられないので、いらないクエストを破棄してから始めてください"));
                else if (accepted >= cap - 2)
                    list.Add(new PreflightItem(Severity.Warn, $"受注中のクエストが {accepted} 本です（上限 {cap} 本）。ジョブクエと前提のクエストを受けるうちに上限に届くと止まります"));
            }
        }

        // 6) Artisan の簡易製作（設定ファイルを読むだけ）
        var quick = ReadArtisanBool("QuickSynthMode");
        if (quick == true && (plan?.Craft.Crafts.Any(x => x.WantHq) ?? false))
            list.Add(new PreflightItem(Severity.Warn, "Artisan の「Use Quick Synthesis where possible」が ON です。HQ 指定の納品物が NQ になります"));

        // 6.5) HQ 指定の品が HQ になる見込み（Artisan 自身の計算を借りて、いまのギアセットの能力値で計算する：ArtisanHqEstimate）。
        // CP が足りないと Lv53 以上の品は HQ になりにくく、HQ にならなかった回数の上限で止まる（材料を使ってから）。始める前に知らせる
        if (plan != null && quick != true && ctx.Artisan.IsLoaded)
            list.AddRange(HqOutlook(ctx, plan));

        // 7) 秘伝書
        if (plan != null && plan.Craft.LockedBySecretBook.Count > 0)
            list.Add(new PreflightItem(Severity.Warn, "秘伝書が未読のレシピがあります。紫貨を稼いで秘伝書を交換・使用してから製作します"));

        if (list.All(x => x.Severity == Severity.Ok))
            list.Add(new PreflightItem(Severity.Ok, "問題は見つかりませんでした"));

        return list;
    }

    /// <summary>
    /// Questionable の設定のうち、ジョブクエの進行を止めるもの（読むだけ）。
    ///  ・Stop.Enabled かつ QuestsToStopAfter / QuestsToStopWhenAccepted に対象のクエスト → そのクエストで止まる（エラー）
    ///  ・Stop.Enabled かつ LevelToStopAfter → そのレベルに届くと止まる（注意）
    ///  ・Advanced.PreventQuestCompletion → 完了の会話をしない＝終わらない（エラー）
    ///  ・General.ConfigureTextAdvance が OFF → 前提のクエストで会話送りが TextAdvance 本体の設定になる（注意）
    ///  ・キャラクターごとの設定（Profiles・CharacterProfiles）があれば、その値が使われる可能性（注意）
    /// あわせて、優先リストを一時的に差し替えることを知らせる。
    /// </summary>
    private static IEnumerable<PreflightItem> QuestionableSettings(JobQuestPlan plan)
    {
        JsonDocument? doc = null;
        try
        {
            var dir = Svc.PluginInterface.ConfigDirectory.Parent;
            var path = dir == null ? null : Path.Combine(dir.FullName, "Questionable.json");
            if (path != null && File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                doc = JsonDocument.Parse(fs);
            }
        }
        catch (Exception)
        {
            doc = null;
        }

        if (doc == null)
        {
            yield return new PreflightItem(Severity.Warn, "Questionable の設定ファイルを読めませんでした（止める条件などを確かめられません）");
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            static bool Flag(JsonElement e, string section, string name)
                => e.TryGetProperty(section, out var sec) && sec.ValueKind == JsonValueKind.Object
                   && sec.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            static List<string> Ids(JsonElement e, string name)
                => e.TryGetProperty("Stop", out var sec) && sec.ValueKind == JsonValueKind.Object && sec.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [];

            if (Flag(root, "Stop", "Enabled"))
            {
                var mine = plan.RemainingQuests.Select(q => q.ShortId.ToString()).ToHashSet();
                var hit = Ids(root, "QuestsToStopAfter").Concat(Ids(root, "QuestsToStopWhenAccepted")).Where(mine.Contains).Distinct().ToList();
                if (hit.Count > 0)
                {
                    var names = plan.RemainingQuests.Where(q => hit.Contains(q.ShortId.ToString())).Select(q => q.ToString());
                    yield return new PreflightItem(Severity.Error,
                        $"Questionable の「止める条件」に、進めるジョブクエが入っています：{string.Join("、", names)}。そのクエストで Questionable が止まるので、条件から外してから始めてください");
                }

                if (Flag(root, "Stop", "LevelToStopAfter"))
                {
                    var level = root.GetProperty("Stop").TryGetProperty("TargetLevel", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetInt32() : 0;
                    yield return new PreflightItem(Severity.Warn, $"Questionable の「レベル {level} で止める」が ON です。ジョブのレベルが届くと、クエストの途中で Questionable が止まります");
                }
            }

            if (Flag(root, "Advanced", "PreventQuestCompletion"))
                yield return new PreflightItem(Severity.Error, "Questionable の「クエストを完了しない」（PreventQuestCompletion）が ON です。クエストが終わらないので、OFF にしてから始めてください");

            if (root.TryGetProperty("General", out var gen) && gen.TryGetProperty("ConfigureTextAdvance", out var ta) && ta.ValueKind == JsonValueKind.False)
                yield return new PreflightItem(Severity.Warn, "Questionable の「TextAdvance を設定する」が OFF です。前提のクエストの会話送り・受注・完了が、TextAdvance 本体の設定のまま動きます");

            var profiles = (root.TryGetProperty("Profiles", out var pf) && pf.ValueKind == JsonValueKind.Object && pf.EnumerateObject().Any())
                           || (root.TryGetProperty("CharacterProfiles", out var cp) && cp.ValueKind == JsonValueKind.Object && cp.EnumerateObject().Any());
            if (profiles)
                yield return new PreflightItem(Severity.Warn, "Questionable にキャラクターごとの設定（プロファイル）があります。ここで確かめたのは基本の設定なので、プロファイルの値が違えばそちらが使われます");
        }

        yield return new PreflightItem(Severity.Ok,
            "クエストの間、Questionable の優先リストを一時的にそのクエストだけにし、終わったら元に戻します（別のクエストへ移って止まらないように）。"
            + "優先リストの「受注のみ」の印は戻せません。優先リストの窓で「Job Quests」プリセットを開いたままにしないでください（職が変わるたびに書き換わります）");
    }

    /// <summary>
    /// HQ 指定の品の見込み。見込みが ArtisanHqEstimate.WarnBelow を下回る品を、能力値つきで注意に出す。
    /// 計算できなかったものは記録に残す（注意にはしない：始めるのを止める理由にならない）。
    /// </summary>
    private static IEnumerable<PreflightItem> HqOutlook(TaskContext ctx, JobQuestPlan plan)
    {
        var targets = plan.Craft.Crafts.Where(c => c.WantHq).Select(c => (c.RecipeId, c.ItemId, c.ClassJobId)).ToList();
        if (targets.Count == 0)
            yield break;

        var results = Ipc.ArtisanHqEstimate.Estimate(targets, out var error);
        if (error != null)
        {
            ctx.Log.Write("事前点検", $"HQ の見込みを計算できませんでした：{error}");
            yield break;
        }

        foreach (var r in results.Where(r => r.Percent == null))
            ctx.Log.Write("事前点検", $"HQ の見込みを計算できませんでした：{CraftPlanner.ItemName(r.ItemId)}（{r.Note}）");
        foreach (var r in results.Where(r => r.Percent != null))
            ctx.Log.Write("事前点検", $"HQ の見込み：{CraftPlanner.ItemName(r.ItemId)} {r.Percent:0.#}%（{r.Stats}・{r.Solver}・{r.Runs}回）");

        var low = results.Where(r => r.Percent is { } p && p < Ipc.ArtisanHqEstimate.WarnBelow).OrderBy(r => r.Percent).ToList();
        if (low.Count > 0)
            yield return new PreflightItem(Severity.Warn,
                $"HQ 指定の品のうち、いまのギアセットの能力値では HQ になりにくいものがあります（見込み {Ipc.ArtisanHqEstimate.WarnBelow:0}% 未満）："
                + string.Join("、", low.Select(r => $"{CraftPlanner.ItemName(r.ItemId)} {r.Percent:0}%（{r.Stats}）"))
                + $"。HQ にならないと作り直し、同じ品で {Math.Max(1, ctx.Config.MaxRetryRounds)} 回 HQ にならないと止まります。CP を上げる装備・マテリアを検討してください"
                + "（Artisan の計算を借り、状態と成否を乱数で振って求めた見込み。レベルはレシピの職レベルまで上がったとして計算）");
        else if (results.Any(r => r.Percent != null))
            yield return new PreflightItem(Severity.Ok, $"HQ 指定の品 {results.Count(r => r.Percent != null)} 件は、いまのギアセットの能力値で HQ の見込みが {Ipc.ArtisanHqEstimate.WarnBelow:0}% 以上です");
    }

    /// <summary>自動で OFF になる RSR の設定（記録に残すだけ。Henched が外れたときの原因の切り分け用：RSR の RSCommands_Actions）。</summary>
    private static readonly string[] RsrAutoOffSettings = ["AutoOffBetweenArea", "AutoOffCutScene", "AutoOffSwitchClass", "AutoOffWhenDead", "AutoOffAfterCombat"];

    /// <summary>
    /// 戦闘で使う RSR の設定の点検（RSR の内部 Service.Config を読むだけ。書き換えは戦闘の間だけ IPC で行う）。
    ///  ・PoslockCasting（詠唱中は移動しない）が ON：RSR が移動中でも詠唱の技を選び、詠唱の間は移動そのものを止める
    ///    （RSR の MovingUpdater → Service.CanMove）。こちらは vnavmesh で敵へ近づくので、途中で止まる・遅れる（注意）
    ///  ・TargetFreely・IgnoreNonFateInFate が ON：戦闘の間だけ OFF にして戻すことを知らせる
    ///  ・自動 OFF の設定：記録に残すだけ（外れたらこちらで送り直す）
    /// </summary>
    private static IEnumerable<PreflightItem> RsrSettings(TaskContext ctx)
    {
        if (Ipc.RsrStateReader.ReadBool("PoslockCasting") == true)
            yield return new PreflightItem(Severity.Warn,
                "RotationSolverReborn の「Lock movement when casting or performing certain actions.」（PoslockCasting）が ON です。"
                + "詠唱の間は RSR が移動を止めるので、戦闘で敵へ近づく途中で止まったり遅れたりします。OFF を勧めます");

        var changed = Ipc.RotationSolverIpc.HenchedFalseSettings.Where(n => Ipc.RsrStateReader.ReadBool(n) == true).ToList();
        if (changed.Count > 0)
            yield return new PreflightItem(Severity.Ok,
                $"戦闘の間だけ RotationSolverReborn の {string.Join("・", changed)} を OFF にし、終わったら戻します"
                + "（TargetFreely は狙いが空のとき RSR が一番近い敵を自分で狙う＝指定外の敵を殴る。IgnoreNonFateInFate は FATE の中では FATE の敵しか殴らない＝狙った敵を殴らない）");

        var autoOff = RsrAutoOffSettings.Select(n => $"{n}={(Ipc.RsrStateReader.ReadBool(n) is { } b ? (b ? "ON" : "OFF") : "読めない")}");
        ctx.Log.Write("事前点検", $"RSR の自動 OFF の設定（外れたら送り直す。原因の切り分け用）：{string.Join("・", autoOff)}");
    }

    /// <summary>Artisan の設定ファイル（pluginConfigs\Artisan.json）の真偽値を読む。読めなければ null。</summary>
    public static bool? ReadArtisanBool(string name) => ReadPluginConfigBool("Artisan.json", name);

    /// <summary>
    /// プラグインの設定ファイル（pluginConfigs\ファイル名）の真偽値を、入れ子の名前の順にたどって読む。読めなければ null。
    /// 他のプラグインが書いている最中でも読めるよう、共有で開く。
    /// </summary>
    public static bool? ReadPluginConfigBool(string file, params string[] path)
    {
        try
        {
            var dir = Svc.PluginInterface.ConfigDirectory.Parent;
            if (dir == null)
                return null;
            var full = Path.Combine(dir.FullName, file);
            if (!File.Exists(full))
                return null;

            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var e = doc.RootElement;
            foreach (var name in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e))
                    return null;
            }

            return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
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
