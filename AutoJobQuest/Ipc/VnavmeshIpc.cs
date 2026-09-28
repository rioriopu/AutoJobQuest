using System.Numerics;

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
public sealed class VnavmeshIpc : IpcGate
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
        => this.TryInvoke("Nav.Reload",
            () => this.Func<bool>("vnavmesh.Nav.Reload").InvokeFunc(), out _);

    /// <summary>指定地点の近くまで移動する。fly=true なら飛んでいく（飛行できるエリアのみ）。</summary>
    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
        => this.TryInvoke("SimpleMove.PathfindAndMoveCloseTo",
               () => this.Func<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                   .InvokeFunc(destination, fly, range), out var ok)
           && ok;

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

    /// <summary>移動を止める。自分が始めた移動のときだけ呼ぶこと（全体に効くため）。</summary>
    public bool Stop()
        => this.TryAction("Path.Stop",
            () => this.Func<object>("vnavmesh.Path.Stop").InvokeAction());
}
