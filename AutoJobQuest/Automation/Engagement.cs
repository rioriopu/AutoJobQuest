using System;
using System.Numerics;

namespace AutoJobQuest.Automation;

/// <summary>
/// 1体の敵と戦う（素材集めの戦闘 CombatTask と、攻撃されたときの反撃 DefenseWatch の両方が使う）。
///
/// 戦い方の決まり：
///  ・狙った敵にこちらから近寄り、攻撃が届く距離になったらすぐ攻撃する（攻撃そのものは RSR の Henched に任せる）。
///  ・キャスター・レンジは、射程に入ったらその場で止まって攻撃する（近寄りすぎない。動いていると詠唱が始まらない）。
///  ・攻撃されたら棒立ちにならない。
/// 届くかは <see cref="Data.AttackReach"/>（ジョブの技の射程と視線をゲームに聞く）。聞けないときは 3.5m 以内を「届く」とする。
///
/// 止まる条件：
///  ・最後に HP が減ってから 45 秒（<see cref="StallWatch"/>）→ <see cref="Result.Stalled"/>（呼び出し側がその敵を諦める）。
///  ・RSR が応答しない（Henched を送っても動作中にならない・状態が読めない：HenchedTracker が3回）→ <see cref="Result.RsrFailed"/>。
///  ・RSR へ送れない（IPC の呼び出しが失敗）が3回続く（3秒おきに数える）→ <see cref="Result.RsrFailed"/>。
/// </summary>
public sealed class Engagement
{
    public enum Result
    {
        /// <summary>戦っている（または近づいている）。</summary>
        Running,

        /// <summary>HP が減らない。この敵は諦める。</summary>
        Stalled,

        /// <summary>RSR に攻撃を任せられない。全体を止める。</summary>
        RsrFailed,
    }

    /// <summary>届くかをゲームに聞けないときの「届く」距離（近接でも届く）。</summary>
    public const float FallbackReach = 3.5f;

    /// <summary>乗ったまま近づいてよい距離（これより近いか、攻撃が届くなら降りる）。</summary>
    public const float DismountDistance = 20f;

    /// <summary>RSR へ送れない失敗を、何回続いたら止めるか（数えるのは3秒に1回）。</summary>
    public const int IpcFailureLimit = 3;

    private readonly StallWatch stall = new();

    // 近づく移動（取り消せる探索）。使えないときは SimpleMove で代える
    private readonly OwnPath path = new();
    private bool simpleApproach;
    private DateTime lastApproach = DateTime.MinValue;
    private DateTime dismountAt = DateTime.MinValue;
    private DateTime lastIpcFailure = DateTime.MinValue;
    private int ipcFailures;

    /// <summary>いま狙っている敵（ID）。</summary>
    public ulong TargetId { get; private set; }

    /// <summary>自分が頼んだ近づく移動が動いているかもしれない（止めるときに使う）。</summary>
    public bool ApproachIssued => this.path.Active || this.simpleApproach;

    /// <summary>近づく移動の行き先（止めた後の見張りに使う）。</summary>
    public Vector3? ApproachDestination { get; private set; }

    /// <summary>最後に止まった理由（RsrFailed のとき）。</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>狙い始める（狙い直す）。</summary>
    public void Start(ICombatWorld world, IFoe foe)
    {
        this.TargetId = foe.Id;
        this.stall.Start(foe.Hp, world.Now);
    }

    /// <summary>待たされていた時間（画面が開いていた等）を「HP が減らない時間」に数えない。</summary>
    public void Resume(ICombatWorld world) => this.stall.Resume(world.Now);

    /// <summary>狙いを外す（ハードターゲットがこの敵なら外す）。</summary>
    public void Forget(ICombatWorld world)
    {
        if (this.TargetId != 0 && world.HardTargetId == this.TargetId)
            world.SetHardTarget(null);
        this.TargetId = 0;
    }

    /// <summary>1フレーム戦う。</summary>
    public Result Tick(ICombatWorld world, IRotationControl rsr, INavControl nav, IFoe t, out string status)
    {
        var now = world.Now;
        var dist = Vector3.Distance(world.MyPosition, t.Position);
        status = $"{t.Name} と戦闘中（{dist:0.0}m）";

        // 最後に HP が減ってから 45 秒たったら、その敵は諦める（届かない・他人が先に攻撃した等）。
        // 回復・無敵で HP が増えたときは進展にしない。近づく移動も止める（諦めた敵へ走り続けない）
        if (this.stall.Observe(t.Hp, now))
        {
            this.StopApproach(nav);
            return Result.Stalled;
        }

        var reach = world.InReach(t, out var code);
        var inReach = reach ?? dist <= FallbackReach;

        // 近くまで来たか、攻撃が届くなら降りる（乗ったままでは攻撃できない）。近さは水平の距離でも見る（不具合の例：
        // 飛んだまま敵の上空に浮き、高さの差で3次元の距離が 56m あったので降りず、地上の経路で近づこうとして高度が下がらないまま止まっていた）。
        // 降りる操作（一般アクション 23）は、飛んでいれば着地、地上なら降りる
        var flat = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(t.Position.X, t.Position.Z));

        // 飛んでいるときは、真下に相手と同じ高さの床があるときだけ降りる（不具合の例：アジス・ラーで、島の縁にいる敵の手前の
        // 空中で「降りる」を2秒おきに送り続け、真下に床が無いので高さ -100 前後から -601 まで降り続けて止まった）。
        // 床が無い・別の高さの床しか無いときは、降りずに相手の位置へ飛んで近づき、床の上に来てから降りる
        var overVoid = world.Mounted && world.Flying && !GroundBelowLikeFoe(nav, world.MyPosition, t.Position);
        if (world.Mounted && !overVoid && (inReach || dist < DismountDistance || flat < DismountDistance))
        {
            // 降りると決めたら、飛んで近づく移動を止める（動いたままだと降りる操作とぶつかる）。「降りる」はゲームが受け付けられるとき
            // （Questionable の LandExecutor と同じ：GetActionStatus が 0）だけ、0.5秒おきに送る（以前は受け付けられない
            // ときにも2秒おきに送り、「現在の状態では使用できません」で断られるたびに2秒待っていた）
            this.StopApproach(nav);
            if (world.DismountReady && now - this.dismountAt >= TimeSpan.FromSeconds(0.5))
            {
                this.dismountAt = now;
                world.Dismount();
            }

            status = $"{t.Name} の近くなのでマウントから降ります（飛んでいれば着地してから。水平 {flat:0.0}m）";
            return Result.Running;
        }

        // Henched を入れる（こちらが入れていれば送り直さない。RSR が自分で OFF になったときだけ入れ直す）
        if (!rsr.EnsureHenched())
        {
            // 送れなかった（IPC の呼び出しの失敗）。3秒に1回だけ数え、続いたら止める（1回の失敗では止めない）
            if (now - this.lastIpcFailure >= Ipc.HenchedTracker.ResendInterval)
            {
                this.lastIpcFailure = now;
                this.ipcFailures++;
                Core.DebugLog.Current?.Line("戦闘", $"RSR へ Henched を送れませんでした（{this.ipcFailures}/{IpcFailureLimit} 回目）");
            }

            if (this.ipcFailures >= IpcFailureLimit)
            {
                this.StopApproach(nav);
                this.Problem = $"RSR を Henched モードにできませんでした（送る操作が {this.ipcFailures} 回続けて失敗）";
                return Result.RsrFailed;
            }
        }
        else
        {
            this.ipcFailures = 0;
        }

        if (rsr.HenchedUnresponsive)
        {
            this.StopApproach(nav);
            this.Problem = $"{rsr.HenchedProblem}。記録の IPC 欄を見てください";
            return Result.RsrFailed;
        }

        // ハードターゲットが外れていたら付け直す（Henched はハードターゲットだけを殴る）
        if (world.HardTargetId != t.Id)
            world.SetHardTarget(t);

        if (inReach && !world.Mounted)
        {
            // 届く。自分の近づく移動は止める（キャスターは動いていると詠唱が始まらない）。あとは RSR に任せる
            this.StopApproach(nav);
            status = $"{t.Name} に攻撃が届く位置です（{dist:0.0}m。攻撃は RSR に任せています）";
            return Result.Running;
        }

        // 詠唱中は動かない（歩き出すとゲームが詠唱を中断する。RSR も移動中は詠唱のある魔法を撃たない）
        if (world.Casting)
        {
            status = $"{t.Name} へ詠唱中です（{dist:0.0}m）";
            return Result.Running;
        }

        // 届かない。近づく（止まっていれば頼む。敵が大きく動いたら頼み直す。1秒に1回まで）。
        // 飛んでいれば飛んで近づく（地上の経路では高度が下がらず、上空に浮いたまま止まった）。水平 20m 以内に来たら上で降りる
        var moving = this.simpleApproach
            ? nav.IsMoving()
            : this.path.Tick(nav, world.MyPosition, allowedToMove: true) is OwnPath.State.Searching or OwnPath.State.Following;
        var moved = this.ApproachDestination is { } d && Vector3.Distance(d, t.Position) > 6f;
        if ((!moving || moved) && now - this.lastApproach > TimeSpan.FromSeconds(1))
        {
            this.lastApproach = now;
            if (this.path.Request(nav, world.MyPosition, t.Position, world.Flying))
            {
                this.simpleApproach = false;
                this.ApproachDestination = t.Position;
            }
            else if (nav.MoveCloseTo(t.Position, world.Flying, 2.5f))
            {
                // 取り消せる探索の窓口が使えない。以前の SimpleMove で代える
                this.simpleApproach = true;
                this.ApproachDestination = t.Position;
            }
        }

        status = overVoid
            ? $"{t.Name} の近くですが、足もとに降りられる床が無いので、床の上まで飛んで近づきます（水平 {flat:0.0}m）"
            : reach == false
                ? $"{t.Name} に近づいています（{dist:0.0}m・{(code == 562 ? "見えない位置" : "射程外")}）"
                : $"{t.Name} に近づいています（{dist:0.0}m）";
        return Result.Running;
    }

    /// <summary>
    /// 真下（水平 1.5m 以内）に、相手のいる床と同じ高さ（8m 以内）の床があるか。相手の床は相手の真下の床（浮いている敵でも床で比べる）で、
    /// 見つからなければ相手の高さから下 20m・上 5m の範囲で見る。地図の準備ができていなければ分からないので true（従来どおり降りる）。
    /// </summary>
    public static bool GroundBelowLikeFoe(INavControl nav, Vector3 me, Vector3 foe)
    {
        if (!nav.IsReady())
            return true;
        // 自分の足もとも 2m 上から下へ探す（地図の床は実際の地面より少し高めに作られることがあり、地面すれすれに浮いていると、
        // 自分の高さより下に床が無いと答えて「床が無い」と取り違え、敵の真上で15秒近づき直し続けた）
        if (nav.PointOnFloor(me + new Vector3(0, 2f, 0), true, 1.5f) is not { } mine)
            return false;
        if (nav.PointOnFloor(foe + new Vector3(0, 2f, 0), true, 2f) is { } foeFloor)
            return MathF.Abs(mine.Y - foeFloor.Y) <= 8f;
        return mine.Y >= foe.Y - 20f && mine.Y <= foe.Y + 5f;
    }

    /// <summary>自分が頼んだ近づく移動を止める（探索は取り消し、自分の経路なら止める。他人の経路は止めない）。</summary>
    public void StopApproach(INavControl nav)
    {
        this.path.Stop(nav);
        if (this.simpleApproach && nav.IsMoving())
            nav.Stop();
        this.simpleApproach = false;
    }
}
