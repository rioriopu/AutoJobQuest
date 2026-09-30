using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Ipc;

namespace AutoJobQuest.Planning;

/// <summary>画面の手動点検をゲームの更新で進める。閉じた画面からゲームメモリを読まない。</summary>
public sealed class PreflightSession : IDisposable
{
    private JobQuestPlan? plan;
    private ArtisanHqEstimate.Job? hq;
    private string? selection;
    private bool buildRequested;
    public bool Complete { get; private set; }
    public string Status => this.hq?.Status ?? "ゲームデータを準備しています";
    public List<PreflightItem>? Items { get; private set; }

    public void Tick(TaskContext ctx)
    {
        if (this.Complete)
            return;
        try
        {
            // 読み込みは点検を始めたときに1回だけ頼む。失敗したら、作り直し続けずに理由を出して終える
            // （以前は毎フレーム頼み直し、失敗が続くと作り直しが途切れず、点検が終わらなかった。作り直しは次の「点検する」で）
            if (!this.buildRequested)
            {
                this.buildRequested = true;
                ctx.Data.EnsureBuilding();
            }

            if (!ctx.Data.IsReady && !ctx.Data.IsBuilding && ctx.Data.BuildError is { } buildError)
            {
                this.Items = [new PreflightItem(Severity.Error, $"ゲームデータを読めませんでした：{buildError}")];
                this.Dispose();
                return;
            }

            if (!ctx.Data.IsReady)
                return;
            this.selection ??= string.Join(",", ctx.Config.SelectedCrafters);
            if (this.selection != string.Join(",", ctx.Config.SelectedCrafters))
                throw new InvalidOperationException("対象の職が変わりました。点検し直してください");
            this.plan ??= PlanBuilder.Build(ctx.Data, ctx.Config.SelectedCrafters);
            this.hq ??= Preflight.BeginHq(this.plan);
            this.hq.Tick();
            if (!this.hq.Complete)
                return;
            if (PlanKey(this.plan) != PlanKey(PlanBuilder.Build(ctx.Data, ctx.Config.SelectedCrafters)))
                throw new InvalidOperationException("計算中に在庫・クエストの計画が変わりました。点検し直してください");
            this.Items = Preflight.Run(ctx, this.plan, this.hq);
            this.Complete = true;
        }
        catch (Exception e)
        {
            this.Items = [new PreflightItem(Severity.Error, $"点検できませんでした：{e.Message}")];
            this.Dispose();
        }
    }

    public static string PlanKey(JobQuestPlan plan)
        => string.Join("|", plan.RemainingQuests.Select(q => q.RowId)) + ";"
            + string.Join("|", plan.Craft.Crafts.Select(c => $"{c.RecipeId}:{c.Crafts}:{c.WantHq}:{c.HqTarget}")) + ";"
            + string.Join("|", plan.Raw.Select(x => $"{x.ItemId}:{x.Shortfall}:{x.Route}")) + ";"
            + string.Join("|", plan.Materia.Select(x => $"{x.TargetItemId}:{x.MateriaItemId}:{x.AlreadyMelded}"));

    public void Dispose()
    {
        this.hq?.Dispose();
        this.Complete = true;
    }
}
