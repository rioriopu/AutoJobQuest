using System;
using System.Collections.Generic;

namespace AutoJobQuest.Core;

/// <summary>
/// 実行の結果の分け方（「全対象完了」「前提未達のため一部除外して完了」「失敗」「利用者停止」「取引結果不明」
/// 「後始末確認待ち」に分け、復旧に使える形にする）。
/// </summary>
public enum RunOutcome
{
    /// <summary>選んだジョブクエをすべて終えた。</summary>
    AllDone,

    /// <summary>前提が未達のクエストを外して、残りを終えた。</summary>
    DoneWithExclusions,

    /// <summary>失敗で止まった。</summary>
    Failed,

    /// <summary>利用者が止めた（停止ボタン・コマンド・確認で「いいえ」・読み込みの解除）。</summary>
    UserStopped,

    /// <summary>マーケットの購入の結果が分からないまま止まった（取引の控えが残っている）。</summary>
    PurchaseUnknown,

    /// <summary>他のプラグインに頼んだことを戻したと、まだ確かめられていない（RSR・GBR・Artisan の見張り）。</summary>
    CleanupPending,
}

/// <summary>終わったときの事実（Services が集める）。</summary>
/// <param name="PurchaseUnknown">結果の分からない購入の控えが残っているか。</param>
/// <param name="CleanupPending">戻したと確かめられていない後始末の名前（無ければ空）。</param>
public readonly record struct RunFacts(bool PurchaseUnknown, IReadOnlyList<string> CleanupPending);

/// <summary>作業が「前提未達で一部を外した」ことを実行係に伝える口（結果の分類に使う）。</summary>
public interface IOutcomeHint
{
    /// <summary>前提が未達で外したクエストがあったか。</summary>
    bool HasExclusions { get; }
}

/// <summary>結果の分類と、画面・記録に出す文言。</summary>
public static class RunOutcomes
{
    /// <summary>
    /// 結果を1つに決める。重い順：取引結果不明（ギルに関わる）→ 失敗 → 後始末確認待ち（次の実行や利用者の操作に響く）
    /// → 利用者停止 → 一部除外して完了 → 全対象完了。
    /// </summary>
    /// <param name="failed">失敗・例外で終わったか。</param>
    /// <param name="userStopped">利用者が止めたか（読み込みの解除を含む）。</param>
    /// <param name="excluded">前提が未達で外したクエストがあるか。</param>
    /// <param name="facts">終わったときの事実。</param>
    public static RunOutcome Classify(bool failed, bool userStopped, bool excluded, RunFacts facts)
    {
        if (facts.PurchaseUnknown)
            return RunOutcome.PurchaseUnknown;
        if (failed)
            return RunOutcome.Failed;
        if (facts.CleanupPending.Count > 0)
            return RunOutcome.CleanupPending;
        if (userStopped)
            return RunOutcome.UserStopped;
        return excluded ? RunOutcome.DoneWithExclusions : RunOutcome.AllDone;
    }

    /// <summary>画面に出す名前。</summary>
    public static string Label(RunOutcome o) => o switch
    {
        RunOutcome.AllDone => "全対象完了",
        RunOutcome.DoneWithExclusions => "前提未達のため一部除外して完了",
        RunOutcome.Failed => "失敗",
        RunOutcome.UserStopped => "利用者停止",
        RunOutcome.PurchaseUnknown => "取引結果不明",
        RunOutcome.CleanupPending => "後始末確認待ち",
        _ => o.ToString(),
    };

    /// <summary>記録に出す停止理由コード（記録を検索しやすいように、変えない短い英字）。</summary>
    public static string Code(RunOutcome o) => o switch
    {
        RunOutcome.AllDone => "DONE",
        RunOutcome.DoneWithExclusions => "DONE-EXCL",
        RunOutcome.Failed => "FAIL",
        RunOutcome.UserStopped => "STOP",
        RunOutcome.PurchaseUnknown => "MB-UNKNOWN",
        RunOutcome.CleanupPending => "CLEANUP",
        _ => "?",
    };

    /// <summary>次に利用者がすること（無ければ空）。</summary>
    public static string NextAction(RunOutcome o, RunFacts facts) => o switch
    {
        RunOutcome.PurchaseUnknown => "マーケットの購入の結果を、ゲームのカバンとギルで確かめてください。次に開始するときに、買えたかどうかを答える確認が出ます",
        RunOutcome.Failed => "止まった理由（上の文）と、ログの「失敗の報告」を確かめてから、もう一度始めてください",
        RunOutcome.CleanupPending => $"戻したと確かめられていないもの：{string.Join("、", facts.CleanupPending)}。止まっている間にこちらで確かめて戻します（戻せたら記録に出ます）",
        RunOutcome.DoneWithExclusions => "前提のクエスト（上の一覧）を済ませてから、もう一度始めると残りを進めます",
        _ => string.Empty,
    };
}

/// <summary>
/// 実行と操作の番号（同じ操作を記録で追えるように）。実行は開始ごとに1つ、操作は作業（AutoTask）が始まるごとに1つ。
/// 記録ファイルの各行の頭に「[実行#操作]」を付ける（<see cref="DebugLog.Line"/>）。
/// </summary>
public static class RunIds
{
    private static long nextOp;

    /// <summary>いまの実行の番号（動いていなければ "-"）。</summary>
    public static string RunId { get; private set; } = "-";

    /// <summary>いま進めている操作の番号（入れ子の作業では、いちばん内側）。</summary>
    public static long CurrentOp { get; set; }

    /// <summary>このプラグインの版。</summary>
    public static string Version => typeof(RunIds).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>記録の行の頭に付ける印（動いていなければ空）。</summary>
    public static string Tag => RunId == "-" ? string.Empty : $"[{RunId}#{CurrentOp}] ";

    /// <summary>実行を始める。</summary>
    public static string BeginRun()
    {
        RunId = $"{DateTime.Now:MMdd-HHmmss}";
        nextOp = 0;
        CurrentOp = 0;
        return RunId;
    }

    /// <summary>実行を終える。</summary>
    public static void EndRun()
    {
        RunId = "-";
        CurrentOp = 0;
    }

    /// <summary>新しい操作の番号。</summary>
    public static long NewOp() => ++nextOp;
}
