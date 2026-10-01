using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>歩いていて、相手がこれより遠ければ（水平）、マウントに乗って飛んで近づく。</summary>
    public const float MountApproachDistance = 40f;

    /// <summary>飛んでいるときに着地する、相手からの水平の距離（相手の立つ地面の上で降りる。それ以上近づけないときは DismountDistance で降りる）。</summary>
    public const float FlyingLandDistance = 8f;

    /// <summary>降りる操作を送っても、この間位置が動かず乗ったままなら「ここでは降りられない」とみる。</summary>
    public static readonly TimeSpan LandStuckAfter = TimeSpan.FromSeconds(2.5);

    /// <summary>降りられない場所から、相手のそばへ動いて降り直す回数の上限（その後は HP が減らない上限で諦める）。</summary>
    public const int LandRelocationLimit = 3;

    /// <summary>RSR へ送れない失敗を、何回続いたら止めるか（数えるのは3秒に1回）。</summary>
    public const int IpcFailureLimit = 3;

    private readonly StallWatch stall = new();

    // 近づく移動（取り消せる探索）。使えないときは SimpleMove で代える
    private readonly OwnPath path = new() { DropCutOffEnd = true };
    private bool simpleApproach;
    private DateTime lastApproach = DateTime.MinValue;
    private DateTime dismountAt = DateTime.MinValue;
    private DateTime mountAt = DateTime.MinValue;

    // 降りる操作の進み具合（不具合の例：高地ドラヴァニアで物の上にいて、「降りる」は受け付けられるのに着地も降りることも進まず、
    // 45秒送り続けて諦めた）：降り始めた時刻・最後に位置が動いた時刻と位置・降りられなかった位置・動いて降り直した回数
    private DateTime landSince = DateTime.MinValue;
    private DateTime landProgressAt = DateTime.MinValue;
    private Vector3 landProgressPos;
    private Vector3? landBlockedAt;
    private int landRelocations;

    // 降りる場所の候補（飛んでいるとき。LandingCandidates）：候補・候補を作ったときの相手の位置・今の候補の番号・
    // 今の候補に一番近づいた水平の距離とその時刻・今の候補の真上で足もとの床が見つからなくなった時刻
    private List<Vector3>? landCandidates;
    private Vector3 landCandidatesFor;
    private int landIndex;
    private float landTargetClosest = float.MaxValue;
    private DateTime landTargetClosestAt = DateTime.MinValue;
    private DateTime? overVoidAtTargetSince;

    // 降りる場所の候補の確かめ（ICE を手本に、地上の地形を把握して降りる）。
    //  ・地上の経路：候補ごとに、候補から相手のそばの床まで歩いて行けるかを vnavmesh に問い合わせる（取り消さない：公式版の vnavmesh は
    //    順番待ちの問い合わせを取り消すと後ろが止まることがあるので、要らなくなったら結果を捨てるだけ）
    //  ・空と足もと：相手に近づいて当たり判定が読み込まれたら、候補の真上からレイキャストして、上に物があるか・水面か・降りられない面かを見る
    private List<(Vector3 Pos, Task<List<Vector3>>? Ground)>? landSurvey;
    private DateTime landSurveySince;
    private bool landGroundOrdered;
    private bool landSkyChecked;

    /// <summary>地上の経路の問い合わせを待つ上限（これを過ぎたら、返ってきた分だけで並べる）。</summary>
    public static readonly TimeSpan LandGroundWait = TimeSpan.FromSeconds(2.5);

    /// <summary>相手からこの水平の距離に来たら、候補の空と足もとをレイキャストで確かめる（当たり判定は自分の周りしか読み込まれない）。</summary>
    public const float SkyCheckDistance = 100f;

    /// <summary>飛んで今の候補へ向かって、これだけ近づかなければ、その候補へは届かないとみて次の候補へ。</summary>
    public static readonly TimeSpan LandTargetStuckAfter = TimeSpan.FromSeconds(6);
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

    /// <summary>諦めた理由（Stalled のとき。null なら「HP が 45 秒減っていない」）。</summary>
    public string? StallReason { get; private set; }

    /// <summary>近づく移動で、相手に 1m も近づかないままこれだけたったら、ここからは近づけないとみる。</summary>
    public static readonly TimeSpan ApproachStuckAfter = TimeSpan.FromSeconds(8);

    // 近づく移動の進み具合：一番近づいた水平の距離と、その時刻・歩いて詰まったときにジャンプを試したか
    private float approachClosest = float.MaxValue;
    private DateTime approachClosestAt = DateTime.MinValue;
    private bool jumpTried;

    /// <summary>狙い始める（狙い直す）。</summary>
    public void Start(ICombatWorld world, IFoe foe)
    {
        this.TargetId = foe.Id;
        this.stall.Start(foe.Hp, world.Now);
        this.landSince = DateTime.MinValue;
        this.landBlockedAt = null;
        this.landRelocations = 0;
        this.StallReason = null;
        this.approachClosest = float.MaxValue;
        this.approachClosestAt = world.Now;
        this.landCandidates = null;
        this.jumpTried = false;
    }

    /// <summary>次の降りる場所の候補へ移る（今の候補へ向かった記録を消す）。</summary>
    private void NextLandCandidate(DateTime now, string why)
    {
        this.landIndex++;
        this.landTargetClosest = float.MaxValue;
        this.landTargetClosestAt = now;
        this.overVoidAtTargetSince = null;
        this.landSince = DateTime.MinValue;
        this.lastApproach = DateTime.MinValue;
        Core.DebugLog.Current?.Line("戦闘", $"降りる場所の候補を変えます（{why}。{this.landIndex + 1}/{this.landCandidates?.Count ?? 0} 番目へ）");
    }

    /// <summary>
    /// 飛んでいるときに降りる場所の候補（不具合の例：西ザナラーンなど物の多い場所で、相手の真下の1か所だけを狙い、
    /// 木の枝・岩のアーチの下で飛んで届かない・物の上で降りられないまま飛び回った）。相手の真下の床と、周り 5m・10m・20m の8方向の床のうち、
    /// 本来の地面とつながっている床（vnavmesh：PointOnFloor の allowUnreachable=false と NearestPointReachable）で、相手の床と同じ高さ（6m 以内）のもの。
    /// 2m 以内に重なるものは1つにし、12か所まで（試す順は下）。地図が使えなければ空。
    /// 20m の輪は、物の多い場所の真ん中ではなく開けた所に降りて、降りた後は地上の経路で近づくため（ICE がコスモで、立ち位置を
    /// つながっている床に吸着させ、地上の経路だけで動いて物をよけているのに合わせた）。
    /// </summary>
    public static List<Vector3> LandingCandidates(INavControl nav, Vector3 foe, Vector3 me)
    {
        var list = new List<Vector3>();
        if (!nav.IsReady())
            return list;
        var baseY = nav.PointOnFloor(foe + new Vector3(0, 2f, 0), true, 2f)?.Y ?? foe.Y;

        void Add(Vector3? p)
        {
            if (p is { } v && MathF.Abs(v.Y - baseY) <= 6f && list.TrueForAll(x => Vector3.Distance(x, v) > 2f))
                list.Add(v);
        }

        Add(nav.PointOnFloor(foe + new Vector3(0, 2f, 0), false, 3f));
        var centerCount = list.Count;
        foreach (var r in new[] { 5f, 10f, 20f })
        {
            for (var k = 0; k < 8; k++)
            {
                var a = 2f * MathF.PI * k / 8;
                Add(nav.NearestPointReachable(new Vector3(foe.X + (MathF.Cos(a) * r), baseY, foe.Z + (MathF.Sin(a) * r)), 2f, 6f));
            }
        }

        // 試す順：相手の真下（最初の1つ）を先に、残りは今いる場所から近い順（こちらから来る途中の、開けた空に近い所から）。
        // 相手に近い順だと、物の多い場所では近い候補ほど同じ理由でだめになり、HP が減らない上限（45秒）までに開けた候補へ届かなかった
        var m2 = new Vector2(me.X, me.Z);
        var first = list.Take(centerCount);
        var rest = list.Skip(centerCount).OrderBy(p => Vector2.Distance(new Vector2(p.X, p.Z), m2));
        return first.Concat(rest).Take(12).ToList();
    }

    /// <summary>候補から相手まで地上を歩いて行けるか。</summary>
    public enum GroundReach
    {
        /// <summary>分からない（問い合わせが返っていない・使えない）。</summary>
        Unknown,

        /// <summary>歩いて行ける（地上の経路が途中で切れない）。</summary>
        Walkable,

        /// <summary>歩いては行けない（経路が無い・途中で切れる＝崖・岩の上の相手など）。</summary>
        CutOff,
    }

    /// <summary>候補の空と足もと。</summary>
    public enum SkyKind
    {
        /// <summary>分からない（まだ確かめていない・当たり判定が読み込まれていない）。</summary>
        Unknown,

        /// <summary>上に物が無く、降りられる地面。</summary>
        Clear,

        /// <summary>水面（浅い水なら立てるが、深い水だと泳いで戦えない）。</summary>
        Water,

        /// <summary>上に物がある（岩のアーチ・木の枝・屋根）。真上から降りられない。</summary>
        Overhead,

        /// <summary>降りられない面。</summary>
        Unlandable,
    }

    /// <summary>当たり判定の材質の「降りられない」（vnavmesh の SceneExtractor.ExtractMaterialFlags と同じ読み方）。</summary>
    public const ulong UnlandableMaterial = 0x200000;

    /// <summary>当たり判定の材質の「釣りができる面＝水面」（同上。0xB800 は潜れない水・0xBC00 は潜れる水で、どちらもこのビットがある）。</summary>
    public const ulong FishableMaterial = 0x8000;

    /// <summary>地上の経路の結果から、歩いて行けるか（経路の最後の区間が 3m を超えれば途中で切れている：<see cref="OwnPath.GapOf"/>）。</summary>
    public static GroundReach Ground(Task<List<Vector3>>? task)
    {
        if (task is not { IsCompletedSuccessfully: true })
            return GroundReach.Unknown;
        var path = task.Result;
        return path is { Count: > 0 } && OwnPath.GapOf(path) <= 3f ? GroundReach.Walkable : GroundReach.CutOff;
    }

    /// <summary>
    /// 真上からのレイキャストで最初に当たった面から、候補の空と足もとを決める。最初に当たった面が候補の床より 2m 以上上なら、上に物がある
    /// （西ザナラーンの例：テリトリアル・ラプトルの岩の上の候補は、上 Y 46〜48 に岩のアーチがあった）。
    /// </summary>
    public static SkyKind Sky(Vector3 candidate, (float TopY, ulong Material)? top)
    {
        if (top is not { } t)
            return SkyKind.Unknown;
        if (t.TopY > candidate.Y + 2f)
            return SkyKind.Overhead;

        // 最初に当たった面が候補の床より 2m 以上下なら、候補の面（岩などの置き物）の当たり判定がまだ無い・見えていないので分からない
        // （その下の水面・地面の材質で決めない。地形だけを読んだ調べで、置き物の岩の上の候補が下の水面と取り違えられた）
        if (t.TopY < candidate.Y - 2f)
            return SkyKind.Unknown;
        if ((t.Material & UnlandableMaterial) != 0)
            return SkyKind.Unlandable;
        if ((t.Material & FishableMaterial) != 0)
            return SkyKind.Water;
        return SkyKind.Clear;
    }

    /// <summary>
    /// 候補を試す順の段（小さいほど先。-1 は外す）。歩いて行けて上が開けた地面を先に、水面・上に物がある所を後に、歩いて行けない所を最後に
    /// （歩いて行けない所も、射程の長いジョブは降りた所から攻撃が届くことがあるので外さない）。降りられない面は外す。
    /// </summary>
    public static int Tier(GroundReach ground, SkyKind sky)
    {
        if (sky == SkyKind.Unlandable)
            return -1;
        var skyRank = sky switch { SkyKind.Clear => 0, SkyKind.Unknown => 1, SkyKind.Water => 2, _ => 3 };
        return ground switch
        {
            GroundReach.Walkable => skyRank,
            GroundReach.Unknown => 4 + (skyRank >= 2 ? 1 : 0),
            _ => 6 + (skyRank >= 2 ? 1 : 0),
        };
    }

    /// <summary>地上の経路を問い合わせ始める（候補を作ったとき）。行き先は相手のそばのつながっている床（ICE の立ち位置の吸着と同じ）。</summary>
    private void StartLandingSurvey(INavControl nav, Vector3 foe, DateTime now)
    {
        var goal = nav.NearestPointReachable(foe, 3f, 5f) ?? foe;
        this.landSurvey = this.landCandidates!
            .Select(c => (c, nav.TryPathfindCancelable(c, goal, false, CancellationToken.None, out var task) ? task : null))
            .ToList();
        this.landSurveySince = now;
        this.landGroundOrdered = this.landSurvey.All(x => x.Ground == null); // 問い合わせが使えなければ、並べ直さない（従来の順）
        this.landSkyChecked = false;
    }

    /// <summary>
    /// 地上の経路が返ってきたら（または待つ上限を過ぎたら）並べ直し、相手に近づいたら空と足もとを確かめてもう一度並べ直す。
    /// 並べ直すのは、まだ試していない候補（今の番号から後）だけ。
    /// </summary>
    private void UpdateLandingSurvey(ICombatWorld world, Vector3 foe, DateTime now)
    {
        if (this.landSurvey == null || this.landCandidates == null)
            return;

        var reorder = false;
        if (!this.landGroundOrdered && (this.landSurvey.All(x => x.Ground is not { IsCompleted: false }) || now - this.landSurveySince > LandGroundWait))
        {
            this.landGroundOrdered = true;
            reorder = true;
        }

        var flat = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(foe.X, foe.Z));
        var sky = new Dictionary<Vector3, SkyKind>();
        if (!this.landSkyChecked && this.landGroundOrdered && flat <= SkyCheckDistance)
        {
            this.landSkyChecked = true;
            foreach (var c in this.landCandidates.Skip(this.landIndex))
                sky[c] = Sky(c, world.SkyAbove(c));
            reorder = sky.Values.Any(v => v != SkyKind.Unknown) || reorder;
        }

        if (!reorder)
            return;

        var ground = this.landSurvey.ToDictionary(x => x.Pos, x => Ground(x.Ground));
        var rest = this.landCandidates.Skip(this.landIndex)
            .Select((c, i) => (Pos: c, Index: i, Ground: ground.GetValueOrDefault(c), Sky: sky.GetValueOrDefault(c)))
            .Select(x => (x.Pos, x.Index, x.Ground, x.Sky, Tier: Tier(x.Ground, x.Sky)))
            .ToList();
        var ordered = rest.Where(x => x.Tier >= 0).OrderBy(x => x.Tier).ThenBy(x => x.Index).ToList();
        this.landCandidates = this.landCandidates.Take(this.landIndex).Concat(ordered.Select(x => x.Pos)).ToList();
        Core.DebugLog.Current?.Line("戦闘", $"降りる場所の候補を、地上の経路{(sky.Count > 0 ? "と空・足もと" : string.Empty)}で並べ直しました：" + string.Join(" ／ ",
            rest.OrderBy(x => x.Tier < 0 ? int.MaxValue : x.Tier).ThenBy(x => x.Index).Select(x => $"({x.Pos.X:0},{x.Pos.Y:0},{x.Pos.Z:0}) {GroundText(x.Ground)}・{SkyText(x.Sky)}{(x.Tier < 0 ? "→外す" : string.Empty)}")));
    }

    private static string GroundText(GroundReach g) => g switch { GroundReach.Walkable => "歩いて行ける", GroundReach.CutOff => "歩いては行けない", _ => "経路不明" };

    private static string SkyText(SkyKind s) => s switch
    {
        SkyKind.Clear => "地面",
        SkyKind.Water => "水面",
        SkyKind.Overhead => "上に物",
        SkyKind.Unlandable => "降りられない面",
        _ => "空は未確認",
    };

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
            this.StallReason = null;
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

        // 降りる距離：地上で乗っていれば、攻撃が届くか 20m 以内。飛んでいれば、相手の立つ地面の上（水平 8m 以内）まで来てから着地する
        // （手前で降りると途中の物の上に降りようとしやすい）。それ以上近づけない（近づく移動が止まった）ときは 20m 以内で降りる
        var near = inReach || dist < DismountDistance || flat < DismountDistance;

        // 飛んでいるときの降りる場所は、vnavmesh の地図で決める：相手の真下の地面のうち、本来の地面とつながっている床
        // （PointOnFloor の allowUnreachable=false。岩の上など、つながっていない孤立した面は外れる。vnavmesh で物や地形の構造を把握して、
        // 不安定な動きを避ける）。その点へ飛び、着いたら（水平 2.5m）降りる。
        // 地図が使えない・点が無いときは、相手から水平 8m 以内で降りる
        // 候補は複数（LandingCandidates）。相手が 6m 以上動いたら作り直す。候補へ飛んで 6秒近づけない・候補の真上で足もとの床が 3秒見つからない・
        // 降りる操作が効かない（下）ときは次の候補へ。全部だめならこの個体を諦める（以前は1か所だけを狙い、45秒たつまで飛び回った）
        Vector3? landAt = null;
        if (world.Mounted && world.Flying)
        {
            if (this.landCandidates == null || Vector3.Distance(this.landCandidatesFor, t.Position) > 6f)
            {
                this.landCandidates = LandingCandidates(nav, t.Position, world.MyPosition);
                this.landCandidatesFor = t.Position;
                this.landIndex = 0;
                this.landTargetClosest = float.MaxValue;
                this.landTargetClosestAt = now;
                this.overVoidAtTargetSince = null;
                this.StartLandingSurvey(nav, t.Position, now);
            }

            this.UpdateLandingSurvey(world, t.Position, now);

            if (this.landCandidates.Count > 0)
            {
                if (this.landIndex >= this.landCandidates.Count)
                {
                    this.StopApproach(nav);
                    this.StallReason = $"の近くに降りられる場所が見つかりません（vnavmesh の床の候補 {this.landCandidates.Count} か所のどこにも、飛んで届かないか降りられませんでした）。";
                    return Result.Stalled;
                }

                landAt = this.landCandidates[this.landIndex];
                var toLand = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(landAt.Value.X, landAt.Value.Z));
                if (toLand < this.landTargetClosest - 1f)
                {
                    this.landTargetClosest = toLand;
                    this.landTargetClosestAt = now;
                }

                if (toLand > 2.5f && now - this.landTargetClosestAt > LandTargetStuckAfter)
                    this.NextLandCandidate(now, $"飛んで {LandTargetStuckAfter.TotalSeconds:0}秒近づけません（あと水平 {toLand:0.0}m）");
                else if (toLand <= 2.5f && overVoid)
                {
                    this.overVoidAtTargetSince ??= now;
                    if (now - this.overVoidAtTargetSince.Value > TimeSpan.FromSeconds(3))
                        this.NextLandCandidate(now, "真上に来ても足もとに床が見つかりません");
                }
                else
                {
                    this.overVoidAtTargetSince = null;
                }

                landAt = this.landIndex < this.landCandidates.Count ? this.landCandidates[this.landIndex] : null;
            }
        }

        var overLanding = landAt is { } la
            ? Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(la.X, la.Z)) <= 2.5f
            : flat <= FlyingLandDistance;
        var haveCandidates = world.Flying && this.landCandidates is { Count: > 0 };

        // 候補の上に着地した（乗ったまま地上にいる）なら、相手まで遠くても降りる（20m の輪の候補に着地したあと、相手まで 20m 以上あると
        // 「近くない」として乗ったまま飛び立ち、物の多い相手の真上へまた向かってしまう）
        var onCandidate = world.Mounted && !world.Flying && this.landCandidates is { Count: > 0 } lc && this.landIndex < lc.Count
                          && Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(lc[this.landIndex].X, lc[this.landIndex].Z)) <= 4f;
        var wantLand = world.Mounted && !overVoid
                       && (world.Flying
                           ? overLanding || (!haveCandidates && near && !nav.IsMoving() && this.lastApproach != DateTime.MinValue)
                           : near || onCandidate);

        // 飛んでいて、候補の地上の経路を確かめている間は降りない（2.5秒まで。確かめてから、歩いて行ける候補に降りる）
        if (world.Flying && haveCandidates && !this.landGroundOrdered)
            wantLand = false;

        // 降りられなかった場所から動いている間は降りない。相手のそば（水平 3m）に着いたか、離れて（5m）止まったら降り直す
        if (this.landBlockedAt is { } blocked)
        {
            var away = Vector2.Distance(new Vector2(world.MyPosition.X, world.MyPosition.Z), new Vector2(blocked.X, blocked.Z));
            if (!world.Mounted || flat <= 3f || (away >= 5f && !nav.IsMoving()))
                this.landBlockedAt = null;
            else
                wantLand = false;
        }

        // 深い水の上（乗ったまま水面にいて泳いでいる）では降りない。降りると泳いで攻撃できない。次の候補へ飛び立つ
        // （vnavmesh は、行き先が上にあれば自分でジャンプして飛び立つ：FollowPath.cs。不具合の例：西ザナラーンの水場）
        if (wantLand && world.Swimming)
        {
            wantLand = false;
            if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count && now - this.landTargetClosestAt > TimeSpan.FromSeconds(1))
            {
                this.NextLandCandidate(now, "水の中です（泳いでいる。降りると攻撃できない）");
                this.lastApproach = DateTime.MinValue;
            }

            status = $"{t.Name} の近くですが、水の中なので降りずに、別の降りる場所へ移ります";
            if (this.landCandidates is { Count: > 0 } lcs && this.landIndex >= lcs.Count)
            {
                this.StopApproach(nav);
                this.StallReason = "の近くに、水の中でない降りられる場所が見つかりません。";
                return Result.Stalled;
            }
        }

        if (wantLand)
        {
            this.approachClosestAt = now;
            // 降りると決めたら、飛んで近づく移動を止める（動いたままだと降りる操作とぶつかる）。「降りる」はゲームが受け付けられるとき
            // （Questionable の LandExecutor と同じ：GetActionStatus が 0）だけ、0.5秒おきに送る（以前は受け付けられない
            // ときにも2秒おきに送り、「現在の状態では使用できません」で断られるたびに2秒待っていた）
            this.StopApproach(nav);

            // 降りる操作が効いているかを位置の変化で見る。送っても 2.5秒位置が動かず乗ったままなら、ここでは降りられない（物の上など）。
            // 相手のそば（相手の立つ地面の上）へ動いてから降り直す（3回まで。その後は HP が減らない上限で諦める）
            if (this.landSince == DateTime.MinValue)
            {
                this.landSince = now;
                this.landProgressAt = now;
                this.landProgressPos = world.MyPosition;
            }
            else if (Vector3.Distance(world.MyPosition, this.landProgressPos) >= 0.3f)
            {
                this.landProgressAt = now;
                this.landProgressPos = world.MyPosition;
            }
            else if (now - this.landProgressAt >= LandStuckAfter && this.landCandidates is { Count: > 0 } && world.Flying
                     && (this.dismountAt >= this.landSince || now - this.landSince >= TimeSpan.FromSeconds(5)))
            {
                // 候補があれば、次の候補へ（同じ場所へ飛び直しても降りられない）
                this.NextLandCandidate(now, "降りる操作を送っても位置が動きません（物の上など）");
                status = $"{t.Name} の近くですが、ここでは降りられないので、別の降りる場所へ移ります（{this.landIndex + 1}/{this.landCandidates.Count} 番目）";
                return Result.Running;
            }
            else if (now - this.landProgressAt >= LandStuckAfter && this.landRelocations < LandRelocationLimit
                     && (this.dismountAt >= this.landSince || now - this.landSince >= TimeSpan.FromSeconds(5)))
            {
                this.landRelocations++;
                this.landBlockedAt = world.MyPosition;
                this.landSince = DateTime.MinValue;
                this.lastApproach = DateTime.MinValue;
                status = $"{t.Name} の近くですが、ここでは降りられないので（物の上など）、相手のそばまで動いてから降り直します（{this.landRelocations}/{LandRelocationLimit} 回目）";
                return Result.Running;
            }

            if (world.DismountReady && now - this.dismountAt >= TimeSpan.FromSeconds(0.5))
            {
                this.dismountAt = now;
                world.Dismount();
            }

            status = $"{t.Name} の近くなのでマウントから降ります（飛んでいれば着地してから。水平 {flat:0.0}m）";
            return Result.Running;
        }

        this.landSince = DateTime.MinValue;

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

        // 降りた後に泳いでいる（深い水に入った）ときは戦えないので、乗れれば乗って、次の降りる場所へ移る
        if (!world.Mounted && world.Swimming && world.CanMountNow)
        {
            this.approachClosestAt = now;
            if (now - this.mountAt >= TimeSpan.FromSeconds(2))
            {
                if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count)
                    this.NextLandCandidate(now, "降りた所が水の中でした（泳いでいる）");
                this.mountAt = now;
                this.StopApproach(nav);
                world.Mount();
            }

            status = $"{t.Name} の近くですが、水の中では戦えないので、乗って別の場所へ移ります";
            return Result.Running;
        }

        if (inReach && !world.Mounted)
        {
            this.approachClosestAt = now;
            // 届く。自分の近づく移動は止める（キャスターは動いていると詠唱が始まらない）。あとは RSR に任せる
            this.StopApproach(nav);
            status = $"{t.Name} に攻撃が届く位置です（{dist:0.0}m。攻撃は RSR に任せています）";
            return Result.Running;
        }

        // 詠唱中は動かない（歩き出すとゲームが詠唱を中断する。RSR も移動中は詠唱のある魔法を撃たない）
        if (world.Casting)
        {
            this.approachClosestAt = now;
            status = $"{t.Name} へ詠唱中です（{dist:0.0}m）";
            return Result.Running;
        }

        // 歩いていて相手が遠ければ（水平 40m 超）、乗って飛んで近づく（倒したらすぐ乗って次の群れへ飛び、いれば降りて倒す）。
        // 乗れないとき（戦闘中・乗れないエリア）は歩く
        if (!world.Mounted && flat > MountApproachDistance && world.CanMountNow)
        {
            this.approachClosestAt = now;
            if (now - this.mountAt >= TimeSpan.FromSeconds(2))
            {
                this.mountAt = now;
                this.StopApproach(nav);
                world.Mount();
            }

            status = $"{t.Name} は遠い（水平 {flat:0}m）ので、マウントに乗って近づきます";
            return Result.Running;
        }

        // 乗っていて飛べるエリアなら、飛ぶ経路で近づく（地上で乗った直後でも、飛び立って向かう）
        var flyPath = world.Flying || (world.Mounted && world.CanFlyHere);

        // 詰まりの判定（壁に向かって走り続けないか）。以前は HP が 45 秒減らないときだけ諦め、
        // その間は崖の上の敵へ壁に向かって走り続けた。
        //  ・歩きの経路が途中で途切れている（vnavmesh の地上の経路の最後の区間が壁を貫く：OwnPath.LastGap）＝歩いては行けない
        //  ・近づく移動をしているのに、8秒たっても相手に 1m も近づかない
        // どちらも、乗れるなら乗って飛んで近づき、乗れない（戦闘中など）なら、この個体は諦めて別の個体を探す
        if (flat < this.approachClosest - 1f)
        {
            this.approachClosest = flat;
            this.approachClosestAt = now;
        }

        var cutOff = !flyPath && this.path.Active && this.path.LastGap > 3f;

        // 歩いていて近づかないときは、まず1回ジャンプを試す（ICE の詰まったときと同じ。小さな段差・物の角に引っかかったときに抜けられる）。
        // 経路が途切れている（歩いては行けない）ときは試さない
        if (!cutOff && !world.Mounted && !flyPath && !this.jumpTried && now - this.approachClosestAt > ApproachStuckAfter)
        {
            this.jumpTried = true;
            this.approachClosestAt = now;
            world.Jump();
            status = $"{t.Name} へ近づけないので、ジャンプして抜けます";
            return Result.Running;
        }

        if (cutOff || now - this.approachClosestAt > ApproachStuckAfter)
        {
            if (!world.Mounted && world.CanMountNow)
            {
                if (now - this.mountAt >= TimeSpan.FromSeconds(2))
                {
                    // 降りた候補から歩いて行けないなら、次の候補へ移ってから乗る（同じ候補へ飛んで降りる、を繰り返さない）
                    if (this.landCandidates is { Count: > 0 } && this.landIndex < this.landCandidates.Count)
                        this.NextLandCandidate(now, "降りた所から歩いて近づけません");
                    this.mountAt = now;
                    this.StopApproach(nav);
                    world.Mount();
                    this.approachClosestAt = now;
                }

                status = $"{t.Name} へは歩いて近づけないので、マウントに乗って飛んで近づきます";
                return Result.Running;
            }

            if (!flyPath)
            {
                this.StopApproach(nav);
                this.StallReason = cutOff
                    ? $"へは歩いて行けません（vnavmesh の経路が {this.path.LastGap:0}m 手前で途切れています）。乗れないので"
                    : $"に {ApproachStuckAfter.TotalSeconds:0}秒近づけません。乗れないので";
                return Result.Stalled;
            }
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
            // 飛んでいれば、vnavmesh の地図で決めた降りる場所へ飛ぶ。地上なら、相手のそばの本来の地面とつながっている床へ歩く
            // （ICE がコスモで立ち位置を NearestPointReachable に吸着させるのと同じ。相手が岩の上・物の際にいても、届く床へ向かう）。無ければ相手の位置
            var goal = landAt ?? (flyPath ? null : nav.NearestPointReachable(t.Position, 3f, 5f)) ?? t.Position;
            if (this.path.Request(nav, world.MyPosition, goal, flyPath))
            {
                this.simpleApproach = false;
                this.ApproachDestination = t.Position;
            }
            else if (nav.MoveCloseTo(goal, flyPath, 2.5f))
            {
                // 取り消せる探索の窓口が使えない。以前の SimpleMove で代える
                this.simpleApproach = true;
                this.ApproachDestination = t.Position;
            }
        }

        status = this.landBlockedAt != null
            ? $"{t.Name} の近くですが、ここでは降りられなかったので、相手のそばまで動いています（水平 {flat:0.0}m）"
            : overVoid
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
