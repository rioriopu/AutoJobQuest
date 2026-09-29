using System;
using AutoJobQuest.Core;

namespace AutoJobQuest.Automation;

/// <summary>
/// こちらの会話ではない会話の窓（Talk）を、状況を確かめてから閉じる（表示されている会話の窓は、
/// 状況を確認してから原則として閉じる。以前は送らずに待ち、消えなければ止まっていた）。流れ全体（JobQuestFlow）が毎フレーム呼ぶ。
///
/// 状況の確かめ（ForeignTalkPolicy）：
///  ・Questionable が動いている → その会話はクエストの進行の一部。触らない（Questionable と TextAdvance が送る）。
///  ・出てすぐ（2秒）→ ほかの操作が送って消えるかを見る。
///  ・TextAdvance をほかのプラグインが動かしている → そちらが送るはずなので10秒まで待つ。
///  ・それでも残っていれば閉じる（会話を 0.3 秒おきに送る）。30秒送っても消えなければ止める。
/// 最初に見たとき、会話の窓の中身（画面の値）と話し手（いまのターゲット）を記録に残す。
/// こちらの会話（話しかけの作業が話しかけた後・手動の報告で TextAdvance を借りている間）は、呼び出し側が除く。
/// 選択肢・確認窓は押さない（会話の窓だけ）。
/// </summary>
public sealed unsafe class ForeignTalk
{
    public enum Result
    {
        /// <summary>会話の窓は出ていない（または Questionable に任せる）。流れを進めてよい。</summary>
        None,

        /// <summary>確かめている・閉じている。流れの次の作業は進めない。</summary>
        Busy,

        /// <summary>閉じられない。止める（理由は status）。</summary>
        Failed,
    }

    private DateTime seenAt = DateTime.MinValue;
    private DateTime? closingSince;
    private DateTime lastClick = DateTime.MinValue;
    private bool logged;

    public Result Tick(TaskContext ctx, out string status)
    {
        status = string.Empty;
        if (!GameUi.IsReady("Talk", out var talk))
        {
            if (this.closingSince != null)
                ctx.Log.Write("会話", "こちらのものではない会話の窓を閉じました");
            this.Reset();
            return Result.None;
        }

        var now = DateTime.UtcNow;
        if (this.seenAt == DateTime.MinValue)
            this.seenAt = now;

        var questionable = ctx.Questionable.IsRunning() == true;
        var others = !questionable && !ctx.TextAdvance.OwnsControl && ctx.TextAdvance.IsInExternalControl() == true;
        if (!this.logged && !questionable)
        {
            this.logged = true;
            var speaker = Svc.Targets.Target?.Name.TextValue;
            ctx.Log.Warn("会話", $"こちらの会話ではない会話の窓が出ています（話し手：{(string.IsNullOrEmpty(speaker) ? "不明" : speaker)}）。状況を確かめてから閉じます");
            DebugLog.Current?.Block("会話", "こちらのものではない会話の窓", AddonRecorder.Describe(talk));
        }

        switch (ForeignTalkPolicy.Decide(questionable, others, now - this.seenAt, this.closingSince is { } c ? now - c : null))
        {
            case ForeignTalkPolicy.Verdict.LeaveToQuestionable:
                this.Reset();
                return Result.None;

            case ForeignTalkPolicy.Verdict.Watch:
                status = "会話の窓が出ています（ほかの操作で閉じられるかを見ています）";
                return Result.Busy;

            case ForeignTalkPolicy.Verdict.WaitOthers:
                status = "会話の窓が出ています（TextAdvance を動かしているほかのプラグインが送るのを待っています）";
                return Result.Busy;

            case ForeignTalkPolicy.Verdict.GiveUp:
                status = $"こちらの会話ではない会話の窓を {ForeignTalkPolicy.CloseLimit.TotalSeconds:0} 秒送っても閉じられませんでした。手で閉じてからやり直してください";
                return Result.Failed;

            default:
                this.closingSince ??= now;
                if (now - this.lastClick >= TimeSpan.FromMilliseconds(300))
                {
                    this.lastClick = now;
                    GameUi.AdvanceTalk();
                }

                status = "こちらの会話ではない会話の窓を閉じています";
                return Result.Busy;
        }
    }

    public void Reset()
    {
        this.seenAt = DateTime.MinValue;
        this.closingSince = null;
        this.logged = false;
    }
}
