using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Artisan の見張りの長さ（止めたとき：CraftOneTask.Cleanup と同じ）。</summary>
    private static readonly TimeSpan ArtisanWatchLength = TimeSpan.FromSeconds(30);
    private DateTime nextLeftoverTry = DateTime.MinValue;
    private DateTime nextRsrLeftoverTry = DateTime.MinValue;
    private DateTime nextConsumableTry = DateTime.MinValue;

    // 特殊通貨の控えをクライアントの表で確かめる次の時刻（ログイン中、1分おき）
    private DateTime nextCurrencyRefresh = DateTime.MinValue;

    // ジャーナルで非表示にしたクエストを元の状態へ戻す（実行していない間。JournalHide）
    private readonly Automation.JournalRestorer journalRestorer = new();
    private DateTime nextJournalRestore = DateTime.MinValue;
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
            Rotation = new RotationSolverIpc(config),
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
        // 終わったときの事実（結果の分類）。後始末の後に集める
        this.Runner.Facts = () =>
        {
            var pending = new List<string>();
            if (this.Ctx.AfterStop.Count > 0)
                pending.AddRange(this.Ctx.AfterStop.Select(a => a.Name));
            // RSR の戻しは、実行していない間に10秒おきに確かめる作り。終わった瞬間は必ず控えが残っているので、ここで一度確かめる
            // （RSR の IPC は同期で、戻した直後に読めば分かる。以前は戦闘を使った実行が成功しても必ず「後始末確認待ち」になった）
            for (var i = 0; i < 2 && this.Ctx.Rotation.RestorePending && this.Ctx.Rotation.IsLoaded; i++)
            {
                try
                {
                    if (this.Ctx.Rotation.RestoreLeftover() is { } done)
                        this.Log.Write("RSR", done);
                }
                catch (Exception ex)
                {
                    this.Debug.Exception("RSR", "終わったときの RSR の戻しの確かめ", ex);
                    break;
                }
            }

            if (this.Ctx.Rotation.RestorePending)
                pending.Add("RSR のモード・範囲攻撃の設定");
            if (this.Ctx.Gbr.HasLeftovers)
                pending.Add("GBR の設定・自動採集リスト");
            if (this.Config.ArtisanTempConsumableRecipes.Count > 0)
                pending.Add($"Artisan の食事・薬の一時指定（{this.Config.ArtisanTempConsumableRecipes.Count} レシピ）");
            return new RunFacts(this.Config.PendingPurchase != null, pending);
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

        // 前の読み込みを解除（更新・無効化）で止めたとき、Artisan の見張りが残っていたなら置き直す
        // （解除の後はフレームが来ないので見張れなかった。見張りが終わるまで開始は受け付けない：Runner.StartBlocker）。
        // 置き直すのは、元の見張りの期限（控えてある）までの残りだけ。長く置くと、その間に利用者が
        // 自分で始めた Artisan の製作まで止めてしまう（以前は5分以内なら30秒置き直していた）
        if (config.PendingArtisanWatchUtc is { } pending)
        {
            config.PendingArtisanWatchUtc = null;
            config.Save();
            var left = pending - DateTime.UtcNow;
            if (left > ArtisanWatchLength)
                left = ArtisanWatchLength; // 控えが壊れていても30秒より長くしない
            if (left > TimeSpan.Zero)
            {
                this.Ctx.AfterStop.Add(Automation.CraftOneTask.ArtisanStopWatch(this.Ctx.Artisan, left));
                log.Write("見張り", $"前の読み込みで止めた Artisan の製作が遅れて始まらないか、残りの {left.TotalSeconds:0} 秒見張ります");
            }
        }
    }

    private readonly RetainerControl retainerControl = new();
    private DateTime nextRetainerRestore;

    public bool InspectionRequested { get; set; }
    public Planning.PreflightSession? Inspection { get; private set; }

    public void Tick()
    {
        // 経路探索を「頼んだのと同じフレームで取り消さない」ためのフレームの番号（OwnPath）
        Automation.OwnPath.Frame++;
        if (this.Runner.IsRunning)
        {
            this.Inspection?.Dispose();
            this.Inspection = null;
            this.InspectionRequested = false;
        }
        else
        {
            if (this.InspectionRequested)
            {
                this.InspectionRequested = false;
                this.Inspection?.Dispose();
                this.Inspection = new Planning.PreflightSession();
            }
            this.Inspection?.Tick(this.Ctx);
        }

        if (!this.Runner.IsRunning && this.Config.RetainerSuppressionPendingRestore && DateTime.UtcNow >= this.nextRetainerRestore)
        {
            this.nextRetainerRestore = DateTime.UtcNow.AddSeconds(10);
            this.retainerControl.Release(this.Config);
        }

        // GBR の一時変更（リスト・設定）が残っていれば、こちらが止まっていて GBR も止まっている間に戻す。
        // 戻せなかったとき（GBR が動いていた・読めなかった・例外）は10秒ごとにやり直す（控えが消えるまで）
        if (!this.Runner.IsRunning && DateTime.UtcNow >= this.nextLeftoverTry && Me.Available && this.Ctx.Gbr.HasLeftovers)
        {
            this.nextLeftoverTry = DateTime.UtcNow.AddSeconds(10);
            this.Ctx.Gbr.RestoreIfIdle(this.Ctx.GatherBuddy.IsAutoGatherEnabled(), this.Ctx.Gbr.VendorIsBusy());
        }

        // 前に RSR を Henched にした・範囲攻撃を Off にしたまま、戻ったと確かめられていなければ（読み込み直し・戻す命令の失敗・
        // 戻した直後）、実行していない間に確かめて、戻っていなければ戻す（10秒おきに試す）
        if (!this.Runner.IsRunning && this.Ctx.Rotation.RestorePending && DateTime.UtcNow >= this.nextRsrLeftoverTry && this.Ctx.Rotation.IsLoaded)
        {
            this.nextRsrLeftoverTry = DateTime.UtcNow.AddSeconds(10);
            try
            {
                if (this.Ctx.Rotation.RestoreLeftover() is { } done)
                {
                    this.Log.Write("RSR", done);
                    Svc.Chat.Print($"[AutoJobQuest] {done}");
                }
            }
            catch (Exception ex)
            {
                this.Debug.Exception("RSR", "戻せなかった Henched の戻し", ex);
            }
        }

        // こちらが一時的に「使わない」にした Artisan の食事・薬が戻っていなければ、止まっていて Artisan も空いている間に戻す（10秒おき）
        if (!this.Runner.IsRunning && this.Config.ArtisanTempConsumableRecipes.Count > 0 && DateTime.UtcNow >= this.nextConsumableTry
            && this.Ctx.Artisan.IsLoaded && this.Ctx.Artisan.IsBusy() == false)
        {
            this.nextConsumableTry = DateTime.UtcNow.AddSeconds(10);
            var n = this.Ctx.Artisan.RestoreLeftoverConsumables(this.Config);
            if (n > 0)
                this.Log.Write("Artisan", $"一時的に「使わない」にしていた食事・薬の指定を {n} レシピ分戻しました");
        }

        // 特殊通貨の控えを、クライアントの表で確かめて書き換える（違っていたら記録して保存）
        if (Me.Available && DateTime.UtcNow >= this.nextCurrencyRefresh)
        {
            this.nextCurrencyRefresh = DateTime.UtcNow.AddMinutes(1);
            try
            {
                if (Data.SpecialCurrency.RefreshFallback(this.Config.SpecialCurrencyFallback))
                    this.Config.Save();
            }
            catch (Exception ex)
            {
                this.Debug.Exception("通貨", "特殊通貨の控えの確かめ", ex);
            }
        }

        // 実行の間だけジャーナルで非表示にしたクエストを、止まっている間に元の状態へ戻す（1本ずつ間を空けて）
        if (!this.Runner.IsRunning && this.Config.JournalHiddenByMe.Count > 0 && Me.Available && DateTime.UtcNow >= this.nextJournalRestore)
        {
            this.nextJournalRestore = DateTime.UtcNow + Automation.JournalHide.SendSpacing;
            try
            {
                if (this.journalRestorer.Tick(this.Config) is { } msg)
                {
                    this.Log.Write("ジャーナル", msg);
                    if (msg.StartsWith("⚠", StringComparison.Ordinal))
                        Svc.Chat.Print($"[AutoJobQuest] {msg}");
                }
            }
            catch (Exception ex)
            {
                this.Debug.Exception("ジャーナル", "非表示にしたクエストの戻し", ex);
            }
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
        this.Runner.RequestStop($"例外: {ex.GetType().Name}", byUser: false);
    }

    /// <summary>今の状態をファイルに書き出す（画面のボタンから）。</summary>
    public string WriteSnapshotNow()
        => this.Debug.WriteSnapshot(StateSnapshot.Capture(this.Ctx, this.Runner));

    public void Dispose()
    {
        this.Inspection?.Dispose();
        // 【実行中に読み込みが解除されたとき（更新・無効化・再読み込み）】
        // その時点で止める。止め方は「停止」ボタンと同じ（各作業の後始末＝こちらが頼んだ処理だけを
        // 取り消す：こちらが ON にした GBR の自動採集・こちらが始めた NPC 購入・Questionable のこちらのクエスト・こちらが頼んだ
        // Artisan の製作・こちらが入れた RSR の Henched〔使う前のモードに戻す〕・こちらの移動・こちらが開いた画面）。
        // 利用者や他のプラグインが自分で動かしている処理には触れない（別のプラグインでは、
        // 再読み込みしただけで利用者が回していた相手の周回まで止まった。ここでは「自分が頼んだもの」に限るので当たらない）。
        // 止めた後は、もう一度読み込んで「ジョブクエ開始」を押せば続きから進む（進み具合はゲームから読み直すため）。
        //
        // ゲームの処理の流れ（フレームワークのスレッド）でないとき（ゲームの終了中など）は、ゲームに触れられないので止められない。
        // そのときは知らせるだけにする。GBR の一時変更（リスト・設定）は控えを保存してあり、次に読み込んだとき GBR が止まっていれば戻す。
        if (this.Runner.IsRunning)
        {
            string msg;
            if (Svc.Framework.IsInFrameworkUpdateThread && !Svc.Framework.IsFrameworkUnloading)
            {
                try
                {
                    this.Runner.StopNow("読み込みの解除（更新・無効化）");
                    msg = "読み込みの解除（更新・無効化）のため、自動動作を止めました。もう一度読み込んだら「ジョブクエ開始」で続きから進みます";
                }
                catch (Exception ex)
                {
                    this.Debug.Exception("実行", "解除時の停止", ex);
                    msg = $"読み込みの解除のとき止めきれませんでした（{ex.GetType().Name}: {ex.Message}）。GBR・Questionable・Artisan・RSR が動き続けていれば、それぞれで止めてください";
                }
            }
            else
            {
                msg = $"実行中（{this.Runner.Root?.Name}）に、ゲームの処理の外で読み込みが解除されたため止められませんでした。"
                      + "GBR・Questionable・Artisan・RSR が動き続けていれば、それぞれで止めてください";
            }

            this.Debug.Line("実行", "⚠ " + msg);
            try
            {
                Svc.Chat.Print($"[AutoJobQuest] {msg}");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[AutoJobQuest] 解除時の案内を出せませんでした");
            }
        }

        // 止めた後の見張り（Artisan が遅れて製作を始めないか）は、解除の後は動けない。次に読み込んだとき置き直す
        this.Safe("Artisan の見張りの引き継ぎ", () =>
        {
            // 控えるのは元の見張りの期限（解除した時刻から数え直すと、残りが数秒でも30秒に延びるため）
            var watch = this.Ctx.AfterStop.FindLast(a => a.Name == Automation.CraftOneTask.ArtisanWatchName);
            if (watch.Name != null)
            {
                this.Config.PendingArtisanWatchUtc = watch.Until;
                this.Config.Save();
            }
        });

        // 実行していなかったのに RSR へ頼んだことが残っている（前の停止で IPC が失敗した等）なら、ここで戻す。
        // ゲームの処理の流れのときだけ（RSR の切り替えはチャットに出るため）
        if (!this.Runner.IsRunning && Svc.Framework.IsInFrameworkUpdateThread && !Svc.Framework.IsFrameworkUnloading)
        {
            this.Safe("RSR の優先ターゲットを外す", this.Ctx.Rotation.ClearOwnPriorities);
            this.Safe("RSR のモードを戻す", this.Ctx.Rotation.ReleaseHenched);
            this.Safe("Artisan の食事・薬の一時指定を戻す", () =>
            {
                if (this.Ctx.Artisan.IsLoaded && this.Ctx.Artisan.IsBusy() == false)
                    this.Ctx.Artisan.RestoreLeftoverConsumables(this.Config);
                if (this.Config.ArtisanTempConsumableRecipes.Count > 0)
                    Svc.Chat.Print("[AutoJobQuest] Artisan の食事・薬は復元待ちです。次回読み込み時に再試行します");
            });
        }

        // 他プラグインの状態（GBR の ON/OFF・リスト・Questionable 等）は、上の停止の後始末でしか触らない。
        // 停止要求の共有データ（YesAlready）は自分の要求を外して手放す（残すと相手が止まったままになる）。
        // 1つが例外で落ちても、残りの解除（フック・登録の外し忘れ）を必ず行う
        this.Safe("画面の記録", this.addonRecorder.Dispose);

        // 自分が取った TextAdvance の外部制御だけは手放す（残すと TextAdvance が利用者の設定を無視し続け、
        // Questionable も制御を取りに行けない）。自分が取っていなければ何もしない。
        this.Safe("TextAdvance の外部制御", this.Ctx.TextAdvance.ReleaseControl);
        this.Safe("YesAlready の停止要求", this.Ctx.YesAlready.Dispose);
        this.Safe("マーケットの見張り", this.Ctx.MarketWatcher.Dispose);
        this.Safe("画面の持ち主の記録", this.Ctx.Ownership.Dispose);
        this.Safe("GBR の参照", this.GbrReflection.Dispose);
        this.Safe("記録ファイル", this.Debug.Dispose);
    }

    /// <summary>後始末を1つ行う。例外は記録して、次の後始末へ進む。</summary>
    private void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try
            {
                Svc.Log.Error(ex, $"[AutoJobQuest] 解除時の後始末（{what}）で例外");
            }
            catch
            {
                // 記録もできないときは何もしない（解除を止めない）
            }
        }
    }
}
