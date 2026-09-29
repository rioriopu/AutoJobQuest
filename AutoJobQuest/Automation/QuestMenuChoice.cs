using System;
using System.Collections.Generic;

namespace AutoJobQuest.Automation;

/// <summary>クエスト名の完全一致が1件だけあるときに選ぶ。装飾は読出し側のSeString展開で除く。</summary>
public static class QuestMenuChoice
{
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
        if (selected == signature)
            return true;
        if (!ctx.TextAdvance.EnsureTurnInControl())
        {
            failure = "会話の操作権が失われたため止めました";
            return true;
        }
        GameUi.Fire(menu, true, index);
        selected = signature;
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
