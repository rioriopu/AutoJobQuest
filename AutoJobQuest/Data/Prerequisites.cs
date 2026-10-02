using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// そのエリアへ行けるか（関連クエストの未達でジョブクエが進められない・詰まることがないよう、
/// 前提をゲームデータから確かめる）。
///
/// 【なぜ要るか】蒼天のジョブクエ（木工・鍛冶・革細工・裁縫の Lv53〜60 など）は手順の場所がイシュガルド（下層 418・上層 419）だが、
/// イシュガルドへ入れるのはメインクエスト「イシュガルドへ」から。これはクエストの前提（PreviousQuest）には出てこないので、
/// 前提のたどり方だけでは素通りして Questionable が行き詰まる（ゲームデータで確認）。
/// 【判断】エリアの「入口のエーテライト」が1つでも解放済みなら行ける。入口のエーテライトは、
///  ・そのエリアにあるエーテライト（Aetheryte.IsAetheryte）
///  ・エーテライトの無いエリアは、そのエリアにあるエーテルネットの中継点（IsAetheryte=false）の組（AethernetGroup）の親のエーテライト
///    （上層 419 → 下層の 70、高地ドラヴァニア 399 → イディルシャイアの 75。ゲームデータで確認。GBR の扱いと同じ）
/// 入口のエーテライトが1つも無いエリア（ID の中など）は判断できないので「分からない」（null）として止めない。
/// </summary>
public static class AreaAccess
{
    private static readonly Dictionary<uint, List<uint>> GateCache = [];

    /// <summary>エリアの入口のエーテライト（ゲームデータから。無ければ空）。</summary>
    public static List<uint> GateAetherytes(uint territory)
    {
        lock (GateCache)
        {
            if (GateCache.TryGetValue(territory, out var cached))
                return cached;
        }

        var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var gates = sheet.Where(a => a.IsAetheryte && a.Territory.RowId == territory).Select(a => a.RowId).ToList();
        if (gates.Count == 0)
        {
            var groups = sheet.Where(a => !a.IsAetheryte && a.Territory.RowId == territory && a.AethernetGroup != 0)
                .Select(a => a.AethernetGroup).ToHashSet();
            gates = sheet.Where(a => a.IsAetheryte && groups.Contains(a.AethernetGroup)).Select(a => a.RowId).ToList();
        }

        lock (GateCache)
            GateCache[territory] = gates;
        return gates;
    }

    /// <summary>行けるか。入口のエーテライトが無いエリアは null（判断できない）。</summary>
    /// <param name="territory">エリア。</param>
    /// <param name="unlocked">解放済みのエーテライト。</param>
    public static bool? Reachable(uint territory, IReadOnlySet<uint> unlocked)
    {
        var gates = GateAetherytes(territory);
        return gates.Count == 0 ? null : gates.Any(unlocked.Contains);
    }

    /// <summary>いま解放済みのエーテライト（ゲームから）。</summary>
    public static HashSet<uint> UnlockedNow()
        => Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();

    /// <summary>エリアの名前（画面・記録用）。</summary>
    public static string Name(uint territory)
        => Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t)
            ? t.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{territory}"
            : $"#{territory}";

    /// <summary>入口のエーテライトの名前（案内用）。</summary>
    public static string GateNames(uint territory)
        => string.Join("・", GateAetherytes(territory)
            .Select(id => Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(id, out var a) ? a.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{id}" : $"#{id}"));

    /// <summary>
    /// 行けないエリアの案内。
    /// メインクエスト「イシュガルドへ」はエーテライトの交感を手順に含まない（QuestParams に AETHERYTE* が無い。Questionable は自分で交感を足している）ので、
    /// 手で進めた人は「クエストは完了・エーテライトは未交感」がありうる。テレポは交感が無いと使えないので止めるのは正しいが、
    /// 「メインクエストで解放して」ではなく「交感して」と案内する。見分けは番号を持たずにゲームデータで行う：
    ///  ・そのエリアに受注・手順の場所があるクエストが1つでも完了していれば、その地域には入れる → 交感を案内する
    ///  ・1つも無ければ、そのエリアに場所のある最初のクエスト（同じエリアの別のクエストを前提の連鎖に持たないもの）を名前で出す
    ///    （イシュガルドの下層 418・上層 419 はどちらも「イシュガルドへ」になる：ゲームデータで確認）
    /// </summary>
    public static string UnreachableHint(uint territory, Func<uint, bool> isComplete)
    {
        var gates = GateNames(territory);
        var (done, first) = EntryQuests(QuestsAt(territory), isComplete, PreviousOf);
        if (done != 0)
            return $"入口のエーテライト「{gates}」に交感していません。「{Unlocks.QuestName(done)}」が完了しているので、その地域には入れます。"
                   + "エーテライトに交感してから進めてください（テレポに要ります）";
        return $"入口のエーテライト「{gates}」が未解放。"
               + (first != 0 ? $"その地域へは「{Unlocks.QuestName(first)}」から入れるようになります。" : string.Empty)
               + "メインクエスト等でその地域を解放してから進めてください";
    }

    /// <summary>エリアに場所のあるクエスト1本（番号・区分 JournalSection）。</summary>
    public sealed record QuestAt(uint Id, uint Section);

    /// <summary>
    /// 行けないエリアの案内に使うクエスト（試せるように分けた判断）。
    /// Done＝完了済みのうち（区分, 番号）が最も小さいもの、First＝根（同じエリアの別のクエストを前提の連鎖に持たない）のうち（区分, 番号）が最も小さいもの。無ければ 0。
    /// </summary>
    /// <param name="quests">そのエリアに場所のあるクエスト。</param>
    /// <param name="isComplete">クエストが完了しているか。</param>
    /// <param name="previousOf">クエストの前提（Quest.PreviousQuest）。</param>
    public static (uint Done, uint First) EntryQuests(IReadOnlyList<QuestAt> quests, Func<uint, bool> isComplete, Func<uint, IEnumerable<uint>> previousOf)
    {
        var ordered = quests.OrderBy(q => q.Section).ThenBy(q => q.Id).ToList();
        var done = ordered.FirstOrDefault(q => isComplete(q.Id))?.Id ?? 0;

        var here = ordered.Select(q => q.Id).ToHashSet();
        bool HasAncestorHere(uint id)
        {
            var seen = new HashSet<uint>();
            var stack = new Stack<uint>(previousOf(id));
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (p == 0 || !seen.Add(p))
                    continue;
                if (here.Contains(p))
                    return true;
                foreach (var pp in previousOf(p))
                    stack.Push(pp);
            }

            return false;
        }

        var first = ordered.FirstOrDefault(q => !HasAncestorHere(q.Id))?.Id ?? 0;
        return (done, first);
    }

    // エリア → そこに場所のあるクエスト（ゲームデータなので変わらない。初めて要るときに全クエストから作る）
    private static Dictionary<uint, List<QuestAt>>? questsByTerritory;

    /// <summary>そのエリアに受注・手順の場所があるクエスト（ゲームデータから）。</summary>
    public static List<QuestAt> QuestsAt(uint territory)
    {
        lock (TerritoryCache)
        {
            if (questsByTerritory == null)
            {
                var map = new Dictionary<uint, List<QuestAt>>();
                foreach (var q in Svc.Data.GetExcelSheet<Quest>())
                {
                    var section = q.JournalGenre.ValueNullable?.JournalCategory.ValueNullable?.JournalSection.RowId ?? uint.MaxValue;
                    foreach (var t in ReadQuestTerritories(q.RowId))
                    {
                        if (!map.TryGetValue(t, out var list))
                            map[t] = list = [];
                        list.Add(new QuestAt(q.RowId, section));
                    }
                }

                questsByTerritory = map;
            }

            return questsByTerritory.TryGetValue(territory, out var found) ? found : [];
        }
    }

    /// <summary>クエストの前提（Quest.PreviousQuest。ゲームデータから）。</summary>
    private static IEnumerable<uint> PreviousOf(uint questId)
        => Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q)
            ? q.PreviousQuest.Select(p => p.RowId).Where(x => x != 0).ToList()
            : [];

    /// <summary>
    /// クエストの受注場所と手順の場所のエリア（Quest.IssuerLocation・TodoParams[].ToDoLocation → Level → Territory。ゲームデータから）。
    /// </summary>
    public static HashSet<uint> QuestTerritories(uint questId)
    {
        lock (TerritoryCache)
        {
            if (TerritoryCache.TryGetValue(questId, out var cached))
                return cached;
        }

        var set = ReadQuestTerritories(questId);
        lock (TerritoryCache)
            TerritoryCache[questId] = set;
        return set;
    }

    // クエストの場所（ゲームデータなので変わらない。画面の点検が5秒おきに呼ぶので控える）
    private static readonly Dictionary<uint, HashSet<uint>> TerritoryCache = [];

    private static HashSet<uint> ReadQuestTerritories(uint questId)
    {
        var set = new HashSet<uint>();
        if (!Svc.Data.GetExcelSheet<Quest>().TryGetRow(questId, out var q))
            return set;

        if (q.IssuerLocation.ValueNullable is { } issuer && issuer.Territory.RowId != 0)
            set.Add(issuer.Territory.RowId);
        foreach (var todo in q.TodoParams)
        {
            foreach (var loc in todo.ToDoLocation)
            {
                if (loc.RowId != 0 && loc.ValueNullable is { } lv && lv.Territory.RowId != 0)
                    set.Add(lv.Territory.RowId);
            }
        }

        return set;
    }
}

/// <summary>
/// 採集・釣りの能力の解放（ゲームデータの Action.UnlockLink をゲームに聞く）。
/// アクションの番号は GBR の AutoGather.ActionIDs.cs と同じ（眼力＝園芸 4095・採掘 4081、収集品採集＝園芸 815・採掘 240）。
/// 刺突漁（ギギング＝7632）は AutoHook の IDs.cs と同じ。ゲームデータに「この能力」という印が無いので番号で持ち、
/// 使う前に「その職のアクションで、解放条件がある」ことをゲームデータで確かめる（違っていれば判断に使わず、記録に残す）。
/// 解放に要るクエスト・レベルは番号ではなくゲームデータ（UnlockLink・ClassJobLevel）から読む。
/// </summary>
public static class GatherAbilities
{
    /// <summary>採掘師（ClassJob 16）の眼力「山師の眼力」。隠し（HIDDEN）の採集物を出す。</summary>
    public const uint MinerLuck = 4081;

    /// <summary>園芸師（ClassJob 17）の眼力「開拓者の眼力」。</summary>
    public const uint BotanistLuck = 4095;

    /// <summary>採掘師の収集品採集。精選の元（収集品）を採るのに要る。</summary>
    public const uint MinerCollect = 240;

    /// <summary>園芸師の収集品採集。</summary>
    public const uint BotanistCollect = 815;

    /// <summary>漁師の刺突漁（ギギング）。銛でしか取れない魚に要る。</summary>
    public const uint Gig = 7632;

    /// <summary>ゲームデータで確かめた職（番号 → 職）。確かめられなかった番号は入らない。</summary>
    private static Dictionary<uint, uint>? verified;

    private static Dictionary<uint, uint> Verified()
    {
        if (verified != null)
            return verified;

        var map = new Dictionary<uint, uint>();
        var expected = new (uint Action, uint Job)[] { (MinerLuck, 16), (BotanistLuck, 17), (MinerCollect, 16), (BotanistCollect, 17), (Gig, 18) };
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        foreach (var (action, job) in expected)
        {
            if (sheet.TryGetRow(action, out var a) && a.ClassJob.RowId == job && a.UnlockLink.RowId != 0)
                map[action] = job;
            else
                Core.DebugLog.Current?.Line("データ", $"⚠ アクション {action} が想定（ClassJob {job}・解放条件あり）と違います。この能力の解放は確かめません（ゲームの更新で番号が変わった可能性）");
        }

        verified = map;
        return map;
    }

    /// <summary>
    /// 使えるか（解放済みで、その職のレベルが足りる）。番号をゲームデータで確かめられなければ null（判断しない）。
    /// </summary>
    public static unsafe bool? Usable(uint action)
    {
        if (!Verified().TryGetValue(action, out var job))
            return null;
        try
        {
            var a = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(action);
            var ui = UIState.Instance();
            if (ui == null)
                return null;
            return ui->IsUnlockLinkUnlockedOrQuestCompleted(a.UnlockLink.RowId) && Jobs.Level(job) >= a.ClassJobLevel;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>その能力を解放するクエスト（Action.UnlockLink がクエストの番号なら、その番号。違えば 0）。</summary>
    public static uint UnlockQuest(uint action)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(action, out var a) && a.UnlockLink.RowId is > 0x10000 and < 0x20000
            ? a.UnlockLink.RowId
            : 0;

    /// <summary>能力の名前（「山師の眼力」など。ゲームデータから読む）。</summary>
    public static string Name(uint action)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(action, out var a) ? $"「{a.Name.ExtractText()}」" : $"アクション {action}";

    /// <summary>その能力を使えるようにする条件の説明（「〇〇 Lv55・クエスト『…』」）。</summary>
    public static string Requirement(uint action)
    {
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!sheet.TryGetRow(action, out var a))
            return $"アクション {action}";
        var quest = a.UnlockLink.RowId is > 0x10000 and < 0x20000 ? $"・クエスト「{Unlocks.QuestName(a.UnlockLink.RowId)}」" : string.Empty;
        return $"「{a.Name.ExtractText()}」（{Jobs.Name(a.ClassJob.RowId)} Lv{a.ClassJobLevel}{quest}）";
    }
}
