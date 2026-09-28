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

    /// <summary>不具合を調べるための記録（ファイル）。</summary>
    public DebugLog Debug { get; }

    private readonly Automation.AddonRecorder addonRecorder;
    private DateTime nextLeftoverTry = DateTime.MinValue;
    private DateTime nextHeartbeat = DateTime.MinValue;

    public Services(Configuration config, RunLog log)
    {
        this.Config = config;
        this.Log = log;
        this.Debug = new DebugLog(config.LogDirectory);
        this.GbrReflection = new GbrReflection();

        this.Ctx = new TaskContext
        {
            Config = config,
            Log = log,
            Confirm = new ConfirmService(),
            Data = new GameDataCache(() => config.ScripCollectableItemId),
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
            Ownership = new Automation.AddonOwnership(),
        };

        this.Runner = new Runner(this.Ctx);
        this.ExtraWindows.Add(new ConfirmWindow(this.Ctx.Confirm, this.Runner));

        // 実行の記録：始まったら実行ファイルを開いて状態を写す。失敗したら状態の写しと直前の記録を書き出す
        this.Runner.Started = task =>
        {
            this.Debug.BeginRun(task.Name);
            this.Debug.Block("実行", "開始時の状態", StateSnapshot.Capture(this.Ctx, this.Runner));
        };
        this.Runner.Finished = (result, failed) =>
        {
            if (failed)
            {
                var path = this.Debug.WriteFailureReport(result, StateSnapshot.Capture(this.Ctx, this.Runner));
                Svc.Chat.Print($"[AutoJobQuest] 止まったときの状況を書き出しました: {path}");
            }
            else
            {
                this.Debug.EndRun(result);
            }
        };

        this.addonRecorder = new Automation.AddonRecorder(() => this.Runner.IsRunning || this.Config.AlwaysRecordAddons);
    }

    public void Tick()
    {
        // GBR の一時変更（リスト・設定）が残っていれば、こちらが止まっていて GBR も止まっている間に戻す。
        // 戻せなかったとき（GBR が動いていた・読めなかった・例外）は10秒ごとにやり直す（控えが消えるまで）
        if (!this.Runner.IsRunning && DateTime.UtcNow >= this.nextLeftoverTry && Me.Available && this.Ctx.Gbr.HasLeftovers)
        {
            this.nextLeftoverTry = DateTime.UtcNow.AddSeconds(10);
            this.Ctx.Gbr.RestoreIfIdle(this.Ctx.GatherBuddy.IsAutoGatherEnabled(), this.Ctx.Gbr.VendorIsBusy());
        }

        this.Ctx.YesAlready.KeepSuppressed();
        this.Runner.Tick();

        // 止めたあとの見張り（実行していない間だけ）
        if (!this.Runner.IsRunning && this.Ctx.AfterStop.Count > 0)
        {
            for (var i = this.Ctx.AfterStop.Count - 1; i >= 0; i--)
            {
                var (name, until, step) = this.Ctx.AfterStop[i];
                bool finished;
                try
                {
                    finished = step();
                }
                catch (Exception ex)
                {
                    this.Debug.Exception("見張り", name, ex);
                    finished = true;
                }

                if (finished || DateTime.UtcNow > until)
                {
                    if (finished)
                    {
                        this.Debug.Line("見張り", $"{name}：終わりました");
                    }
                    else
                    {
                        // 期限切れは「止まったと確かめられた」ではない。利用者に確かめてもらう
                        this.Debug.Line("見張り", $"⚠ {name}：期限までに止まったことを確かめられませんでした");
                        this.Log.Warn("見張り", $"{name}：期限までに止まったことを確かめられませんでした。相手のプラグインが止まっているか確かめてから始めてください");
                        Svc.Chat.Print($"[AutoJobQuest] {name}：止まったことを確かめられませんでした。相手のプラグインが止まっているか確かめてから始めてください");
                    }

                    this.Ctx.AfterStop.RemoveAt(i);
                }
            }
        }

        // 実行中は30秒ごとに状態を1行残す（止まったまま動かない不具合の手がかり）
        if (this.Runner.IsRunning && DateTime.UtcNow >= this.nextHeartbeat)
        {
            this.nextHeartbeat = DateTime.UtcNow.AddSeconds(30);
            this.Debug.Line("状態", StateSnapshot.Compact(this.Runner));
        }
    }

    /// <summary>フレーム処理で拾えなかった例外。動いていれば止める。</summary>
    public void OnUnhandled(Exception ex)
    {
        this.Debug.Exception("例外", "フレーム処理で拾えなかった例外", ex);
        this.Runner.RequestStop($"例外: {ex.GetType().Name}");
    }

    /// <summary>今の状態をファイルに書き出す（画面のボタンから）。</summary>
    public string WriteSnapshotNow()
        => this.Debug.WriteSnapshot(StateSnapshot.Capture(this.Ctx, this.Runner));

    public void Dispose()
    {
        // 他プラグインの状態（GBR の ON/OFF・リスト・Questionable 等）には触らない。
        // 停止要求の共有データ（YesAlready）だけは自分の要求を外して手放す（残すと相手が止まったままになる）。
        //
        // 【実行中に読み込みが解除されたとき】「アンロード経路では相手に触れない」ので、こちらが頼んだ処理
        // （GBR の自動採集・NPC 購入、Questionable、Artisan の製作、RSR の Henched、移動）も止めない
        // （こちらを更新・再読み込みしただけで相手の動作が変わる事故が、別のプラグインで実際に起きたため）。
        // その代わり、何が残りうるかを利用者に知らせる（止め方は「先に停止ボタンを押す」）。
        // GBR の一時変更（リスト・設定）は控えを保存してあり、次に読み込んだとき GBR が止まっていれば戻す。
        if (this.Runner.IsRunning)
        {
            var msg = $"実行中（{this.Runner.Root?.Name}：{this.Runner.Root?.Status}）に読み込みが解除されました。"
                      + "こちらが頼んだ処理（GBR の自動採集・NPC 購入、Questionable、Artisan の製作、RSR の Henched、移動）は止めていません。"
                      + "動き続けていれば、それぞれのプラグインで止めてください。次からは先に「停止」を押してから解除してください";
            this.Debug.Line("実行", "⚠ " + msg);
            try
            {
                Svc.Chat.PrintError($"[AutoJobQuest] {msg}");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[AutoJobQuest] 解除時の案内を出せませんでした");
            }
        }

        this.addonRecorder.Dispose();

        // 自分が取った TextAdvance の外部制御だけは手放す（残すと TextAdvance が利用者の設定を無視し続け、
        // Questionable も制御を取りに行けない）。自分が取っていなければ何もしない。
        this.Ctx.TextAdvance.ReleaseControl();
        this.Ctx.YesAlready.Dispose();
        this.Ctx.MarketWatcher.Dispose();
        this.Ctx.Ownership.Dispose();
        this.GbrReflection.Dispose();
        this.Debug.Dispose();
    }
}
