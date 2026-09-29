using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 敵に「攻撃が届くか」の判断（近接は近寄って、キャスター・レンジは射程に入ったらすぐ攻撃。
/// 攻撃そのものは RSR に任せる）。
///
/// 距離を自分で決め打ちしない。ジョブの「敵を狙う GCD（魔法・ウェポンスキル）」をゲームデータから選び、
/// 実際に届くか（射程・視線）はゲームの ActionManager.GetActionInRangeOrLoS に聞く（RSR の TargetHelper.cs と同じ考え方）。
///  ・近接の役割（ClassJob.Role 1＝タンク・2＝近接）：いちばん低いレベルで覚える技（ファストブレード等。近接の射程）。
///    遠くまで届く技（シールドロブ等）を基準にすると、離れた所から投げ続けて近寄らないため。
///  ・遠隔の役割（3＝レンジ・キャスター、4＝ヒーラー）：覚えている技のうち、いちばん遠くまで届くもの（ゲームの GetActionRange で比べる）。
///    Action.Range の欄は「-1＝武器の射程」で弓の技も -1 なので、欄の値だけでは近接か遠隔か決められない（ゲームデータで確認）。
/// 戻り値の番号（LogMessage で確認）：0＝使える、562＝ターゲットが見えない、565＝範囲外（向き）、566＝射程外。
/// 565 は向きを変えれば使えるので「届く」に入れる（RSR と同じ）。
/// </summary>
public static class AttackReach
{
    /// <summary>候補の技（ゲームデータから）。</summary>
    public readonly record struct Candidate(uint ActionId, byte Level, sbyte Range);

    /// <summary>ゲームに聞いた結果で「届く」とみなす番号。</summary>
    public static readonly uint[] ReachableCodes = [0, 565];

    private static readonly Dictionary<uint, List<Candidate>> CandidateCache = [];
    private static readonly Dictionary<(uint Job, int Level), uint> PickCache = [];

    /// <summary>近接の役割（タンク・近接）か。ゲームデータが読めなければ近接とみなす（近寄る側に倒す）。</summary>
    public static bool IsMeleeRole(uint classJob)
        => !Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var row) || row.Role is 1 or 2;

    /// <summary>
    /// そのジョブ（とその基本クラス）の、敵を狙う GCD（魔法・ウェポンスキル）。PvP の技・プレイヤーの技でないものは除く。
    /// </summary>
    public static List<Candidate> Candidates(uint classJob)
    {
        lock (CandidateCache)
        {
            if (CandidateCache.TryGetValue(classJob, out var cached))
                return cached;
        }

        var jobs = new HashSet<uint> { classJob };
        if (Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(classJob, out var cj) && cj.ClassJobParent.RowId != 0)
            jobs.Add(cj.ClassJobParent.RowId);

        // ActionCategory 2＝魔法、3＝ウェポンスキル（ゲームデータの ActionCategory の名前で確認）
        var list = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(a => a.IsPlayerAction && !a.IsPvP && a.CanTargetHostile && a.Range != 0
                        && a.ActionCategory.RowId is 2 or 3 && jobs.Contains(a.ClassJob.RowId))
            .Select(a => new Candidate(a.RowId, a.ClassJobLevel, a.Range))
            .OrderBy(c => c.Level).ThenBy(c => c.ActionId)
            .ToList();

        lock (CandidateCache)
            CandidateCache[classJob] = list;
        return list;
    }

    /// <summary>
    /// 候補から、届くかを聞くのに使う技を1つ選ぶ（ゲームを起動せずに試せるように、射程はもらう）。0 なら選べない。
    /// </summary>
    /// <param name="melee">近接の役割か。</param>
    /// <param name="candidates">候補（<see cref="Candidates"/>）。</param>
    /// <param name="level">いまのレベル（覚えていない技は選ばない）。</param>
    /// <param name="rangeOf">技の実際の射程（遠隔の役割のときだけ使う）。</param>
    public static uint Choose(bool melee, IReadOnlyList<Candidate> candidates, int level, Func<uint, float> rangeOf)
    {
        var learned = candidates.Where(c => c.Level <= Math.Max(level, 1)).ToList();
        if (learned.Count == 0)
            return 0;

        if (melee)
            return learned.OrderBy(c => c.Level).ThenBy(c => c.ActionId).First().ActionId;

        return learned
            .Select(c => (c.ActionId, c.Level, Range: rangeOf(c.ActionId)))
            .OrderByDescending(x => x.Range).ThenBy(x => x.Level).ThenBy(x => x.ActionId)
            .First().ActionId;
    }

    /// <summary>いまのジョブ・レベルで、届くかを聞くのに使う技（ゲームの射程で選ぶ）。0 なら選べない。</summary>
    public static unsafe uint PickAction(uint classJob, int level)
    {
        if (classJob == 0 || level <= 0)
            return 0;
        if (PickCache.TryGetValue((classJob, level), out var cached))
            return cached;

        uint pick;
        try
        {
            pick = Choose(IsMeleeRole(classJob), Candidates(classJob), level,
                id => FFXIVClientStructs.FFXIV.Client.Game.ActionManager.GetActionRange(id));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"攻撃の届く範囲の技を選べませんでした（{ex.Message}）。距離で判断します");
            pick = 0;
        }

        PickCache[(classJob, level)] = pick;
        if (pick != 0)
            Core.DebugLog.Current?.Line("戦闘", $"攻撃が届くかは {ActionName(pick)}（Action {pick}）で確かめます（{Jobs.Name(classJob)} Lv{level}）");
        return pick;
    }

    /// <summary>
    /// いまの自分から、その敵に攻撃が届くか（射程・視線）。技を選べない・聞けないときは null（呼び出し側が距離で判断する）。
    /// </summary>
    public static unsafe bool? InReach(Dalamud.Game.ClientState.Objects.Types.IGameObject target, out uint code)
    {
        code = 0;
        var me = Svc.Objects.LocalPlayer;
        if (me == null || target == null)
            return null;

        var action = PickAction(me.ClassJob.RowId, me.Level);
        if (action == 0)
            return null;

        try
        {
            code = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.GetActionInRangeOrLoS(
                action,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)me.Address,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address);
            return ReachableCodes.Contains(code);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("戦闘", $"攻撃が届くかをゲームに聞けませんでした（{ex.Message}）。距離で判断します");
            return null;
        }
    }

    private static string ActionName(uint id)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(id, out var a) ? a.Name.ExtractText() : $"#{id}";
}
