using System;
using System.Collections.Generic;
using AutoJobQuest.Core;
using Dalamud.Game.Chat;

namespace AutoJobQuest.Automation;

/// <summary>
/// 実行の間のチャットとゲームの記録（LogMessage）を、記録のファイルに残す。
/// 受注できない理由・製作や採集の結果・「どこかに魚影が出現したようだ」などのゲームの知らせは、チャットにしか出ないことがあるため。
/// ほかのプレイヤーの会話（Say・パーティ・リンクシェル・フリーカンパニーなど）と、戦闘のダメージ・回復・効果の行は残さない。
/// ゲームの記録（LogMessage）は番号も残す（どの知らせかを番号で引けるように）。戦闘中は残さない（数が多いため）。
/// </summary>
public sealed class ChatRecorder : IDisposable
{
    /// <summary>
    /// 残さないチャットの種類（XivChatType の値）：Say(10)〜Yell(30)・CrossParty(32)・PvPTeam(36)・CrossLinkShell1(37)・CrossLinkShell2〜8(101〜107)、
    /// 戦闘の Damage(41)・Miss(42)・Healing(45)・GainBuff(46)・GainDebuff(47)・LoseBuff(48)・LoseDebuff(49)。
    /// </summary>
    private static readonly HashSet<int> Skipped = [.. RangeOf(10, 30), 32, 36, 37, .. RangeOf(101, 107), 41, 42, 45, 46, 47, 48, 49];

    private readonly Func<bool> shouldRecord;

    public ChatRecorder(Func<bool> shouldRecord)
    {
        this.shouldRecord = shouldRecord;
        Svc.Chat.ChatMessageHandled += this.OnMessage;
        Svc.Chat.ChatMessageUnhandled += this.OnMessage;
        Svc.Chat.LogMessage += this.OnLogMessage;
    }

    private static IEnumerable<int> RangeOf(int from, int to)
    {
        for (var i = from; i <= to; i++)
            yield return i;
    }

    /// <summary>残すチャットの種類か（種類の値の下7ビットで見る）。</summary>
    public static bool ShouldKeep(int logKind) => !Skipped.Contains(logKind & 0x7F);

    private void OnMessage(IChatMessage message)
    {
        try
        {
            if (!this.shouldRecord() || !ShouldKeep((int)message.LogKind))
                return;
            var sender = message.Sender.TextValue;
            DebugLog.Current?.Line("チャット", $"[{message.LogKind}{(message.IsHandled ? "・ほかのプラグインが隠した" : string.Empty)}]"
                                              + $"{(sender.Length > 0 ? $" {sender}:" : string.Empty)} {message.Message.TextValue}");
        }
        catch (Exception ex)
        {
            DebugLog.Current?.Line("チャット", $"チャットを記録できませんでした: {ex.Message}");
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            if (!this.shouldRecord() || GameUi.InCombat)
                return;
            DebugLog.Current?.Line("ゲームの記録", $"LogMessage {message.LogMessageId}：{message.FormatLogMessageForDebugging().ExtractText()}");
        }
        catch (Exception ex)
        {
            DebugLog.Current?.Line("ゲームの記録", $"ゲームの記録を残せませんでした: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Svc.Chat.ChatMessageHandled -= this.OnMessage;
        Svc.Chat.ChatMessageUnhandled -= this.OnMessage;
        Svc.Chat.LogMessage -= this.OnLogMessage;
    }
}
