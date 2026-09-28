using System;
using System.Collections.Generic;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;

namespace AutoJobQuest.Core;

/// <summary>作業が使う道具一式。</summary>
public sealed class TaskContext
{
    public required Configuration Config { get; init; }

    public required RunLog Log { get; init; }

    public required ConfirmService Confirm { get; init; }

    public required GameDataCache Data { get; init; }

    public required ArtisanIpc Artisan { get; init; }

    public required QuestionableIpc Questionable { get; init; }

    public required LifestreamIpc Lifestream { get; init; }

    public required VnavmeshIpc Navmesh { get; init; }

    public required GatherBuddyIpc GatherBuddy { get; init; }

    public required GbrOperations Gbr { get; init; }

    public required RotationSolverIpc Rotation { get; init; }

    public required AutoHookIpc AutoHook { get; init; }

    public required TextAdvanceIpc TextAdvance { get; init; }

    public required YesAlreadyIpc YesAlready { get; init; }

    /// <summary>
    /// 止めたあとにしばらく見張る処理（例：Artisan が止めた直後に遅れて製作を始めたら止める）。
    /// 実行係が止まっている間だけ、毎フレーム呼ばれる。true を返すか期限が来たら外れる。
    /// </summary>
    public List<(string Name, DateTime Until, Func<bool> Step)> AfterStop { get; } = [];

    /// <summary>マーケットの検索結果（件数）の通知。</summary>
    public required Automation.MarketBoardWatcher MarketWatcher { get; init; }

    /// <summary>自分の操作が開かせた画面の記録（自分が開いた画面だけを閉じる・押すため）。</summary>
    public required Automation.AddonOwnership Ownership { get; init; }
}
