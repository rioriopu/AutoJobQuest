using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoJobQuest.Automation;

/// <summary>
/// 攻撃されたときの反撃（RSR は、使い終わったときに途中で Off になっていたら戻さないのが基本。
/// ただし敵に攻撃されていることを検知したら、一時的に ON にする）。流れ全体（JobQuestFlow）が毎フレーム呼ぶ。
///
///  ・攻撃されている＝戦闘状態で、敵視リストに載った敵か、自分を狙っている敵がいる（戦闘の作業と同じ見方：CombatTask.FindHater）。
///  ・戦闘の作業（CombatTask）の最中は、そちらが敵視リストの敵も倒すので何もしない。Questionable が動いている間も何もしない（自分で戦う）。
///  ・今のジョブが戦闘ジョブなら、その敵をターゲットして RSR を Henched にする（戦闘の作業と同じやり方。指定の敵だけを殴る）。
///    その間、流れの次の作業は進めない（テレポ・着替え・移動の頼み直しを戦闘中に出さない）。
///    戦闘が解けたら RSR を元に戻す（Henched にする前に読んだモード。いまも Henched のときだけ：RsrRestore）。
///  ・製作職・採集職のときは、戦闘中はジョブを変えられないので戦えない。記録に残し、今までどおり戦闘が解けるのを待つ。
/// </summary>
public sealed class DefenseWatch
{
    private bool defending;
    private bool reportedCannotFight;
    private ulong targetId;
    private HashSet<uint>? combatJobs;

    /// <summary>1フレーム見る。反撃している（流れの次の作業を進めない）なら true。</summary>
    public bool Tick(TaskContext ctx, out string status)
    {
        status = string.Empty;

        // 戦闘状態でなければ、何も呼ばずに終える（IPC を毎フレーム呼ばない）
        if (!GameUi.InCombat)
        {
            this.End(ctx);
            this.reportedCannotFight = false;
            return false;
        }

        var questionable = !ctx.CombatInProgress && ctx.Questionable.IsRunning() == true;
        var attacker = ctx.CombatInProgress || questionable ? null : CombatTask.FindHater([], out _);
        this.combatJobs ??= Jobs.CombatJobs().Select(c => c.RowId).ToHashSet();
        var job = Jobs.CurrentClassJob;
        var verdict = DefensePolicy.Decide(true, ctx.CombatInProgress, questionable, attacker != null, this.combatJobs.Contains(job), ctx.Rotation.IsLoaded);

        switch (verdict)
        {
            case DefensePolicy.Verdict.CannotFight:
                this.End(ctx);
                if (!this.reportedCannotFight)
                {
                    this.reportedCannotFight = true;
                    ctx.Log.Warn("反撃", this.combatJobs.Contains(job)
                        ? $"{attacker!.Name} に攻撃されていますが、RotationSolverReborn が読み込まれていないので反撃できません。戦闘が解けるのを待ちます"
                        : $"{attacker!.Name} に攻撃されていますが、今のジョブ（{Jobs.Name(job)}）では戦えません（戦闘中はジョブを変えられません）。戦闘が解けるのを待ちます");
                }

                status = "攻撃されています（反撃できないので、戦闘が解けるのを待っています）";
                return false;

            case DefensePolicy.Verdict.Defend:
                if (!this.defending)
                {
                    this.defending = true;
                    ctx.Log.Warn("反撃", $"{attacker!.Name} に攻撃されているので、RSR を一時的に Henched にして反撃します（戦闘が解けたら元に戻します）");
                }

                // 狙っている敵が倒れた・別の敵に変わったら、いま攻撃してくる敵を狙い直す
                if (Svc.Targets.Target is not IBattleNpc current || current.GameObjectId != this.targetId || current.IsDead)
                {
                    Svc.Targets.Target = attacker;
                    this.targetId = attacker!.GameObjectId;
                }

                ctx.Rotation.EnsureHenched();
                status = $"攻撃されているので反撃しています（{attacker!.Name}）";
                return true;

            default:
                this.End(ctx);
                return false;
        }
    }

    /// <summary>反撃を終える（RSR を元に戻し、こちらが付けたターゲットを外す）。</summary>
    public void End(TaskContext ctx)
    {
        if (!this.defending)
            return;

        this.defending = false;
        ctx.Rotation.ReleaseHenched();
        if (this.targetId != 0 && Svc.Targets.Target?.GameObjectId == this.targetId)
            Svc.Targets.Target = null;
        this.targetId = 0;
        ctx.Log.Write("反撃", "反撃を終えました（RSR を元のモードに戻しました）");
    }
}
