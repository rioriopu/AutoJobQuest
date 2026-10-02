using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;

namespace AutoJobQuest.Data;

/// <summary>
/// クエストを進めている人の前にだけ湧く敵の出現位置を、ゲームのデータで見分けて外す（普段いない敵のうち、
/// ゲームのデータで分かるものは自動で外す。新しく入れた PC でも最初から効く。番号はコードに書かず、すべてゲームのデータから引く）。
/// 敵の出現位置（LuminaSupplemental の MobSpawn）は、プレイヤーがゲームの中で見た位置を集めたもの（Allagan Tools の記録・Tracky）で、
/// クエストの敵も入っている（西ザナラーンのアーリマン＝黒魔道士 Lv40「異郷なる友」の敵。実際には野外にいない）。
/// 野外の敵の配置はゲームのデータ（配置ファイル）に無い（サーバー側）が、クエストの敵の置き場所はある：
/// Quest の台本の引数（QuestParams）のうち、命令に ENEMY を含むものの値＝Level の行で、種類 <see cref="LevelTypeBNpc"/>（BNpcBase の置き場所）。
/// 外すのは、次の3つがそろう出現位置だけ：
///  ・クエストの敵の置き場所と、同じエリア・同じ種類（BNpcBase）で、地図座標の距離が <see cref="NearMap"/> 以内
///  ・その敵の名前と種類の組が、どこのエリアでも、クエストの敵の置き場所以外に記録されていない（クエスト専用の敵）
///    （モードゥナのニクス・北部森林のジャッカルのように、クエストが普段の敵と同じ名前・種類の個体を置くことがある。その場合は外さない）
///  ・その敵の名前が、そのエリアの討伐手帳（MonsterNoteTarget）の対象に載っていない。討伐手帳に載る敵は普段から野外にいる
///    （外地ラノシアのリングテイル・西ラノシアのファットドードー・西ザナラーンのカッパーコブラン・東部森林のシルヴァン・スナールは、
///    クエストの敵と同じ種類を使い、クエストの置き場所が普段の生息地の中にあるので、上の2つだけでは外れてしまった。4組とも討伐手帳に載る）
/// ギルドリーヴの討伐対象は使わない（普段の敵と同じ名前・種類の個体を討伐対象に使い〔東ラノシアのウィンドスプライト等〕、
/// リーヴの範囲も広いので、普段の敵と見分けられない）。
/// 外部の資料（Lodestone・Console Games Wiki・Gamer Escape）で、ジョブクエの素材に関わる 63 組を照合し、上の 4 組のほかはクエストの敵と確かめた。
/// 外した敵は、利用者がデバッグタブで戻せる（HuntPrefs.IsRestored）。
/// </summary>
public sealed class EventOnlySpawns
{
    /// <summary>クエストの敵の置き場所と同じとみなす、地図座標の距離（0.3＝ワールドで約 15m。記録は敵が湧いた位置から少し動いた所のこともある）。</summary>
    public const float NearMap = 0.3f;

    /// <summary>Level の表の種類のうち、BNpcBase（敵の種類）の置き場所（EXDSchema の Level.yml：Object の切り替え 9 → BNpcBase）。</summary>
    public const byte LevelTypeBNpc = 9;

    // クエストの台本の敵の置き場所（エリア・種類 → 地図座標とクエストの名前）
    private readonly Dictionary<(uint Territory, uint Base), List<(Vector2 At, string Quest)>> questEnemies = [];

    // 討伐手帳の対象（敵の名前・場所の名前〔PlaceName〕）。場所は TerritoryType.PlaceName と同じ（PlaceNameZone ではない：外地ラノシア＝350）
    private readonly HashSet<(uint Name, uint Place)> monsterNote = [];

    // エリア → 場所の名前（TerritoryType.PlaceName）
    private readonly Dictionary<uint, uint> placeOf = [];

    /// <summary>見つけたクエストの台本の敵の置き場所（Level の行）の数（調べ用）。</summary>
    public int QuestPlaces { get; private set; }

    /// <summary>ゲームのデータから、クエストの台本の敵の置き場所と、討伐手帳の対象を集める。</summary>
    public static EventOnlySpawns Build()
    {
        var r = new EventOnlySpawns();
        var level = Svc.Data.GetExcelSheet<Level>();
        var levels = new HashSet<uint>();
        foreach (var q in Svc.Data.GetExcelSheet<Quest>())
        {
            foreach (var p in q.QuestParams)
            {
                // 先に安い判定（値が Level の行で、敵の置き場所）。命令の名前を読むのはその後
                if (p.ScriptArg == 0 || !level.TryGetRow(p.ScriptArg, out var l) || l.Type != LevelTypeBNpc
                    || !p.ScriptInstruction.ExtractText().Contains("ENEMY", System.StringComparison.Ordinal))
                    continue;
                var at = MapCoords.ToMap(l.Territory.RowId, l.X, l.Z);
                if (at == Vector2.Zero)
                    continue;
                var key = (l.Territory.RowId, l.Object.RowId);
                if (!r.questEnemies.TryGetValue(key, out var list))
                    r.questEnemies[key] = list = [];
                list.Add((at, q.Name.ExtractText()));
                levels.Add(l.RowId);
            }
        }

        r.QuestPlaces = levels.Count;

        foreach (var n in Svc.Data.GetExcelSheet<MonsterNoteTarget>())
        {
            foreach (var zone in n.PlaceNameZone)
            {
                if (n.BNpcName.RowId != 0 && zone.RowId != 0)
                    r.monsterNote.Add((n.BNpcName.RowId, zone.RowId));
            }
        }

        foreach (var t in Svc.Data.GetExcelSheet<TerritoryType>())
            r.placeOf[t.RowId] = t.PlaceName.RowId;

        return r;
    }

    /// <summary>その出現位置がクエストの敵の置き場所と一致すれば、そのクエストの名前。一致しなければ null。</summary>
    public string? QuestAt(uint baseId, uint territory, float mapX, float mapY)
    {
        if (!this.questEnemies.TryGetValue((territory, baseId), out var places))
            return null;
        var here = new Vector2(mapX, mapY);
        foreach (var (at, quest) in places)
        {
            if (Vector2.Distance(at, here) <= NearMap)
                return quest;
        }

        return null;
    }

    /// <summary>その敵が、そのエリアの討伐手帳の対象に載っているか（載っていれば普段から野外にいる）。</summary>
    public bool InMonsterNote(uint nameId, uint territory)
        => this.placeOf.TryGetValue(territory, out var place) && place != 0 && this.monsterNote.Contains((nameId, place));

    /// <summary>
    /// 出現位置のうち、クエスト専用の敵のもの（外す行と、そのクエストの名前）。クエストの敵の置き場所と一致し、その名前と種類の組が、
    /// ほかに（クエストの敵の置き場所以外に）1行も無く、そのエリアの討伐手帳の対象でもないもの。
    /// </summary>
    public Dictionary<MobSpawnPosition, string> QuestOnly(IReadOnlyList<MobSpawnPosition> rows)
    {
        var matched = new Dictionary<MobSpawnPosition, string>(ReferenceEqualityComparer.Instance);
        foreach (var s in rows)
        {
            if (this.QuestAt(s.BNpcBaseId, s.TerritoryTypeId, s.Position.X, s.Position.Y) is { } quest)
                matched[s] = quest;
        }

        // どこかで、クエストの敵の置き場所以外に記録されている名前と種類の組＝普段の敵
        var normal = rows.Where(s => !matched.ContainsKey(s)).Select(s => (s.BNpcNameId, s.BNpcBaseId)).ToHashSet();
        var result = new Dictionary<MobSpawnPosition, string>(ReferenceEqualityComparer.Instance);
        foreach (var (row, quest) in matched)
        {
            if (!normal.Contains((row.BNpcNameId, row.BNpcBaseId)) && !this.InMonsterNote(row.BNpcNameId, row.TerritoryTypeId))
                result[row] = quest;
        }

        return result;
    }
}
