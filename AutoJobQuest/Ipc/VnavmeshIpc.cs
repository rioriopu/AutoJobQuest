using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AutoJobQuest.Ipc;

/// <summary>
/// vnavmesh への窓口（地面の点の問い合わせも含む）。
///
/// IPC 名と型は vnavmesh のソースで確認済み（ffxiv_navmesh/vnavmesh/IPCProvider.cs）。
///
/// 注意（実装から確かめた事実）:
///  ・経路探索に失敗しても例外は飛ばず、経路が空のまま終わる。「移動失敗」と「移動完了」が
///    IPC 上は区別できない → 距離と制限時間を必ず自分で確かめる。
///  ・Path.Stop は全体に効く（他のプラグインが始めた移動も止まる）。自分が始めた移動のときだけ呼ぶ。
///  ・Nav.PathfindCancelAll の中身はメッシュの再読み込み（Reload(true)）なので使わない。
/// </summary>
public sealed class VnavmeshIpc : IpcGate, Automation.INavControl
{
    public override string InternalName => "vnavmesh";

    /// <summary>ナビメッシュが使える状態か（エリア移動直後は読み込み中で false）。</summary>
    public bool IsReady()
        => this.TryInvoke("Nav.IsReady",
               () => this.Func<bool>("vnavmesh.Nav.IsReady").InvokeFunc(), out var ready)
           && ready;

    /// <summary>構築の進み具合。0以上1未満＝構築中、負＝構築していない。読めなければ null。</summary>
    public float? BuildProgress()
        => this.TryInvoke("Nav.BuildProgress",
            () => this.Func<float>("vnavmesh.Nav.BuildProgress").InvokeFunc(), out var p)
            ? p
            : null;

    /// <summary>いまのエリアのナビメッシュを読み込ませる（自動読み込みが OFF の環境向け。設定は変えない）。</summary>
    public bool Reload()
        => this.TraceThen("Nav.Reload()") && this.TryInvoke("Nav.Reload",
            () => this.Func<bool>("vnavmesh.Nav.Reload").InvokeFunc(), out _);

    /// <summary>指定地点の近くまで移動する。fly=true なら飛んでいく（飛行できるエリアのみ）。</summary>
    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
        => this.TraceThen($"PathfindAndMoveCloseTo(({destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0}), 飛行={fly}, 範囲={range:0.0})") && this.TryInvoke("SimpleMove.PathfindAndMoveCloseTo",
               () => this.Func<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                   .InvokeFunc(destination, fly, range), out var ok)
           && ok;

    /// <summary>
    /// 経路だけを求める。あとから取り消せる（vnavmesh の IPCProvider.cs：Nav.PathfindCancelable、
    /// 型は Func&lt;Vector3, Vector3, bool, CancellationToken, Task&lt;List&lt;Vector3&gt;&gt;&gt;。導入版 1.2.3.14 の DLL にも名前がある）。
    /// 求めた経路は、こちらが <see cref="MoveAlong"/> を呼ぶまで使われない（SimpleMove と違い、止めた後に遅れて歩き出さない）。
    /// 戻り値は必ず Task のまま受け取る（List で受け取ると Dalamud が JSON に変えようとして失敗する：実測）。
    /// 範囲（range）の引数は無い（0 固定）。手前で止めるのはこちらの到着の判断で行う。
    /// </summary>
    public bool TryPathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancel, out Task<List<Vector3>>? task)
        => this.TryInvoke("Nav.PathfindCancelable",
            () => this.Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable")
                .InvokeFunc(from, to, fly, cancel), out task);

    /// <summary>
    /// 求めた経路をたどらせる（vnavmesh の Path.MoveTo。Action なので InvokeAction で呼ぶ）。
    /// 経路をたどる仕組みは vnavmesh 全体で1つなので、他の経路を上書きする（SimpleMove も同じ）。
    /// </summary>
    public bool MoveAlong(List<Vector3> waypoints, bool fly)
        => this.TraceThen($"Path.MoveTo({waypoints.Count} 点, 飛行={fly})") && this.TryAction("Path.MoveTo",
            () => this.Func<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction(waypoints, fly));

    /// <summary>
    /// 指定の水平位置の真下（上下 halfExtentXZ の範囲）で、立てる床の点を返す。
    /// 地図座標から作った位置は高さが分からないので、これで床に合わせる。無ければ null。
    /// </summary>
    public Vector3? PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ)
        => this.TryInvoke("Query.Mesh.PointOnFloor",
            () => this.Func<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor")
                .InvokeFunc(p, allowUnlandable, halfExtentXZ), out var v)
            ? v
            : null;

    /// <summary>
    /// 近くのメッシュ上の点のうち、本来の地面とつながっている床（vnavmesh が地図を作るときに塗り広げて、たどり着けると印を付けた床）の点。
    /// 岩の上・物の中など、たどり着けない床は選ばない（vnavmesh の Query.Mesh.NearestPointReachable。IPCProvider.cs で確認）。無ければ null。
    /// </summary>
    public Vector3? NearestPointReachable(Vector3 p, float halfExtentXZ, float halfExtentY)
        => this.TryInvoke("Query.Mesh.NearestPointReachable",
            () => this.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable")
                .InvokeFunc(p, halfExtentXZ, halfExtentY), out var v)
            ? v
            : null;

    /// <summary>近くのメッシュ上の点。無ければ null。</summary>
    public Vector3? NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY)
        => this.TryInvoke("Query.Mesh.NearestPoint",
            () => this.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint")
                .InvokeFunc(p, halfExtentXZ, halfExtentY), out var v)
            ? v
            : null;

    /// <summary>いま移動中か（走行状態3種のどれか1つでも進行中なら true）。</summary>
    public bool IsMoving()
    {
        if (this.TryInvoke("Path.IsRunning",
                () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running) && running)
            return true;

        if (this.TryInvoke("Nav.PathfindInProgress",
                () => this.Func<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc(), out var navBusy) && navBusy)
            return true;

        if (this.TryInvoke("SimpleMove.PathfindInProgress",
                () => this.Func<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(), out var moveBusy) && moveBusy)
            return true;

        return false;
    }

    /// <summary>
    /// 経路をたどって実際に動いている最中か（Path.IsRunning だけ。経路の計算中は含まない）。
    /// Path.Stop が止められるのはこれだけ。計算中の経路は取り消せず、計算が終わると遅れて動き出す
    /// （vnavmesh の IPCProvider.cs：Path.Stop は FollowPath の停止だけ。SimpleMove の計算は別）。
    /// </summary>
    public bool IsFollowingPath()
        => this.TryInvoke("Path.IsRunning",
               () => this.Func<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running)
           && running;

    /// <summary>SimpleMove の経路の計算が進行中か（読めなければ null）。</summary>
    public bool? SimplePathfindInProgress()
        => this.TryInvoke("SimpleMove.PathfindInProgress",
            () => this.Func<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(), out var v) ? v : null;

    /// <summary>いまたどっている経路の残りの点の数（vnavmesh の Path.NumWaypoints：通り過ぎた点は消える）。読めなければ null。</summary>
    public int? NumWaypoints()
        => this.TryInvoke("Path.NumWaypoints",
            () => this.Func<int>("vnavmesh.Path.NumWaypoints").InvokeFunc(), out var n) ? n : null;

    /// <summary>いまたどっている経路の終点（経路が無い・読めなければ null）。自分の行き先の経路かを確かめるのに使う。</summary>
    public Vector3? LastWaypoint()
        => this.TryInvoke("Path.ListWaypoints",
            () => this.Func<System.Collections.Generic.List<Vector3>>("vnavmesh.Path.ListWaypoints").InvokeFunc(), out var list)
           && list is { Count: > 0 }
            ? list[^1]
            : null;

    /// <summary>移動を止める。自分が始めた移動のときだけ呼ぶこと（全体に効くため）。</summary>
    public bool Stop()
        => this.TraceThen("Path.Stop()") && this.TryAction("Path.Stop",
            () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
