using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using AutoJobQuest.Automation;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;

namespace AutoJobQuest.Ui;

/// <summary>
/// 主画面。
///
/// 画面の形:
///  ① 各ジョブごとにチェックボックス
///  ② チェックを入れたジョブのジョブクエだけを、上部の「ジョブクエ開始」ボタンで自動化
///  ※ 全部を ON にするチェックボックスも置く
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Yellow = new(1f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 Red = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.5f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Grey = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly Configuration config;
    private readonly RunLog log;
    private readonly Services services;

    private JobQuestPlan? plan;
    private bool planRequested;
    private List<ReportSection>? report;
    private DateTime reportAt;
    private bool reportRequested = true;
    private string? lastWritten;
    private DateTime dataReadyAt = DateTime.MinValue;

    public MainWindow(Configuration config, RunLog log, Services services)
        : base("ジョブクエ自動化##AutoJobQuest")
    {
        this.config = config;
        this.log = log;
        this.services = services;
        this.services.CharacterChanged += this.OnCharacterChanged;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(1600, 1600),
        };
    }

    private TaskContext Ctx => this.services.Ctx;

    public override void Draw()
    {
        this.DrawHeader();
        ImGui.Separator();
        this.DrawJobSelection();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##ajqtabs");
        if (!tabs)
            return;

        using (var t = ImRaii.TabItem("計画"))
        {
            if (t)
                this.DrawPlanTab();
        }

        using (var t = ImRaii.TabItem("事前点検"))
        {
            if (t)
                this.DrawPreflightTab();
        }

        using (var t = ImRaii.TabItem("キャラクター"))
        {
            if (t)
                this.DrawCharacterTab();
        }

        using (var t = ImRaii.TabItem("設定"))
        {
            if (t)
                this.DrawSettingsTab();
        }

        using (var t = ImRaii.TabItem("記録"))
        {
            if (t)
                this.DrawLogTab();
        }
    }

    // ------------------------------------------------------------------

    private void DrawHeader()
    {
        var runner = this.services.Runner;
        var anySelected = this.config.SelectedCrafters.Any(x => x);
        var blocker = runner.IsRunning
            ? null
            : this.config.OwnerContentId == 0
                ? "キャラクターにログインしてから開始してください（設定はキャラクターごとに保存します）"
                : runner.StartBlocker() ?? Jobs.StartProblem(this.config.SelectedCrafters, Jobs.Level) ?? GearCheck.GearProblem(this.config.SelectedCrafters)
                  ?? this.DataBlocker(anySelected) ?? this.BagBlocker(anySelected);

        // 一時停止：止めると他のプラグインに頼んだことを全部戻す（止めている間に Artisan・GBR・Questionable が
        // 勝手に動き続けないように）。進み具合は毎回ゲームから読み直すので、もう一度開始すれば続きから進む。
        // 利用者が止めた後は、開始ボタンを「続きから再開」と出す
        var resume = !runner.IsRunning && runner.LastOutcome == RunOutcome.UserStopped;
        using (ImRaii.Disabled(runner.IsRunning || !anySelected || blocker != null))
        {
            if (ImGui.Button(resume ? "続きから再開" : "ジョブクエ開始", new Vector2(160, 32)))
                this.StartFlow();
        }

        if (resume && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("進み具合（クエスト・所持品）はゲームから読み直すので、止めたところから続きます");

        ImGui.SameLine();
        using (ImRaii.Disabled(!runner.IsRunning))
        {
            if (ImGui.Button("停止", new Vector2(100, 32)))
                runner.RequestStop("停止ボタン");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("一時停止にも使えます。止めると他のプラグインに頼んだこと（RSR・GBR・TextAdvance 等）を戻します。"
                             + "もう一度「続きから再開」を押せば、進み具合をゲームから読み直して続きから進みます");

        ImGui.SameLine();
        ImGui.BeginGroup();
        if (runner.IsRunning)
        {
            ImGui.TextColored(Green, "動作中");
            ImGui.TextUnformatted(runner.Root?.Status ?? string.Empty);

            // 流れが中断している（反撃・他者の会話の窓）間は、その理由を出す（作業の上限の時間は止めている）
            if (WorkClock.PauseReason is { } paused)
                ImGui.TextColored(Yellow, $"中断中：{paused}（この間は作業の上限の時間を数えていません）");
        }
        else
        {
            ImGui.TextColored(Grey, "停止中");
            if (runner.LastResult.Length > 0)
            {
                // 結果の分類で色を分ける（取引結果不明・失敗・後始末確認待ちは目立たせる）
                var color = runner.LastOutcome switch
                {
                    RunOutcome.PurchaseUnknown or RunOutcome.Failed => Red,
                    RunOutcome.CleanupPending or RunOutcome.DoneWithExclusions => Yellow,
                    _ => Green,
                };
                ImGui.TextColored(color, $"前回: {runner.LastResult}");
                if (runner.LastNextAction.Length > 0)
                    ImGui.TextWrapped($"次にすること：{runner.LastNextAction}");
            }
        }

        ImGui.EndGroup();

        if (!anySelected)
            ImGui.TextColored(Grey, "ジョブを1つ以上選んでください");

        ImGui.PushTextWrapPos(0);
        if (blocker != null)
            ImGui.TextColored(Yellow, blocker);
        ImGui.TextColored(Yellow, Preflight.Premise);

        // 確かめ待ちの控え（復旧に使える形で出す）
        if (this.config.RetainerSuppressionPendingRestore && !runner.IsRunning)
            ImGui.TextColored(Yellow, "AutoRetainerの抑制解除を確認しています。読戻しが成功するまで復元待ちの記録を残します。");
        if (this.config.PendingPurchase is { } pending)
            ImGui.TextColored(Red, $"⚠ 前回のマーケット購入の結果が確かめられていません：{pending.Describe()}。マーケットボードの取引履歴で確かめてから始めてください（事前点検の確認で「はい」を押すと控えを消します）");
        if (this.Ctx.Rotation.RestorePending && !runner.IsRunning)
            ImGui.TextColored(Grey, "RSR のモード・範囲攻撃の設定を元に戻したかを確かめています（10秒おき。戻っていなければ戻します）");
        if (this.config.RsrBoolOriginals.Count > 0)
        {
            ImGui.TextColored(Yellow, "RSR の真偽設定を同じ値に手動変更したことは自動検出できません。今の値を残す場合は次のボタンを使ってください");
            if (ImGui.Button("RSR の真偽設定を今の値で保持して停止"))
            {
                runner.RequestStop("RSR の真偽設定を利用者へ引き渡しました");
                this.Ctx.Rotation.KeepCurrentBoolSettings();
            }
        }
        if (this.config.ArtisanTempConsumableRecipes.Count > 0 && !runner.IsRunning)
            ImGui.TextColored(Yellow, "Artisan の食事・薬の一時指定は復元待ちです。Artisan が空いてから再試行します");

        if (this.config.GbrConfigAwaitingSave.Count > 0)
            ImGui.TextColored(Grey, $"GBR の設定（{string.Join("、", this.config.GbrConfigAwaitingSave.Keys)}）を元に戻しました。保存ファイルに書かれたかを確かめています");

        // 戻せなかった GBR のリスト（見つからない・同じ名前が複数）。利用者が GBR で確かめるまで出し続ける
        var unresolved = this.config.GbrUnresolvedListRefs;
        if (unresolved.Count > 0)
        {
            ImGui.TextColored(Yellow,
                "⚠ 一時的に無効にした GBR の自動採集リストのうち、自動で有効に戻せなかったものがあります（見つからない、または同じ名前が複数あるため）。"
                + "GBR の画面で有効にし直してください："
                + string.Join("、", unresolved.Select(u => u.FolderPath.Length > 0 ? $"「{u.FolderPath}/{u.Name}」" : $"「{u.Name}」")));
            if (ImGui.Button("GBR で確かめた（この表示を消す）"))
            {
                unresolved.Clear();
                this.config.Save();
            }
        }

        ImGui.PopTextWrapPos();
    }

    private void DrawJobSelection()
    {
        ImGui.TextWrapped("対象職はそれぞれLv70以上が必要です（ジョブクエは Lv70 まで）。選んだ職のジョブクエをまとめて進め、始めに呼び鈴でリテイナーから必要品をまとめて引き出します。"
                          + "鞄の空きが足りなければ開始できません"
                          + "（チョコボかばん・リテイナーの収集品・装備中・出品中・マテリア付きの品は使いません）。");
        var sel = this.config.SelectedCrafters;
        var all = sel.All(x => x);

        // 選んだ職はキャラクターごとに保存する。ログインしていない間は保存先が無いので変えさせない
        using (ImRaii.Disabled(this.services.Runner.IsRunning || this.config.OwnerContentId == 0))
        {
            if (ImGui.Checkbox("全てON", ref all))
            {
                for (var i = 0; i < sel.Length; i++)
                    sel[i] = all;
                this.config.Save();
                this.plan = null;
                this.OnSelectionChanged();
            }

            using (var table = ImRaii.Table("##jobs", 4))
            {
                if (table)
                {
                    for (var i = 0; i < Jobs.QuestJobs.Length; i++)
                    {
                        ImGui.TableNextColumn();
                        var v = sel[i];
                        var label = Jobs.QuestJobNames[i];
                        if (this.plan != null)
                        {
                            var remaining = this.plan.RemainingQuests.Count(q => q.ClassJobId == Jobs.QuestJobs[i]);
                            label += v ? $"（残り{remaining}本）" : string.Empty;
                        }

                        if (ImGui.Checkbox($"{label}##job{i}", ref v))
                        {
                            sel[i] = v;
                            this.config.Save();
                            this.plan = null;
                            this.OnSelectionChanged();
                        }
                    }
                }
            }
        }

        // 選んだジョブのうち、前提のクエスト（メインクエスト等）が未完了で進められないジョブクエ
        // （チェックを入れた時点で、どのクエストが未達か分かるように）
        var blocked = this.BlockedForSelection();
        if (blocked == null)
        {
            if (sel.Any(x => x))
                ImGui.TextColored(Grey, "（前提のクエストが未完了で進められないジョブクエは、ゲームデータの読み込み後にここへ出ます）");
        }
        else if (blocked.Count > 0)
        {
            ImGui.PushTextWrapPos(0);
            // 開始は止めない（進められるところまで進め、受けられないジョブクエの手前で止まる。
            // メインクエストに止められる職は、最後にその受注の NPC に話しかけて、断られたところで止まる）
            ImGui.TextColored(Yellow, "⚠ 前提のクエストが未完了のため、次のジョブクエは進められません（その手前まで進めて止まります。素材も集めません）：");
            foreach (var line in JobQuestPlan.SummarizeBlocked(blocked))
                ImGui.TextColored(Yellow, $"　・{line}");
            if (this.MainQuestProblem() is { } note)
                ImGui.TextColored(Yellow, $"　{note}");
            if (PlanBuilder.KnockCandidates(blocked, MainQuestGate.IsMainScenario).FirstOrDefault() is { } knock)
                ImGui.TextColored(Yellow, $"　最後に{Jobs.Name(knock.Quest.ClassJobId)}のジョブクエ「{knock.Quest.Name}」（Lv{knock.Quest.Level}）の受注の NPC に話しかけ、断られたところで止まります");
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// 選んだ職があれば、ゲームデータの読み込みを始め、読み終わるまで開始させない（以前は読み込み前に押せてしまい、
    /// メインクエストの前提で止まるときも「失敗」として失敗の報告が書き出された）。読み込みに失敗していれば、その理由を出す
    /// （作り直しはジョブの選び直しで行う。毎フレーム作り直さない）。
    /// </summary>
    private string? DataBlocker(bool anySelected)
    {
        var data = this.Ctx.Data;
        if (!anySelected || data.IsReady)
            return null;
        if (data.BuildError is { } error)
            return $"ゲームデータを読めませんでした（{error}）。ジョブの選び直しで読み直します";

        // ログインの直後（エリアの読み込み中）には重い走査を始めない（起動時に開く設定だと、ログイン直後に走っていた）
        if (!data.IsBuilding && (Automation.GameUi.BetweenAreas || !Automation.GameUi.PlayerFree()))
            return "動ける状態になったら、ゲームデータを読み込みます（読み終わると開始できます）";
        data.EnsureBuilding();
        return "ゲームデータを読み込んでいます（読み終わると開始できます）";
    }

    // 選んだ職のジョブクエに要る鞄の空きが足りないか（チェックを変えたとき・5秒ごとに調べ直す）
    private string? bagProblem;
    private string bagKey = string.Empty;
    private DateTime bagAt = DateTime.MinValue;

    private string? BagBlocker(bool anySelected)
    {
        var data = this.Ctx.Data;
        if (!anySelected || !data.IsReady || !Me.Available)
            return null;
        var key = string.Concat(this.config.SelectedCrafters.Select(x => x ? '1' : '0')) + "|" + this.config.KeepFreeBagSlots;
        if (key != this.bagKey || DateTime.UtcNow - this.bagAt > TimeSpan.FromSeconds(5))
        {
            try
            {
                var plan = PlanBuilder.Build(data, this.config.SelectedCrafters);
                var need = BagEstimate.ForPlan(plan);
                var free = Inventory.FreeBagSlots();
                this.bagProblem = BagEstimate.Shortage(need, free, this.config.KeepFreeBagSlots) > 0
                    ? BagEstimate.ShortageText(need, free, this.config.KeepFreeBagSlots)
                    : null;
            }
            catch (Exception ex)
            {
                this.log.Warn("計画", $"鞄の空きを見積もれませんでした: {ex.Message}");
                this.bagProblem = null;
            }

            this.bagKey = key;
            this.bagAt = DateTime.UtcNow;
        }

        return this.bagProblem;
    }

    // 選んだ職のジョブクエに要るメインクエストが未完了か（チェックを変えたとき・5秒ごとに調べ直す）
    private string? msqProblem;
    private string msqKey = string.Empty;
    private DateTime msqAt = DateTime.MinValue;

    private string? MainQuestProblem()
    {
        var data = this.Ctx.Data;
        if (!data.IsReady || !Me.Available)
            return null;
        var key = string.Concat(this.config.SelectedCrafters.Select(x => x ? '1' : '0'));
        if (key != this.msqKey || DateTime.UtcNow - this.msqAt > TimeSpan.FromSeconds(5))
        {
            try
            {
                this.msqProblem = MainQuestGate.StopNote(data, this.config.SelectedCrafters);
            }
            catch (Exception ex)
            {
                this.log.Warn("計画", $"メインクエストの前提を調べられませんでした: {ex.Message}");
                this.msqProblem = null;
            }

            this.msqKey = key;
            this.msqAt = DateTime.UtcNow;
        }

        return this.msqProblem;
    }

    // 進められないジョブクエの控え（チェックを変えたとき・5秒ごとに調べ直す。毎フレームは調べない）
    private List<BlockedQuest>? blockedCache;
    private string blockedKey = string.Empty;
    private DateTime blockedAt = DateTime.MinValue;

    private void OnSelectionChanged()
    {
        // ゲームデータがまだなら読み始める（利用者がジョブを選んだとき＝使うと分かったときだけ。起動時には読まない）
        this.Ctx.Data.EnsureBuilding();
        this.blockedAt = DateTime.MinValue;
        this.msqAt = DateTime.MinValue;
    }

    private List<BlockedQuest>? BlockedForSelection()
    {
        var data = this.Ctx.Data;
        if (!data.IsReady || !Me.Available)
            return null;

        var key = string.Concat(this.config.SelectedCrafters.Select(x => x ? '1' : '0'));
        if (this.blockedCache == null || key != this.blockedKey || DateTime.UtcNow - this.blockedAt > TimeSpan.FromSeconds(5))
        {
            try
            {
                this.blockedCache = PlanBuilder.FindBlocked(data, this.config.SelectedCrafters);
            }
            catch (Exception ex)
            {
                this.log.Warn("計画", $"進められないジョブクエを調べられませんでした: {ex.Message}");
                this.blockedCache = [];
            }

            this.blockedKey = key;
            this.blockedAt = DateTime.UtcNow;
        }

        return this.blockedCache;
    }

    private void StartFlow()
    {
        this.Ctx.Data.EnsureBuilding();
        var selected = (bool[])this.config.SelectedCrafters.Clone();
        this.services.Runner.Start(new JobQuestFlow(selected));
    }

    // ------------------------------------------------------------------

    private void DrawPlanTab()
    {
        var data = this.Ctx.Data;

        if (ImGui.Button("計画を更新"))
        {
            data.EnsureBuilding();
            this.planRequested = true;
        }

        ImGui.SameLine();
        if (data.BuildError != null)
            ImGui.TextColored(Red, $"ゲームデータを読めませんでした: {data.BuildError}");
        else if (data.IsBuilding)
            ImGui.TextColored(Grey, "ゲームデータを読み込み中…");

        if (this.planRequested && data.IsReady)
        {
            this.planRequested = false;
            try
            {
                this.plan = PlanBuilder.Build(data, this.config.SelectedCrafters);
            }
            catch (Exception ex)
            {
                this.log.Warn("計画", $"計画を立てられませんでした: {ex.Message}");
            }
        }

        var p = this.plan;
        if (p == null)
        {
            ImGui.TextWrapped("「計画を更新」を押すと、選んだジョブの残りのジョブクエ・製作・必要な素材と入手手段を表示します。");
            return;
        }

        ImGui.TextUnformatted($"作成: {p.CreatedAt:HH:mm:ss}　残りのジョブクエ {p.RemainingQuests.Count} 本　製作 {p.Craft.Crafts.Count} 種 / {p.Craft.Crafts.Sum(x => x.Crafts)} 回");

        foreach (var w in p.Warnings)
            ImGui.TextColored(Yellow, $"・{w}");

        if (p.Blocked.Count > 0 && ImGui.CollapsingHeader($"前提が未完了で進められないジョブクエ（{p.Blocked.Count}）　※計画には入れていません", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.PushTextWrapPos(0);
            foreach (var line in p.BlockedSummary())
                ImGui.TextColored(Yellow, $"  {line}");
            foreach (var b in p.Blocked)
                ImGui.TextUnformatted($"    {Jobs.Name(b.Quest.ClassJobId)} {b.Quest}：{b.Reason}");
            ImGui.PopTextWrapPos();
        }

        if (ImGui.CollapsingHeader($"残りのジョブクエ（{p.RemainingQuests.Count}）"))
        {
            foreach (var g in p.RemainingQuests.GroupBy(x => x.ClassJobId))
            {
                ImGui.TextColored(Green, Jobs.Name(g.Key));
                foreach (var q in g)
                {
                    var items = string.Join("、", q.Items.Select(x => $"{CraftPlanner.ItemName(x.ItemId)}{(x.Hq ? "(HQ)" : string.Empty)}×{x.Count}"));
                    var materia = q.Materia is { } m
                        ? $"　＋マテリア装着（{(m.MateriaItemId is { } mid ? CraftPlanner.ItemName(mid) : $"任意 → {CraftPlanner.ItemName(this.Ctx.Config.AnyMateriaItemId)}")}）"
                        : string.Empty;
                    var stageNote = p.ItemStages.GetValueOrDefault(q.RowId, Automation.QuestItemStage.Stage.All) switch
                    {
                        Automation.QuestItemStage.Stage.None => "（納品物は渡し終えています）",
                        Automation.QuestItemStage.Stage.HeldOnly => "（途中まで渡しています。残りの分を用意します）",
                        _ => string.Empty,
                    };
                    ImGui.TextUnformatted($"  Lv{q.Level} {q.Name}：{items}{materia}{stageNote}");
                }
            }
        }

        var shortfalls = p.Shortfalls.ToList();
        if (ImGui.CollapsingHeader($"足りない素材（{shortfalls.Count} 品目）", ImGuiTreeNodeFlags.DefaultOpen))
        {
            using var table = ImRaii.Table("##raw", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable);
            if (table)
            {
                ImGui.TableSetupColumn("素材");
                ImGui.TableSetupColumn("必要");
                ImGui.TableSetupColumn("不足");
                ImGui.TableSetupColumn("入手手段");
                ImGui.TableHeadersRow();
                foreach (var r in shortfalls.OrderBy(x => x.Route).ThenBy(x => x.ItemId))
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Total.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Shortfall.ToString());
                    ImGui.TableNextColumn();
                    var text = RouteName(r.Route);
                    if (r.Fallbacks.Count > 0)
                        text += $"（次: {string.Join("→", r.Fallbacks.Select(RouteName))}）";
                    if (r.Route == Route.Unknown)
                        ImGui.TextColored(Red, text);
                    else
                        ImGui.TextUnformatted(text);
                }
            }
        }

        if (ImGui.CollapsingHeader($"製作（{p.Craft.Crafts.Count} 種）"))
        {
            foreach (var c in p.Craft.Crafts)
                ImGui.TextUnformatted($"  {Jobs.Name(c.ClassJobId)} Lv{c.RecipeLevel} {CraftPlanner.ItemName(c.ItemId)} ×{c.Crafts}回{(c.Yield > 1 ? $"（{c.Yield}個ずつ）" : string.Empty)}{(c.WantHq ? "　HQ" : string.Empty)}");
        }

        if (p.Materia.Count > 0 && ImGui.CollapsingHeader($"マテリア装着（{p.Materia.Count} 件）"))
        {
            foreach (var m in p.Materia)
            {
                string mat;
                if (m.MateriaItemId is { } mid)
                {
                    mat = CraftPlanner.ItemName(mid);
                }
                else
                {
                    // 任意のマテリア：設定の品（AnyMateriaItemId）。付けられなければ理由を出す
                    var any = MateriaCatalog.ResolveAny(this.Ctx.Config.AnyMateriaItemId, m.TargetItemId, out var problem);
                    mat = any is { } a ? $"{CraftPlanner.ItemName(a)}（任意のマテリアの指定）" : $"（付けられません：{problem}）";
                }


                ImGui.TextUnformatted($"  {m.Quest}：{CraftPlanner.ItemName(m.TargetItemId)}{(m.TargetHq ? "(HQ)" : string.Empty)} に {mat}{(m.AlreadyMelded ? "　…装着済み" : string.Empty)}");
            }
        }
    }

    private void DrawPreflightTab()
    {
        using (ImRaii.Disabled(this.services.Runner.IsRunning))
        {
            if (ImGui.Button("点検する"))
                this.services.InspectionRequested = true;
        }
        if (this.services.Inspection is { Complete: false } pending)
            ImGui.TextUnformatted(pending.Status);
        var preflight = this.services.Inspection?.Items;

        if (!this.Ctx.Data.IsReady)
        {
            ImGui.SameLine();
            ImGui.TextColored(Grey, "（装備の基準値はゲームデータの読み込み後に点検します）");
        }

        if (preflight == null)
            return;

        foreach (var item in preflight)
        {
            var color = item.Severity switch
            {
                Severity.Error => Red,
                Severity.Warn => Yellow,
                _ => Green,
            };
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(color, $"{(item.Severity == Severity.Error ? "×" : item.Severity == Severity.Warn ? "！" : "○")} {item.Text}");
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// 設定（購入の確認の基準・作り直しの上限などに絞る。入力の範囲を確かめる。内部の控えはここに出さない）。
    /// 動作中は変えられない（途中で基準が変わると、確認の判断が食い違うため）。
    /// </summary>
    /// <summary>
    /// ログインしているキャラクターが替わった（設定の中身が入れ替わった）。前のキャラクターの計画・前提の判定・キャラクター情報の控えを消す
    /// </summary>
    private void OnCharacterChanged()
    {
        this.plan = null;
        this.planRequested = false;
        this.msqKey = string.Empty;
        this.msqAt = DateTime.MinValue;
        this.msqProblem = null;
        this.blockedCache = null;
        this.blockedKey = string.Empty;
        this.blockedAt = DateTime.MinValue;
        this.bagKey = string.Empty;
        this.bagProblem = null;
        this.report = null;
        this.reportRequested = true;
    }

    private void DrawSettingsTab()
    {
        var running = this.services.Runner.IsRunning;
        ImGui.PushTextWrapPos(0);

        // 設定はキャラクターごと。ログインしていない間は保存先が無いので変えさせない
        var loggedIn = this.config.OwnerContentId != 0;
        ImGui.TextColored(Grey, loggedIn
            ? $"この設定は {this.config.CharacterName}（{this.config.HomeWorld}）のものです（キャラクターごとに保存します）"
            : "ログインしていないため、設定を変えられません（設定はキャラクターごとに保存します）");
        if (this.config.LastSaveProblem is { } saveProblem)
            ImGui.TextColored(Yellow, $"⚠ 設定を保存できませんでした：{saveProblem}");

        using (ImRaii.Disabled(running || !loggedIn))
        {
            ImGui.TextUnformatted("マーケットボードの購入の確認");

            var perPurchase = this.config.ConfirmPurchaseAboveGil;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("1回の購入額がこれを超えたら確かめる（ギル）", ref perPurchase, 10_000, 100_000))
            {
                this.config.ConfirmPurchaseAboveGil = Math.Clamp(perPurchase, 0, 999_999_999);
                this.config.Save();
            }

            ImGui.TextColored(this.config.ConfirmPurchaseAboveGil == 0 ? Red : Grey,
                this.config.ConfirmPurchaseAboveGil == 0
                    ? "0 になっています：1回の購入額では確かめません（確認なしで買います）"
                    : "既定は 500,000。0 にすると、1回の購入額では確かめません");

            var ratio = (float)this.config.ConfirmUnitPriceRatio;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputFloat("単価が最近の取引の中央値の何倍を超えたら確かめる", ref ratio, 0.5f, 1f, "%.1f"))
            {
                // 0（確かめない）か、1.5 倍以上（1倍近くだと、ふつうの値動きで毎回確かめることになる）
                this.config.ConfirmUnitPriceRatio = ratio <= 0 ? 0 : Math.Clamp(Math.Round(ratio, 1), 1.5, 100);
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 3.0。0 で確かめない。取引履歴が届かない品では確かめられません");

            var runTotal = (int)Math.Min(this.config.ConfirmRunTotalAboveGil, 999_999_999);
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("この実行でマーケットに払う合計がこれを超えたら確かめる（ギル）", ref runTotal, 100_000, 1_000_000))
            {
                this.config.ConfirmRunTotalAboveGil = Math.Clamp(runTotal, 0, 999_999_999);
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 0（使わない）。「はい」で続けると、さらにこの額を払ったところでまた確かめます");

            var excess = (int)Math.Min(this.config.ConfirmExcessAboveGil, 999_999_999);
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("必要な数より多く買う分（余り）の額がこれを超えたら確かめる（ギル）", ref excess, 10_000, 100_000))
            {
                this.config.ConfirmExcessAboveGil = Math.Clamp(excess, 0, 999_999_999);
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 100,000。0 で確かめない。大きいまとまりしか出品が無いとき、余りの分の無駄に気づくための確認です");

            // リテイナーの段の設定（以前は設定ファイルを手で書き換えないと変えられなかった）
            ImGui.Separator();
            ImGui.TextUnformatted("リテイナーと鞄");
            var useRetainers = this.config.UseRetainerStock;
            if (ImGui.Checkbox("開始したら、呼び鈴でリテイナーから要る品をまとめて引き出す", ref useRetainers))
            {
                this.config.UseRetainerStock = useRetainers;
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は ON。切ると、手持ちだけで計画します（リテイナーにある品も、採集・購入などで集めます）");

            var keepFree = this.config.KeepFreeBagSlots;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("鞄に残しておく空き（枠）", ref keepFree, 1, 5))
            {
                this.config.KeepFreeBagSlots = Math.Clamp(keepFree, 0, 100);
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 5。選んだ職に要る鞄の見積もりが、この空きを残して入らなければ開始できません");

            ImGui.Separator();
            ImGui.TextUnformatted("製作");
            var retry = this.config.MaxRetryRounds;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("HQ ができなかったとき、同じ品を何回まで作り直すか", ref retry, 1, 1))
            {
                this.config.MaxRetryRounds = Math.Clamp(retry, 1, 10);
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 3（1〜10）。品目ごとに数え、届いたら何を見直せばよいかを出して止めます");

            var questRetry = this.config.QuestCraftRetryRounds;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("受注後に作る品（Lv61〜70）が HQ にならなかったとき、何回まで作り直すか", ref questRetry, 1, 1))
            {
                this.config.QuestCraftRetryRounds = Math.Clamp(questRetry, 0, 30);
                Planning.PlanBuilder.QuestCraftSpare = this.config.QuestCraftRetryRounds;
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は 10。材料はクエストがくれて、何度でももらい直せます（失うのは1回ごとのクリスタル）。クリスタルはこの回数分を先に用意します");

            var otherGear = this.config.RequireGearForOtherCrafters;
            if (ImGui.Checkbox("選んでいない製作職も、主道具〜足が Lv68 以上の装備でなければ、中間素材を作らずにマーケットボードで買う", ref otherGear))
            {
                this.config.RequireGearForOtherCrafters = otherGear;
                Data.CraftAbility.RequireGear = otherGear;
                this.config.Save();
                this.plan = null;
            }

            ImGui.TextColored(Grey, "既定は ON。中間素材のレシピのレベルに届く職がいれば、素材を集めて作ります。届く職がいない・ギアセットが無い"
                                    + "（ON なら装備も足りない）ときは、作らずにマーケットボードで買います。選んだ職の装備は、この設定にかかわらず開始の条件です");

            var consumables = this.config.UseArtisanConsumables;
            if (ImGui.Checkbox("ジョブクエの製作で Artisan の既定の食事・薬を使う", ref consumables))
            {
                this.config.UseArtisanConsumables = consumables;
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は OFF。Artisan はレシピごとの設定が無いと既定の食事・薬を使うので、高価な消耗品を Lv1〜60 の製作で使わないよう、"
                                    + "こちらが頼む製作の間だけ使わない指定にして、終わったら戻します。HQ 指定の品が HQ にならないときは ON にすると出やすくなります");

            var hideOthers = this.config.HideOtherQuestsDuringRun;
            if (ImGui.Checkbox("実行の間、受注中のほかのクエストをジャーナルで非表示にする", ref hideOthers))
            {
                this.config.HideOtherQuestsDuringRun = hideOthers;
                this.config.Save();
            }

            ImGui.TextColored(Grey, "既定は OFF（実機での確認がまだ少ない機能です）。有効にすると、進めるクエスト以外をジャーナルの「非表示」にし、"
                                    + "止まったら元の状態（通常・優先表示）に戻します。途中で自分で表示の状態を変えたクエストは戻しません");
        }

        if (running)
            ImGui.TextColored(Grey, "動作中は変えられません");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// キャラクターの状態（プラグインがゲームから読み取って把握している内容）を出す。
    /// 収集品納品の解放などをキャラクター情報から読み、画面で確認できるように。
    /// </summary>
    private void DrawCharacterTab()
    {
        var data = this.Ctx.Data;
        if (ImGui.Button("読み直す"))
        {
            data.EnsureBuilding();
            this.reportRequested = true;
        }

        ImGui.SameLine();
        if (data.IsBuilding)
            ImGui.TextColored(Grey, "ゲームデータを読み込み中…（読み終わると秘伝書・装備の基準も出ます）");
        else if (this.report != null)
            ImGui.TextColored(Grey, $"{this.reportAt:HH:mm:ss} に読み取り");

        // データの読み込みが終わった時刻を覚え、それより前に作った一覧なら作り直す
        if (data.IsReady && data.AllBooks != null && this.dataReadyAt == DateTime.MinValue)
            this.dataReadyAt = DateTime.Now;

        if (this.reportRequested || (this.report != null && this.dataReadyAt != DateTime.MinValue && this.reportAt < this.dataReadyAt))
        {
            data.EnsureBuilding();
            this.reportRequested = false;
            this.report = CharacterReport.Build(this.Ctx);
            this.reportAt = DateTime.Now;
        }

        if (this.report == null)
            return;

        using var child = ImRaii.Child("##chara", new Vector2(0, 0), false);
        if (!child)
            return;

        foreach (var sec in this.report)
        {
            ImGui.TextColored(Green, sec.Title);
            foreach (var line in sec.Lines)
            {
                var color = line.Severity switch
                {
                    Severity.Error => Red,
                    Severity.Warn => Yellow,
                    _ => new Vector4(1f, 1f, 1f, 1f),
                };
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(color, $"  {(line.Severity == Severity.Error ? "×" : line.Severity == Severity.Warn ? "！" : "○")} {line.Text}");
                ImGui.PopTextWrapPos();
            }

            ImGui.Spacing();
        }
    }

    private void DrawLogTab()
    {
        if (ImGui.Button("クリップボードへ写す"))
            ImGui.SetClipboardText(string.Join("\n", this.log.Snapshot()));
        ImGui.SameLine();
        if (ImGui.Button("消す"))
            this.log.Clear();
        ImGui.SameLine();
        if (ImGui.Button("今の状態を書き出す"))
            this.lastWritten = this.services.WriteSnapshotNow();
        ImGui.SameLine();
        if (ImGui.Button("記録のフォルダを開く"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{this.services.Debug.Directory}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.log.Warn("記録", $"フォルダを開けませんでした: {ex.Message}");
            }
        }

        ImGui.TextColored(Grey, $"記録の置き場所：{this.services.Debug.Directory}");
        if (this.services.Debug.RunFile is { } run)
            ImGui.TextColored(Grey, $"この実行の記録：{System.IO.Path.GetFileName(run)}");
        if (this.services.Debug.LastFailureReport is { } fail)
            ImGui.TextColored(Yellow, $"最後に止まったときの報告：{System.IO.Path.GetFileName(fail)}");
        if (this.lastWritten != null)
            ImGui.TextColored(Grey, $"書き出しました：{System.IO.Path.GetFileName(this.lastWritten)}");

        var always = this.config.AlwaysRecordAddons;
        if (ImGui.Checkbox("実行していないときも、ショップ・マーケット等の画面を記録する", ref always))
        {
            this.config.AlwaysRecordAddons = always;
            this.config.Save();
        }

        using var child = ImRaii.Child("##log", new Vector2(0, 0), true);
        if (!child)
            return;

        foreach (var line in this.log.Snapshot())
        {
            if (line.Contains('⚠'))
                ImGui.TextColored(Yellow, line);
            else
                ImGui.TextUnformatted(line);
        }

        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);
    }

    public static string RouteName(Route r) => r switch
    {
        Route.MarketBoard => "マーケット",
        Route.Vendor => "NPC購入",
        Route.Gather => "採集",
        Route.Fish => "釣り",
        Route.Combat => "戦闘",
        Route.Reduce => "採集→精選",
        _ => "入手手段なし",
    };
}
