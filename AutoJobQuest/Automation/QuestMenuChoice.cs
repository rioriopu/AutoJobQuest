using System;
using System.Collections.Generic;

namespace AutoJobQuest.Automation;

/// <summary>クエスト名の完全一致が1件だけあるときに選ぶ。装飾は読出し側のSeString展開で除く。</summary>
public static class QuestMenuChoice
{
    /// <summary>選択肢を送ってから、窓が閉じるのを待つ上限。</summary>
    public static readonly TimeSpan SentLimit = TimeSpan.FromSeconds(5);

    /// <summary>自分の話しかけ以降の窓だけを操作する。1つの窓・内容・生成時刻につき1回だけ送る。</summary>
    public static unsafe bool Handle(Core.TaskContext ctx, uint quest, DateTime interactedAt, ref string? selected, out string? failure)
    {
        failure = null;
        if (interactedAt == DateTime.MinValue || GameUi.MenuEntries(out var menu) is not { } entries)
            return false;
        DateTime? opened = null;
        foreach (var addon in new[] { "SelectIconString", "SelectString" })
            if (ctx.Ownership.TryGetOwnedSince(addon, interactedAt, out var candidate, out var at) && candidate == menu)
                opened = at;
        if (opened == null)
        {
            failure = "話しかけた後の選択肢と確認できないため操作しません";
            return true;
        }
        var name = Data.Unlocks.QuestName(quest);
        var index = Unique(entries, name);
        if (index < 0)
        {
            failure = $"「{name}」の選択肢を一意に選べません：{string.Join(" / ", entries)}";
            return true;
        }
        var signature = $"{(nint)menu}|{opened.Value.Ticks}|{string.Join("|", entries)}";
        // 送った窓（控えは「署名@送った時刻」）。同じ窓が上限を過ぎても開いたままなら、理由を出して止める
        // （以前は理由を出さずに、クエストの上限の10〜30分まで待ち続けた。上限は送りすぎを防ぐためで、進む条件は窓が閉じたか）
        if (selected is { } sent && sent.StartsWith(signature + "@", StringComparison.Ordinal))
        {
            if (long.TryParse(sent[(signature.Length + 1)..], out var at) && DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc) > SentLimit)
                failure = $"選択肢「{entries[Unique(entries, Data.Unlocks.QuestName(quest)) is var i and >= 0 ? i : 0]}」を送りましたが、{SentLimit.TotalSeconds:0} 秒たっても窓が閉じません";
            return true;
        }
        if (!ctx.TextAdvance.VerifyTurnInControlNow())
        {
            failure = $"会話の操作権が失われたため止めました（{ctx.TextAdvance.LossReason ?? "理由を読めません"}）";
            return true;
        }
        GameUi.Fire(menu, true, index);
        selected = $"{signature}@{DateTime.UtcNow.Ticks}";
        ctx.Log.Write("クエスト", $"選択肢「{entries[index]}」を選びました。段の変化を待ちます");
        return true;
    }

    public static int Unique(IReadOnlyList<string> entries, string questName)
    {
        if (string.IsNullOrWhiteSpace(questName))
            return -1;
        var found = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(entries[i].Trim(), questName.Trim(), StringComparison.Ordinal))
                continue;
            if (found >= 0)
                return -1;
            found = i;
        }
        return found;
    }
}
