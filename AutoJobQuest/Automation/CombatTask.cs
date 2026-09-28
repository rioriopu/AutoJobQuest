using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>戦闘で集める1品目。</summary>
/// <param name="ItemId">アイテム。</param>
/// <param name="TargetOwned">持っていたい総数（今の所持数＋不足数）。</param>
/// <param name="Mobs">そのエリアで落とすモンスター（名前 ID）。</param>
public sealed record CombatNeed(uint ItemId, int TargetOwned, List<uint> Mobs);

/// <summary>
/// 1つのエリアで、指定のモンスターを倒して素材を集める。
///
/// 仕様:
///  ・RSR で指定モンスターだけを攻撃する。
///  ・動作停止後、または次のモンスターを殴るとき、前に指定したモンスターの設定は消す。
///  ・ドロップはロットなしでカバンに入る（所持数だけで進み具合を見る）。
/// 判定基準は「倒した数」ではなく「アイテムの所持数」。
///
/// 攻撃のさせ方:
///  RSR を Henched（5）にすると、敵への行動は「いまのハードターゲット」だけが対象になり、
///  こちらを狙っていない敵（ノンアクティブ）も攻撃してよい扱いになる。
///  → こちらがターゲットしたモンスターだけを RSR が殴る。優先リストにもその名前 ID を入れる。
///
/// 出現位置は LuminaSupplemental の MobSpawn（地図座標）。高さは vnavmesh に床の点を問い合わせて補う。
/// </summary>
public sealed class CombatTask : AutoTask
{
    private readonly uint territory;
    private readonly List<CombatNeed> needs;
    private readonly List<Vector2> spots;
    private readonly TimeSpan limit;

    private int spotIndex;
    private MoveToTask? moving;
    private IBattleNpc? target;
    private uint targetNameId;
    private bool rsrOn;
    private DateTime spotArrivedAt = DateTime.MinValue;
    private DateTime lastApproach = DateTime.MinValue;
    private DateTime dismountAt = DateTime.MinValue;

    // 同じ敵に長くダメージが入らない（届かない場所・他人の獲物など）ときは諦める
    private readonly HashSet<ulong> giveUp = [];
    private DateTime targetSince = DateTime.MinValue;
    private uint targetHpAtStart;

    public List<uint> Unfinished { get; } = [];

    public CombatTask(uint territory, List<CombatNeed> needs, List<Vector2> mapSpots, TimeSpan limit)
    {
        this.territory = territory;
        this.needs = needs;
        this.limit = limit;

        // 近い出現点どうしは1つにまとめる（地図座標で 1.0 以内）
        this.spots = [];
        foreach (var s in mapSpots)
            if (!this.spots.Any(x => Vector2.Distance(x, s) < 1.0f))
                this.spots.Add(s);
    }

    public override string Name => $"戦闘: {TeleportTask.TerritoryName(this.territory)}";

    private HashSet<uint> WantedMobs()
        => this.needs
            .Where(n => Inventory.CountNow(n.ItemId) < n.TargetOwned)
            .SelectMany(n => n.Mobs)
            .ToHashSet();

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (!ctx.Rotation.IsLoaded)
            return this.Fail("RotationSolverReborn が読み込まれていません");
        if (this.spots.Count == 0)
            return this.Fail("出現位置のデータがありません");

        // 近い出現点から回る
        this.spots.Sort((a, b) => DistanceToSpot(a).CompareTo(DistanceToSpot(b)));
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        var wanted = this.WantedMobs();
        if (wanted.Count == 0)
        {
            ctx.Log.Write("戦闘", $"{TeleportTask.TerritoryName(this.territory)}: そろいました");
            return TaskResult.Done;
        }

        if (this.Elapsed > this.limit)
        {
            this.CollectUnfinished();
            ctx.Log.Warn("戦闘", $"{this.limit.TotalMinutes:0}分たっても集めきれませんでした");
            return TaskResult.Done;
        }

        if (Svc.Objects.LocalPlayer is { } me && me.IsDead)
            return this.Fail("倒されました。自動動作を止めます");

        if (Me.Territory != this.territory)
            return this.Fail("エリアが変わりました");

        // ショップ等の画面が出ていたら何もしない
        if (GameUi.IsShopOrMarketOpen())
        {
            this.Status = "ショップ等の画面が開いているので待っています";
            return TaskResult.Running;
        }

        // 1) いまの相手がまだ生きていれば、近づいて RSR に任せる
        if (this.target != null && IsAlive(this.target))
            return this.Engage(ctx);

        this.target = null;

        // 2) こちらを狙っている敵がいれば先に片づける（Henched はハードターゲットしか殴らないため）
        var hater = FindHater();
        if (hater != null)
        {
            this.SetTarget(ctx, hater);
            return TaskResult.Running;
        }

        // 3) 指定のモンスターを探す
        var mob = FindMob(wanted, this.giveUp);
        if (mob != null)
        {
            this.CancelMove(ctx);
            this.SetTarget(ctx, mob);
            return TaskResult.Running;
        }

        // 4) いなければ出現点を回る
        return this.Patrol(ctx);
    }

    private TaskResult Engage(TaskContext ctx)
    {
        var t = this.target!;
        var dist = Vector3.Distance(Me.Position, t.Position);
        this.Status = $"{t.Name} と戦闘中（{dist:0.0}m）";

        // 45秒たっても HP が1も減らなければ、その敵は諦める（届かない・他人が先に攻撃した等）
        if (DateTime.UtcNow - this.targetSince > TimeSpan.FromSeconds(45) && t.CurrentHp >= this.targetHpAtStart)
        {
            ctx.Log.Warn("戦闘", $"{t.Name} に攻撃が入らないので、別の個体を探します");
            this.giveUp.Add(t.GameObjectId);
            this.target = null;
            if (ctx.Navmesh.IsMoving())
                ctx.Navmesh.Stop();
            return TaskResult.Running;
        }

        // 近くまで来たらマウントから降りる（乗ったままでは攻撃できない）
        if (GameUi.Mounted && dist < 20 && DateTime.UtcNow - this.dismountAt > TimeSpan.FromSeconds(2))
        {
            this.dismountAt = DateTime.UtcNow;
            GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：ゲームデータで確認）
            return TaskResult.Running;
        }

        if (!this.rsrOn)
        {
            this.rsrOn = ctx.Rotation.ChangeOperatingMode(RotationSolverIpc.ModeHenched);
            if (!this.rsrOn)
                return this.Fail("RSR を Henched モードにできませんでした");
        }

        // ハードターゲットが外れていたら付け直す
        if (Svc.Targets.Target?.GameObjectId != t.GameObjectId)
            Svc.Targets.Target = t;

        // 攻撃が届く距離まで寄る（近接でも届くように 3m。遠隔ジョブでも近づいて困ることはない）
        if (dist > 3.5f)
        {
            if (!ctx.Navmesh.IsMoving() && DateTime.UtcNow - this.lastApproach > TimeSpan.FromSeconds(1))
            {
                this.lastApproach = DateTime.UtcNow;
                ctx.Navmesh.MoveCloseTo(t.Position, false, 2.5f);
            }
        }
        else if (ctx.Navmesh.IsMoving())
        {
            ctx.Navmesh.Stop();
        }

        return TaskResult.Running;
    }

    private void SetTarget(TaskContext ctx, IBattleNpc mob)
    {
        // 次のモンスターに移るとき、前に指定した優先設定を消す
        if (this.targetNameId != mob.NameId)
        {
            ctx.Rotation.ClearOwnPriorities();
            ctx.Rotation.AddPriority(mob.NameId);
            this.targetNameId = mob.NameId;
        }

        this.target = mob;
        this.targetSince = DateTime.UtcNow;
        this.targetHpAtStart = mob.CurrentHp;
        Svc.Targets.Target = mob;
        this.rsrOn = false; // Engage で Henched を入れ直す（RSR はエリア移動・死亡・着替えで自動 OFF になるため）
        this.Status = $"{mob.Name} を狙います";
    }

    private TaskResult Patrol(TaskContext ctx)
    {
        if (this.rsrOn)
        {
            // 戦っていないので RSR は一旦止める（無関係な敵を殴らないように）
            ctx.Rotation.ChangeOperatingMode(RotationSolverIpc.ModeOff);
            this.rsrOn = false;
        }

        if (this.moving != null)
        {
            var r = this.moving.Step(ctx);
            this.Status = $"出現点 {this.spotIndex + 1}/{this.spots.Count} へ: {this.moving.Status}";
            if (r == TaskResult.Running)
                return TaskResult.Running;

            this.moving.Cleanup(ctx);
            this.moving = null;
            this.spotArrivedAt = DateTime.UtcNow;
            if (r == TaskResult.Failed)
                this.spotIndex = (this.spotIndex + 1) % this.spots.Count;
            return TaskResult.Running;
        }

        // 出現点に着いてから少し待っても湧かなければ次の出現点へ
        if (this.spotArrivedAt != DateTime.MinValue && DateTime.UtcNow - this.spotArrivedAt < TimeSpan.FromSeconds(20))
        {
            this.Status = $"出現点 {this.spotIndex + 1}/{this.spots.Count} で湧きを待っています";
            return TaskResult.Running;
        }

        if (this.spotArrivedAt != DateTime.MinValue)
            this.spotIndex = (this.spotIndex + 1) % this.spots.Count;

        var spot = this.spots[this.spotIndex];
        var world = MapCoords.ToWorld(this.territory, spot.X, spot.Y);
        var onFloor = ctx.Navmesh.NearestPoint(new Vector3(world.X, Me.Position.Y, world.Z), 10f, 300f)
                      ?? ctx.Navmesh.PointOnFloor(new Vector3(world.X, Me.Position.Y + 100f, world.Z), false, 10f);
        if (onFloor == null)
        {
            // 床が見つからない出現点は飛ばす
            this.spotIndex = (this.spotIndex + 1) % this.spots.Count;
            this.spotArrivedAt = DateTime.MinValue;
            return TaskResult.Running;
        }

        this.moving = new MoveToTask(onFloor.Value, 8f, $"出現点 {spot.X:0.0},{spot.Y:0.0}", TimeSpan.FromMinutes(3));
        this.spotArrivedAt = DateTime.MinValue;
        return TaskResult.Running;
    }

    private void CancelMove(TaskContext ctx)
    {
        if (this.moving == null)
            return;
        this.moving.Cleanup(ctx);
        this.moving = null;
    }

    private void CollectUnfinished()
    {
        foreach (var n in this.needs)
            if (Inventory.CountNow(n.ItemId) < n.TargetOwned)
                this.Unfinished.Add(n.ItemId);
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.CancelMove(ctx);
        if (ctx.Navmesh.IsMoving())
            ctx.Navmesh.Stop();

        // 止めたら優先指定を消し、RSR を止める（動作停止後は設定を消す）
        ctx.Rotation.ClearOwnPriorities();
        ctx.Rotation.ChangeOperatingMode(RotationSolverIpc.ModeOff);
        this.rsrOn = false;

        if (this.target != null && Svc.Targets.Target?.GameObjectId == this.target.GameObjectId)
            Svc.Targets.Target = null;
        this.target = null;

        if (this.Unfinished.Count == 0)
            this.CollectUnfinished();
    }

    private float DistanceToSpot(Vector2 spot)
    {
        var w = MapCoords.ToWorld(this.territory, spot.X, spot.Y);
        return Vector2.Distance(new Vector2(w.X, w.Z), new Vector2(Me.Position.X, Me.Position.Z));
    }

    private static bool IsAlive(IBattleNpc npc)
        => npc.IsValid() && !npc.IsDead && npc.CurrentHp > 0 && npc.IsTargetable;

    /// <summary>近くの指定モンスター（生きている・ターゲットできる・他人と戦っていない）。</summary>
    private static IBattleNpc? FindMob(HashSet<uint> wanted, HashSet<ulong> giveUp)
    {
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        return Svc.Objects
            .OfType<IBattleNpc>()
            .Where(o => o.BattleNpcKind == BattleNpcSubKind.Combatant && wanted.Contains(o.NameId) && IsAlive(o) && !giveUp.Contains(o.GameObjectId))
            .Where(o => !o.StatusFlags.HasFlag(StatusFlags.InCombat) || o.TargetObjectId == meId)
            .Where(o => Vector3.Distance(o.Position, Me.Position) < 60f)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
    }

    /// <summary>こちらを狙っている敵。</summary>
    private static IBattleNpc? FindHater()
    {
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        if (meId == 0 || !GameUi.InCombat)
            return null;

        return Svc.Objects
            .OfType<IBattleNpc>()
            .Where(o => o.BattleNpcKind == BattleNpcSubKind.Combatant && IsAlive(o) && o.TargetObjectId == meId)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
    }
}

/// <summary>戦闘で集める計画を、エリアごとにまとめる。</summary>
public static class CombatPlanner
{
    /// <summary>
    /// 品目ごとに「どのエリアで、どのモンスターを倒すか」を決める。
    /// 同じエリアで複数の品目が取れるなら、そのエリアにまとめる（移動を減らす）。
    /// 飛べないエリア・解放済みエーテライトの無いエリアは後回しにする。
    /// </summary>
    public static List<(uint Territory, List<CombatNeed> Needs, List<Vector2> Spots)> Plan(
        SourceIndex sources, IReadOnlyDictionary<uint, int> shortfalls)
    {
        var inv = Inventory.Snapshot();
        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();
        bool HasAetheryte(uint terr) => aetherytes.Any(a => a.IsAetheryte && a.Territory.RowId == terr && unlocked.Contains(a.RowId));

        // 品目 → エリア → モンスター
        var options = new Dictionary<uint, Dictionary<uint, List<uint>>>();
        foreach (var (item, _) in shortfalls)
        {
            var byTerr = new Dictionary<uint, List<uint>>();
            foreach (var mob in sources.Get(item).DropMobs)
            {
                foreach (var spot in sources.SpawnsOf(mob))
                {
                    if (!byTerr.TryGetValue(spot.Territory, out var l))
                        byTerr[spot.Territory] = l = [];
                    if (!l.Contains(mob))
                        l.Add(mob);
                }
            }

            options[item] = byTerr;
        }

        // 貪欲法：多くの品目を賄えるエリアから決める（解放済みエーテライトのあるエリアを優先）
        var remaining = shortfalls.Keys.Where(k => options.TryGetValue(k, out var o) && o.Count > 0).ToHashSet();
        var result = new List<(uint, List<CombatNeed>, List<Vector2>)>();
        while (remaining.Count > 0)
        {
            var best = remaining
                .SelectMany(i => options[i].Keys)
                .Distinct()
                .Select(t => (Terr: t, Count: remaining.Count(i => options[i].ContainsKey(t)), Reach: HasAetheryte(t)))
                .OrderByDescending(x => x.Reach)
                .ThenByDescending(x => x.Count)
                .First();

            var needs = new List<CombatNeed>();
            var spots = new List<Vector2>();
            foreach (var item in remaining.Where(i => options[i].ContainsKey(best.Terr)).ToList())
            {
                var mobs = options[item][best.Terr];
                needs.Add(new CombatNeed(item, inv.CountAll(item) + shortfalls[item], mobs));
                foreach (var mob in mobs)
                    spots.AddRange(sources.SpawnsOf(mob).Where(s => s.Territory == best.Terr).Select(s => new Vector2(s.MapX, s.MapY)));
                remaining.Remove(item);
            }

            result.Add((best.Terr, needs, spots));
        }

        return result;
    }
}
