using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoJobQuest.Automation;

/// <summary>
/// 受注中のほかのクエストを、実行の間だけジャーナルで非表示にする（Questionable が無関係なクエストへ移らないように）。
///
/// 【仕組み（ゲーム本体の逆アセンブルで確定）】
///  ・ジャーナルの行の左の枠は「通常(0) → 優先表示(1) → 非表示(2)」と巡回する。中身は QuestWork.Flags の IsPriority（bit0）・IsHidden（bit3）。
///  ・押すと、ゲームは GameMain.ExecuteCommand(0x2C0, 1, 受注枠の番号, 新しい状態, 0) をサーバーへ送る（受注枠＝QuestManager.NormalQuests の何番目か）。
///    こちらも同じ命令を送る（ジャーナルを開かずに送れる。画面に撃つ方法は、ジャーナルが閉じていると落ちる作りなので使わない）。
///  ・ローカルの Flags は、サーバーから更新が届いてから変わる。戻したかは Flags で確かめる（送ったことを「戻した」とはしない）。
/// 【効き目】Questionable は優先リストの先頭（受注可能か受注中）を ToDo より先に選ぶ（QuestController.cs：確定）ので、受注後の脇道は優先リストで
/// 防げている。非表示は、ToDo（TrackedQuests）から無関係なクエストが消えるなら、さらに脇道を減らす。非表示のクエストが TrackedQuests から消えるかは
/// クライアントのコードからは決められない（実機で1回確かめる）。
/// 【戻す】控え（設定 JournalHiddenByMe：クエスト → 元の状態）を送る前に保存し、実行していない間に元の状態へ戻す（<see cref="JournalRestorer"/>）。
/// 途中で利用者が状態を変えた（非表示でなくなった）・受注していない（終えた・破棄した）ものは戻さずに控えを消す。
/// </summary>
public static class JournalHide
{
    /// <summary>ジャーナルの状態：通常。</summary>
    public const byte Normal = 0;

    /// <summary>ジャーナルの状態：優先表示。</summary>
    public const byte Priority = 1;

    /// <summary>ジャーナルの状態：非表示。</summary>
    public const byte Hidden = 2;

    /// <summary>ゲームがジャーナルの状態を変えるときに送る命令の番号（ゲーム本体の逆アセンブル：AgentQuestJournal → Journal → ExecuteCommand）。</summary>
    public const int CommandId = 0x2C0;

    /// <summary>命令と命令の間隔（画面から押すのと同じくらいの頻度にする。進む条件ではなく、送りすぎの防止）。</summary>
    public static readonly TimeSpan SendSpacing = TimeSpan.FromMilliseconds(250);

    /// <summary>元の状態へ戻す命令を送ってから、反映を待つ上限（過ぎたら送り直す。3回で諦めて案内を出す）。</summary>
    public static readonly TimeSpan ConfirmLimit = TimeSpan.FromSeconds(5);

    /// <summary>元の状態へ戻す命令を送る回数の上限。</summary>
    public const int MaxRestoreAttempts = 3;

    /// <summary>受注枠1つ（番号・クエスト・非表示か・優先表示か）。</summary>
    public readonly record struct Slot(int Index, ushort QuestId, bool IsHidden, bool IsPriority)
    {
        /// <summary>ジャーナルの状態（0＝通常・1＝優先表示・2＝非表示）。</summary>
        public byte State => this.IsHidden ? Hidden : this.IsPriority ? Priority : Normal;
    }

    /// <summary>非表示にするクエスト：受注中で、対象のクエストでも、すでに非表示でもなく、控えに無いもの（控えにあるものは二度送らない）。</summary>
    public static List<Slot> ToHide(IReadOnlyList<Slot> accepted, ushort own, IReadOnlyDictionary<ushort, byte> records)
        => accepted.Where(s => s.QuestId != 0 && s.QuestId != own && !s.IsHidden && !records.ContainsKey(s.QuestId)).ToList();

    /// <summary>
    /// 対象のクエストを、前にこちらが非表示にしていた（前のクエストの間に隠した・前の実行の控えが残っている）なら、先に戻す状態。戻さないなら null。
    /// </summary>
    public static byte? OwnRestore(IReadOnlyList<Slot> accepted, ushort own, IReadOnlyDictionary<ushort, byte> records)
        => records.TryGetValue(own, out var original) && accepted.Any(s => s.QuestId == own && s.IsHidden) && original != Hidden ? original : null;

    /// <summary>控え1件の戻し方。</summary>
    public enum RestoreAction
    {
        /// <summary>元の状態を送る（まだこちらが非表示にしたまま）。</summary>
        Send,

        /// <summary>もう戻っている・利用者が変えた・受注していない：控えを消す。</summary>
        Forget,
    }

    /// <summary>控え1件の戻し方（受注枠が見つからなければ受注していない）。</summary>
    public static RestoreAction DecideRestore(Slot? slot, byte original)
        => slot is { IsHidden: true } && original != Hidden ? RestoreAction.Send : RestoreAction.Forget;

    /// <summary>いまの受注枠（ゲームから。フレームワークのスレッドで呼ぶ）。</summary>
    public static unsafe List<Slot> ReadSlots()
    {
        var list = new List<Slot>();
        var qm = QuestManager.Instance();
        if (qm == null)
            return list;
        var quests = qm->NormalQuests;
        for (var i = 0; i < quests.Length; i++)
        {
            ref var q = ref quests[i];
            if (q.QuestId != 0)
                list.Add(new Slot(i, q.QuestId, q.IsHidden, q.IsPriority));
        }

        return list;
    }

    /// <summary>ジャーナルの状態を変える命令を送る（ゲームが画面から押したときと同じ命令）。送れたら true。</summary>
    public static bool Send(int slotIndex, byte state)
        => GameMain.ExecuteCommand(CommandId, 1, slotIndex, state, 0);

    /// <summary>クエストの名前（記録用）。</summary>
    public static string Name(ushort questId) => Data.Unlocks.QuestName(questId + 65536u);
}

/// <summary>
/// こちらが非表示にしたクエストを、実行していない間に元の状態へ戻す（1回の呼び出しで1本まで。Services が SendSpacing おきに呼ぶ）。
/// 送った後は反映（Flags が非表示でなくなる）を ConfirmLimit まで待ち、変わらなければ送り直す。3回送っても戻らなければ、手で戻すよう案内して控えを消す。
/// </summary>
public sealed class JournalRestorer
{
    private readonly Dictionary<ushort, (int Attempts, DateTime LastSent)> sent = [];

    /// <summary>1歩進める。記録に残す文があれば返す。</summary>
    public string? Tick(Configuration config)
    {
        if (config.JournalHiddenByMe.Count == 0)
            return null;

        var slots = JournalHide.ReadSlots();
        foreach (var (questId, original) in config.JournalHiddenByMe.ToList())
        {
            JournalHide.Slot? slot = slots.Any(s => s.QuestId == questId) ? slots.First(s => s.QuestId == questId) : null;
            var name = JournalHide.Name(questId);
            if (JournalHide.DecideRestore(slot, original) == JournalHide.RestoreAction.Forget)
            {
                var wasSent = this.sent.Remove(questId);
                config.JournalHiddenByMe.Remove(questId);
                config.Save();
                return slot == null
                    ? $"ジャーナル：「{name}」は受注していないので、表示の状態は戻しません（控えを消しました）"
                    : wasSent
                        ? $"ジャーナル：「{name}」の表示を元の状態に戻しました"
                        : $"ジャーナル：「{name}」はもう非表示ではない（利用者が変えた等）ので、そのままにします（控えを消しました）";
            }

            if (this.sent.TryGetValue(questId, out var s))
            {
                if (DateTime.UtcNow - s.LastSent < JournalHide.ConfirmLimit)
                    continue;
                if (s.Attempts >= JournalHide.MaxRestoreAttempts)
                {
                    this.sent.Remove(questId);
                    config.JournalHiddenByMe.Remove(questId);
                    config.Save();
                    return $"⚠ ジャーナル：「{name}」を元の状態に戻す命令を {s.Attempts} 回送りましたが、非表示のままです。ジャーナルでそのクエストの左の枠を押して戻してください";
                }
            }

            var attempts = (this.sent.TryGetValue(questId, out var prev) ? prev.Attempts : 0) + 1;
            this.sent[questId] = (attempts, DateTime.UtcNow);
            var ok = JournalHide.Send(slot!.Value.Index, original);
            return $"ジャーナル：「{name}」を元の状態（{(original == JournalHide.Priority ? "優先表示" : "通常")}）に戻す命令を{(ok ? "送りました" : "送れませんでした")}（{attempts} 回目）";
        }

        return null;
    }
}
