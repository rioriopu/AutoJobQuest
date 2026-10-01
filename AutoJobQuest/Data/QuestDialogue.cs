using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace AutoJobQuest.Data;

/// <summary>
/// クエストの会話の表（quest/〈番号/100〉/〈Quest の Id〉）の1行：鍵（TEXT_…）と本文。Questionable の QuestDialogueText と同じ形。
/// </summary>
[Sheet("QuestDialogueText")]
public readonly struct QuestDialogueText(ExcelPage page, uint offset, uint row) : IExcelRow<QuestDialogueText>
{
    public uint RowId => row;

    public ReadOnlySeString Key => page.ReadString(offset, offset);

    public ReadOnlySeString Value => page.ReadString(offset + 4, offset);

    ExcelPage IExcelRow<QuestDialogueText>.ExcelPage => page;

    uint IExcelRow<QuestDialogueText>.RowOffset => offset;

    static QuestDialogueText IExcelRow<QuestDialogueText>.Create(ExcelPage page, uint offset, uint row) => new(page, offset, row);
}

/// <summary>
/// Questionable の経路の会話の選択肢（鍵）を、ゲームデータの本文に引き当てて、画面の本文と比べる。
/// 引き当て方は Questionable の ExcelFunctions.GetRawDialogueText と同じ（Quest の行 0x10000＋番号 の Id から表の名前を作り、鍵の一致する行の本文）。
/// 比べ方：本文のうち固定の文字の部分（差し込み〔名前など〕を除く）が、画面の本文に順にすべて含まれ、本文の頭・終わりが固定の文字なら
/// 頭・終わりも一致するか（差し込みの無い本文は完全一致。「はい」が「はい、そうです」に合わないように）。私用領域の文字と空白・改行は
/// 除いてから比べる（Questionable の GameStringEquals も差し込みを除いて比べる）。
/// </summary>
public static class QuestDialogue
{
    /// <summary>引き当てた本文：固定の文字の部分（正規化済み）と、頭・終わりが固定の文字か。</summary>
    public sealed record Expected(IReadOnlyList<string> Parts, bool AnchorStart, bool AnchorEnd);

    private static readonly Dictionary<(uint Quest, string Key), Expected?> Cache = [];

    /// <summary>そのクエストの会話の鍵の本文。引き当てられなければ null。</summary>
    public static Expected? Resolve(uint questShortId, string key)
    {
        if (Cache.TryGetValue((questShortId, key), out var cached))
            return cached;

        Expected? parts = null;
        try
        {
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(0x10000 + questShortId, out var quest))
            {
                var name = $"quest/{questShortId / 100:000}/{quest.Id.ExtractText()}";
                foreach (var row in Svc.Data.GetExcelSheet<QuestDialogueText>(name: name))
                {
                    if (row.Key.ExtractText() != key)
                        continue;
                    parts = FromSeString(row.Value);
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 表が無い・読めないときは引き当てられない扱い（答えない）
            parts = null;
        }

        Cache[(questShortId, key)] = parts;
        return parts;
    }

    private static readonly Dictionary<uint, Expected?> AddonCache = [];

    /// <summary>ゲームデータ Addon の行の本文（確認の窓の文面など）。引き当てられなければ null。</summary>
    public static Expected? ResolveAddon(uint rowId)
    {
        if (AddonCache.TryGetValue(rowId, out var cached))
            return cached;
        Expected? result = null;
        try
        {
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().TryGetRow(rowId, out var row))
                result = FromSeString(row.Text);
        }
        catch (Exception)
        {
            result = null;
        }

        AddonCache[rowId] = result;
        return result;
    }

    private static Expected? FromSeString(ReadOnlySeString text)
    {
        var payloads = text.ToList();
        var list = new List<string>();
        foreach (var p in payloads)
        {
            if (p.Type != ReadOnlySePayloadType.Text)
                continue;
            var t = Normalize(p.ToString());
            if (t.Length > 0)
                list.Add(t);
        }

        return list.Count > 0
            ? new Expected(list, payloads[0].Type == ReadOnlySePayloadType.Text, payloads[^1].Type == ReadOnlySePayloadType.Text)
            : null;
    }

    /// <summary>画面の本文 <paramref name="actual"/> が、引き当てた本文 <paramref name="expected"/> に合うか（比べ方はこの型の説明）。</summary>
    public static bool Matches(string? actual, Expected? expected)
    {
        if (actual == null || expected == null || expected.Parts.Count == 0)
            return false;
        var text = Normalize(actual);
        if (expected.AnchorStart && !text.StartsWith(expected.Parts[0], StringComparison.Ordinal))
            return false;
        if (expected.AnchorEnd && !text.EndsWith(expected.Parts[^1], StringComparison.Ordinal))
            return false;
        var at = 0;
        foreach (var part in expected.Parts)
        {
            var i = text.IndexOf(part, at, StringComparison.Ordinal);
            if (i < 0)
                return false;
            at = i + part.Length;
        }

        return true;
    }

    /// <summary>私用領域の文字（ギルの記号など）と空白・改行を除く。</summary>
    public static string Normalize(string s)
        => new(s.Where(c => !char.IsWhiteSpace(c) && c is not (>= '' and <= '')).ToArray());
}
