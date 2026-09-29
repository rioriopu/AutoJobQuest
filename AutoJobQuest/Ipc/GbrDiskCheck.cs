using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AutoJobQuest.Ipc;

/// <summary>
/// GBR に戻した設定・リストが、GBR の保存ファイル（ディスク）に書かれたかを確かめる。
///
/// 【なぜ要るか】GBR の設定の Save() は「保存の旗を立てるだけ」で、250ミリ秒静かになってから別の処理で書き出す
/// （GatherBuddyReborn の Config/Configuration.cs：Save・SaveIfDirty）。採集リストの Save() はその場で書くが、
/// 書き出しの失敗は GBR の記録に出すだけで呼んだ側に伝わらない（AutoGather/Lists/AutoGatherListsManager.cs：Save）。
/// 以前は Save を呼んだ時点で控えを消していたので、書かれる前に終わると、次に読み込んだとき一時変更が残ったまま戻す根拠が無くなった。
/// ファイルの形（実物で確認）：設定は pluginConfigs\GatherBuddyReborn.json の AutoGatherConfig・CollectableConfig、
/// 採集リストは pluginConfigs\GatherBuddyReborn\auto_gather_lists.json（配列。Name・FolderPath・Enabled）。
/// </summary>
public static class GbrDiskCheck
{
    public enum Result
    {
        /// <summary>ファイルに書かれている。</summary>
        Saved,

        /// <summary>ファイルはまだ違う値（書かれていない）。</summary>
        NotYet,

        /// <summary>ファイルを読めない・形が違う（確かめられない）。</summary>
        Unreadable,
    }

    /// <summary>設定がファイルに書かれたか（名前 → 書かれているはずの値）。名前の頭に "Collectable." が付けば収集品の設定。</summary>
    public static Result Config(string? json, IReadOnlyDictionary<string, bool> expected)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Unreadable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var (name, value) in expected)
            {
                var (section, key) = name.StartsWith(GbrHandle.CollectablePrefix, StringComparison.Ordinal)
                    ? ("CollectableConfig", name[GbrHandle.CollectablePrefix.Length..])
                    : ("AutoGatherConfig", name);
                if (!doc.RootElement.TryGetProperty(section, out var sec) || !sec.TryGetProperty(key, out var v)
                    || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Result.Unreadable;
                if (v.GetBoolean() != value)
                    return Result.NotYet;
            }

            return Result.Saved;
        }
        catch (JsonException)
        {
            return Result.Unreadable;
        }
    }

    /// <summary>
    /// 採集リストがファイルに書かれたか：こちらのリスト（ownName）が無効で、戻したリストが有効になっている。
    /// </summary>
    public static Result Lists(string? json, string ownName, IEnumerable<GbrListRef> shouldBeEnabled)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Unreadable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Result.Unreadable;

            var lists = new List<(string Name, string Folder, bool Enabled)>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("Name", out var n) || !e.TryGetProperty("Enabled", out var en))
                    return Result.Unreadable;
                var folder = e.TryGetProperty("FolderPath", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : string.Empty;
                lists.Add((n.GetString() ?? string.Empty, folder, en.ValueKind == JsonValueKind.True));
            }

            if (lists.Any(l => l.Name == ownName && l.Enabled))
                return Result.NotYet;
            foreach (var r in shouldBeEnabled)
            {
                var matches = lists.Where(l => l.Name == r.Name && l.Folder == r.FolderPath).ToList();
                if (matches.Count > 0 && matches.All(l => !l.Enabled))
                    return Result.NotYet;
            }

            return Result.Saved;
        }
        catch (JsonException)
        {
            return Result.Unreadable;
        }
    }
}
