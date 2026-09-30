using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Planning;

/// <summary>開始条件の種類。</summary>
public enum StartConditionKind
{
    Level,
    Gear,
    Bag,
    MainQuest,
    AetherCurrents,
    Aetherytes,
    QuestCount,
    Plugins,
    Gil,
}

/// <summary>開始条件1つ（画面に ✓／✗ で出す）。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Label">条件の文。</param>
/// <param name="Ok">満たしているか（まだ分からない・読めなければ null。null も開始できない）。</param>
/// <param name="Detail">今の値・足りないもの。</param>
public sealed record StartCondition(StartConditionKind Kind, string Label, bool? Ok, string Detail);

/// <summary>開始条件を判定するための、ゲームから読んだ値（読めない・まだ分からないものは null）。</summary>
/// <param name="Selected">選んだ職とそのレベル。</param>
/// <param name="GearProblem">選んだ製作職の装備が足りない理由（足りていれば null）。</param>
/// <param name="AnyCrafterSelected">製作職を選んでいるか（装備を見るのは製作職だけ）。</param>
/// <param name="Bag">鞄：要る枠・空き・予備（計算前なら null）。</param>
/// <param name="MainQuest">要るメインクエストのうち一番先のものと、クリア済みか（ゲームデータの読み込み前なら null）。</param>
/// <param name="AetherCurrents">蒼天・紅蓮のエリアと、風脈をすべて開放済みか（読めなければ null）。</param>
/// <param name="Aetherytes">蒼天・紅蓮のフィールドのエーテライトと、解放済みか。</param>
/// <param name="AcceptedQuests">受注中のクエストの数（読めなければ null）。</param>
/// <param name="MaxAccepted">受注中のクエストの数の上限（開始の条件）。</param>
/// <param name="Plugins">必須プラグインと、読み込まれているか。</param>
/// <param name="Gil">所持ギル。</param>
public sealed record StartFacts(
    IReadOnlyList<(uint Job, int Level)> Selected,
    string? GearProblem,
    bool AnyCrafterSelected,
    (int Need, int Free, int Keep)? Bag,
    (string Name, bool Done)? MainQuest,
    IReadOnlyList<(string Zone, bool? Done)> AetherCurrents,
    IReadOnlyList<(string Name, string Zone, bool Unlocked)> Aetherytes,
    int? AcceptedQuests,
    int MaxAccepted,
    IReadOnlyList<(string Name, bool Loaded)> Plugins,
    long Gil);

/// <summary>
/// ジョブクエを始める条件（職を選んだら、条件ごとに緑の ✓ か赤の ✗ を出し、全部そろうまで開始させない）。
///  ・選んだ職が Lv70 以上／選んだ製作職の装備が Lv68 以上（アクセサリーは除く：主道具・副道具・頭・胴・腕・脚・足）
///  ・鞄の空き（要る枠＋予備の枠。予備は設定タブで変える）
///  ・メインクエスト「いざ山岳地帯へ」をクリア済み（ジョブクエ・機能の解放に要るメインクエストのうち一番先のもの。ゲームデータから求める。
///    これをクリアしていれば、精選の解放の前提「わだかまる雲霧」も、潜水の「大海原に泳ぎ出せ！」もクリア済み）
///  ・蒼天・紅蓮のエリアの風脈をすべて開放済み／蒼天・紅蓮のフィールドのエーテライトをすべて解放済み
///    （新生のエリアは、ストーリーを進めても解放しないエーテライトがあるので見ない）
///  ・受注中のクエストの数が上限以下／必須プラグインが読み込まれている／ギル（選んだ職×15万ギル。目安）
/// 判定の本体はゲームに触らない（<see cref="Evaluate"/>）。ゲームから読むのは <see cref="Read"/>。
/// </summary>
public static class StartConditions
{
    /// <summary>1職あたりのギルの目安。</summary>
    public const long GilPerJob = 150_000;

    /// <summary>対象の拡張（蒼天＝1・紅蓮＝2。ExVersion）。</summary>
    public static readonly uint[] Expansions = [1, 2];

    /// <summary>条件を並べて判定する（ゲームに触らない）。</summary>
    public static List<StartCondition> Evaluate(StartFacts f)
    {
        var list = new List<StartCondition>();

        var low = f.Selected.Where(s => s.Level < Jobs.MinimumAutomationLevel).ToList();
        list.Add(new StartCondition(StartConditionKind.Level, $"選択したジョブが Lv{Jobs.MinimumAutomationLevel} 以上",
            f.Selected.Count > 0 && low.Count == 0,
            f.Selected.Count == 0
                ? "ジョブを選んでください"
                : string.Join("・", f.Selected.Select(s => $"{Jobs.Name(s.Job)} Lv{s.Level}"))));

        list.Add(new StartCondition(StartConditionKind.Gear, $"Lv{GearCheck.RequiredEquipLevel} 以上の装備をしていること（アクセサリーは除く）",
            f.GearProblem == null,
            f.GearProblem ?? (f.AnyCrafterSelected
                ? "選んだ製作職の主道具・副道具・頭・胴・腕・脚・足を見ました"
                : "製作職を選んでいないので見ません（採集職の装備は見ません）")));

        list.Add(f.Bag is { } bag
            ? new StartCondition(StartConditionKind.Bag, "選択したジョブクエで必要となる空き枠＋予備枠（予備は設定タブで変更可）",
                BagEstimate.Shortage(bag.Need, bag.Free, bag.Keep) <= 0,
                $"必要 {bag.Need} 枠＋予備 {bag.Keep} 枠 ／ 空き {bag.Free} 枠")
            : new StartCondition(StartConditionKind.Bag, "選択したジョブクエで必要となる空き枠＋予備枠（予備は設定タブで変更可）", null, "計算中（ゲームデータの読み込み後に出ます）"));

        list.Add(f.MainQuest is { } msq
            ? new StartCondition(StartConditionKind.MainQuest, $"メインクエスト「{msq.Name}」をクリア済み", msq.Done,
                msq.Done ? "クリア済み" : "未クリア（ジョブクエの前提・精選の解放・潜水に要ります）")
            : new StartCondition(StartConditionKind.MainQuest, "メインクエストをクリア済み", null, "ゲームデータの読み込み後に出ます"));

        var currentsMissing = f.AetherCurrents.Where(a => a.Done != true).ToList();
        list.Add(new StartCondition(StartConditionKind.AetherCurrents, "蒼天・紅蓮の全ての風脈解放済み",
            f.AetherCurrents.Count == 0 ? null : f.AetherCurrents.Any(a => a.Done == null) ? null : currentsMissing.Count == 0,
            f.AetherCurrents.Count == 0
                ? "読めません"
                : currentsMissing.Count == 0
                    ? $"{f.AetherCurrents.Count} エリアすべて開放済み"
                    : $"未開放：{string.Join("、", currentsMissing.Select(a => a.Done == null ? $"{a.Zone}（読めない）" : a.Zone))}"));

        var aethMissing = f.Aetherytes.Where(a => !a.Unlocked).ToList();
        list.Add(new StartCondition(StartConditionKind.Aetherytes, "蒼天・紅蓮のフィールドのエーテライトを全て解放済み（新生のエリアは見ません）",
            f.Aetherytes.Count == 0 ? null : aethMissing.Count == 0,
            f.Aetherytes.Count == 0
                ? "読めません"
                : aethMissing.Count == 0
                    ? $"{f.Aetherytes.Count} か所すべて解放済み"
                    : $"未解放：{string.Join("、", aethMissing.Select(a => $"{a.Name}（{a.Zone}）"))}"));

        list.Add(new StartCondition(StartConditionKind.QuestCount, $"受注しているクエストの数が {f.MaxAccepted} 件以下である",
            f.AcceptedQuests is { } n ? n <= f.MaxAccepted : null,
            f.AcceptedQuests is { } m ? $"いま {m} 件" : "読めません"));

        var pluginsMissing = f.Plugins.Where(p => !p.Loaded).ToList();
        list.Add(new StartCondition(StartConditionKind.Plugins, "必須プラグインをインストール済み（「必須プラグイン」タブ）",
            pluginsMissing.Count == 0,
            pluginsMissing.Count == 0 ? $"{f.Plugins.Count} 個すべて読み込み済み" : $"入っていない・読み込まれていない：{string.Join("、", pluginsMissing.Select(p => p.Name))}"));

        var gilNeed = GilPerJob * f.Selected.Count;
        list.Add(new StartCondition(StartConditionKind.Gil, $"ギル（選択したジョブ×{GilPerJob / 10_000}万ギル）※目安",
            f.Selected.Count > 0 && f.Gil >= gilNeed,
            $"所持 {f.Gil:N0} ギル ／ 目安 {gilNeed:N0} ギル（{f.Selected.Count} 職）"));

        return list;
    }

    /// <summary>全部そろっているか（まだ分からない条件があれば false）。</summary>
    public static bool AllOk(IEnumerable<StartCondition> list) => list.All(c => c.Ok == true);

    /// <summary>そろっていない条件の文（開始できない理由。そろっていれば null）。</summary>
    public static string? Blocker(IReadOnlyList<StartCondition> list)
    {
        var ng = list.Where(c => c.Ok != true).ToList();
        return ng.Count == 0 ? null : "開始条件がそろっていません：" + string.Join(" / ", ng.Select(c => c.Label));
    }

    /// <summary>
    /// 受注中のクエストの数の上限（開始の条件）：受注枠（QuestManager.NormalQuests。今は30）から2つ引いた数。
    /// この自動化が同時に新しく受けるのは1本ずつで、精選の解放をあきらめたときだけ1本を受けたまま残す。
    /// なので空きが2つあれば、途中で受注の上限に当たらない。
    /// </summary>
    public static int MaxAcceptedFor(int slots) => Math.Max(0, slots - 2);

    // 一番先のメインクエスト（ゲームデータの読み込みごとに1回だけ求める）
    private static uint latestMainQuest;
    private static object? latestMainQuestFor;
    private static List<uint>? fieldZones;

    /// <summary>ゲームから読む（フレームワークのスレッドから呼ぶ）。</summary>
    /// <param name="config">設定（選んだ職）。</param>
    /// <param name="data">ゲームデータの表（読み込み前なら、メインクエストは null）。</param>
    /// <param name="bag">鞄の見込み（計算前なら null）。</param>
    public static unsafe StartFacts Read(Configuration config, GameDataCache data, (int Need, int Free, int Keep)? bag)
    {
        var selected = Jobs.QuestJobs.Where((_, i) => i < config.SelectedCrafters.Length && config.SelectedCrafters[i])
            .Select(j => (j, Jobs.Level(j))).ToList();

        (string, bool)? msq = null;
        if (data.IsReady && data.Quests is { } catalog)
        {
            if (!ReferenceEquals(latestMainQuestFor, catalog))
            {
                latestMainQuest = LatestMainQuest(catalog.Quests);
                latestMainQuestFor = catalog;
            }

            if (latestMainQuest != 0)
                msq = (Unlocks.QuestName(latestMainQuest), FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(latestMainQuest));
        }

        var zones = fieldZones ??= FieldZones();
        var currents = zones.Select(z => (AreaAccess.Name(z), Automation.GameMemory.AetherCurrentsComplete(z))).ToList();
        var unlocked = AreaAccess.UnlockedNow();
        var aetheryteSheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var aetherytes = FieldAetherytes(zones).Select(id =>
        {
            var row = aetheryteSheet.GetRow(id);
            return (row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{id}", AreaAccess.Name(row.Territory.RowId), unlocked.Contains(id));
        }).ToList();

        var qm = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.Instance();
        int? accepted = qm == null ? null : qm->NumAcceptedQuests;
        var slots = qm == null ? 30 : qm->NormalQuests.Length;

        var plugins = Ipc.PluginInstaller.Required.Select(p => (p.Display, Ipc.PluginInstaller.Read(p).State == Ipc.PluginInstaller.State.Loaded)).ToList();

        return new StartFacts(selected, GearCheck.GearProblem(config.SelectedCrafters), selected.Any(s => Jobs.IsCrafter(s.j)), bag, msq,
            currents, aetherytes, accepted, MaxAcceptedFor(slots), plugins, Inventory.Gil());
    }

    // ------------------------------------------------------------------
    // ゲームデータから求めるもの（決め打ちしない）

    /// <summary>風脈のある蒼天・紅蓮のエリア（AetherCurrentCompFlgSet のエリアのうち、拡張が蒼天・紅蓮のもの）。</summary>
    public static List<uint> FieldZones()
    {
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        return Svc.Data.GetExcelSheet<AetherCurrentCompFlgSet>()
            .Select(r => r.Territory.RowId)
            .Where(t => t != 0 && terr.TryGetRow(t, out var row) && Expansions.Contains(row.ExVersion.RowId))
            .Distinct()
            .ToList();
    }

    /// <summary>そのエリアたちにあるエーテライト（エーテライト本体。アルテマイトなどの小さなものは除く）。</summary>
    public static List<uint> FieldAetherytes(IReadOnlyCollection<uint> zones)
        => Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.IsAetheryte && zones.Contains(a.Territory.RowId))
            .Select(a => a.RowId)
            .ToList();

    /// <summary>
    /// ジョブクエ（全部の職）と、自動で解放する機能のクエスト（マテリア装着・精選・刺突漁）に要るメインクエストのうち、一番先のもの（無ければ 0）。
    /// 前提の欄・受注の場面の判定をたどって見つけたメインクエストから、ほかのものを前提の連鎖に持つものを選ぶ（<see cref="MainQuestGate.MostAdvanced"/>）。
    /// </summary>
    public static uint LatestMainQuest(IEnumerable<JobQuest> allQuests)
    {
        var found = MainQuestGate.Missing(allQuests, _ => false, null);
        var unlocks = new[] { Unlocks.UnlockQuest(Unlocks.Meld), Unlocks.UnlockQuest(Unlocks.Reduction), GigUnlockQuest() }.Where(u => u != 0);
        found.UnionWith(MainQuestGate.Missing(unlocks, _ => false));
        return MainQuestGate.MostAdvanced(found);
    }

    /// <summary>刺突漁を解放するクエスト（ギギングの UnlockLink）。無ければ 0。</summary>
    public static uint GigUnlockQuest()
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(GatherAbilities.Gig, out var a) && a.UnlockLink.RowId is > 0x10000 and < 0x20000
            ? a.UnlockLink.RowId
            : 0;
}
