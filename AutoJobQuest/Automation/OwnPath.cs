using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoJobQuest.Automation;

/// <summary>
/// 自分が頼んだ経路探索と、その経路の追従（止めた後・行き先を変えた後・読み込みを解除した後に、古い探索の結果で
/// 歩き出さない）。
///
/// 【以前】vnavmesh の SimpleMove.PathfindAndMoveCloseTo は「探索が終わったら vnavmesh が自分で歩き出す」作りで、
/// Path.Stop は探索中のものを取り消せない。止めた後に遅れて歩き出すのを、25秒の見張りで追いかけていた。
/// 【いま】Nav.PathfindCancelable で経路だけを求め、こちらが <see cref="Tick"/> で結果を受け取って、まだ使ってよいときだけ
/// Path.MoveTo で歩かせる。止めたら結果は捨てる。vnavmesh が自分から歩き出すのは SimpleMove の探索が終わったときだけなので、
/// こちらの探索の結果で遅れて歩き出すことは無い（ffxiv_navmesh の AsyncMoveRequest.cs）。
///
/// 注意（ffxiv_navmesh のソースから）：
///  ・頼んだのと同じフレームで取り消すと、vnavmesh の探索件数が戻らず Nav.PathfindInProgress が true のまま残る。
///    同じフレームのうちは取り消さず、結果を捨てるだけにする（探索は最後まで走るが、こちらが使わないので歩き出さない）。
///  ・取り消した探索は、すぐ終わるとは限らない（前の探索を待ってから Faulted で終わる）。終わるのを待たない。
///  ・経路の最初の点は、頼んだときの出発点。捨ててから渡す。探索の間に経路の上を進んでいたら、通り過ぎた点も捨てる
///    （歩きながら引き直すため。vnavmesh は先頭の点から順にたどるので、残すと戻ろうとする：ffxiv_navmesh の FollowPath.cs）。
///  ・探索の間に経路から離れていたら（反撃など）、その経路は使わない（引き直す）。
///  ・新しい探索の間も、前に渡した経路は歩き続ける（止めない＝立ち止まらない）。前の経路も自分のものとして覚えておき、止めるときは止める。
///  ・範囲（手前で止まる距離）は無い。到着はこちらで判断して止める。
///  ・探索は1件だけ持つ（vnavmesh は頼めば何件でも順番に積む）。
/// </summary>
public sealed class OwnPath
{
    public enum State
    {
        /// <summary>何もしていない（探索も追従もしていない）。</summary>
        Idle,

        /// <summary>探索中。</summary>
        Searching,

        /// <summary>自分の経路をたどっている。</summary>
        Following,

        /// <summary>経路が見つからなかった。</summary>
        NoPath,

        /// <summary>探索が失敗した・経路を渡せなかった。</summary>
        Failed,

        /// <summary>探索の間にこちらが動いていたので、結果を捨てた（数えずに頼み直してよい）。</summary>
        Stale,
    }

    /// <summary>フレームの番号（プラグインが毎フレーム1つ進める。試験では直接動かす）。</summary>
    public static long Frame { get; set; }

    /// <summary>探索の結果の経路から、いまの位置がこれ以上離れていたら結果を使わない（m）。</summary>
    public const float StaleDistance = 5f;

    private CancellationTokenSource? cts;
    private Task<List<Vector3>>? task;
    private long issuedFrame;
    private Vector3 from;
    private bool fly;
    private Vector3? followingEnd;

    /// <summary>探索中か、自分の経路を渡した後か。</summary>
    public bool Active => this.task != null || this.followingEnd != null;

    /// <summary>探索中か。</summary>
    public bool Searching => this.task != null;

    /// <summary>実際に渡した経路の終点（探索中の行き先とは分ける）。</summary>
    public Vector3? FollowingEnd => this.followingEnd;

    /// <summary>歩きの経路の最後の2点がこれより離れていたら、最後の区間（壁・崖を貫く直線）を捨てる。</summary>
    public const float DropGap = 8f;

    /// <summary>途切れた歩きの経路の最後の区間を捨てるか（移動の処理と敵へ近づく処理で使う。既定は捨てない）。</summary>
    public bool DropCutOffEnd { get; init; }

    /// <summary>
    /// 最後に受け取った経路の、最後の2点の隔たり（地上の経路が途中までしか引けなかった目安）。
    /// vnavmesh の地上の経路探しは、行き先にたどり着けないとき「たどり着ける範囲で一番近い所までの経路」の最後に、元の行き先をそのまま
    /// 1点足して返す（ffxiv_navmesh の NavmeshQuery.PathfindMesh：手前に詰める処理は無効化されている）。そのため最後の区間が壁・崖を貫く直線になる。
    /// 届く経路なら最後の2点はほぼ同じ点（string pulling の終点＝行き先に、行き先を足すため）。
    /// </summary>
    public float LastGap { get; private set; }

    /// <summary>最後に渡した経路の長さ（今の位置から経路の点を順にたどった長さ）。</summary>
    public float RouteLength { get; private set; }

    /// <summary>
    /// 探索を頼む（前の探索は取り消す・捨てる）。取り消せる探索の窓口が使えなければ false（呼び出し側が SimpleMove で代える）。
    /// 前に渡した経路は止めない（新しい経路を渡すまで歩き続ける。止めるのは <see cref="Stop"/>）。
    /// </summary>
    public bool Request(INavControl nav, Vector3 start, Vector3 destination, bool flying)
    {
        this.Abandon();
        var source = new CancellationTokenSource();
        if (!nav.TryPathfindCancelable(start, destination, flying, source.Token, out var t) || t == null)
        {
            source.Dispose();
            return false;
        }

        this.cts = source;
        this.task = t;
        this.issuedFrame = Frame;
        this.from = start;
        this.fly = flying;
        return true;
    }

    /// <summary>
    /// 1フレーム見る。探索が終わっていて、使ってよければ経路を渡す。
    /// </summary>
    /// <param name="nav">vnavmesh。</param>
    /// <param name="now">いまの自分の位置。</param>
    /// <param name="allowedToMove">いま歩き出してよいか（画面が開いている・止める途中などは false：結果は捨てる）。</param>
    public State Tick(INavControl nav, Vector3 now, bool allowedToMove)
    {
        if (this.task is { } t)
        {
            if (!t.IsCompleted)
                return State.Searching;

            this.task = null;
            this.DisposeSource();
            if (t.IsFaulted || t.IsCanceled)
            {
                _ = t.Exception; // 読んでおく（読まない例外として記録されないように）
                return State.Failed;
            }

            if (!allowedToMove)
                return State.Idle;

            var points = t.Result;
            if (points == null || points.Count == 0)
                return State.NoPath;
            // vnavmesh の経路は「出発点・途中の点・終点」に行き先を1点足すので必ず3点以上（届く経路なら最後の2点は同じ点）。2点以下は見ない
            this.LastGap = points.Count >= 3 ? Vector3.Distance(points[^2], points[^1]) : 0f;

            // 歩きの経路の最後が大きく途切れていたら（たどり着けない行き先）、壁・崖を貫く最後の区間は捨てて、たどり着ける一番近い所で止まる
            // （不具合の例：崖に向かって走り込んだまま止まった）。小さな途切れ（行き先が机の向こう・台の上など）は従来どおり残す
            if (this.DropCutOffEnd && !this.fly && this.LastGap > DropGap)
                points = points.Take(points.Count - 1).ToList();

            // いまの位置より先の点だけを渡す（出発点・通り過ぎた点は捨てる）。経路から離れていたら使わない
            // 探索の間に動いていなければ、従来どおり出発点だけを捨てて渡す（出発点が navmesh の外〔台の上など〕で、経路の最初の点から
            // 5m 超離れて見えても使えるように）。動いていたら、いまの位置より先の点だけを渡す
            var route = Vector3.Distance(now, this.from) <= StaleDistance
                ? (points.Count > 1 ? points.Skip(1).ToList() : points)
                : AheadOf(points, this.from, now, StaleDistance);
            if (route == null)
                return State.Stale;
            if (!nav.MoveAlong(route, this.fly))
                return State.Failed;

            var length = 0f;
            var at = now;
            foreach (var p in route)
            {
                length += Vector3.Distance(at, p);
                at = p;
            }

            this.RouteLength = length;
            this.followingEnd = route[^1];
            return State.Following;
        }

        if (this.followingEnd is { } end)
        {
            if (this.IsMine(nav, end))
                return State.Following;
            this.followingEnd = null;
        }

        return State.Idle;
    }

    /// <summary>
    /// 経路のうち、いまの位置より先の点。いまの位置に一番近い区間を探し、その区間の終わりの点から返す
    /// （一番近い区間と 1m 以内の差の区間が手前にあれば、経路の順で手前のもの：経路が折り返して近くを2度通るとき、先の区間へ近道しない）。
    /// 1点だけの経路は「頼んだときの出発点 → その点」の区間とみなす。どの区間からも maxOff より離れていれば null（その経路は使わない）。
    /// </summary>
    public static List<Vector3>? AheadOf(IReadOnlyList<Vector3> points, Vector3 from, Vector3 now, float maxOff)
    {
        if (points.Count == 0)
            return null;
        var pts = points.Count == 1 ? new List<Vector3> { from, points[0] } : points;

        var dists = new float[pts.Count - 1];
        var best = float.MaxValue;
        for (var i = 0; i < dists.Length; i++)
        {
            dists[i] = DistanceToSegment(now, pts[i], pts[i + 1]);
            best = MathF.Min(best, dists[i]);
        }

        if (best > maxOff)
            return null;

        var idx = Array.FindIndex(dists, d => d <= best + 1f);
        return pts.Skip(idx + 1).ToList();
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var len = ab.LengthSquared();
        var t = len <= 0 ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len, 0f, 1f);
        return Vector3.Distance(p, a + (ab * t));
    }

    /// <summary>止める：探索は取り消す（同じフレームなら捨てるだけ）。自分の経路をたどっていれば止める。他人の経路は止めない。</summary>
    public void Stop(INavControl nav)
    {
        this.Abandon();
        if (this.followingEnd is { } end && this.IsMine(nav, end))
            nav.Stop();
        this.followingEnd = null;
    }

    private bool IsMine(INavControl nav, Vector3 end)
        => nav.IsFollowingPath() && nav.LastWaypoint() is { } w && Vector3.Distance(w, end) < 1f;

    /// <summary>いまの探索を使わないことにする。頼んだのと同じフレームなら取り消さず、捨てるだけ。</summary>
    private void Abandon()
    {
        if (this.task is not { } t)
            return;

        this.task = null;
        var source = this.cts;
        this.cts = null;

        // 捨てた探索の例外は読んでおく。取り消しの元は、探索が終わってから片づける（向こうが使っている間に捨てない）
        t.ContinueWith(done =>
        {
            _ = done.Exception;
            source?.Dispose();
        }, TaskScheduler.Default);

        if (Frame != this.issuedFrame)
        {
            try
            {
                source?.Cancel();
            }
            catch
            {
                // すでに片づいている
            }
        }
    }

    private void DisposeSource()
    {
        try
        {
            this.cts?.Dispose();
        }
        catch
        {
            // 片づけの失敗は無視する
        }

        this.cts = null;
    }
}
