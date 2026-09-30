using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// ジョブの対応表。
///
/// 【ClassJob の行 ID について】
/// クラフター8職は ClassJob 8〜15、ギャザラー3職は 16〜18 に固定で並んでいる。
/// Artisan も「recipe.CraftType + 8 = ClassJob」と同じ前提で書かれており（CraftingList.cs の
/// TaskClassChange）、ECommons の Job 列挙も同じ値を持つ。ゲームの基本構造なのでここだけは定数で持つ。
/// その代わり、起動時にシートの中身（DohDolJobIndex）と一致するかを確かめ、ずれていれば警告する。
/// </summary>
public static class Jobs
{
    /// <summary>クラフターの ClassJob（木工→調理の順。Recipe.CraftType の 0〜7 と同じ並び）。</summary>
    public static readonly uint[] Crafters = [8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>ギャザラーの ClassJob（採掘・園芸・漁師）。</summary>
    public static readonly uint[] Gatherers = [16, 17, 18];

    /// <summary>画面に出す短い名前（チェックボックス用）。並びは <see cref="Crafters"/> と同じ。</summary>
    public static readonly string[] CrafterShortNames = ["木工", "鍛冶", "甲冑", "彫金", "革細工", "裁縫", "錬金", "調理"];

    public static readonly uint[] QuestJobs = [.. Crafters, .. Gatherers];

    public static readonly string[] QuestJobNames = [.. CrafterShortNames, "採掘", "園芸", "漁師"];

    public const int MinimumAutomationLevel = 70;

    public static string? StartProblem(bool[] selected, System.Func<uint, int> level)
    {
        var targets = QuestJobs.Where((_, i) => i < selected.Length && selected[i]).ToList();
        if (targets.Count == 0) return "自動化する職を選んでください";
        var low = targets.Where(j => level(j) < MinimumAutomationLevel).ToList();
        return low.Count == 0 ? null : "対象職はLv70以上が必要です：" + string.Join("・", low.Select(j => $"{Name(j)} Lv{level(j)}"));
    }

    public static bool IsCrafter(uint classJobId) => classJobId is >= 8 and <= 15;

    public static bool IsGatherer(uint classJobId) => classJobId is >= 16 and <= 18;

    /// <summary>Recipe.CraftType（0〜7）から ClassJob へ。</summary>
    public static uint CraftTypeToClassJob(uint craftType) => craftType + 8;

    /// <summary>ClassJob の名前（クライアント言語）。</summary>
    public static string Name(uint classJobId)
        => Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var row)
            ? row.Name.ExtractText()
            : $"ClassJob#{classJobId}";

    /// <summary>
    /// 定数の並びがシートと一致するかを確かめる。一致しなければ理由を返す。
    /// クラフターは DohDolJobIndex が CraftType と同じ 0〜7、ギャザラーは 0〜2 になっているはず。
    /// </summary>
    public static string? VerifyLayout()
    {
        var sheet = Svc.Data.GetExcelSheet<ClassJob>();
        for (var i = 0; i < Crafters.Length; i++)
        {
            if (!sheet.TryGetRow(Crafters[i], out var row) || row.DohDolJobIndex != i)
                return $"ClassJob {Crafters[i]} がクラフター{i}番目になっていません（ゲームデータの並びが変わった可能性）";
        }

        for (var i = 0; i < Gatherers.Length; i++)
        {
            if (!sheet.TryGetRow(Gatherers[i], out var row) || row.DohDolJobIndex != i)
                return $"ClassJob {Gatherers[i]} がギャザラー{i}番目になっていません（ゲームデータの並びが変わった可能性）";
        }

        return null;
    }

    /// <summary>
    /// 戦闘ジョブ（クラス含む）の一覧。
    /// 「クラフターでもギャザラーでもなく、経験値の欄を持つもの」を戦闘ジョブとみなす。
    /// 青魔道士のような制限ジョブ（IsLimitedJob）は、RSR で普通に戦えないので外す。
    /// </summary>
    public static List<ClassJob> CombatJobs()
        => Svc.Data.GetExcelSheet<ClassJob>()
            .Where(x => x.RowId != 0 && !IsCrafter(x.RowId) && !IsGatherer(x.RowId) && x.ExpArrayIndex >= 0 && !x.IsLimitedJob)
            .ToList();

    /// <summary>そのジョブのレベル。読めなければ 0。</summary>
    public static unsafe int Level(uint classJobId)
    {
        if (Automation.GameMemory.Test is { } test)
            return test.JobLevel(classJobId);
        var sheet = Svc.Data.GetExcelSheet<ClassJob>();
        if (!sheet.TryGetRow(classJobId, out var row) || row.ExpArrayIndex < 0)
            return 0;

        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (ps == null)
            return 0;

        var idx = row.ExpArrayIndex;
        var levels = ps->ClassJobLevels;
        return idx < levels.Length ? levels[idx] : 0;
    }

    /// <summary>いま就いているジョブ。</summary>
    public static uint CurrentClassJob
        => Automation.GameMemory.Test is { } test ? test.CurrentClassJob : Svc.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
}
