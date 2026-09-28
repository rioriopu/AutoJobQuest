using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 機能（マテリア装着・精選）の解放と、解放するクエストをゲームデータから引く（
/// 動かす前に解放済みか確かめ、未解放なら Questionable で解放する）。
///
///  ・解放済みか：一般アクションの解放条件（GeneralAction.UnlockLink）をゲームに聞く
///    （UIState.IsUnlockLinkUnlockedOrQuestCompleted）。状態（戦闘中・移動中）に左右されない。
///  ・解放するクエスト：Quest.GeneralActionReward にその一般アクションを持つクエスト（名前や番号を決め打ちしない）。
///    ゲームデータで確認（解析ツール jqa unlockq / qinfo）：
///      マテリア装着（12）← 66175「想いを伝える力」（クラフター Lv19。前提 66174「想いが生み出す力」全クラス Lv19）
///      精選（21）← 67633「生命、精選、もうひとつの答え」（ギャザラー Lv56。前提 67118「わだかまる雲霧」＝蒼天のメインクエスト）
///  ・前提のクエストはたどって、未完了のものを古い順に進める。ただし、解放クエストと区分（JournalSection）の違う
///    クエスト（メインクエスト等）が未完了なら、自動では進めない（メインストーリーを勝手に進めない）。
/// </summary>
public static class Unlocks
{
    /// <summary>一般アクション 12＝マテリア装着（ゲームデータで確認：jqa genact）。</summary>
    public const uint Meld = 12;

    /// <summary>一般アクション 21＝精選（ゲームデータで確認：jqa genact）。</summary>
    public const uint Reduction = 21;

    private static Dictionary<uint, uint>? unlockQuests;

    /// <summary>この実行で解放をあきらめた一般アクション（前提が満たせない等）。実行の最初に空にする。</summary>
    public static HashSet<uint> GaveUp { get; } = [];

    public static string Name(uint generalAction)
        => Svc.Data.GetExcelSheet<GeneralAction>().TryGetRow(generalAction, out var r) ? r.Name.ExtractText() : $"一般アクション{generalAction}";

    /// <summary>解放済みか（一般アクションの解放条件をゲームに聞く）。読めなければ false。</summary>
    public static unsafe bool IsUnlocked(uint generalAction)
    {
        try
        {
            var ui = UIState.Instance();
            return ui != null
                   && Svc.Data.GetExcelSheet<GeneralAction>().TryGetRow(generalAction, out var row)
                   && ui->IsUnlockLinkUnlockedOrQuestCompleted(row.UnlockLink);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>その一般アクションを解放するクエスト（Quest.GeneralActionReward）。無ければ 0。</summary>
    public static uint UnlockQuest(uint generalAction)
    {
        if (unlockQuests == null)
        {
            var map = new Dictionary<uint, uint>();
            foreach (var q in Svc.Data.GetExcelSheet<Quest>())
            {
                foreach (var ga in q.GeneralActionReward)
                {
                    if (ga.RowId != 0 && !map.ContainsKey(ga.RowId))
                        map[ga.RowId] = q.RowId;
                }
            }

            if (map.Count > 0)
                unlockQuests = map;
            else
                return 0; // 空は控えない（シートが引けない時機に呼ばれた可能性）
        }

        return unlockQuests.GetValueOrDefault(generalAction);
    }

    public static string QuestName(uint questId)
        => Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q) ? q.Name.ExtractText() : questId.ToString();

    /// <summary>
    /// 解放クエストまでに進めるクエスト（未完了のものを古い順）。自動で進められない前提があれば blocked に理由を入れて空を返す。
    /// 解放クエストがすでに完了なら空（blocked も null）。
    /// </summary>
    public static List<uint> ChainToRun(uint questId, out string? blocked)
    {
        blocked = null;
        var quests = Svc.Data.GetExcelSheet<Quest>();
        if (!quests.TryGetRow(questId, out var target))
        {
            blocked = $"クエスト {questId} がゲームデータにありません";
            return [];
        }

        var section = SectionOf(target);
        var order = new List<uint>();
        var seen = new HashSet<uint>();
        string? reason = null;

        bool Visit(uint id, int depth)
        {
            if (!seen.Add(id) || QuestManager.IsQuestComplete(id))
                return true;
            if (depth > 6 || !quests.TryGetRow(id, out var q))
            {
                reason = $"前提のクエスト「{QuestName(id)}」をたどりきれません";
                return false;
            }

            if (SectionOf(q) != section)
            {
                reason = $"前提のクエスト「{q.Name.ExtractText()}」（{q.JournalGenre.ValueNullable?.Name.ExtractText()}）が未完了です。メインクエスト等は自動では進めません";
                return false;
            }

            foreach (var p in q.PreviousQuest)
            {
                if (p.RowId != 0 && !Visit(p.RowId, depth + 1))
                    return false;
            }

            order.Add(id); // 前提を先に入れてから自分（＝古い順）
            return true;
        }

        var ok = Visit(questId, 0);
        blocked = reason;
        return ok ? order : [];
    }

    private static uint SectionOf(Quest q)
        => q.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId ?? uint.MaxValue;

    /// <summary>
    /// そのクエストを受けられる職（受注の対象で、レベルが足りて、ギアセットがあるもの）。いまの職が受けられるならそれ。無ければ null。
    /// 対象かどうかは ClassJobCategory の職ごとの列（列名は英語の略称：CRP・MIN など）で判定する。
    /// </summary>
    public static uint? PickJobFor(uint questId)
    {
        if (!Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q))
            return null;

        var category = q.ClassJobCategory0.RowId;
        var level = (int)q.ClassJobLevel[0];
        bool Eligible(uint job) => InCategory(category, job) && Jobs.Level(job) >= level;

        var current = Jobs.CurrentClassJob;
        if (current != 0 && Eligible(current))
            return current;

        var candidates = Jobs.Crafters.Concat(Jobs.Gatherers).Concat(Jobs.CombatJobs().Select(c => c.RowId));
        foreach (var job in candidates.Where(Eligible).OrderByDescending(Jobs.Level))
        {
            if (GearCheck.FindGearset(job) >= 0)
                return job;
        }

        return null;
    }

    private static bool InCategory(uint categoryId, uint classJobId)
    {
        try
        {
            if (!Svc.Data.GetExcelSheet<ClassJobCategory>().TryGetRow(categoryId, out var cat))
                return false;
            if (!Svc.Data.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(classJobId, out var cj))
                return false;
            var prop = typeof(ClassJobCategory).GetProperty(cj.Abbreviation.ExtractText());
            return prop?.GetValue(cat) is true;
        }
        catch
        {
            return false;
        }
    }
}
