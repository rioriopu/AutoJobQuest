using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 選んだ職のジョブクエ（Lv70 まで）に要るメインクエストのうち、未完了のもの。
/// 開始は止めない：進められるジョブクエは進め、受けられないジョブクエの手前で止める
/// （画面と開始の確認に「メインクエスト：○○未達のため、その手前で止まります」と出す。以前は開始そのものを止めていた）。
///
/// 【要るメインクエストの見つけ方（ゲームデータの調査。242本すべてで確認）】
///  ① 前提の欄（Quest.PreviousQuest）：11職とも Lv50 の蒼天の入口 ← 希望の灯火、Lv60 の紅蓮の入口 ← 宿命の果て。
///  ② 受注の場面の判定：クエストの台本が受注の場面でメインクエストの完了を確かめ、未完了なら断る（6本：彫金 Lv63 ← その声に押されて 等）。
///     シートの前提の欄には出ない。見分け方（規則B）：QuestParams の命令名が QST か QUEST で始まり、値がクエストの行を指し、
///     かつ本文の _SYSTEM_ の行に「「その名前」を完了」とある（242本で台本の結果と一致：該当6・誤り0・取りこぼし0）。
///  ③ 場所のエリアの入口：手順の場所のエリアのエーテライトが未解放で、そのエリアに入れるようになる最初のクエストがメインクエストなら、それも要る
///     （クガネ・ヤンサなど紅蓮のエリア。エーテライトは交感済みで行けるなら要らない）。
///     採集職の納品物は、受注場所・手順の場所に加えて採集点のエリアも見る（採掘 Lv63〜70 の品は紅蓮のエリアでしか採れず、
///     園芸 Lv70 のラールガースタッフはギラバニア湖畔地帯＝「塩の湖畔地帯」から。受注場所だけでは見落とす）。
/// 職ごとに、未完了のもののうち一番進んだもの（ほかの未完了のものを前提の連鎖に持つもの）を出す。
/// メインクエストの区分は JournalSection の日本語名が「メインクエスト」で始まるもの（番号を決め打ちしない）。
/// </summary>
public static class MainQuestGate
{
    private static readonly ConcurrentDictionary<uint, IReadOnlyList<uint>> AcceptCache = new();
    private static HashSet<uint>? mainSections;

    /// <summary>区分（JournalSection）がメインクエストか。</summary>
    public static bool IsMainSection(uint section)
    {
        mainSections ??= Svc.Data.GetExcelSheet<JournalSection>(ClientLanguage.Japanese)
            .Where(s => s.Name.ExtractText().StartsWith("メインクエスト", StringComparison.Ordinal))
            .Select(s => s.RowId)
            .ToHashSet();
        return mainSections.Contains(section);
    }

    /// <summary>クエストの区分（JournalSection）。分からなければ uint.MaxValue。</summary>
    public static uint SectionOf(uint questId)
        => Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q)
            ? q.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId ?? uint.MaxValue
            : uint.MaxValue;

    public static bool IsMainScenario(uint questId) => IsMainSection(SectionOf(questId));

    /// <summary>
    /// 受注の場面で台本が完了を確かめるクエスト（②の規則B）。一度読んだら控える（ゲームデータなので変わらない）。
    /// </summary>
    public static IReadOnlyList<uint> AcceptRequirements(uint questId)
        => AcceptCache.GetOrAdd(questId, ReadAcceptRequirements);

    private static IReadOnlyList<uint> ReadAcceptRequirements(uint questId)
    {
        var quests = Svc.Data.GetExcelSheet<Quest>();
        var questsJa = Svc.Data.GetExcelSheet<Quest>(ClientLanguage.Japanese);
        if (!quests.TryGetRow(questId, out var q))
            return [];

        var refs = new List<uint>();
        foreach (var p in q.QuestParams)
        {
            var name = p.ScriptInstruction.ExtractText();
            if (!name.StartsWith("QST", StringComparison.Ordinal) && !name.StartsWith("QUEST", StringComparison.Ordinal))
                continue;
            // 値はクエストの行（65536 以上）。短い番号で書かれていても引けるようにする
            foreach (var id in new[] { p.ScriptArg, p.ScriptArg < 0x10000 ? p.ScriptArg + 0x10000 : 0u })
                if (id != 0 && id != questId && quests.TryGetRow(id, out _))
                {
                    refs.Add(id);
                    break;
                }
        }

        if (refs.Count == 0)
            return [];
        // 鍵は _SYSTEM_ のほか、錬金などで _SYS_ の形もある
        var system = QuestCatalog.ReadQuestText(questId)
            .Where(t => t.Key.Contains("_SYS", StringComparison.Ordinal))
            .Select(t => t.Value)
            .ToList();
        if (system.Count == 0)
            return [];
        return refs
            .Where(id => questsJa.TryGetRow(id, out var r) && r.Name.ExtractText() is { Length: > 0 } n
                         && system.Any(s => s.Contains($"「{n}」を完了", StringComparison.Ordinal)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 未完了のジョブクエを受けるのに要るメインクエストのうち、未完了のもの（①②③。同じ区分の前提はたどる）。
    /// </summary>
    /// <param name="quests">その職のジョブクエ。</param>
    /// <param name="isComplete">クエストが完了済みか。</param>
    /// <param name="areaReachable">エリアへ行けるか（入口のエーテライトが無いエリアは null）。null なら③を見ない。</param>
    /// <param name="sources">入手元の索引（採集職の納品物の採集点のエリアを見る）。null なら見ない。</param>
    public static HashSet<uint> Missing(IEnumerable<JobQuest> quests, Func<uint, bool> isComplete, Func<uint, bool?>? areaReachable, SourceIndex? sources = null)
    {
        var list = quests.ToList();
        return MissingCore(list.Select(q => q.RowId).ToList(), list, isComplete, areaReachable, sources);
    }

    /// <summary>上と同じ（クエストの番号だけで調べる。機能の解放のクエストなど、ジョブクエでないもの用。エリアと採集点は見ない）。</summary>
    public static HashSet<uint> Missing(IEnumerable<uint> questIds, Func<uint, bool> isComplete)
        => MissingCore(questIds.ToList(), [], isComplete, null, null);

    private static HashSet<uint> MissingCore(IReadOnlyList<uint> ids, IReadOnlyList<JobQuest> list, Func<uint, bool> isComplete, Func<uint, bool?>? areaReachable, SourceIndex? sources)
    {
        var found = new HashSet<uint>();
        var seen = new HashSet<uint>();
        var sheet = Svc.Data.GetExcelSheet<Quest>();

        // エリアへ行けなければ、そのエリアに入れるようになるメインクエスト（完了済みのクエストがそのエリアに1つも無いときだけ）。無ければ 0
        uint AreaGate(uint terr)
        {
            var (done, first) = AreaAccess.EntryQuests(AreaAccess.QuestsAt(terr), isComplete, PreviousOf);
            return done == 0 && first != 0 && !isComplete(first) && IsMainScenario(first) ? first : 0;
        }

        void AddAreaGate(uint terr)
        {
            if (AreaGate(terr) is var gate and not 0)
                found.Add(gate);
        }

        void Visit(uint id, uint section, int depth)
        {
            if (!seen.Add(id) || isComplete(id) || depth > 64 || !sheet.TryGetRow(id, out var q))
                return;
            var here = SectionOf(id);
            if (here != section)
            {
                if (IsMainSection(here))
                    found.Add(id);
                return;
            }

            foreach (var p in q.PreviousQuest)
                if (p.RowId != 0)
                    Visit(p.RowId, section, depth + 1);
            foreach (var r in AcceptRequirements(id))
                Visit(r, section, depth + 1);

            if (areaReachable == null)
                return;
            foreach (var terr in AreaAccess.QuestTerritories(id))
            {
                if (areaReachable(terr) == false)
                    AddAreaGate(terr);
            }
        }

        foreach (var id in ids)
            Visit(id, SectionOf(id), 0);

        // 採集職の納品物：採れるエリアがどれも行けなければ、その入口のメインクエストも要る
        if (areaReachable != null && sources != null)
            foreach (var q in list.Where(q => Jobs.IsGatherer(q.ClassJobId)))
                foreach (var item in q.Items)
                {
                    var spots = sources.Get(item.ItemId).Gather.Select(g => g.Territory).Distinct().ToList();
                    if (spots.Count == 0 || spots.Any(t => areaReachable(t) != false))
                        continue;
                    // どこか1つのエリアに入れれば採れるので、一番早く入れるようになるものだけを要るとする
                    var gates = spots.Select(AreaGate).Where(g => g != 0).Distinct().ToList();
                    if (LeastAdvanced(gates) is var need and not 0)
                        found.Add(need);
                }

        return found;
    }

    /// <summary>未完了のメインクエストのうち、一番進んだもの（ほかのものを前提の連鎖に持つもの。同点なら行の番号が大きいもの）。</summary>
    public static uint MostAdvanced(IReadOnlyCollection<uint> msqs)
    {
        if (msqs.Count == 0)
            return 0;
        var ancestors = msqs.ToDictionary(m => m, Ancestors);
        return msqs
            .OrderByDescending(m => msqs.Count(o => o != m && ancestors[m].Contains(o)))
            .ThenByDescending(m => m)
            .First();
    }

    /// <summary>一番早いもの（ほかのものの前提の連鎖に入っているもの。同点なら行の番号が小さいもの）。無ければ 0。</summary>
    public static uint LeastAdvanced(IReadOnlyCollection<uint> msqs)
    {
        if (msqs.Count == 0)
            return 0;
        var ancestors = msqs.ToDictionary(m => m, Ancestors);
        return msqs
            .OrderByDescending(m => msqs.Count(o => o != m && ancestors[o].Contains(m)))
            .ThenBy(m => m)
            .First();
    }

    public static HashSet<uint> Ancestors(uint questId)
    {
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>(PreviousOf(questId));
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (p == 0 || !seen.Add(p))
                continue;
            foreach (var pp in PreviousOf(p))
                stack.Push(pp);
        }

        return seen;
    }

    private static IEnumerable<uint> PreviousOf(uint questId)
        => Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q)
            ? q.PreviousQuest.Select(p => p.RowId).Where(x => x != 0).ToList()
            : [];

    /// <summary>
    /// 止まる場所の案内（選んだ職のジョブクエに要るメインクエストが未完了）。無ければ null。
    /// 文言は「メインクエスト：○○未達のため、その手前で止まります（職名）」。同じメインクエストの職はまとめる。
    /// あわせて、漁師 Lv68 の刺突漁のように、納品物を取る能力の解放クエストが未完了のものも同じ形で出す（「サブクエスト：…」）。
    /// 実際に止まる場所は計画の進められないジョブクエ（<see cref="Planning.PlanBuilder.FindBlocked(IEnumerable{JobQuest}, Planning.PrereqContext)"/>）で決まる。
    /// </summary>
    public static string? StopNote(QuestCatalog catalog, SourceIndex? sources, bool[] selected, Func<uint, bool> isComplete,
        Func<uint, bool?>? areaReachable, Func<uint, bool?> abilityUsable)
    {
        var lines = new List<(uint Quest, uint Job)>();
        for (var i = 0; i < selected.Length && i < Jobs.QuestJobs.Length; i++)
        {
            if (!selected[i])
                continue;
            var job = Jobs.QuestJobs[i];
            var quests = catalog.Quests.Where(q => q.ClassJobId == job && !isComplete(q.RowId)).ToList();
            var missing = Missing(quests, isComplete, areaReachable, sources);
            if (MostAdvanced(missing) is var msq and not 0)
                lines.Add((msq, job));

            // 刺突漁の解放クエストは、自動で進められるなら止まらない（そのジョブクエの前にこちらで進める）
            if (sources != null && GigQuestMissing(quests, sources, abilityUsable) is var gig and not 0 && !isComplete(gig)
                && quests.FirstOrDefault(q => GigQuestMissing([q], sources, abilityUsable) != 0) is { } needsGig
                && !Planning.PlanBuilder.GigUnlockRunnable(needsGig, gig, isComplete))
                lines.Add((gig, job));
        }

        if (lines.Count == 0)
            return null;
        return string.Join(" / ", lines.GroupBy(l => l.Quest).Select(g =>
            $"{SectionLabel(g.Key)}：{Unlocks.QuestName(g.Key)}未達のため、その手前で止まります（{string.Join("・", g.Select(x => Jobs.Name(x.Job)))}）"));
    }

    /// <summary>ゲームから読む版（フレームワークのスレッドから呼ぶ）。ゲームデータの読み込み前は null（判断しない）。</summary>
    public static string? StopNote(GameDataCache data, bool[] selected)
    {
        if (data.Quests is not { } catalog)
            return null;
        var unlocked = AreaAccess.UnlockedNow();
        return StopNote(catalog, data.Sources, selected, FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete,
            terr => AreaAccess.Reachable(terr, unlocked), GatherAbilities.Usable);
    }

    /// <summary>
    /// 銛でしか取れない納品物があるのに刺突漁が使えないとき、刺突漁を解放するクエスト（Action の UnlockLink）。無ければ 0
    /// （漁師 Lv68「減少を食い止めろ」の大方士：Questionable も受注の前提に「「刺突漁」で魚を狙え」を足している）。
    /// </summary>
    public static uint GigQuestMissing(IEnumerable<JobQuest> quests, SourceIndex sources, Func<uint, bool?> abilityUsable)
    {
        var needsGig = quests.SelectMany(q => q.Items).Any(i => sources.Get(i.ItemId) is { Spearfish: true, Fish: false });
        if (!needsGig || abilityUsable(GatherAbilities.Gig) != false)
            return 0;
        return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(GatherAbilities.Gig, out var a)
               && a.UnlockLink.RowId is > 0x10000 and < 0x20000
            ? a.UnlockLink.RowId
            : 0;
    }

    /// <summary>「メインクエスト」「サブクエスト」など、区分の呼び名（JournalSection の日本語名。メインクエストは番号なし）。</summary>
    private static string SectionLabel(uint questId)
    {
        var section = SectionOf(questId);
        if (IsMainSection(section))
            return "メインクエスト";
        return Svc.Data.GetExcelSheet<JournalSection>(ClientLanguage.Japanese).TryGetRow(section, out var s)
            ? s.Name.ExtractText()
            : "クエスト";
    }
}
