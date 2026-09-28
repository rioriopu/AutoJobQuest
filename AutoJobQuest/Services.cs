using System;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using AutoJobQuest.Ui;

namespace AutoJobQuest;

/// <summary>プラグイン全体で使う部品をまとめて持つ。</summary>
public sealed class Services : IDisposable
{
    public Configuration Config { get; }

    public RunLog Log { get; }

    public TaskContext Ctx { get; }

    public Runner Runner { get; }

    public GbrReflection GbrReflection { get; }

    public List<Window> ExtraWindows { get; } = [];

    private bool leftoversRestored;
    private DateTime nextLeftoverTry = DateTime.MinValue;

    public Services(Configuration config, RunLog log)
    {
        this.Config = config;
        this.Log = log;
        this.GbrReflection = new GbrReflection();

        this.Ctx = new TaskContext
        {
            Config = config,
            Log = log,
            Confirm = new ConfirmService(),
            Data = new GameDataCache(),
            Artisan = new ArtisanIpc(),
            Questionable = new QuestionableIpc(),
            Lifestream = new LifestreamIpc(),
            Navmesh = new VnavmeshIpc(),
            GatherBuddy = new GatherBuddyIpc(),
            Gbr = new GbrOperations(this.GbrReflection, config),
            Rotation = new RotationSolverIpc(),
            AutoHook = new AutoHookIpc(),
            TextAdvance = new TextAdvanceIpc(),
            YesAlready = new YesAlreadyIpc(),
            MarketWatcher = new Automation.MarketBoardWatcher(Svc.Hook),
        };

        this.Runner = new Runner(this.Ctx);
        this.ExtraWindows.Add(new ConfirmWindow(this.Ctx.Confirm, this.Runner));
    }

    public void Tick()
    {
        // 前回落ちたときに残った GBR の一時変更を、動いていないときに1回だけ戻す
        if (!this.leftoversRestored && !this.Runner.IsRunning && DateTime.UtcNow >= this.nextLeftoverTry && Me.Available)
        {
            this.nextLeftoverTry = DateTime.UtcNow.AddSeconds(10);
            if (this.Ctx.Gbr.RestoreLeftovers())
                this.leftoversRestored = true;
        }

        this.Ctx.YesAlready.KeepSuppressed();
        this.Runner.Tick();
    }

    /// <summary>フレーム処理で拾えなかった例外。動いていれば止める。</summary>
    public void OnUnhandled(Exception ex)
        => this.Runner.RequestStop($"例外: {ex.GetType().Name}");

    public void Dispose()
    {
        // 他プラグインの状態（GBR の ON/OFF・リスト・Questionable 等）には触らない。
        // 停止要求の共有データ（YesAlready）だけは自分の要求を外して手放す（残すと相手が止まったままになる）。
        this.Ctx.YesAlready.Dispose();
        this.Ctx.MarketWatcher.Dispose();
        this.GbrReflection.Dispose();
    }
}
