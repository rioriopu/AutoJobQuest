using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>納品物1品目の条件。</summary>
/// <param name="ItemId">納品するアイテム。</param>
/// <param name="Count">個数。</param>
/// <param name="Hq">HQ 指定か。</param>
/// <param name="Evidence">個数の根拠にした本文（確認用）。</param>
public sealed record QuestItemReq(uint ItemId, int Count, bool Hq, string Evidence);

/// <summary>マテリア装着の条件。</summary>
/// <param name="TargetItemId">マテリアを付けるアイテム。</param>
/// <param name="MateriaItemId">指定のマテリア。null なら種類不問。</param>
public sealed record MateriaReq(uint TargetItemId, uint? MateriaItemId);

/// <summary>製作系ジョブクエ1本。</summary>
public sealed class JobQuest
{
    /// <summary>Quest シートの行 ID（65536 以上）。</summary>
    public required uint RowId { get; init; }

    /// <summary>Questionable が使うクエスト番号（RowId − 65536）。</summary>
    public ushort ShortId => (ushort)(this.RowId & 0xFFFF);

    public required uint ClassJobId { get; init; }

    public required int Level { get; init; }

    public required string Name { get; init; }

    public required List<QuestItemReq> Items { get; init; }

    public MateriaReq? Materia { get; init; }

    /// <summary>画面・記録用の短い表記。</summary>
    public override string ToString() => $"Lv{this.Level} {this.Name}";
}

/// <summary>
/// 製作系ジョブクエの納品要件をゲームデータから組み立てる。
///
/// 【どこに何が入っているか（実測済み）】
///  ・品目：Quest.QuestParams のうち命令名が RITEM で始まるものの値。
///  ・個数・HQ：Quest シートには無い。クエスト本文シート quest/NNN/&lt;Quest.Id&gt; にだけある。
///      HQ  … 本文のマクロが &lt;sheet(ItemHQ,ID,0)&gt;（NQ なら &lt;sheet(Item,ID,0)&gt;）
///      個数 … あらすじ（_SEQ_）のうち品目マクロを含む文の「3つ」「12箱」など
///  ・マテリア：TODO に「マテリア/マテリダ」が出るか、Materia シートのアイテムが本文に出るか。
///      付ける対象は「&lt;マテリア&gt;のついた&lt;品目&gt;」の形から読む。
///
/// 本文は日本語シートで読む（数え方の正規表現が日本語のため）。クライアント言語には依存しない。
/// game8 の掲載値 120 行と全件一致することを確かめてある。
///
/// 重いので起動時には作らない。「計画」を押したときに別スレッドで作る。
/// </summary>
public sealed class QuestCatalog
{
    public IReadOnlyList<JobQuest> Quests { get; }

    /// <summary>作るときに気づいたこと（本文が読めなかった等）。</summary>
    public IReadOnlyList<string> Notes { get; }

    private QuestCatalog(List<JobQuest> quests, List<string> notes)
    {
        this.Quests = quests;
        this.Notes = notes;
    }

    private static readonly Regex CountPattern = new(
        @"([0-9０-９]+)(つ|個|本|枚|着|足|組|杯|皿|人前|束|点|セット|箱)|(ひとつ|ふたつ|みっつ)",
        RegexOptions.Compiled);

    private static readonly Regex MacroPattern = new(@"<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex ItemMacroPattern = new(@"<sheet\(Item(?:HQ)?,\s*(\d+),", RegexOptions.Compiled);

    // 「<マテリア>のついた<品目>」。鉤括弧が挟まる書き方もある（TODO_02: 「<sheet(Item,　5676,0)>」のついた<sheet(ItemHQ,　1663,0)>）
    private static readonly Regex AttachedPattern = new(
        @"<sheet\(Item,\s*(\d+),\d+\)>」?\s*のついた\s*「?<sheet\(Item(?:HQ)?,\s*(\d+),",
        RegexOptions.Compiled);

    /// <summary>ゲームデータから組み立てる。maxLevel 以下のクエストだけを対象にする。</summary>
    public static QuestCatalog Build(int maxLevel)
    {
        var notes = new List<string>();
        var quests = Svc.Data.GetExcelSheet<Quest>();
        var questsJa = Svc.Data.GetExcelSheet<Quest>(ClientLanguage.Japanese);

        // マテリアとみなすアイテム（Materia シートの Item 列に載っているもの）
        var materiaItems = new HashSet<uint>();
        foreach (var m in Svc.Data.GetExcelSheet<Materia>())
            foreach (var it in m.Item)
                if (it.RowId != 0)
                    materiaItems.Add(it.RowId);

        // JournalGenre → ジョブ。同じ分類のクエストの ClassJobRequired の多数決で決める。
        // （分類名の文字列で判定すると言語に依存するため。Lv1 のギルド加入クエストなど
        //   ClassJobRequired が 0 のものがあるので、分類単位で決めてから当てはめる）
        var genreJob = quests
            .Where(q => q.JournalGenre.RowId != 0 && q.ClassJobRequired.RowId != 0)
            .GroupBy(q => q.JournalGenre.RowId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(q => q.ClassJobRequired.RowId).OrderByDescending(x => x.Count()).First().Key);

        var result = new List<JobQuest>();
        foreach (var q in quests)
        {
            if (!genreJob.TryGetValue(q.JournalGenre.RowId, out var job) || !Jobs.IsCrafter(job))
                continue;

            // 分類の多数決と、クエスト自身の指定が食い違うものは除く（別ジョブのクエが混ざらないように）
            if (q.ClassJobRequired.RowId != 0 && q.ClassJobRequired.RowId != job)
                continue;

            var level = q.ClassJobLevel[0];
            if (level > maxLevel)
                continue;

            var ritems = new List<uint>();
            foreach (var p in q.QuestParams)
            {
                if (p.ScriptInstruction.ExtractText().StartsWith("RITEM", StringComparison.Ordinal)
                    && p.ScriptArg is > 0 and < 1_000_000)
                    ritems.Add(p.ScriptArg);
            }

            if (ritems.Count == 0)
                continue;

            var idText = q.Id.ExtractText();
            var texts = ReadQuestText(idText, notes, q.RowId);

            var all = string.Join("\n", texts.Select(t => t.Value));
            var todo = string.Join("\n", texts.Where(t => t.Key.Contains("TODO", StringComparison.Ordinal)).Select(t => t.Value));
            var seqs = texts.Where(t => t.Key.Contains("_SEQ_", StringComparison.Ordinal)).Select(t => t.Value).ToList();

            var items = new List<QuestItemReq>();
            foreach (var it in ritems.Distinct())
            {
                var hq = Regex.IsMatch(all, $@"sheet\(ItemHQ,\s*{it},");
                var (count, evidence) = FindCount(seqs, it);
                items.Add(new QuestItemReq(it, count, hq, evidence));
            }

            var materia = FindMateria(all, todo, ritems, materiaItems);

            var name = questsJa.TryGetRow(q.RowId, out var ja) ? ja.Name.ExtractText() : q.Name.ExtractText();
            result.Add(new JobQuest
            {
                RowId = q.RowId,
                ClassJobId = job,
                Level = level,
                Name = name,
                Items = items,
                Materia = materia,
            });
        }

        result = result
            .OrderBy(x => Array.IndexOf(Jobs.Crafters, x.ClassJobId))
            .ThenBy(x => x.Level)
            .ThenBy(x => x.RowId)
            .ToList();

        return new QuestCatalog(result, notes);
    }

    /// <summary>
    /// クエスト本文シートを読む。名前は quest/&lt;番号の上3桁&gt;/&lt;Quest.Id&gt;。
    /// 例: Quest.Id が "ClsWdk020_00142" なら quest/001/ClsWdk020_00142。
    /// </summary>
    private static List<KeyValuePair<string, string>> ReadQuestText(string idText, List<string> notes, uint rowId)
    {
        var list = new List<KeyValuePair<string, string>>();
        var us = idText.LastIndexOf('_');
        if (us < 0 || idText.Length - us - 1 < 3)
        {
            notes.Add($"Quest {rowId}: Id の形が想定外です（{idText}）");
            return list;
        }

        var num = idText[(us + 1)..];
        var sheetName = $"quest/{num[..3]}/{idText}";

        try
        {
            var sheet = Svc.Data.GetExcelSheet<RawRow>(ClientLanguage.Japanese, sheetName);
            foreach (var r in sheet)
                list.Add(new(r.ReadStringColumn(0).ExtractText(), r.ReadStringColumn(1).ToMacroString()));
        }
        catch (Exception ex)
        {
            notes.Add($"Quest {rowId}: 本文シート {sheetName} を読めませんでした（{ex.GetType().Name}）。個数は1とみなします");
        }

        return list;
    }

    /// <summary>
    /// あらすじのうち、その品目のマクロを含む文から「数字＋助数詞」を拾う。
    /// 品目マクロの中の ID を数字と取り違えないよう、マクロを伏せ字にしてから探す。
    /// </summary>
    private static (int Count, string Evidence) FindCount(List<string> seqs, uint itemId)
    {
        var itemRef = new Regex($@"sheet\(Item(HQ)?,\s*{itemId},");
        foreach (var s in seqs)
        {
            foreach (var sentence in s.Split('。'))
            {
                if (!itemRef.IsMatch(sentence))
                    continue;

                var plain = MacroPattern.Replace(sentence, "◇");
                var m = CountPattern.Match(plain);
                if (!m.Success)
                    continue;

                var count = m.Groups[3].Success
                    ? m.Groups[3].Value switch { "ひとつ" => 1, "ふたつ" => 2, _ => 3 }
                    : ZenToInt(m.Groups[1].Value);

                return (count, plain.Trim());
            }
        }

        return (1, string.Empty);
    }

    /// <summary>
    /// マテリア装着が要るかと、その対象を読む。
    /// 複数品目のクエスト（例: 木工 Lv50 はローズウッド材とクラブボウ）では、
    /// 「&lt;マテリア&gt;のついた&lt;品目&gt;」の形からどれに付けるかを決める。
    /// </summary>
    private static MateriaReq? FindMateria(string all, string todo, List<uint> ritems, HashSet<uint> materiaItems)
    {
        var inText = ItemMacroPattern.Matches(all)
            .Select(m => uint.Parse(m.Groups[1].Value))
            .Where(materiaItems.Contains)
            .Distinct()
            .ToList();

        var needs = todo.Contains("マテリア", StringComparison.Ordinal)
                    || todo.Contains("マテリダ", StringComparison.Ordinal)
                    || inText.Count > 0;
        if (!needs)
            return null;

        var attached = AttachedPattern.Match(all);
        if (attached.Success)
        {
            var materiaId = uint.Parse(attached.Groups[1].Value);
            var target = uint.Parse(attached.Groups[2].Value);
            if (ritems.Contains(target))
                return new MateriaReq(target, materiaItems.Contains(materiaId) ? materiaId : null);
        }

        // 形が読めなければ、装備品（マテリア穴のあるもの）を対象にする
        var items = Svc.Data.GetExcelSheet<Item>();
        var equip = ritems.FirstOrDefault(x => items.TryGetRow(x, out var r) && r.MateriaSlotCount > 0);
        if (equip == 0)
            equip = ritems[0];

        return new MateriaReq(equip, inText.Count == 1 ? inText[0] : null);
    }

    private static int ZenToInt(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c);
        return int.TryParse(sb.ToString(), out var v) ? v : 1;
    }
}
