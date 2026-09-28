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
    private List<PreflightItem>? preflight;
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
        var blocker = runner.IsRunning ? null : runner.StartBlocker();

        using (ImRaii.Disabled(runner.IsRunning || !anySelected || blocker != null))
        {
            if (ImGui.Button("ジョブクエ開始", new Vector2(160, 32)))
                this.StartFlow();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!runner.IsRunning))
        {
            if (ImGui.Button("停止", new Vector2(100, 32)))
                runner.RequestStop("停止ボタン");
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        if (runner.IsRunning)
        {
            ImGui.TextColored(Green, "動作中");
            ImGui.TextUnformatted(runner.Root?.Status ?? string.Empty);
        }
        else
        {
            ImGui.TextColored(Grey, "停止中");
            if (runner.LastResult.Length > 0)
                ImGui.TextUnformatted($"前回: {runner.LastResult}");
        }

        ImGui.EndGroup();

        if (!anySelected)
            ImGui.TextColored(Grey, "ジョブを1つ以上選んでください");

        ImGui.PushTextWrapPos(0);
        if (blocker != null)
            ImGui.TextColored(Yellow, blocker);
        ImGui.TextColored(Yellow, Preflight.Premise);

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
        var sel = this.config.SelectedCrafters;
        var all = sel.All(x => x);

        using (ImRaii.Disabled(this.services.Runner.IsRunning))
        {
            if (ImGui.Checkbox("全てON", ref all))
            {
                for (var i = 0; i < sel.Length; i++)
                    sel[i] = all;
                this.config.Save();
                this.plan = null;
            }

            using var table = ImRaii.Table("##jobs", 4);
            if (!table)
                return;

            for (var i = 0; i < Jobs.Crafters.Length; i++)
            {
                ImGui.TableNextColumn();
                var v = sel[i];
                var label = Jobs.CrafterShortNames[i];
                if (this.plan != null)
                {
                    var remaining = this.plan.RemainingQuests.Count(q => q.ClassJobId == Jobs.Crafters[i]);
                    label += v ? $"（残り{remaining}本）" : string.Empty;
                }

                if (ImGui.Checkbox($"{label}##job{i}", ref v))
                {
                    sel[i] = v;
                    this.config.Save();
                    this.plan = null;
                }
            }
        }
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
                    ImGui.TextUnformatted($"  Lv{q.Level} {q.Name}：{items}{materia}");
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
        if (ImGui.Button("点検する"))
        {
            this.Ctx.Data.EnsureBuilding();
            this.preflight = Preflight.Run(this.Ctx, this.plan);
        }

        if (!this.Ctx.Data.IsReady)
        {
            ImGui.SameLine();
            ImGui.TextColored(Grey, "（装備の基準値はゲームデータの読み込み後に点検します）");
        }

        if (this.preflight == null)
            return;

        foreach (var item in this.preflight)
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
