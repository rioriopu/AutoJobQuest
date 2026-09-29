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

    /// <summary>
    /// 実行係が止まるところで、後始末をしている最中か（「停止」・失敗・完了・読み込みの解除）。
    /// 作業の後始末は、実行の途中で子の作業を取り替えるときにも呼ばれるので、止めた後に残す見張りはこれが true のときだけ置く。
    /// </summary>
    public bool Stopping { get; set; }

    /// <summary>戦闘の作業（CombatTask）の最中か。攻撃されたときの反撃（DefenseWatch）は、この間は何もしない（戦闘の作業が敵視リストの敵も倒す）。</summary>
    public bool CombatInProgress { get; set; }

    /// <summary>
    /// こちらが NPC と会話している最中か（話しかけの作業が話しかけた後）。この間の会話の窓はこちらのものなので、
    /// 「こちらの会話ではない会話の窓を閉じる」（ForeignTalk）は触らない。
    /// </summary>
    public bool InOwnConversation { get; set; }

    /// <summary>
    /// 自分（AutoJobQuest）が頼んだ移動の控え（反撃を始めたとき、自分の移動だけを止めるため）。
    /// 移動の作業（MoveToTask）が頼んだときに書き、終わったら消す。
    /// </summary>
    public Automation.OwnMovement OwnMove { get; } = new();

    /// <summary>マーケットの検索結果（件数）の通知。</summary>
    public required Automation.MarketBoardWatcher MarketWatcher { get; init; }

    /// <summary>自分の操作が開かせた画面の記録（自分が開いた画面だけを閉じる・押すため）。</summary>
    public required Automation.AddonOwnership Ownership { get; init; }
}
