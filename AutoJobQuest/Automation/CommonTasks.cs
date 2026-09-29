using System;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 指定地点の近くまで移動する（vnavmesh）。
///
///  ・ショップ・マーケットボードの画面が開いている間は動き出さない。
///  ・ナビメッシュが読み込み中なら待つ。自動読み込みが OFF で構築が始まらないなら読み込みを頼む。
///  ・vnavmesh は経路が引けなくても例外を出さず止まるだけなので、距離で到着を確かめ、止まったら数回やり直す。
///  ・遠ければマウントに乗り、飛べるエリアなら飛ぶ。
///  ・経路は取り消せる探索で求め、こちらが渡したときだけ歩く（<see cref="OwnPath"/>。止めた後に古い探索の結果で
///    遅れて歩き出さない）。取り消せる探索の窓口が使えないときだけ、以前の SimpleMove で代える。
/// </summary>
public sealed class MoveToTask : AutoTask
{
    private readonly Vector3 destination;
    private readonly float range;
    private readonly string label;
    private readonly TimeSpan limit;

    private bool started;
    private int retries;
    private bool fly;
    private bool reloadRequested;
    private DateTime mountTriedAt = DateTime.MinValue;

    // ショップ等の画面が開いたので、自分の移動を止めて待っている（閉じたら経路を引き直す）
    private bool pausedByUi;

    // 頼んだときの「ほかの処理が自分の移動を止めた回数」（反撃で止められたら、経路が引けなかったとは数えずに頼み直す）
    private int interruptionsAtIssue;

    // 自分の経路探索と追従（取り消せる）。使えないときは SimpleMove で代える
    private readonly OwnPath path = new();
    private bool usingSimpleMove;

    public MoveToTask(Vector3 destination, float range, string label, TimeSpan? limit = null)
    {
        this.destination = destination;
        this.range = range;
        this.label = label;
        this.limit = limit ?? TimeSpan.FromMinutes(4);
    }

    public override string Name => $"移動: {this.label}";

    private float Distance => Vector2.Distance(new Vector2(Me.Position.X, Me.Position.Z), new Vector2(this.destination.X, this.destination.Z));

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.Distance <= this.range && MathF.Abs(Me.Position.Y - this.destination.Y) < 8f)
        {
            // 着いた。自分の経路を止める（vnavmesh は止めない限り終点まで歩く）
            this.path.Stop(ctx.Navmesh);
            if (this.started && this.usingSimpleMove && ctx.Navmesh.IsMoving())
                ctx.Navmesh.Stop();
            this.ForgetOwnMove(ctx);
            return TaskResult.Done;
        }

        // 上限は実際に移動できた時間で測る（反撃・会話の窓の処理と、他者の画面を待った時間は数えない）。
        // ただし中断が続いても終わるよう、実際の時間にも上限を置く
        if (this.WorkElapsed > this.limit)
            return this.Fail($"{this.label} へ {this.limit.TotalMinutes:0}分以内に着けませんでした（残り {this.Distance:0}m）");
        if (this.WallElapsed > (this.limit * 3) + WindowWaitLimit)
            return this.Fail($"{this.label} へ向かう途中で中断が続き、{this.WallElapsed.TotalMinutes:0}分たっても着けませんでした（残り {this.Distance:0}m）");

        if (this.WaitForWindows(out var windowFail))
        {
            if (windowFail != null)
                return this.Fail(windowFail);

            // 移動を始めた後に画面が開いたら、自分の移動を止める（以前は新しい移動を頼まないだけで、
            // 走っている移動はそのまま続いていた＝画面が開いている間は移動しない、という決まりを満たしていなかった）。
            // 自分の探索は取り消す（結果は使わない）。SimpleMove で代えていたときは、計算が終わると遅れて動き出すので、開いている間は毎回見て止める
            if (this.started || this.pausedByUi)
            {
                this.pausedByUi = true;
                this.started = false;
                this.path.Stop(ctx.Navmesh);
                if (this.usingSimpleMove && ctx.Navmesh.IsFollowingPath())
                    ctx.Navmesh.Stop();
            }

            return TaskResult.Running;
        }

        // 画面が閉じた。止めていた移動は経路を引き直して続ける（retries は数えない）
        this.pausedByUi = false;

        if (GameUi.BetweenAreas)
        {
            this.Status = "エリア移動中";
            return TaskResult.Running;
        }

        if (!ctx.Navmesh.IsReady())
        {
            var p = ctx.Navmesh.BuildProgress();
            if (p is >= 0 and < 1)
            {
                this.Status = $"ナビメッシュ構築中 {p * 100:0}%";
            }
            else if (!this.reloadRequested)
            {
                ctx.Navmesh.Reload();
                this.reloadRequested = true;
                this.Status = "ナビメッシュの読み込みを頼みました";
            }

            return TaskResult.Running;
        }

        if (!this.started)
        {
            // 遠いならマウント（飛べるなら飛ぶ）。乗れない場所では歩く
            if (this.Distance > 60 && !GameUi.Mounted && !GameUi.InCombat && CanMountHere()
                && DateTime.UtcNow - this.mountTriedAt > TimeSpan.FromSeconds(5))
            {
                this.mountTriedAt = DateTime.UtcNow;
                GameUi.UseGeneralAction(9); // マウント・ルーレット（GeneralAction 9：実測済み）
                this.Status = "マウントに乗っています";
                return TaskResult.Running;
            }

            if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounting] || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting])
                return TaskResult.Running;

            this.fly = GameUi.Mounted && CanFlyHere();
            if (this.path.Request(ctx.Navmesh, Me.Position, this.destination, this.fly))
            {
                this.usingSimpleMove = false;
            }
            else if (ctx.Navmesh.MoveCloseTo(this.destination, this.fly, Math.Max(1f, this.range * 0.7f)))
            {
                // 取り消せる探索の窓口が使えない（vnavmesh の版が古い等）。以前の SimpleMove で代える（記録に残す）
                this.usingSimpleMove = true;
                Core.DebugLog.Current?.Line("移動", "取り消せる経路探索（Nav.PathfindCancelable）が使えないので、SimpleMove で移動します");
            }
            else
            {
                this.Status = "経路探索の順番待ち";
                return TaskResult.Running;
            }

            this.started = true;
            ctx.OwnMove.Issued(this.destination, Math.Max(1f, this.range * 0.7f));
            this.interruptionsAtIssue = ctx.OwnMove.Interruptions;
            this.NextPhase($"{this.label} へ移動中（{this.Distance:0}m）");
            return TaskResult.Running;
        }

        if (!this.usingSimpleMove)
        {
            switch (this.path.Tick(ctx.Navmesh, Me.Position, allowedToMove: true))
            {
                case OwnPath.State.Searching:
                    this.Status = $"{this.label} への経路を探しています（残り {this.Distance:0}m）";
                    return TaskResult.Running;
                case OwnPath.State.Following:
                    this.Status = $"{this.label} へ移動中（残り {this.Distance:0}m）";
                    return TaskResult.Running;
                case OwnPath.State.Stale:
                    // 探索の間に動いていた（反撃など）。古い出発点からの経路は使わず、数えずに引き直す
                    this.started = false;
                    this.Status = "動いた後なので経路を引き直します";
                    return TaskResult.Running;
            }
        }
        else if (ctx.Navmesh.IsMoving())
        {
            this.Status = $"{this.label} へ移動中（残り {this.Distance:0}m）";
            return TaskResult.Running;
        }

        // 反撃などで自分の移動を止められた。経路が引けなかったとは数えずに頼み直す
        if (ctx.OwnMove.Interruptions != this.interruptionsAtIssue)
        {
            this.started = false;
            this.Status = "止められた移動を頼み直します";
            return TaskResult.Running;
        }

        // 止まったのに着いていない＝経路が引けなかった。やり直す
        if (this.retries++ >= 3)
            return this.Fail($"{this.label} への経路が引けませんでした（残り {this.Distance:0}m）");

        this.started = false;
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // 自分の探索は取り消し（結果は使わない）、自分の経路なら止める
        this.path.Stop(ctx.Navmesh);
        if ((this.started || this.pausedByUi) && this.usingSimpleMove && ctx.Navmesh.IsMoving())
            ctx.Navmesh.Stop();

        // SimpleMove の計算中だった経路（代えていたとき、または vnavmesh が詰まって自分で探し直したとき）が遅れて動き出さないか見張る
        if (this.started || this.pausedByUi)
            WatchPendingPath(ctx, this.destination, Math.Max(1f, this.range * 0.7f));
        this.ForgetOwnMove(ctx);
    }

    /// <summary>自分の移動の控えを消す（控えがこの移動の行き先のときだけ。別の移動の控えは残す）。</summary>
    private void ForgetOwnMove(TaskContext ctx)
    {
        if (ctx.OwnMove.Destination is { } d && Vector3.Distance(d, this.destination) < 0.01f)
            ctx.OwnMove.Clear();
    }

    /// <summary>見張りの名前。</summary>
    public const string PendingPathWatchName = "計算中だった経路が遅れて動き出さないか";

    /// <summary>
    /// 止めたときに経路の計算が進行中なら、止めた後の見張りを置く（計算が終わってこちらの行き先へ動き出したら止める。25秒まで）。
    /// Path.Stop は計算中の経路を取り消せないため（以前は止めた後に遅れて歩き出すことがあった）。
    /// 置くのは実行係が止まるときだけ（<see cref="TaskContext.Stopping"/>）。実行の途中で移動を取り替えたときに置くと、
    /// 実行中は見張りが動かないので、実行が終わってから古い行き先で判断してしまう。
    /// 実行の途中の取り替えでは、次の移動の作業が自分の経路を頼み直す。
    /// </summary>
    public static void WatchPendingPath(TaskContext ctx, Vector3 destination, float range)
    {
        if (!ctx.Stopping || ctx.Navmesh.SimplePathfindInProgress() != true)
            return;

        var nav = ctx.Navmesh;
        var tolerance = range + 3f;
        var giveUpAt = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        ctx.AfterStop.RemoveAll(a => a.Name == PendingPathWatchName);
        ctx.AfterStop.Add((PendingPathWatchName, DateTime.UtcNow + TimeSpan.FromSeconds(30), () =>
        {
            switch (PendingPath.Decide(nav.IsFollowingPath(), nav.LastWaypoint(), nav.SimplePathfindInProgress(), destination, tolerance))
            {
                case PendingPath.Verdict.StopAndFinish:
                    Core.DebugLog.Current?.Line("見張り", "止めた後に、こちらの行き先への経路が動き出したので止めました");
                    nav.Stop();
                    return true;
                case PendingPath.Verdict.Finish:
                    return true;
                default:
                    // 計算が長引いている（または読めない）。期限切れの警告（「相手が止まったか確かめて」）は当たらないので、
                    // 期限の少し前に黙って外す（その後に歩き出したら、利用者が止める）
                    if (DateTime.UtcNow < giveUpAt)
                        return false;
                    Core.DebugLog.Current?.Line("見張り", "計算中だった経路が25秒たっても終わらないので、見張りをやめます");
                    return true;
            }
        }));
    }

    private static bool CanMountHere()
    {
        var terr = Svc.Data.GetExcelSheet<TerritoryType>();
        return terr.TryGetRow(Me.Territory, out var t) && t.Mount;
    }

    private static unsafe bool CanFlyHere()
    {
        var ps = PlayerState.Instance();
        return ps != null && ps->CanFly;
    }
}

/// <summary>
/// 指定エリアへテレポートする（Lifestream）。そのエリアに解放済みのエーテライトが無ければ失敗。
/// 既にそのエリアにいれば何もしない。
/// </summary>
public sealed class TeleportTask : AutoTask
{
    private readonly uint territory;
    private readonly Vector3? near;
    private bool requested;
    private int attempts;

    /// <param name="territory">行き先のエリア。</param>
    /// <param name="near">エリア内で近づきたい位置（複数のエーテライトがあるとき、一番近いものを選ぶ）。</param>
    public TeleportTask(uint territory, Vector3? near = null)
    {
        this.territory = territory;
        this.near = near;
    }

    public override string Name => $"テレポ: {TerritoryName(this.territory)}";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (Me.Territory == this.territory && GameUi.PlayerFree())
            return TaskResult.Done;

        // 上限は実際に作業できた時間で測る（反撃・会話の窓の処理と、他者の画面を待った時間は数えない）
        if (this.WorkElapsed > TimeSpan.FromMinutes(2))
            return this.Fail($"{TerritoryName(this.territory)} へのテレポが2分以内に終わりませんでした");
        if (this.WallElapsed > TimeSpan.FromMinutes(6) + WindowWaitLimit)
            return this.Fail($"{TerritoryName(this.territory)} へのテレポの途中で中断が続き、{this.WallElapsed.TotalMinutes:0}分たっても終わりませんでした");

        if (this.WaitForWindows(out var windowFail))
            return windowFail != null ? this.Fail(windowFail) : TaskResult.Running;

        if (this.requested)
        {
            // 詠唱→エリア移動が始まるのを待つ。詠唱が始まらずに一定時間たったらやり直す
            if (GameUi.BetweenAreas || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting])
            {
                this.Status = "テレポ中";
                return TaskResult.Running;
            }

            if (this.PhaseElapsed < TimeSpan.FromSeconds(10))
                return TaskResult.Running;

            this.requested = false;
        }

        if (GameUi.InCombat || !GameUi.PlayerFree())
        {
            this.Status = "動ける状態になるのを待っています";
            return TaskResult.Running;
        }

        if (this.attempts++ >= 3)
            return this.Fail($"{TerritoryName(this.territory)} へテレポできませんでした");

        var aetheryte = FindAetheryte(this.territory, this.near);
        if (aetheryte == null)
            return this.Fail($"{TerritoryName(this.territory)} に解放済みのエーテライトがありません");

        if (!ctx.Lifestream.TryTeleport(aetheryte.Value, 0, out var accepted) || !accepted)
        {
            this.Status = "Lifestream がテレポを受け付けませんでした。やり直します";
            this.NextPhase(this.Status);
            this.requested = true;
            return TaskResult.Running;
        }

        this.requested = true;
        this.NextPhase($"{TerritoryName(this.territory)} へテレポ中");
        return TaskResult.Running;
    }

    /// <summary>そのエリアの解放済みエーテライト（近い位置が分かれば一番近いもの）。</summary>
    public static uint? FindAetheryte(uint territory, Vector3? near)
    {
        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var levels = Svc.Data.GetExcelSheet<Level>();

        var candidates = sheet
            .Where(a => a.IsAetheryte && a.Territory.RowId == territory && unlocked.Contains(a.RowId))
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (near == null || candidates.Count == 1)
            return candidates[0].RowId;

        return candidates
            .OrderBy(a =>
            {
                var lv = a.Level.FirstOrDefault(l => l.RowId != 0);
                return lv.RowId != 0 && levels.TryGetRow(lv.RowId, out var row)
                    ? Vector2.Distance(new Vector2(row.X, row.Z), new Vector2(near.Value.X, near.Value.Z))
                    : float.MaxValue;
            })
            .First().RowId;
    }

    public static string TerritoryName(uint territory)
        => Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t)
            ? t.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{territory}"
            : $"#{territory}";
}

/// <summary>ギアセットで指定のジョブに着替える。成功は「いまのジョブが変わった」で確かめる。</summary>
public sealed class EquipJobTask : AutoTask
{
    private readonly uint classJob;
    private bool requested;
    private int requests;

    public EquipJobTask(uint classJob)
    {
        this.classJob = classJob;
    }

    public override string Name => $"着替え: {Jobs.Name(this.classJob)}";

    protected override unsafe TaskResult Tick(TaskContext ctx)
    {
        if (Jobs.CurrentClassJob == this.classJob)
            return TaskResult.Done;

        // 動けない間（戦闘中・会話中など）の待ちは長めの上限で別に数える
        // （以前は全体 20 秒の中に含めていたため、戦闘が 20 秒以上続くと着替えに失敗していた）
        if (GameUi.InCombat || !GameUi.PlayerFree())
        {
            this.Status = "動ける状態になるのを待っています";
            return this.Elapsed > TimeSpan.FromMinutes(5)
                ? this.Fail($"5分たっても動ける状態にならず、{Jobs.Name(this.classJob)} に着替えられませんでした")
                : TaskResult.Running;
        }

        // 着替えを頼んでから 3 秒たっても変わらなければ頼み直す（ゲームの応答を待つ間隔）。頼むのは 5 回まで
        if (this.requested && this.PhaseElapsed < TimeSpan.FromSeconds(3))
            return TaskResult.Running;
        if (this.requests >= 5)
            return this.Fail($"{Jobs.Name(this.classJob)} に着替えられませんでした（5 回頼んでも変わりません）");

        var idx = GearCheck.FindGearset(this.classJob);
        if (idx < 0)
            return this.Fail($"{Jobs.Name(this.classJob)} のギアセットがありません");

        RaptureGearsetModule.Instance()->EquipGearset(idx);
        this.requested = true;
        this.requests++;
        this.NextPhase("着替え中");
        return TaskResult.Running;
    }
}

/// <summary>条件が満たされるまで待つだけの作業（上限つき）。</summary>
public sealed class WaitUntilTask : AutoTask
{
    private readonly string name;
    private readonly Func<bool> condition;
    private readonly TimeSpan limit;

    public WaitUntilTask(string name, Func<bool> condition, TimeSpan limit)
    {
        this.name = name;
        this.condition = condition;
        this.limit = limit;
    }

    public override string Name => this.name;

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.condition())
            return TaskResult.Done;
        return this.Elapsed > this.limit ? this.Fail($"{this.name}: {this.limit.TotalSeconds:0}秒待っても条件が満たされませんでした") : TaskResult.Running;
    }
}

/// <summary>理由を添えてその場で止める（順番に並べた作業の途中で、続けても意味が無いと分かったとき用）。</summary>
public sealed class StopTask : AutoTask
{
    private readonly string reason;

    public StopTask(string reason)
    {
        this.reason = reason;
    }

    public override string Name => "停止";

    protected override TaskResult OnStart(TaskContext ctx) => this.Fail(this.reason);

    protected override TaskResult Tick(TaskContext ctx) => this.Fail(this.reason);
}
