using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AutoJobQuest.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoJobQuest.Automation;

// 戦闘（反撃・素材集めの戦闘）が使う「ゲームの観測」「RSR」「vnavmesh」の差し替え口
// （判断の部品だけでなく、本番の呼び出し順をゲーム無しで試せるようにする）。
// 本番は下の Game〜 と、Ipc の RotationSolverIpc・VnavmeshIpc が中身。試験は偽物を渡して、同じ DefenseWatch・Engagement を動かす。

/// <summary>戦う相手（1フレームの写し。参照をフレームをまたいで持たない）。</summary>
public interface IFoe
{
    ulong Id { get; }

    uint NameId { get; }

    uint BaseId { get; }

    string Name { get; }

    uint Hp { get; }

    Vector3 Position { get; }
}

/// <summary>戦闘で読むゲームの状態と、ゲームへの操作（ターゲット・降りる）。</summary>
public interface ICombatWorld
{
    DateTime Now { get; }

    bool InCombat { get; }

    bool PlayerDead { get; }

    bool Mounted { get; }

    /// <summary>飛んでいるか（近づく移動を飛んで行うか決める）。</summary>
    bool Flying { get; }

    /// <summary>自分が詠唱中か（詠唱中に歩き出すと詠唱が切れるので、近づく移動を頼まない）。</summary>
    bool Casting { get; }

    /// <summary>ショップ等の画面（確認窓・選択肢・納品窓も含む）が開いているか（開いている間は移動しない）。</summary>
    bool WindowOpen { get; }

    Vector3 MyPosition { get; }

    /// <summary>いまのジョブが戦闘ジョブか。</summary>
    bool CombatJob { get; }

    /// <summary>こちらと戦闘状態にある敵（諦めた敵は除く。諦めた敵しかいなければ onlyGivenUp）。</summary>
    IFoe? FindHater(HashSet<ulong> giveUp, out bool onlyGivenUp);

    /// <summary>ID で引き直す（消えた・名前 ID か BaseId が違う・倒れた・ターゲットできないなら null）。</summary>
    IFoe? Get(ulong id, uint nameId, uint baseId);

    ulong HardTargetId { get; }

    void SetHardTarget(IFoe? foe);

    /// <summary>攻撃が届くか（射程・視線）。聞けなければ null。</summary>
    bool? InReach(IFoe foe, out uint code);

    void Dismount();

    /// <summary>「降りる」（一般アクション 23）を今ゲームが受け付けられるか（Questionable の LandExecutor と同じ：GetActionStatus が 0）。既定は true。</summary>
    bool DismountReady => true;

    /// <summary>今マウントに乗れるか（乗っていない・戦闘中でない・乗れるエリア・一般アクション 9 が使える）。既定は false。</summary>
    bool CanMountNow => false;

    /// <summary>このエリアで飛べるか（風脈を開放済み）。既定は false。</summary>
    bool CanFlyHere => false;

    /// <summary>マウントに乗る（一般アクション 9：マウント・ルーレット）。既定は何もしない。</summary>
    void Mount()
    {
    }
}

/// <summary>RSR の操作（本番は RotationSolverIpc）。</summary>
public interface IRotationControl
{
    bool IsLoaded { get; }

    bool EnsureHenched();

    bool HenchedUnresponsive { get; }

    string HenchedProblem { get; }

    void ReleaseHenched();
}

/// <summary>vnavmesh の操作（本番は VnavmeshIpc）。</summary>
public interface INavControl
{
    bool IsMoving();

    bool IsFollowingPath();

    Vector3? LastWaypoint();

    bool? SimplePathfindInProgress();

    bool MoveCloseTo(Vector3 destination, bool fly, float range);

    bool Stop();

    /// <summary>経路だけを求める（取り消せる）。</summary>
    bool TryPathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancel, out Task<List<Vector3>>? task);

    /// <summary>求めた経路をたどらせる。</summary>
    bool MoveAlong(List<Vector3> waypoints, bool fly);

    /// <summary>地図（ナビメッシュ）の準備ができていて、床を問い合わせられるか。既定は false（床の有無で判断しない）。</summary>
    bool IsReady() => false;

    /// <summary>
    /// 点 <paramref name="p"/> の真下（水平 <paramref name="halfExtentXZ"/> 以内）で、p より低い床のうち一番高いもの
    /// （vnavmesh の Query.Mesh.PointOnFloor）。無ければ null。既定は null。
    /// </summary>
    Vector3? PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ) => null;
}

/// <summary>
/// 自分（AutoJobQuest）が頼んだ移動の控え（反撃を始めたとき、所有を確かめられる自分の移動だけを止める）。
/// 移動の作業・近づく移動が、頼んだときに行き先を書き、終わったら消す。
/// 反撃などで止めた回数（<see cref="Interruptions"/>）が変わったら、移動の作業は「経路が引けなかった」に数えずに頼み直す。
/// </summary>
public sealed class OwnMovement
{
    public Vector3? Destination { get; private set; }

    public float Tolerance { get; private set; }

    /// <summary>ほかの処理（反撃など）が自分の移動を止めた回数。</summary>
    public int Interruptions { get; private set; }

    public void Issued(Vector3 destination, float range)
    {
        this.Destination = destination;
        this.Tolerance = range + 3f;
    }

    public void Clear() => this.Destination = null;

    /// <summary>
    /// いま動いている経路が自分の頼んだものなら止める（経路の終点が自分の行き先に近いときだけ。他のプラグインの移動は止めない）。
    /// 行き先の控えは消さない：経路の計算中に止めても、計算が終わってから遅れて動き出すので、呼び出し側が毎フレーム呼んで止め続ける。
    /// 止めたら true。
    /// </summary>
    /// <param name="nav">vnavmesh。</param>
    /// <param name="exclude">止めない行き先（反撃で敵へ近づく移動。終点がこれに近い経路は止めない）。</param>
    public bool StopIfMine(INavControl nav, Vector3? exclude = null)
    {
        if (this.Destination is not { } dest)
            return false;

        if (!nav.IsFollowingPath())
            return false;

        var last = nav.LastWaypoint();
        if (last is not { } w || Vector3.Distance(w, dest) > this.Tolerance)
            return false;
        if (exclude is { } ex && Vector3.Distance(w, ex) <= 3f)
            return false;

        nav.Stop();
        this.Interruptions++;
        return true;
    }
}

/// <summary>本番の IFoe（その場で IBattleNpc から写す）。</summary>
public sealed class NpcFoe : IFoe
{
    public NpcFoe(IBattleNpc npc)
    {
        this.Npc = npc;
        this.Id = npc.GameObjectId;
        this.NameId = npc.NameId;
        this.BaseId = npc.BaseId;
        this.Name = npc.Name.TextValue;
        this.Hp = npc.CurrentHp;
        this.Position = npc.Position;
    }

    public IBattleNpc Npc { get; }

    public ulong Id { get; }

    public uint NameId { get; }

    public uint BaseId { get; }

    public string Name { get; }

    public uint Hp { get; }

    public Vector3 Position { get; }
}

/// <summary>本番の ICombatWorld（Dalamud とゲームを読む）。</summary>
public sealed class GameCombatWorld : ICombatWorld
{
    public static readonly GameCombatWorld Instance = new();

    private HashSet<uint>? combatJobs;

    public DateTime Now => DateTime.UtcNow;

    public bool InCombat => GameUi.InCombat;

    public bool PlayerDead => Svc.Objects.LocalPlayer is { } me && me.IsDead;

    public bool Mounted => GameUi.Mounted;

    public bool Flying => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight];

    public bool Casting => Svc.Objects.LocalPlayer is { } me && me.IsCasting;

    public bool WindowOpen => GameUi.IsShopOrMarketOpen();

    public Vector3 MyPosition => Me.Position;

    public bool CombatJob
    {
        get
        {
            this.combatJobs ??= Jobs.CombatJobs().Select(c => c.RowId).ToHashSet();
            return this.combatJobs.Contains(Jobs.CurrentClassJob);
        }
    }

    public IFoe? FindHater(HashSet<ulong> giveUp, out bool onlyGivenUp)
        => CombatTask.FindHater(giveUp, out onlyGivenUp) is { } npc ? new NpcFoe(npc) : null;

    public IFoe? Get(ulong id, uint nameId, uint baseId)
        => id != 0 && Svc.Objects.SearchById(id) is IBattleNpc npc && npc.NameId == nameId && npc.BaseId == baseId && CombatTask.IsAlive(npc)
            ? new NpcFoe(npc)
            : null;

    public ulong HardTargetId => Svc.Targets.Target?.GameObjectId ?? 0;

    public void SetHardTarget(IFoe? foe)
        => Svc.Targets.Target = foe is NpcFoe n ? n.Npc : null;

    public bool? InReach(IFoe foe, out uint code)
    {
        code = 0;
        return foe is NpcFoe n ? AttackReach.InReach(n.Npc, out code) : null;
    }

    public void Dismount() => GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：ゲームデータで確認）

    public bool DismountReady => GameUi.GeneralActionStatus(23) == 0;

    public bool CanMountNow => !GameUi.Mounted && !GameUi.InCombat && MoveToTask.CanMountHere() && GameUi.GeneralActionStatus(9) == 0;

    public bool CanFlyHere => MoveToTask.CanFlyHere();

    public void Mount() => GameUi.UseGeneralAction(9); // マウント・ルーレット（GeneralAction 9：MoveToTask と同じ）
}
