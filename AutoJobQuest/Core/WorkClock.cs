using System;
using System.Diagnostics;

namespace AutoJobQuest.Core;

/// <summary>
/// 作業の時間を測る時計（作業時間と、中断による待ち時間を分ける）。
///
///  ・<see cref="Now"/>：単調に進む時計（Stopwatch）。PC の時計を合わせ直しても戻らない（期限が逆行しない）。
///  ・<see cref="PausedTotal"/>：流れ全体が「中断」していた時間の合計（反撃・他者の会話の窓の処理で、子の作業を進めなかった間）。
///    作業の上限の時間（移動4分・テレポ2分など）は「実際に作業できた時間」で測るので、この分を引く
///    （以前は反撃や会話の窓の処理で待った時間も上限に入り、待ったあと直ぐに時間切れになりえた）。
///  ・中断の理由（<see cref="PauseReason"/>）は画面に出す。
/// 送った購入の応答待ち・結果の照合の期限は、中断しても止めない（実際の時間で測る：AutoTask.PhaseWallElapsed）。
/// 実行係は1つなので、時計も1つ（静的）。
/// </summary>
public static class WorkClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static TimeSpan pausedTotal;
    private static TimeSpan? pausedAt;

    /// <summary>試験用：時計の今を差し替える（null なら本物の時計。本番では使わない）。</summary>
    public static Func<TimeSpan>? TestNow { get; set; }

    /// <summary>単調に進む時計の今。</summary>
    public static TimeSpan Now => TestNow?.Invoke() ?? Watch.Elapsed;

    /// <summary>中断していた時間の合計（いま中断中ならその分も含む）。</summary>
    public static TimeSpan PausedTotal => pausedTotal + (pausedAt is { } p ? Now - p : TimeSpan.Zero);

    /// <summary>いま中断しているなら、その理由（していなければ null）。</summary>
    public static string? PauseReason { get; private set; }

    /// <summary>中断を始める／続ける（理由は画面用に上書きする）。</summary>
    public static void Pause(string reason)
    {
        pausedAt ??= Now;
        PauseReason = reason;
    }

    /// <summary>中断を終える（中断していなければ何もしない）。</summary>
    public static void Resume()
    {
        if (pausedAt is { } p)
            pausedTotal += Now - p;
        pausedAt = null;
        PauseReason = null;
    }

}
