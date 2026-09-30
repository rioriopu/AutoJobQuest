using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>ギアセットから読んだ製作能力値。</summary>
/// <param name="GearsetIndex">そのジョブの最初のギアセットの番号（無ければ -1）。</param>
public sealed record CrafterGear(int GearsetIndex, int Craftsmanship, int Control);

/// <summary>
/// 製作装備の点検。
///
/// 【基準値】「ギルショップで買える装備Lv60の製作装備（ノーマル品＝NQ）」の7部位
/// （主道具・副道具・頭・胴・手・脚・足）の作業精度・加工精度の合計。
/// 同じ部位に複数の候補があるときは一番低い値を使う。主道具には「アチーブメント報酬の再購入」の
/// ブレスド系（IL200）もギルで売られているが、ノーマル品ではないので最小値を取れば自然に外れる。
/// 実測（ゲームデータ 2026.09.15）では8職とも 作業精度 611 / 加工精度 564。
/// 数値を決め打ちせず、毎回ゲームデータから計算する。
///
/// 【現在値】各ジョブの最初のギアセットの装備から計算する（GBR の GearsetStatsReader と同じ選び方）。
/// HQ は ItemId が 1,000,000 以上。HQ 加算値とマテリアの値を足す。部位ごとの上限（頭打ち）は掛けない
/// ので、マテリアを上限以上に付けている場合は実際より高く出る。
/// </summary>
public static class GearCheck
{
    /// <summary>BaseParam の行：作業精度・加工精度（行名をゲームデータで確認済み）。</summary>
    public const uint Craftsmanship = 70;
    public const uint Control = 71;

    /// <summary>ギアセットの部位のうち点検する7部位（GearsetItemIndex）。</summary>
    private static readonly int[] CheckedSlots = [0, 1, 2, 3, 4, 6, 7]; // 主・副・頭・胴・手・脚・足（5=腰は除く）

    /// <summary>
    /// 基準値を計算する（重いので別スレッドで1回）。ClassJob → (作業精度, 加工精度)。
    /// </summary>
    public static Dictionary<uint, (int Craftsmanship, int Control)> ComputeBaselines()
    {
        var result = new Dictionary<uint, (int, int)>();

        var gilItems = new HashSet<uint>();
        foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>())
            foreach (var row in shop)
                if (row.Item.RowId != 0)
                    gilItems.Add(row.Item.RowId);

        var esc = Svc.Data.GetExcelSheet<EquipSlotCategory>();
        var cjc = Svc.Data.GetExcelSheet<ClassJobCategory>();
        var cjEn = Svc.Data.GetExcelSheet<ClassJob>(ClientLanguage.English);

        // 部位（0主 1副 2頭 3胴 4手 5脚 6足）× ジョブ → 候補の (作業, 加工) の最小
        var best = new Dictionary<(uint Job, int Slot), (int Cr, int Co)>();
        foreach (var it in Svc.Data.GetExcelSheet<Item>())
        {
            if (it.LevelEquip != 60 || it.EquipSlotCategory.RowId == 0 || !gilItems.Contains(it.RowId))
                continue;

            int cr = 0, co = 0;
            for (var i = 0; i < it.BaseParam.Count; i++)
            {
                var p = it.BaseParam[i].RowId;
                if (p == Craftsmanship)
                    cr += it.BaseParamValue[i];
                else if (p == Control)
                    co += it.BaseParamValue[i];
            }

            if (cr == 0 && co == 0)
                continue;

            if (!esc.TryGetRow(it.EquipSlotCategory.RowId, out var e))
                continue;

            var slots = new List<int>();
            if (e.MainHand == 1) slots.Add(0);
            if (e.OffHand == 1) slots.Add(1);
            if (e.Head == 1) slots.Add(2);
            if (e.Body == 1) slots.Add(3);
            if (e.Gloves == 1) slots.Add(4);
            if (e.Legs == 1) slots.Add(5);
            if (e.Feet == 1) slots.Add(6);
            if (slots.Count != 1)
                continue;

            if (!cjc.TryGetRow(it.ClassJobCategory.RowId, out var cat))
                continue;

            foreach (var job in Jobs.Crafters)
            {
                var abbr = cjEn.TryGetRow(job, out var cj) ? cj.Abbreviation.ExtractText() : string.Empty;
                var prop = typeof(ClassJobCategory).GetProperty(abbr);
                if (prop?.GetValue(cat) is not true)
                    continue;

                var key = (job, slots[0]);
                if (!best.TryGetValue(key, out var cur) || cr + co < cur.Cr + cur.Co)
                    best[key] = (cr, co);
            }
        }

        foreach (var job in Jobs.Crafters)
        {
            int tCr = 0, tCo = 0;
            for (var slot = 0; slot < 7; slot++)
            {
                if (best.TryGetValue((job, slot), out var v))
                {
                    tCr += v.Cr;
                    tCo += v.Co;
                }
            }

            result[job] = (tCr, tCo);
        }

        return result;
    }

    /// <summary>そのジョブの最初のギアセットの番号。無ければ -1。</summary>
    public static unsafe int FindGearset(uint classJobId)
    {
        if (Automation.GameMemory.Test is { } test)
            return test.FindGearset(classJobId);
        var m = RaptureGearsetModule.Instance();
        if (m == null)
            return -1;

        for (var i = 0; i < 100; i++)
        {
            if (!m->IsValidGearset(i))
                continue;
            var g = m->GetGearset(i);
            if (g == null || g->ClassJob != classJobId)
                continue;
            return i;
        }

        return -1;
    }

    /// <summary>そのジョブの最初のギアセットの作業精度・加工精度。</summary>
    /// <summary>
    /// 開始に要る装備の欄（ギアセットの欄の番号と名前）。製作の失敗・NQ で素材を失わないよう、選んだ製作職は、
    /// この7か所が <see cref="RequiredEquipLevel"/> 以上の装備でないと開始できない。
    /// 欄の番号は ClientStructs の RaptureGearsetModule.GearsetItemIndex（主道具0・副道具1・頭2・胴3・腕4・腰5・脚6・足7）。
    /// </summary>
    public static readonly (int Slot, string Name)[] RequiredSlots =
        [(0, "主道具"), (1, "副道具"), (2, "頭"), (3, "胴"), (4, "腕"), (6, "脚"), (7, "足")];

    /// <summary>開始に要る装備のレベル。</summary>
    public const int RequiredEquipLevel = 68;

    /// <summary>
    /// ギアセットの欄の品（欄の番号 → 品。HQ は 100万を足した番号のまま）から、装備レベルが足りない欄（空の欄を含む）の名前。
    /// </summary>
    public static List<string> LowSlots(IReadOnlyDictionary<int, uint> items, Func<uint, int> equipLevel, int minLevel)
        => RequiredSlots.Where(s => !items.TryGetValue(s.Slot, out var id) || id == 0 || equipLevel(id % 1_000_000) < minLevel).Select(s => s.Name).ToList();

    /// <summary>その職のギアセットで、装備レベルが足りない欄（空の欄を含む）。ギアセットが無ければ null。</summary>
    public static unsafe List<string>? LowGearsetSlots(uint classJobId)
    {
        var idx = FindGearset(classJobId);
        if (idx < 0)
            return null;
        if (Automation.GameMemory.Test != null)
            return []; // 検証の仕組み：ギアセットがあれば装備は足りているとみる
        var m = RaptureGearsetModule.Instance();
        var g = m == null ? null : m->GetGearset(idx);
        if (g == null)
            return null;
        var items = new Dictionary<int, uint>();
        for (var k = 0; k < g->Items.Length; k++)
            items[k] = g->Items[k].ItemId;
        var sheet = Svc.Data.GetExcelSheet<Item>();
        return LowSlots(items, id => sheet.TryGetRow(id, out var row) ? row.LevelEquip : 0, RequiredEquipLevel);
    }

    /// <summary>選んだ製作職のうち、ギアセットの装備が足りない職の説明（開始できない理由）。足りていれば null。</summary>
    public static string? GearProblem(bool[] selected)
    {
        var lines = new List<string>();
        for (var i = 0; i < selected.Length && i < Jobs.QuestJobs.Length; i++)
        {
            var job = Jobs.QuestJobs[i];
            if (!selected[i] || !Jobs.Crafters.Contains(job))
                continue;
            var low = LowGearsetSlots(job);
            if (low == null)
                lines.Add($"{Jobs.Name(job)}：ギアセットがありません");
            else if (low.Count > 0)
                lines.Add($"{Jobs.Name(job)}：{string.Join("・", low)}");
        }

        return lines.Count == 0
            ? null
            : $"製作の失敗で素材を失わないよう、選んだ製作職のギアセットの主道具・副道具・頭・胴・腕・脚・足は Lv{RequiredEquipLevel} 以上の装備にしてください"
              + $"（足りない欄：{string.Join(" / ", lines)}）";
    }

    public static unsafe CrafterGear ReadGearset(uint classJobId)
    {
        var idx = FindGearset(classJobId);
        if (idx < 0)
            return new CrafterGear(-1, 0, 0);

        var m = RaptureGearsetModule.Instance();
        var g = m->GetGearset(idx);
        var items = Svc.Data.GetExcelSheet<Item>();
        var materia = Svc.Data.GetExcelSheet<Materia>();

        int cr = 0, co = 0;
        foreach (var slot in CheckedSlots)
        {
            var gi = g->Items[slot];
            if (gi.ItemId == 0)
                continue;

            var hq = gi.ItemId >= 1_000_000;
            var id = gi.ItemId % 1_000_000;
            if (!items.TryGetRow(id, out var it))
                continue;

            for (var i = 0; i < it.BaseParam.Count; i++)
            {
                var p = it.BaseParam[i].RowId;
                if (p == Craftsmanship)
                    cr += it.BaseParamValue[i];
                else if (p == Control)
                    co += it.BaseParamValue[i];
            }

            if (hq)
            {
                for (var i = 0; i < it.BaseParamSpecial.Count; i++)
                {
                    var p = it.BaseParamSpecial[i].RowId;
                    if (p == Craftsmanship)
                        cr += it.BaseParamValueSpecial[i];
                    else if (p == Control)
                        co += it.BaseParamValueSpecial[i];
                }
            }

            for (var mi = 0; mi < 5; mi++)
            {
                var row = gi.Materia[mi];
                if (row == 0 || !materia.TryGetRow(row, out var mr))
                    continue;
                var grade = gi.MateriaGrades[mi];
                if (grade >= mr.Value.Count)
                    continue;
                if (mr.BaseParam.RowId == Craftsmanship)
                    cr += mr.Value[grade];
                else if (mr.BaseParam.RowId == Control)
                    co += mr.Value[grade];
            }
        }

        return new CrafterGear(idx, cr, co);
    }
}
