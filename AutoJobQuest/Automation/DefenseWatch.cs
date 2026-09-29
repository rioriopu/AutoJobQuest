using System;
using System.Collections.Generic;
using AutoJobQuest.Core;

namespace AutoJobQuest.Automation;

/// <summary>
/// 攻撃されたときの反撃（RSR は、使い終わったときに途中で Off になっていたら戻さないのが基本。
/// ただし敵に攻撃されていることを検知したら、一時的に ON にする。対象外のモンスターに攻撃されたら、棒立ちにならず必ず反撃する）。
/// 流れ全体（JobQuestFlow）が毎フレーム呼ぶ。
///
///  ・攻撃されている＝戦闘状態で、敵視リストに載った敵か、自分を狙っている敵がいる（戦闘の作業と同じ見方：CombatTask.FindHater）。
///  ・戦闘の作業（CombatTask）の最中は、そちらが敵視リストの敵も倒すので何もしない。Questionable が動いている間も何もしない（自分で戦う）。
///  ・今のジョブが戦闘ジョブなら、その敵をターゲットして RSR を Henched にし、攻撃が届く所まで近づく（<see cref="Engagement"/>。
///    戦闘の作業と同じ部品。遠くから撃ってくる敵にも近寄る）。その間、流れの次の作業は進めない（テレポ・着替え・移動の頼み直しを戦闘中に出さない）。
///    戦闘が解けたら RSR を元に戻す（Henched にする前に読んだモード。いまも Henched のときだけ：RsrRestore）。
///  ・製作職・採集職のときは、戦闘中はジョブを変えられないので戦えない。記録に残し、流れはそのまま進める。
///
/// 止まる条件（以前は上限が無く、RSR が効かないと反撃のまま待ち続けた）：
///  ・RSR が応答しない・送れない（Engagement.Result.RsrFailed）→ <see cref="Result.Failed"/>。
///  ・1体の HP が 45 秒減らない → その敵は諦めて次の敵へ。諦めた敵にしか狙われていない状態が <see cref="OnlyGivenUpLimit"/> 続いたら Failed。
///  ・反撃を始めてから <see cref="OverallLimit"/> たっても戦闘が解けない → Failed。
/// 反撃を始めたときは、自分が頼んだ移動だけを止める（他のプラグインの移動は止めない：OwnMovement.StopIfMine）。
/// </summary>
public sealed class DefenseWatch
{
    public enum Result
    {
        /// <summary>何もしていない（流れを進めてよい）。</summary>
        Idle,

        /// <summary>反撃している（流れの次の作業を進めない）。</summary>
        Defending,

        /// <summary>反撃を続けられない（流れ全体を止める）。</summary>
        Failed,
    }

    /// <summary>反撃を始めてから戦闘が解けるまでの上限。</summary>
    public static readonly TimeSpan OverallLimit = TimeSpan.FromMinutes(5);

    /// <summary>諦めた敵にしか狙われていない（攻撃の入らない敵に追われている）状態の上限。</summary>
    public static readonly TimeSpan OnlyGivenUpLimit = TimeSpan.FromMinutes(2);

    private readonly ICombatWorld world;
    private readonly Engagement engage = new();
    private readonly HashSet<ulong> giveUp = [];

    private bool defending;
    private bool reportedCannotFight;
    private DateTime startedAt;
    private DateTime onlyGivenUpSince = DateTime.MinValue;

    // 狙っている敵（ID・名前 ID・BaseId で引き直す）
    private ulong targetId;
    private uint targetNameId;
    private uint targetBaseId;

    public DefenseWatch(ICombatWorld? world = null)
    {
        this.world = world ?? GameCombatWorld.Instance;
    }

    /// <summary>反撃しているか。</summary>
    public bool Defending => this.defending;

    /// <summary>1フレーム見る（本番）。</summary>
    public Result Tick(TaskContext ctx, out string status)
        => this.Tick(ctx.Log, ctx.Rotation, ctx.Navmesh, ctx.OwnMove, ctx.CombatInProgress,
            () => ctx.Questionable.IsRunning() == true, out status);

    /// <summary>1フレーム見る（試験からも同じものを呼ぶ）。</summary>
    public Result Tick(RunLog log, IRotationControl rsr, INavControl nav, OwnMovement own, bool combatTaskActive, Func<bool> questionableRunning, out string status)
    {
        status = string.Empty;

        // 戦闘状態でなければ、何も呼ばずに終える（IPC を毎フレーム呼ばない）
        if (!this.world.InCombat)
        {
            this.End(log, rsr, nav, own);
            this.reportedCannotFight = false;
            return Result.Idle;
        }

        var questionable = !combatTaskActive && questionableRunning();
        IFoe? attacker = null;
        var onlyGivenUp = false;
        if (!combatTaskActive && !questionable)
            attacker = this.PickTarget(out onlyGivenUp);
        var verdict = DefensePolicy.Decide(true, combatTaskActive, questionable, attacker != null || (!combatTaskActive && !questionable && onlyGivenUp),
            this.world.CombatJob, rsr.IsLoaded);

        switch (verdict)
        {
            case DefensePolicy.Verdict.CannotFight:
                this.End(log, rsr, nav, own);
                if (!this.reportedCannotFight)
                {
                    this.reportedCannotFight = true;
                    var who = attacker?.Name ?? "敵";
                    log.Warn("反撃", this.world.CombatJob
                        ? $"{who} に攻撃されていますが、RotationSolverReborn が読み込まれていないので反撃できません。戦闘が解けるのを待ちます"
                        : $"{who} に攻撃されていますが、今のジョブでは戦えません（戦闘中はジョブを変えられません）。戦闘が解けるのを待ちます");
                }

                status = "攻撃されています（反撃できないので、戦闘が解けるのを待っています）";
                return Result.Idle;

            case DefensePolicy.Verdict.Defend:
                return this.Defend(log, rsr, nav, own, attacker, out status);

            default:
                this.End(log, rsr, nav, own);
                return Result.Idle;
        }
    }

    private Result Defend(RunLog log, IRotationControl rsr, INavControl nav, OwnMovement own, IFoe? attacker, out string status)
    {
        var now = this.world.Now;
        if (!this.defending)
        {
            this.defending = true;
            this.startedAt = now;
            this.onlyGivenUpSince = DateTime.MinValue;
            this.giveUp.Clear();
            log.Warn("反撃", $"{attacker?.Name ?? "敵"} に攻撃されているので、RSR を一時的に Henched にして反撃します（戦闘が解けたら元に戻します）");
        }

        // 自分が頼んだ移動は止める（反撃の間も、計算中だった経路が遅れて動き出したら止める。他のプラグインの移動は止めない）
        if (own.StopIfMine(nav, this.engage.ApproachIssued ? this.engage.ApproachDestination : null))
            Core.DebugLog.Current?.Line("反撃", "攻撃されたので、自分が頼んだ移動を止めました");

        if (now - this.startedAt > OverallLimit)
            return this.Failed(log, rsr, nav, own, $"反撃を始めてから {OverallLimit.TotalMinutes:0} 分たっても戦闘状態が解けません", out status);

        if (attacker == null)
        {
            // 諦めた敵にしか狙われていない＝攻撃の入らない敵に追われている。上限まで待ってから止める
            if (this.onlyGivenUpSince == DateTime.MinValue)
                this.onlyGivenUpSince = now;
            if (now - this.onlyGivenUpSince > OnlyGivenUpLimit)
                return this.Failed(log, rsr, nav, own, $"攻撃の入らない敵に狙われ続けていて、{OnlyGivenUpLimit.TotalMinutes:0} 分たっても戦闘状態が解けません", out status);

            status = "攻撃の入らない敵にだけ狙われています（戦闘が解けるのを待っています）";
            return Result.Defending;
        }

        this.onlyGivenUpSince = DateTime.MinValue;
        if (attacker.Id != this.targetId)
        {
            this.targetId = attacker.Id;
            this.targetNameId = attacker.NameId;
            this.targetBaseId = attacker.BaseId;
            this.engage.Start(this.world, attacker);
        }

        switch (this.engage.Tick(this.world, rsr, nav, attacker, out status))
        {
            case Engagement.Result.Stalled:
                log.Warn("反撃", $"{attacker.Name} の HP が 45 秒減っていないので、この敵は諦めて別の敵を狙います（HP {attacker.Hp}）");
                this.giveUp.Add(attacker.Id);
                this.engage.Forget(this.world);
                this.targetId = 0;

                // 「諦めた敵にしか狙われていない」時間は、諦めた時点から数える（ほかに攻撃してくる敵がいれば、次のフレームで数え直しになる）
                this.onlyGivenUpSince = now;
                return Result.Defending;

            case Engagement.Result.RsrFailed:
                return this.Failed(log, rsr, nav, own, this.engage.Problem, out status);
        }

        status = $"攻撃されているので反撃しています：{status}";
        return Result.Defending;
    }

    /// <summary>狙う敵：いま狙っている敵がまだ生きていればそれ、いなければいま攻撃してくる敵（諦めた敵は除く）。</summary>
    private IFoe? PickTarget(out bool onlyGivenUp)
    {
        onlyGivenUp = false;
        if (this.targetId != 0 && !this.giveUp.Contains(this.targetId)
            && this.world.Get(this.targetId, this.targetNameId, this.targetBaseId) is { } current)
            return current;

        return this.world.FindHater(this.giveUp, out onlyGivenUp);
    }

    private Result Failed(RunLog log, IRotationControl rsr, INavControl nav, OwnMovement own, string reason, out string status)
    {
        status = reason;
        log.Warn("反撃", reason);
        this.End(log, rsr, nav, own);
        return Result.Failed;
    }

    /// <summary>反撃を終える（本番の後始末から）。</summary>
    public void End(TaskContext ctx) => this.End(ctx.Log, ctx.Rotation, ctx.Navmesh, ctx.OwnMove);

    /// <summary>反撃を終える（RSR を元に戻し、こちらが付けたターゲットを外し、近づく移動を止める）。</summary>
    public void End(RunLog log, IRotationControl rsr, INavControl nav, OwnMovement own)
    {
        if (!this.defending)
            return;

        this.defending = false;
        this.engage.StopApproach(nav);
        rsr.ReleaseHenched();
        this.engage.Forget(this.world);
        this.targetId = 0;
        this.giveUp.Clear();
        log.Write("反撃", "反撃を終えました（RSR を元のモードに戻しました）");
    }
}
