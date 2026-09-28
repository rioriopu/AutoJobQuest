using System;

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

    public Runner(TaskContext ctx)
    {
        this.ctx = ctx;
    }

    public bool IsRunning => this.root != null;

    /// <summary>いま動いている作業（画面表示用）。</summary>
    public AutoTask? Root => this.root;

    /// <summary>最後に終わったときの結果（画面表示用）。</summary>
    public string LastResult { get; private set; } = string.Empty;

    public void Start(AutoTask task)
    {
        if (this.root != null)
        {
            this.ctx.Log.Warn("実行", "すでに動いています");
            return;
        }

        this.stopReason = null;
        this.root = task;
        this.LastResult = string.Empty;
        this.ctx.Log.Write("実行", $"開始: {task.Name}");
    }

    /// <summary>止める。次のフレームで後始末をして終わる。</summary>
    public void RequestStop(string reason)
    {
        if (this.root == null)
            return;

        this.stopReason = reason;
    }

    public void Tick()
    {
        if (this.root == null)
            return;

        if (this.stopReason != null)
        {
            this.Finish($"止めました（{this.stopReason}）");
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
            this.Finish($"例外で止まりました: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        switch (r)
        {
            case TaskResult.Done:
                this.Finish("すべて終わりました");
                break;
            case TaskResult.Failed:
                this.Finish($"失敗で止まりました: {this.root.FailReason}");
                break;
        }
    }

    private void Finish(string result)
    {
        var task = this.root;
        this.root = null;

        if (task != null)
        {
            try
            {
                task.Cleanup(this.ctx);
            }
            catch (Exception ex)
            {
                this.ctx.Log.Warn("実行", $"後始末で例外: {ex.Message}");
            }
        }

        this.ctx.Confirm.Withdraw();
        this.LastResult = result;
        this.ctx.Log.Write("実行", result);
        Svc.Chat.Print($"[AutoJobQuest] {result}");
    }
}
