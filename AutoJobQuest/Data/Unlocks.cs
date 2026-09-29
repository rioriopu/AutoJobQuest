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

    /// <summary>
    /// この実行の「機能の解放」の段を終えたか。終えた後は解放クエストを進めないので、未解放の機能は使えない
    /// （以前は段の後でも「解放できる見込み」で精選を手段に入れ、精選の作業で失敗してから外していた）。
    /// 実行の最初と終わりに false に戻す（止まっている間の計画の表示は、解放できる見込みも含めて出す）。
    /// </summary>
    public static bool UnlockStagePassed { get; set; }

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
        => ChainToRun(questId, out blocked, out _);

    /// <summary>上と同じ。止めている前提のクエスト（自動で進められない未完了のもの）も返す（無ければ 0）。</summary>
    public static List<uint> ChainToRun(uint questId, out string? blocked, out uint blockingQuest)
        => ChainCore(questId, QuestManager.IsQuestComplete, out blocked, out blockingQuest);

    /// <summary>
    /// 前提のたどり方の本体。「完了済みか」を外から渡す（ゲームを起動せずに、完了済みの組み合わせを仮定して試せるように）。
    /// 前提（Quest.PreviousQuest）は全部そろえる側に倒す（結合条件 PreviousQuestJoin の意味はソースで確かめられなかったため）。
    /// </summary>
    public static List<uint> ChainCore(uint questId, Func<uint, bool> isComplete, out string? blocked, out uint blockingQuest)
    {
        blocked = null;
        blockingQuest = 0;
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
        uint blocker = 0;

        // 深さの上限は、壊れたデータで延々とたどらないための歯止め（循環は seen で防いでいる）。
        // ジョブクエは Lv1〜60 で十数本つながるので、Lv1 から始めるキャラでも届く深さにする
        // （以前の 6 だと、未完了の前提が7本以上続くと「たどりきれません」になった）
        bool Visit(uint id, int depth)
        {
            if (!seen.Add(id) || isComplete(id))
                return true;
            if (depth > 64 || !quests.TryGetRow(id, out var q))
            {
                reason = $"前提のクエスト「{QuestName(id)}」をたどりきれません";
                blocker = id;
                return false;
            }

            if (SectionOf(q) != section)
            {
                reason = $"前提のクエスト「{q.Name.ExtractText()}」（{q.JournalGenre.ValueNullable?.Name.ExtractText()}）が未完了です。メインクエスト等は自動では進めません";
                blocker = id;
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
        blockingQuest = blocker;
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
