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
    private DateTime spotArrivedAt = DateTime.MinValue;
    private DateTime lastApproach = DateTime.MinValue;
    private DateTime dismountAt = DateTime.MinValue;

    // 狙っている敵。オブジェクトの参照はフレームをまたいで持たず、GameObjectId で毎回引き直して
    // 名前 ID・BaseId が同じかを確かめる（Dalamud の ObjectTable は枠ごとの入れ物を使い回し、
    // アドレスを書き換える＝ObjectTable.cs:189-225。持ち続けた参照は、敵が消えた後に同じ枠へ入った別の敵を指しうる）
    private ulong targetId;
    private uint targetMobNameId;
    private uint targetBaseId;

    // RSR の優先リストに入れた名前 ID（次のモンスターに移るとき消す）
    private uint targetNameId;

    // 同じ敵に長くダメージが入らない（届かない場所・他人の獲物など）ときは諦める。
    // 「最後に HP が減った時刻」から数える（以前は狙い始めた時の HP と比べていたので、
    // 1回でも減った後に届かなくなると、制限時間の 25 分まで気づけなかった）
    private readonly HashSet<ulong> giveUp = [];
    private readonly StallWatch stall = new();

    // 自分が頼んだ近づく移動（画面が開いたときに止めてよいのはこれと出現点への移動だけ）
    private bool approachIssued;

    // 近づく移動を頼んだ行き先（止めた後、計算中だった経路の見張りに使う）
    private Vector3? approachDestination;
    private bool pausedByUi;
    private DateTime uiOpenSince = DateTime.MinValue;

    // 諦めた敵にしか狙われていないのに戦闘状態が続いている時刻（続けば止める）
    private DateTime stuckSince = DateTime.MinValue;

    public List<uint> Unfinished { get; } = [];

    // そろった時刻（そろったあとに残った敵を片づける時間の上限に使う）
    private DateTime collectedSince = DateTime.MinValue;

    // 制限時間を過ぎたか（過ぎたら新しい敵は狙わず、残った敵だけ片づけて終わる）
    private bool timedOut;

    private DateTime CollectedAt()
    {
        if (this.collectedSince == DateTime.MinValue)
            this.collectedSince = DateTime.UtcNow;
        return this.collectedSince;
    }

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
        ctx.CombatInProgress = true;
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (Svc.Objects.LocalPlayer is { } dead && dead.IsDead)
            return this.Fail("倒されました。自動動作を止めます");

        // 制限時間を過ぎたら、以後はもう狙わない。集めきれなかった品を記録し、戦闘中なら片づけてから終わる
        // （時間切れの瞬間はたいてい戦闘中。そのまま終わると RSR が止まり、次のテレポ・採集が戦闘中で詰まる）
        if (!this.timedOut && this.Elapsed > this.limit)
        {
            this.timedOut = true;
            this.CollectUnfinished();
            ctx.Log.Warn("戦闘", $"{this.limit.TotalMinutes:0}分たっても集めきれませんでした（戦闘中なら片づけてから次へ進みます）");
        }

        // ショップ等の画面が出ていたら、自分の移動を止めて待つ（片づけの戦闘中も同じ。
        // 以前は新しい移動を頼まないだけで、走っている移動は止めず、片づけの分岐はこの確認より前にあった）
        if (GameUi.IsShopOrMarketOpen())
        {
            this.PauseOwnMovement(ctx);
            if (this.uiOpenSince == DateTime.MinValue)
                this.uiOpenSince = DateTime.UtcNow;
            if (DateTime.UtcNow - this.uiOpenSince > TimeSpan.FromMinutes(5))
                return this.Fail("ショップ等の画面が5分たっても閉じないので、戦闘を続けられません（閉じてからやり直してください）");
            this.Status = "ショップ等の画面が開いているので待っています";
            return TaskResult.Running;
        }

        // 画面が閉じた。待っていた間は「HP が減らない時間」に数えない
        if (this.uiOpenSince != DateTime.MinValue)
            this.stall.Resume(DateTime.UtcNow);
        this.pausedByUi = false;
        this.uiOpenSince = DateTime.MinValue;

        var wanted = this.timedOut ? [] : this.WantedMobs();
        if (wanted.Count == 0)
        {
            // そろっても（または時間切れでも）、こちらを狙っている敵が残っていれば片づけてから終わる
            // （戦闘中のまま次の作業（テレポ・採集）に移ると、そこで失敗する）
            if (GameUi.InCombat && DateTime.UtcNow - this.CollectedAt() < TimeSpan.FromMinutes(2))
            {
                if (this.CurrentTarget() is { } current)
                    return this.Engage(ctx, current);
                var remaining = FindHater(this.giveUp, out _);
                if (remaining != null)
                {
                    this.SetTarget(ctx, remaining);
                    return TaskResult.Running;
                }

                this.Status = "戦闘状態が解けるのを待っています";
                return TaskResult.Running;
            }

            if (GameUi.InCombat)
                ctx.Log.Warn("戦闘", "2分たっても戦闘状態が解けません。このまま次へ進みます");

            if (!this.timedOut)
                ctx.Log.Write("戦闘", $"{TeleportTask.TerritoryName(this.territory)}: そろいました");
            return TaskResult.Done;
        }

        this.collectedSince = DateTime.MinValue;

        if (Me.Territory != this.territory)
            return this.Fail("エリアが変わりました");

        // 1) いまの相手がまだ生きていれば、近づいて RSR に任せる
        if (this.CurrentTarget() is { } t)
            return this.Engage(ctx, t);

        this.targetId = 0;

        // 2) こちらを狙っている敵がいれば先に片づける（Henched はハードターゲットしか殴らないため）。
        //    諦めた敵は選び直さない（以前は敵視リスト経由で同じ敵をすぐ選び直していた）
        var hater = FindHater(this.giveUp, out var onlyGivenUp);
        if (hater != null)
        {
            this.stuckSince = DateTime.MinValue;
            this.SetTarget(ctx, hater);
            return TaskResult.Running;
        }

        // 諦めた敵にしか狙われていないまま戦闘状態が続く＝攻撃の入らない敵に追われている。
        // マウントにもテレポにも移れないので、上限（2分）を過ぎたら理由を出して止める
        if (onlyGivenUp && GameUi.InCombat)
        {
            if (this.stuckSince == DateTime.MinValue)
                this.stuckSince = DateTime.UtcNow;
            if (DateTime.UtcNow - this.stuckSince > TimeSpan.FromMinutes(2))
                return this.Fail("攻撃の入らない敵に狙われ続けていて、2分たっても戦闘状態が解けません");
        }
        else
        {
            this.stuckSince = DateTime.MinValue;
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

    private TaskResult Engage(TaskContext ctx, IBattleNpc t)
    {
        var dist = Vector3.Distance(Me.Position, t.Position);
        this.Status = $"{t.Name} と戦闘中（{dist:0.0}m）";

        // 最後に HP が減ってから 45 秒たったら、その敵は諦める（届かない・他人が先に攻撃した等。StallWatch）。
        // 回復・無敵で HP が増えたときは進展にしない。ハードターゲットも外す（諦めた敵を RSR が殴り続けないように）
        var hp = t.CurrentHp;
        if (this.stall.Observe(hp, DateTime.UtcNow))
        {
            ctx.Log.Warn("戦闘", $"{t.Name} の HP が 45 秒減っていないので、この個体は諦めて別の個体を探します（HP {hp}）");
            this.giveUp.Add(t.GameObjectId);
            this.targetId = 0;
            if (Svc.Targets.Target?.GameObjectId == t.GameObjectId)
                Svc.Targets.Target = null;
            if (this.approachIssued && ctx.Navmesh.IsMoving())
                ctx.Navmesh.Stop();
            this.approachIssued = false;
            return TaskResult.Running;
        }

        // 近くまで来たらマウントから降りる（乗ったままでは攻撃できない）
        if (GameUi.Mounted && dist < 20 && DateTime.UtcNow - this.dismountAt > TimeSpan.FromSeconds(2))
        {
            this.dismountAt = DateTime.UtcNow;
            GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：ゲームデータで確認）
            return TaskResult.Running;
        }

        // Henched を入れる（こちらが入れていれば送り直さない。RSR が自分で OFF になったときだけ入れ直す）
        if (!ctx.Rotation.EnsureHenched())
            return this.Fail("RSR を Henched モードにできませんでした");
        if (ctx.Rotation.HenchedUnresponsive)
            return this.Fail($"{ctx.Rotation.HenchedProblem}。記録の IPC 欄を見てください");

        // ハードターゲットが外れていたら付け直す
        if (Svc.Targets.Target?.GameObjectId != t.GameObjectId)
            Svc.Targets.Target = t;

        // 攻撃が届く距離まで寄る（近接でも届くように 3m。遠隔ジョブでも近づいて困ることはない）
        if (dist > 3.5f)
        {
            if (!ctx.Navmesh.IsMoving() && DateTime.UtcNow - this.lastApproach > TimeSpan.FromSeconds(1))
            {
                this.lastApproach = DateTime.UtcNow;
                if (ctx.Navmesh.MoveCloseTo(t.Position, false, 2.5f))
                {
                    this.approachIssued = true;
                    this.approachDestination = t.Position;
                }
            }
        }
        else if (this.approachIssued && ctx.Navmesh.IsMoving())
        {
            ctx.Navmesh.Stop();
            this.approachIssued = false;
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

        this.targetId = mob.GameObjectId;
        this.targetMobNameId = mob.NameId;
        this.targetBaseId = mob.BaseId;
        this.stall.Start(mob.CurrentHp, DateTime.UtcNow);
        Svc.Targets.Target = mob;
        this.Status = $"{mob.Name} を狙います";
    }

    /// <summary>
    /// 狙っている敵を GameObjectId で引き直す。消えた・別の敵に入れ替わった（名前 ID か BaseId が違う）・倒れた・
    /// ターゲットできないなら null。
    /// </summary>
    private IBattleNpc? CurrentTarget()
    {
        if (this.targetId == 0)
            return null;
        return Svc.Objects.SearchById(this.targetId) is IBattleNpc npc
               && npc.NameId == this.targetMobNameId && npc.BaseId == this.targetBaseId && IsAlive(npc)
            ? npc
            : null;
    }

    /// <summary>
    /// ショップ等の画面が開いたとき、自分が頼んだ移動（出現点への移動・敵へ近づく移動）だけを止める。
    /// 経路の計算中に止めても計算後に遅れて動き出すので、開いている間は毎回見て止める。
    /// </summary>
    private void PauseOwnMovement(TaskContext ctx)
    {
        if (this.moving != null || this.approachIssued)
            this.pausedByUi = true;

        this.CancelMove(ctx);
        this.approachIssued = false;
        if (this.pausedByUi && ctx.Navmesh.IsFollowingPath())
            ctx.Navmesh.Stop();
    }

    private TaskResult Patrol(TaskContext ctx)
    {
        // Henched はハードターゲットしか殴らないので、出現点を回る間は入れたままでよい
        // （止めたり入れたりを繰り返すと、RSR の切り替え表示がチャットにあふれる）

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
        ctx.CombatInProgress = false;
        this.CancelMove(ctx);
        if ((this.approachIssued || this.pausedByUi) && ctx.Navmesh.IsMoving())
            ctx.Navmesh.Stop();
        if (this.approachIssued && this.approachDestination is { } dest)
            MoveToTask.WatchPendingPath(ctx, dest, 2.5f);
        this.approachIssued = false;
        this.pausedByUi = false;

        // 止めたら優先指定を消し、RSR を止める（動作停止後は設定を消す）
        ctx.Rotation.ClearOwnPriorities();
        ctx.Rotation.ReleaseHenched();

        if (this.targetId != 0 && Svc.Targets.Target?.GameObjectId == this.targetId)
            Svc.Targets.Target = null;
        this.targetId = 0;

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

    /// <summary>
    /// こちらと戦闘状態にある敵（指定外でも倒す。倒さないとマウントにもテレポにも移れないため）。
    /// ゲームの敵視リスト（UIState.Hater。画面の敵リストと同じ。BossMod の AggroPlayer の実体）で拾う。
    /// 自分を狙っていない敵（チョコボを狙っている等）も、敵視リストに載っていれば戦闘状態の原因なので含める。
    /// 敵視リストが読めないときは、自分を狙っている敵で代える。
    /// 諦めた敵（<paramref name="giveUp"/>）は選ばない。諦めた敵しか残っていなければ <paramref name="onlyGivenUp"/> が true。
    /// </summary>
    internal static unsafe IBattleNpc? FindHater(HashSet<ulong> giveUp, out bool onlyGivenUp)
    {
        onlyGivenUp = false;
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        if (meId == 0 || !GameUi.InCombat)
            return null;

        var haters = new HashSet<uint>();
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (ui != null)
        {
            ref var hater = ref ui->Hater;
            for (var i = 0; i < hater.HaterCount && i < hater.Haters.Length; i++)
                if (hater.Haters[i].EntityId != 0)
                    haters.Add(hater.Haters[i].EntityId);
        }

        var all = Svc.Objects
            .OfType<IBattleNpc>()
            .Where(o => o.BattleNpcKind == BattleNpcSubKind.Combatant && IsAlive(o)
                        && (haters.Contains(o.EntityId) || o.TargetObjectId == meId))
            .ToList();
        var pick = all.Where(o => !giveUp.Contains(o.GameObjectId))
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
        onlyGivenUp = pick == null && all.Count > 0;
        return pick;
    }
}

/// <summary>戦闘で集める計画を、エリアごとにまとめる。</summary>
public static class CombatPlanner
{
    /// <summary>
    /// 品目ごとに「どのエリアで、どのモンスターを倒すか」を決める。
    /// 同じエリアで複数の品目が取れるなら、そのエリアにまとめる（移動を減らす）。
    ///
    /// 行けるエリアだけを使う：野外（TerritoryIntendedUse=1。ゲームデータで確認：0=街・1=野外・2=宿屋・3=ダンジョン）で、
    /// 解放済みのエーテライトがあること。行けないエリアを選ぶとテレポの段で全体が止まるため。
    /// 行けるエリアが1つも無い品目は <paramref name="unreachable"/> に入れて返す（呼び出し側が戦闘をあきらめて次の手段へ回す）。
    /// </summary>
    /// <summary>戦闘で行けるエリアか（野外で、解放済みのエーテライトがある）。</summary>
    public static bool IsReachable(uint territory, HashSet<uint>? unlockedAetherytes = null)
    {
        var unlocked = unlockedAetherytes ?? Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        return Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) && t.TerritoryIntendedUse.RowId == 1
               && Svc.Data.GetExcelSheet<Aetheryte>().Any(a => a.IsAetheryte && a.Territory.RowId == territory && unlocked.Contains(a.RowId));
    }

    /// <summary>その品を落とすモンスターが、行けるエリアに1か所でも出るか。</summary>
    public static bool HasReachableSpawn(SourceIndex sources, uint itemId)
    {
        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        return sources.Get(itemId).DropMobs.Any(m => sources.SpawnsOf(m).Any(s => IsReachable(s.Territory, unlocked)));
    }

    public static List<(uint Territory, List<CombatNeed> Needs, List<Vector2> Spots)> Plan(
        SourceIndex sources, IReadOnlyDictionary<uint, int> shortfalls, out List<uint> unreachable)
    {
        var inv = Inventory.Snapshot();
        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        bool Reachable(uint terr) => IsReachable(terr, unlocked);

        // 品目 → エリア → モンスター
        var options = new Dictionary<uint, Dictionary<uint, List<uint>>>();
        foreach (var (item, _) in shortfalls)
        {
            var byTerr = new Dictionary<uint, List<uint>>();
            foreach (var mob in sources.Get(item).DropMobs)
            {
                foreach (var spot in sources.SpawnsOf(mob))
                {
                    if (!Reachable(spot.Territory))
                        continue;
                    if (!byTerr.TryGetValue(spot.Territory, out var l))
                        byTerr[spot.Territory] = l = [];
                    if (!l.Contains(mob))
                        l.Add(mob);
                }
            }

            options[item] = byTerr;
        }

        // 行けるエリアが1つも無い品目は戦闘では集められない
        unreachable = shortfalls.Keys.Where(k => !options.TryGetValue(k, out var o) || o.Count == 0).ToList();

        // 貪欲法：多くの品目を賄えるエリアから決める（行けるエリアだけが候補に残っている）
        var remaining = shortfalls.Keys.Where(k => options.TryGetValue(k, out var o) && o.Count > 0).ToHashSet();
        var result = new List<(uint, List<CombatNeed>, List<Vector2>)>();
        while (remaining.Count > 0)
        {
            var best = remaining
                .SelectMany(i => options[i].Keys)
                .Distinct()
                .Select(t => (Terr: t, Count: remaining.Count(i => options[i].ContainsKey(t))))
                .OrderByDescending(x => x.Count)
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
