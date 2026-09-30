using System;
using System.Linq;

namespace AutoJobQuest.Core;

/// <summary>
/// 利用者への確認（はい／いいえ）。作業は <see cref="Ask"/> で質問を出し、<see cref="Answer"/> が
/// 入るまで待つ。画面側（ConfirmWindow）が質問を表示して答えを書き込む。
/// </summary>
public sealed class ConfirmService
{
    private readonly object gate = new();

    /// <summary>いま出している質問。無ければ null。</summary>
    public string? Question { get; private set; }

    /// <summary>質問の題名。</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>答え。null は未回答。</summary>
    public bool? Answer { get; private set; }

    private int serial;

    /// <summary>質問を出す。戻り値の番号で、答えがどの質問のものかを確かめる。</summary>
    public int Ask(string title, string question)
    {
        lock (this.gate)
        {
            this.serial++;
            this.Title = title;
            this.Question = question;
            this.Answer = null;
            DebugLog.Current?.Block("確認", $"確認窓を出しました（{this.serial}）：{title}", question);
            return this.serial;
        }
    }

    /// <summary>番号の質問に答えが入っていれば返す。</summary>
    public bool? Poll(int ticket)
    {
        lock (this.gate)
            return ticket == this.serial ? this.Answer : null;
    }

    /// <summary>画面から答えを書き込む。</summary>
    public void Reply(bool yes)
    {
        lock (this.gate)
        {
            if (this.Question == null)
                return;
            this.Answer = yes;
            DebugLog.Current?.Line("確認", $"確認窓（{this.serial}）「{this.Title}」に「{(yes ? "はい" : "いいえ")}」が選ばれました");
            this.Question = null;
        }
    }

    /// <summary>質問を取り下げる（止めたときなど）。</summary>
    public void Withdraw()
    {
        lock (this.gate)
        {
            this.Question = null;
            this.Answer = null;
            this.serial++;
        }
    }
}

/// <summary>
/// 作業を1本走らせる実行係。開始・停止・毎フレームの進行を受け持つ。
/// </summary>
public sealed class Runner
{
    private readonly TaskContext ctx;
    private AutoTask? root;
    private string? stopReason;
    private bool stopByUser;

    public Runner(TaskContext ctx)
    {
        this.ctx = ctx;
    }

    public bool IsRunning => this.root != null;

    /// <summary>始まったとき（記録の開始に使う）。</summary>
    public Action<AutoTask>? Started { get; set; }

    /// <summary>終わったとき（結果の文言, 失敗したか）。失敗なら記録係が状態の写しを書き出す。</summary>
    public Action<string, bool>? Finished { get; set; }

    /// <summary>いま動いている作業（画面表示用）。</summary>
    public AutoTask? Root => this.root;

    /// <summary>最後に終わったときの結果（画面表示用）。</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>最後に終わったときの結果の分類。まだ一度も終わっていなければ null。</summary>
    public RunOutcome? LastOutcome { get; private set; }

    /// <summary>最後に終わったときに、次に利用者がすること（無ければ空）。</summary>
    public string LastNextAction { get; private set; } = string.Empty;

    /// <summary>終わったときの事実を集める（Services が設定する。無ければ「残りなし」とみなす）。</summary>
    public Func<RunFacts>? Facts { get; set; }

    /// <summary>
    /// 新しく始めてはいけない理由（無ければ null）。前回止めたときの後始末（例：Artisan が遅れて製作を始めないかの見張り）が
    /// 残っている間は始めない（見張りは止まっている間しか動かないので、すぐ始めると見張りが休み、
    /// 前回の製作が遅れて始まりうる。逆に古い見張りが新しい実行の製作を止めることもありうる）。
    /// </summary>
    public string? StartBlocker()
    {
        if (this.ctx.AfterStop.Count == 0)
            return null;
        return $"前回止めたときの後始末が終わっていません（{string.Join("、", this.ctx.AfterStop.Select(a => a.Name))}）。終わるまで待ってください";
    }

    public void Start(AutoTask task)
    {
        if (this.root != null)
        {
            this.ctx.Log.Warn("実行", "すでに動いています");
            return;
        }

        if (this.StartBlocker() is { } blocked)
        {
            this.ctx.Log.Warn("実行", blocked);
            Svc.Chat.Print($"[AutoJobQuest] 開始できません：{blocked}");
            return;
        }

        this.stopReason = null;
        this.stopByUser = false;
        this.root = task;
        this.LastResult = string.Empty;
        var runId = RunIds.BeginRun();
        this.Started?.Invoke(task);
        this.ctx.Log.Write("実行", $"開始: {task.Name}（実行 {runId}・AutoJobQuest {RunIds.Version}）");
    }

    /// <summary>止める。次のフレームで後始末をして終わる。</summary>
    /// <param name="reason">止める理由（記録と画面に出す）。</param>
    /// <param name="byUser">利用者の操作で止めたか（停止ボタン・コマンド・確認で「いいえ」）。false は例外などで止めたとき（結果は「失敗」）。</param>
    public void RequestStop(string reason, bool byUser = true)
    {
        if (this.root == null)
            return;

        this.stopReason = reason;
        this.stopByUser = byUser;
    }

    /// <summary>
    /// その場で止めて後始末まで済ませる（次のフレームを待たない）。読み込みの解除（更新・無効化）のときに使う
    /// （解除の後はフレームが来ないため）。フレームワークのスレッドから呼ぶこと。
    /// </summary>
    public void StopNow(string reason)
    {
        if (this.root == null)
            return;

        this.Finish($"止めました（{reason}）", false, userStopped: true);
    }

    public void Tick()
    {
        if (this.root == null)
            return;

        if (this.stopReason != null)
        {
            // 例外などで止めたとき（利用者の操作でない）は「失敗」として扱う（失敗の報告も書き出す）
            this.Finish($"止めました（{this.stopReason}）", !this.stopByUser, userStopped: this.stopByUser);
            return;
        }

        TaskResult r;
        try
        {
            r = this.root.Step(this.ctx);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoJobQuest] 実行中の例外");
            DebugLog.Current?.Exception("実行", "実行中の例外", ex);
            this.Finish($"例外で止まりました: {ex.GetType().Name}: {ex.Message}", true);
            return;
        }

        switch (r)
        {
            case TaskResult.Done:
                this.Finish("すべて終わりました", false);
                break;
            case TaskResult.Failed:
                this.Finish($"失敗で止まりました: {this.root.FailReason}", true);
                break;
        }
    }

    private void Finish(string result, bool failed, bool userStopped = false)
    {
        // 失敗の写しは後始末の前に取る（後始末で状態が変わるため）
        if (failed)
        {
            try
            {
                this.Finished?.Invoke(result, true);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[AutoJobQuest] 失敗の記録に失敗");
            }
        }

        var task = this.root;
        this.root = null;

        if (task != null)
        {
            this.ctx.Stopping = true;
            try
            {
                task.Cleanup(this.ctx);
            }
            catch (Exception ex)
            {
                this.ctx.Log.Warn("実行", $"後始末で例外: {ex.Message}");
            }
            finally
            {
                this.ctx.Stopping = false;
            }
        }

        this.ctx.Confirm.Withdraw();

        // 結果を分ける。後始末の後に集めるので、戻せなかったものがここに出る
        RunFacts facts;
        try
        {
            facts = this.Facts?.Invoke() ?? new RunFacts(false, []);
        }
        catch (Exception ex)
        {
            this.ctx.Log.Warn("実行", $"終わったときの状態を集められませんでした: {ex.Message}");
            facts = new RunFacts(false, ["状態を集められなかった"]);
        }

        var excluded = task is IOutcomeHint hint && hint.HasExclusions;
        var outcome = RunOutcomes.Classify(failed, userStopped, excluded, facts);
        var next = RunOutcomes.NextAction(outcome, facts);
        this.LastOutcome = outcome;
        this.LastNextAction = next;
        result = $"【{RunOutcomes.Label(outcome)}】{result}";
        this.LastResult = result;
        this.ctx.Log.Write("実行", $"{result}（停止理由コード {RunOutcomes.Code(outcome)}・実行 {RunIds.RunId}）");
        if (next.Length > 0)
            this.ctx.Log.Write("実行", $"次にすること：{next}");
        Svc.Chat.Print($"[AutoJobQuest] {result}");
        if (!failed)
            this.Finished?.Invoke(result, false);
        else
            DebugLog.Current?.EndRun(result);
        RunIds.EndRun();
    }
}
