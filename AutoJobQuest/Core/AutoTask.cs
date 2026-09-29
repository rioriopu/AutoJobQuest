using System;
using System.Collections.Generic;

namespace AutoJobQuest.Core;

/// <summary>1コマ進めた結果。</summary>
public enum TaskResult
{
    Running,
    Done,
    Failed,
}

/// <summary>
/// 毎フレーム1コマずつ進める作業の型。
///
/// 【開発規約】
///  ・固定時間待ってから次へ進まない。進む条件は必ずゲームの状態（所持数・画面・エリア）で確かめる。
///    時間は「これ以上待っても無駄」と諦める上限にだけ使う（<see cref="TimedOut"/>）。
///  ・成功は「減った AND 増えた」のように両側で確かめる。
///  ・他プラグインに頼んだことは、終わったとき（成功・失敗・中止のどれでも）<see cref="Cleanup"/> で必ず戻す。
///
/// 【時間の測り方】<see cref="Elapsed"/>・<see cref="PhaseElapsed"/> は「実際に作業できた時間」
/// （流れ全体が反撃・他者の会話の窓の処理で中断していた時間を引く：<see cref="WorkClock"/>）。
/// 送った購入の応答待ちなど、中断しても止めない期限には <see cref="WallElapsed"/>・<see cref="PhaseWallElapsed"/>（実際の時間）を使う。
/// どちらも単調に進む時計で測る（PC の時計を合わせ直しても逆行しない）。
/// </summary>
public abstract class AutoTask
{
    private TimeSpan startedAt;
    private TimeSpan phaseAt;
    private TimeSpan pausedAtStart;
    private TimeSpan pausedAtPhase;

    // 他者の画面（ショップ・確認窓など）が閉じるのを待った時間（作業時間から引く。独立した上限を持つ）
    private TimeSpan windowWaitTotal;
    private TimeSpan? windowWaitSince;

    /// <summary>他者の画面が開いたままのとき、続けて待つ上限。</summary>
    public static readonly TimeSpan WindowWaitLimit = TimeSpan.FromMinutes(5);

    /// <summary>画面に出す作業名。</summary>
    public abstract string Name { get; }

    /// <summary>いま何をしているか（画面表示用）。</summary>
    public string Status { get; protected set; } = string.Empty;

    /// <summary>失敗したときの理由。</summary>
    public string? FailReason { get; private set; }

    public bool Started { get; private set; }

    /// <summary>始まってから、実際に作業できた時間（中断していた時間を引く）。</summary>
    public TimeSpan Elapsed => WorkClock.Now - this.startedAt - (WorkClock.PausedTotal - this.pausedAtStart);

    /// <summary>始まってからの実際の時間（中断も含む）。</summary>
    public TimeSpan WallElapsed => WorkClock.Now - this.startedAt;

    /// <summary>今の段に入ってから、実際に作業できた時間（中断していた時間を引く）。</summary>
    protected TimeSpan PhaseElapsed => WorkClock.Now - this.phaseAt - (WorkClock.PausedTotal - this.pausedAtPhase);

    /// <summary>今の段に入ってからの実際の時間（中断も含む。送った操作の応答待ちなど、中断しても止めない期限に使う）。</summary>
    protected TimeSpan PhaseWallElapsed => WorkClock.Now - this.phaseAt;

    /// <summary>作業時間から、他者の画面を待った時間も引いたもの（移動などの上限に使う）。</summary>
    protected TimeSpan WorkElapsed
    {
        get
        {
            var w = this.Elapsed - this.windowWaitTotal - (this.windowWaitSince is { } s ? WorkClock.Now - s : TimeSpan.Zero);
            return w < TimeSpan.Zero ? TimeSpan.Zero : w;
        }
    }

    /// <summary>
    /// ショップ・確認窓など、介入してはいけない画面が開いていれば待つ（true：待っている。呼び出し側は Running を返す）。
    /// 待った時間は <see cref="WorkElapsed"/> に数えず、続けて <see cref="WindowWaitLimit"/> を超えたら failReason に理由を入れる
    /// （具体的な画面の名前と解消の仕方を出す。他者の画面は閉じない）。
    /// </summary>
    protected bool WaitForWindows(out string? failReason)
        => this.WaitForWindows(Automation.GameUi.OpenBlockingAddon(), out failReason);

    /// <summary>上と同じ。開いている画面の名前を外から渡す（ゲームを起動せずに試すため）。</summary>
    protected bool WaitForWindows(string? openWindow, out string? failReason)
    {
        failReason = null;
        if (openWindow == null)
        {
            if (this.windowWaitSince is { } since)
                this.windowWaitTotal += WorkClock.Now - since;
            this.windowWaitSince = null;
            return false;
        }

        this.windowWaitSince ??= WorkClock.Now;
        var waited = WorkClock.Now - this.windowWaitSince.Value;
        var label = Automation.GameUi.WindowLabel(openWindow);
        this.Status = $"{label}の画面が開いているので待っています（閉じると続けます。{waited.TotalSeconds:0}/{WindowWaitLimit.TotalSeconds:0}秒）";
        if (waited > WindowWaitLimit)
            failReason = $"{label}の画面が {WindowWaitLimit.TotalMinutes:0} 分開いたままなので進めません。その画面を閉じてから、もう一度始めてください";
        return true;
    }

    /// <summary>実行係から呼ばれる。初回だけ <see cref="OnStart"/> を呼ぶ。</summary>
    public TaskResult Step(TaskContext ctx)
    {
        if (!this.Started)
        {
            this.Started = true;
            this.startedAt = WorkClock.Now;
            this.phaseAt = this.startedAt;
            this.pausedAtStart = WorkClock.PausedTotal;
            this.pausedAtPhase = this.pausedAtStart;
            var first = this.OnStart(ctx);
            if (first != TaskResult.Running)
                return first;
        }

        return this.Tick(ctx);
    }

    /// <summary>最初の1回。Running 以外を返すと Tick を呼ばずに終わる。</summary>
    protected virtual TaskResult OnStart(TaskContext ctx) => TaskResult.Running;

    /// <summary>毎フレーム呼ばれる。</summary>
    protected abstract TaskResult Tick(TaskContext ctx);

    /// <summary>
    /// 終わったとき（成功・失敗・中止）に1回だけ呼ばれる。
    /// 他プラグインに頼んだこと（停止要求・設定の一時変更・移動）をここで戻す。
    /// </summary>
    public virtual void Cleanup(TaskContext ctx)
    {
    }

    /// <summary>段を切り替える（段ごとの経過時間を測り直す）。</summary>
    protected void NextPhase(string status)
    {
        this.phaseAt = WorkClock.Now;
        this.pausedAtPhase = WorkClock.PausedTotal;
        this.Status = status;
    }

    /// <summary>今の段が上限時間を過ぎたか。</summary>
    protected bool TimedOut(TimeSpan limit) => this.PhaseElapsed > limit;

    protected TaskResult Fail(string reason)
    {
        this.FailReason = reason;
        return TaskResult.Failed;
    }
}

/// <summary>
/// 子の作業を順に実行する作業。子は必要になったときに作る（前の子の結果を見てから中身を決めたいため）。
/// </summary>
public class SequenceTask : AutoTask
{
    private readonly string name;
    private readonly Queue<Func<TaskContext, AutoTask?>> factories;
    private AutoTask? current;

    public SequenceTask(string name, IEnumerable<Func<TaskContext, AutoTask?>> steps)
    {
        this.name = name;
        this.factories = new Queue<Func<TaskContext, AutoTask?>>(steps);
    }

    public override string Name => this.name;

    /// <summary>いま動いている子。</summary>
    public AutoTask? Current => this.current;

    /// <summary>後ろに作業を足す。</summary>
    public void Enqueue(Func<TaskContext, AutoTask?> factory) => this.factories.Enqueue(factory);

    protected override TaskResult Tick(TaskContext ctx)
    {
        while (true)
        {
            if (this.current == null)
            {
                if (this.factories.Count == 0)
                    return TaskResult.Done;

                this.current = this.factories.Dequeue()(ctx);
                if (this.current == null)
                    continue; // やることが無かった段は飛ばす

                ctx.Log.Write("段", $"開始: {this.current.Name}");
            }

            TaskResult r;
            try
            {
                r = this.current.Step(ctx);
            }
            catch (Exception ex)
            {
                ctx.Log.Warn("段", $"{this.current.Name} で例外: {ex.GetType().Name}: {ex.Message}");
                Svc.Log.Error(ex, $"[AutoJobQuest] {this.current.Name}");
                DebugLog.Current?.Exception("段", $"{this.current.Name} で例外", ex);
                r = TaskResult.Failed;
            }

            this.Status = $"{this.current.Name}: {this.current.Status}";

            if (r == TaskResult.Running)
                return TaskResult.Running;

            this.SafeCleanup(ctx, this.current);

            if (r == TaskResult.Failed)
            {
                var reason = this.current.FailReason ?? "理由不明";
                ctx.Log.Warn("段", $"失敗: {this.current.Name} … {reason}");
                this.current = null;
                return this.Fail($"{this.name} / {reason}");
            }

            ctx.Log.Write("段", $"完了: {this.current.Name}");
            this.current = null;

            // 1フレームに1段だけ進める（画面やゲームの状態が次のフレームで反映されることが多いため）
            return TaskResult.Running;
        }
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.current != null)
        {
            this.SafeCleanup(ctx, this.current);
            this.current = null;
        }
    }

    private void SafeCleanup(TaskContext ctx, AutoTask task)
    {
        try
        {
            task.Cleanup(ctx);
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("段", $"{task.Name} の後始末で例外: {ex.Message}");
        }
    }
}
