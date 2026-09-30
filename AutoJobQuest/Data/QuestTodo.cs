using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AutoJobQuest.Data;

/// <summary>
/// 日誌の手順（TODO_NN）に✓が付いているかを、ゲーム自身に判定させる（同じ段で複数の相手に渡すクエストで、どの相手に渡し済みか）。
///
/// 【なぜゲームに聞くか】✓の条件はクエストのスクリプトの IsTodoChecked に書かれていて（例：Q65677 の手順1＝UI8AL ≥ 1、手順2＝UI8BH ≥ 1）、
/// クエストごとに変数の使い方が違う。こちらでビットの対応を持つと番号を抱えることになるので、ゲームの判定をそのまま使う
/// （QuestEventHandler.IsTodoChecked：FFXIVClientStructs の QuestEventHandler.cs）。
/// 【前例】HaselTweaks の AutoOpenRecipe は、受けているクエストのハンドラを EventFramework.GetEventHandlerById で取り、
/// null を確かめてから同じハンドラの GetTodoArgs を呼んでいる。
/// 【確かめること】取れたハンドラがクエストのもので、番号が合うか（Info.EventId の ContentId＝Quest・EntryId＝クエスト番号）。
/// 違う種類のハンドラを QuestEventHandler として読むと、構造体の外を読むおそれがあるため。
/// </summary>
public static unsafe class QuestTodo
{
    /// <summary>
    /// そのクエストの手順 todo に✓が付いているか。ハンドラが無い（読み込まれていない）・自分のキャラクターがいない・種類や番号が合わなければ null。
    /// フレームワークのスレッドから呼ぶ。
    /// </summary>
    public static bool? IsChecked(uint questRowId, byte todo)
    {
        if (Automation.GameMemory.Test is { } test)
            return test.TodoChecked(questRowId, todo);
        try
        {
            return IsCheckedCore(questRowId, todo);
        }
        catch (Exception)
        {
            // ゲームの関数の場所を解決できない（ゲームの更新の後など）。読めないとして今までの判断に戻す
            return null;
        }
    }

    private static bool? IsCheckedCore(uint questRowId, byte todo)
    {
        var ef = EventFramework.Instance();
        if (ef == null)
            return null;
        var handler = ef->GetEventHandlerById(questRowId);
        if (handler == null)
            return null;
        var id = handler->Info.EventId;
        if (id.ContentId != EventHandlerContent.Quest || id.EntryId != (ushort)(questRowId & 0xFFFF))
            return null;

        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return null;
        return ((QuestEventHandler*)handler)->IsTodoChecked((BattleChara*)me.Address, todo);
    }
}
