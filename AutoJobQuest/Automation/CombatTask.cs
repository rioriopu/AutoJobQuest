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
/// 攻撃のさせ方（RSR のソースで確かめた）:
///  RSR を Henched（5）にすると、敵への単体の行動は「いまのハードターゲット」だけが対象になり、
///  こちらを狙っていない敵（ノンアクティブ）も攻撃してよい扱いになる。
///  → こちらがターゲットしたモンスターだけを RSR が殴る。ただし範囲攻撃は指定外の敵も巻き込み、自分中心の範囲攻撃は
///  ハードターゲットが無くても撃つので、Henched の間は RSR の範囲攻撃を Off にする（RotationSolverIpc の RsrAoe）。
///  優先リストにもその名前 ID を入れる（Henched では効かないが、上の仕様どおり足して消す）。
///
/// 近づき方：狙った敵にこちらから近寄り、攻撃が届いたら止まって RSR に任せる
///  （キャスター・レンジは射程に入った所で止まる）。敵と戦う処理は反撃（DefenseWatch）と共通の <see cref="Engagement"/>。
///  攻撃してくる敵（敵視リスト）がいれば、まだ殴っていない指定の敵より先に倒す（棒立ちで殴られ続けない）。
///
/// 出現位置は LuminaSupplemental の MobSpawn（地図座標）。高さは vnavmesh に床の点を問い合わせて補う。
/// </summary>
public sealed class CombatTask : AutoTask
{
    private readonly uint territory;
    private readonly List<CombatNeed> needs;
    private readonly List<Vector2> spots;
    private readonly TimeSpan limit;

    // 出現点ごとの敵（名前の番号。spots と同じ並び）・見回りの点か・回る順（一筆書き。決めたら変えない）
    private readonly List<uint> spotMobs = [];
    private readonly List<bool> spotRoam = [];
    private List<int> tour = [];

    private int spotIndex;
    private MoveToTask? moving;
    private DateTime spotArrivedAt = DateTime.MinValue;

    /// <summary>出現点の周りの見回りの点の数（方向）。</summary>
    public const int RoamDirections = 6;

    /// <summary>同じ敵の出現点・見かけた位置を1つの群れにまとめる距離（地図座標。1.5＝ワールドで約 75m）。</summary>
    public const float ClusterMerge = 1.5f;

    /// <summary>出現点の巡回で、マウントに乗る距離（これより遠ければ乗って飛ぶ）。</summary>
    public const float PatrolMountOver = 15f;

    /// <summary>見回りの点の、出現点からの距離（地図座標。1.0＝ワールドで 50m）。</summary>
    public const float RoamRadiusMap = 1.2f;

    // 見かけた目当ての敵の位置を記録した時刻（2秒に1回まで）
    private DateTime sightedAt = DateTime.MinValue;

    // 移動を作らずに着いた扱いにした時刻（続けて着いたままなら間をあける）
    private DateTime lastInPlaceAt = DateTime.MinValue;

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
    // 1回でも減った後に届かなくなると、制限時間の 25 分まで気づけなかった）。数えるのは Engagement の中の StallWatch
    // 諦めた時刻（2分たったら、もう一度狙う。不具合の例：飛んだまま降りずに 45 秒たって諦めた個体を、その後すぐ隣にいても狙わなかった。
    // 諦めの原因がこちらの不具合や一時的なもの〔届かない位置にいた等〕でも、ずっと狙わないままにしない）
    private readonly Dictionary<ulong, DateTime> giveUpAt = [];

    // 出現点の床が続けて見つからなかった回数（全部の出現点で見つからなければ止める）
    private int floorMisses;

    /// <summary>諦めた個体をもう一度狙うまでの時間。</summary>
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(2);

    /// <summary>いま諦めている個体（諦めてから <see cref="GiveUpFor"/> たっていないもの）。</summary>
    private HashSet<ulong> giveUp
    {
        get
        {
            var now = DateTime.UtcNow;
            return this.giveUpAt.Where(kv => now - kv.Value < GiveUpFor).Select(kv => kv.Key).ToHashSet();
        }
    }

    // 敵と戦う処理（近づく・届いたら止まる・降りる・RSR・HP の停滞）。反撃と共通
    private readonly Engagement engage = new();
    private readonly ICombatWorld world = GameCombatWorld.Instance;
    private bool pausedByUi;

    // ナビメッシュの読み込みを頼んだ（1回だけ。移動の作業と同じ）
    private bool navReloadRequested;
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

    public CombatTask(uint territory, List<CombatNeed> needs, List<(Vector2 Spot, uint Mob)> mapSpots, TimeSpan limit)
    {
        this.territory = territory;
        this.needs = needs;
        this.limit = limit;

        // 群れ（A・B・C に何匹かずつ固まっているなら、A で倒したらすぐ乗って B へ飛び、いれば降りて倒し、C へ、
        // そして A へ戻って確かめる、を繰り返す）。同じ敵の地図の出現点と前に見かけた位置を、地図座標で 1.5（約 75m）以内なら1つの群れにまとめる。
        // どの敵の群れかを持たせる（集め終えた敵の群れは回らない）
        this.spots = [];
        foreach (var (s, mob) in mapSpots)
            this.AddSpot(s, mob, ClusterMerge, roam: false);

        // 出現点を足す（不具合の例：フリーズドラゴンは出現点が1か所だけで、2匹倒すと湧き直すまでその場で止まり続けた。
        // 「敵がいなければ次の位置へ回る」を独自に作る。フィールドの敵の湧く場所はゲームのデータに無い）。
        //  ・前に戦ったときに見かけた位置（MobSightings：このプラグインが自分で記録したもの）
        //  ・元の出現点の周りの見回りの点（6方向・地図座標で 1.2＝ワールドで約 60m 先）
        var mobs = needs.SelectMany(n => n.Mobs).Distinct().ToList();
        foreach (var mob in mobs)
        {
            foreach (var seen in MobSightings.Get(territory, [mob]))
            {
                var m = MapCoords.ToMap(territory, seen.X, seen.Z);
                if (m != Vector2.Zero)
                    this.AddSpot(m, mob, ClusterMerge, roam: false);
            }
        }

        // 群れが1つしかない敵だけ、その周りの見回りの点（6方向・約 60m 先）を足す（ほかに回る先が無く、1か所に立っていても湧く場所が広くて
        // 見えない個体がいる：フリーズドラゴン）。群れが2つ以上あれば、群れの間を回る
        foreach (var mob in mobs)
        {
            var own = Enumerable.Range(0, this.spots.Count).Where(i => this.spotMobs[i] == mob).ToList();
            if (own.Count != 1)
                continue;
            var center = this.spots[own[0]];
            for (var k = 0; k < RoamDirections; k++)
            {
                var angle = 2f * MathF.PI * k / RoamDirections;
                this.AddSpot(center + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * RoamRadiusMap), mob, 1.0f, roam: true);
            }
        }
    }

    /// <summary>出現点（群れ）を足す（同じ敵の点が地図座標で <paramref name="merge"/> 以内にあれば足さない）。</summary>
    private void AddSpot(Vector2 p, uint mob, float merge, bool roam)
    {
        for (var i = 0; i < this.spots.Count; i++)
            if (this.spotMobs[i] == mob && Vector2.Distance(this.spots[i], p) < merge)
                return;
        this.spots.Add(p);
        this.spotMobs.Add(mob);
        this.spotRoam.Add(roam);
    }

    /// <summary>回る順を決める：今いる場所から一番近い点を始めに、そこから一番近い未訪の点、と一筆書きにする（決めたら変えない）。</summary>
    private void BuildTour()
    {
        var left = Enumerable.Range(0, this.spots.Count).ToList();
        var order = new List<int>();
        var at = new Vector2(Me.Position.X, Me.Position.Z);
        while (left.Count > 0)
        {
            var from = at;
            var next = left.MinBy(i =>
            {
                var w = MapCoords.ToWorld(this.territory, this.spots[i].X, this.spots[i].Y);
                return Vector2.Distance(new Vector2(w.X, w.Z), from);
            });
            order.Add(next);
            left.Remove(next);
            var nw = MapCoords.ToWorld(this.territory, this.spots[next].X, this.spots[next].Y);
            at = new Vector2(nw.X, nw.Z);
        }

        this.tour = order;
    }

    /// <summary>
    /// 次に向かう出現点（群れ）：回る順（一筆書き）で今の点の次の、まだ集め終えていない敵の点。最後まで行ったら最初へ戻る（A→B→C→A…）。
    /// （不具合の例：アジス・ラーで長い距離を行ったり来たりした。以前は始めた場所からの距離だけで並べた順に回っていたので、
    /// エーテライトから同じくらいの距離にある南西〔メラシディアン・アンフィプテレ〕と北東〔アラガン・キマイラ〕の点が交互に並び、約1000m を往復していた）
    /// </summary>
    private int NextSpot(HashSet<uint> wanted)
    {
        if (this.tour.Count != this.spots.Count)
            this.BuildTour();
        var at = this.tour.IndexOf(this.spotIndex);
        for (var k = 1; k <= this.tour.Count; k++)
        {
            var i = this.tour[(Math.Max(0, at) + k) % this.tour.Count];
            if (wanted.Contains(this.spotMobs[i]))
                return i;
        }

        return this.spotIndex;
    }

    /// <summary>まだ集め終えていない敵の群れ（見回りの点を除く）の数。1つ以下なら、ほかに回る先が無い。</summary>
    private int WantedClusters(HashSet<uint> wanted)
        => Enumerable.Range(0, this.spots.Count).Count(i => !this.spotRoam[i] && wanted.Contains(this.spotMobs[i]));

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

        // 回る順（一筆書き）を決め、今いる場所から一番近い点から回る
        this.BuildTour();
        var wantedAtStart = this.WantedMobs();
        this.spotIndex = this.tour.FirstOrDefault(i => wantedAtStart.Contains(this.spotMobs[i]), this.tour[0]);
        var roams = this.spotRoam.Count(r => r);
        ctx.Log.Write("戦闘", $"{TeleportTask.TerritoryName(this.territory)}：群れ {this.spots.Count - roams} か所"
                             + (roams > 0 ? $"（ほかに見回りの点 {roams} か所）" : string.Empty)
                             + $"を、{string.Join("→", this.tour.Select(i => $"{this.spots[i].X:0.0},{this.spots[i].Y:0.0}"))} の順に回ります");
        ctx.CombatInProgress = true;
        return TaskResult.Running;
    }

    // 倒されたときの立て直し：倒された回数・倒れているのを見たか・最後に「はい」を押した時刻・戻りのテレポ
    private int deaths;
    private bool deadSeen;
    private DateTime returnPressedAt = DateTime.MinValue;
    private AutoTask? comeBack;

    /// <summary>倒されても立て直す回数（これを超えて倒されたら止める）。</summary>
    public const int DeathLimit = 3;

    /// <summary>倒されたときの帰還の確認の文（Addon 111「戦闘不能状態になりました。ホームポイントに戻りますか？ ホームポイント：…」：ゲームデータで確認）。</summary>
    public const uint ReturnHomeConfirmAddon = 111;

    /// <summary>確認の窓の文が、倒されたときの帰還の確認か（後ろのホームポイントの名前は変わるので、前の決まった部分で比べる）。</summary>
    public static bool IsReturnHomeConfirm(string body)
    {
        if (!Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().TryGetRow(ReturnHomeConfirmAddon, out var row))
            return false;
        static string Flat(string t) => GameUi.Normalize(t).Replace("\n", string.Empty).Replace("\r", string.Empty);
        var fixedPart = Flat(row.Text.ExtractText());
        return fixedPart.Length > 0 && Flat(body).StartsWith(fixedPart, StringComparison.Ordinal);
    }

    /// <summary>
    /// 倒されたら、帰還の確認に「はい」を押して起き上がり（ホームポイントへ戻る）、戦闘のエリアでなければテレポで戻ってやり直す。
    /// <see cref="DeathLimit"/> 回を超えて倒されたら止める。立て直しの途中なら Running、止めるなら Failed、何も無ければ null。
    /// 以前は倒されたら全体を止めていた。
    /// </summary>
    private unsafe TaskResult? HandleDeath(TaskContext ctx)
    {
        var now = DateTime.UtcNow;
        if (Svc.Objects.LocalPlayer is { } me && me.IsDead)
        {
            if (!this.deadSeen)
            {
                this.deadSeen = true;
                this.deaths++;
                this.PauseOwnMovement(ctx);
                this.engage.Forget(this.world);
                if (this.deaths > DeathLimit)
                    return this.Fail($"倒されました（{this.deaths} 回目）。自動動作を止めます");
                ctx.Log.Warn("戦闘", $"倒されました（{this.deaths} 回目）。ホームポイントに戻って起き上がり、{TeleportTask.TerritoryName(this.territory)} へ戻ってやり直します");
            }

            if (GameUi.YesnoText(out var confirm) is { } body && confirm != null && IsReturnHomeConfirm(body) && now - this.returnPressedAt > TimeSpan.FromSeconds(2))
            {
                this.returnPressedAt = now;
                if (GameUi.ClickYes(confirm))
                    ctx.Log.Write("戦闘", "帰還の確認に「はい」を押しました（ホームポイントに戻ります）");
            }

            this.Status = "倒されました。ホームポイントに戻ります";
            return TaskResult.Running;
        }

        if (!this.deadSeen)
            return null;

        // 起き上がった：エリアの移動中・動けない間は待ち、戦闘のエリアでなければテレポで戻る
        if (GameUi.BetweenAreas || !GameUi.PlayerFree())
        {
            this.Status = "起き上がるのを待っています";
            return TaskResult.Running;
        }

        if (Me.Territory != this.territory)
        {
            this.comeBack ??= new TeleportTask(this.territory);
            var r = this.comeBack.Step(ctx);
            this.Status = this.comeBack.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.comeBack.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.comeBack.FailReason : null;
            this.comeBack = null;
            if (failed != null)
                return this.Fail($"倒された後、{TeleportTask.TerritoryName(this.territory)} へ戻れませんでした（{failed}）");
            return TaskResult.Running;
        }

        this.deadSeen = false;
        ctx.Log.Write("戦闘", $"{TeleportTask.TerritoryName(this.territory)} へ戻りました。戦闘を続けます");
        return null;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.HandleDeath(ctx) is { } death)
            return death;

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
            this.engage.Resume(this.world);
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

        // 1) こちらを攻撃してくる敵（敵視リスト）がいて、いま狙っている相手がまだこちらと戦っていない（指定の敵へ近づいている途中など）
        //    なら、攻撃してくる敵を先に倒す（対象外に攻撃されたら棒立ちにならず反撃する。
        //    Henched はハードターゲットしか殴らないので、狙いを変えないと殴られ続ける）。諦めた敵は選び直さない
        var hater = FindHater(this.giveUp, out var onlyGivenUp);
        var aimed = this.CurrentTarget();
        if (hater != null && (aimed == null || (aimed.GameObjectId != hater.GameObjectId && !IsHater(aimed))))
        {
            if (aimed != null)
                ctx.Log.Write("戦闘", $"{hater.Name} に攻撃されているので、先に倒します（{aimed.Name} は後で狙います）");
            this.stuckSince = DateTime.MinValue;
            this.SetTarget(ctx, hater);
            return TaskResult.Running;
        }

        // 2) いまの相手がまだ生きていれば、近づいて RSR に任せる
        if (aimed != null)
            return this.Engage(ctx, aimed);

        this.targetId = 0;

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
        this.RecordSightings(wanted);
        var mob = FindMob(wanted, this.giveUp);
        if (mob != null)
        {
            this.CancelMove(ctx);
            this.SetTarget(ctx, mob);
            return TaskResult.Running;
        }

        // 4) いなければ出現点を回る
        return this.Patrol(ctx, wanted);
    }

    private TaskResult Engage(TaskContext ctx, IBattleNpc t)
    {
        var foe = new NpcFoe(t);
        switch (this.engage.Tick(this.world, ctx.Rotation, ctx.Navmesh, foe, out var status))
        {
            case Engagement.Result.Stalled:
                // 最後に HP が減ってから 45 秒たった（届かない・他人が先に攻撃した等）。この個体は諦める。
                // ハードターゲットも外す（諦めた敵を RSR が殴り続けないように）
                ctx.Log.Warn("戦闘", $"{t.Name} の HP が 45 秒減っていないので、この個体は諦めて別の個体を探します（HP {t.CurrentHp}）");
                this.giveUpAt[t.GameObjectId] = DateTime.UtcNow;
                this.engage.Forget(this.world);
                this.targetId = 0;
                return TaskResult.Running;

            case Engagement.Result.RsrFailed:
                return this.Fail(this.engage.Problem);
        }

        // 範囲攻撃を Off にできない・勝手に狙う設定を切れないときは止める（指定モンスター以外は攻撃しないため。
        // 以前は警告だけで戦い続け、巻き込んだ敵を「攻撃してきた敵」として次々に倒しに行きえた）
        if (ctx.Rotation.TargetingProblem is { } problem)
            return this.Fail($"RSR が指定のモンスター以外を攻撃しうる状態なので、戦闘を止めます：{problem}");

        this.Status = status;
        return TaskResult.Running;
    }

    private void SetTarget(TaskContext ctx, IBattleNpc mob)
    {
        // 出現点へ向かう移動は止める（止めないと、狙った敵をよそに出現点へ走り続け、キャスターは詠唱できない：
        // RSR の調査で分かった不具合。以前は指定の敵を見つけたときだけ止め、攻撃してくる敵に切り替えたときは止めていなかった）
        this.CancelMove(ctx);

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
        this.engage.Start(this.world, new NpcFoe(mob));
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
        if (this.moving != null || this.engage.ApproachIssued)
            this.pausedByUi = true;

        this.CancelMove(ctx);
        this.engage.StopApproach(ctx.Navmesh);
        if (this.pausedByUi && ctx.Navmesh.IsFollowingPath())
            ctx.Navmesh.Stop();
    }

    /// <summary>見えている目当ての敵の位置を記録する（2秒に1回まで。次から出現点の候補に足す）。</summary>
    private void RecordSightings(HashSet<uint> wanted)
    {
        if (wanted.Count == 0 || DateTime.UtcNow - this.sightedAt < TimeSpan.FromSeconds(2))
            return;
        this.sightedAt = DateTime.UtcNow;
        foreach (var o in Svc.Objects.OfType<IBattleNpc>().Where(o => o.BattleNpcKind == BattleNpcSubKind.Combatant && wanted.Contains(o.NameId) && IsAlive(o)))
            MobSightings.Record(this.territory, o.NameId, o.Position);
        MobSightings.Save();
    }

    private TaskResult Patrol(TaskContext ctx, HashSet<uint> wanted)
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

            // 着けなかった出現点は「着いた」にしない（以前は着いた印も付けたので、次のフレームで
            // もう1つ進み、出現点を1つ飛ばしていた。出現点が2つだと、遠い方へ一度も行かずに着けない方を繰り返した）
            (this.spotIndex, this.spotArrivedAt) = SpotAfterMove(this.spotIndex, this.spots.Count, r == TaskResult.Failed, DateTime.UtcNow);
            if (r == TaskResult.Failed)
            {
                // 着けなかった点は飛ばして、回る順の次の点へ（SpotAfterMove の「次の番号」は回る順と違うので使わない）
                this.spotIndex = this.NextSpot(wanted);
            }

            return TaskResult.Running;
        }

        // 出現点に着いた。目当ての敵がいなければ、待たずにすぐ次の群れへ乗って飛ぶ。
        // ほかに回る群れが無いとき（群れが1つ以下）だけ、目当ての死体か、他人と戦っている個体が近くにいれば、湧き直し・決着が近いので待つ
        // （上限 20 秒。湧きの時刻はサーバー側で決められないので、見えるものから決める）
        if (this.spotArrivedAt != DateTime.MinValue && this.WantedClusters(wanted) <= 1)
        {
            var (dead, engaged) = NearbyWanted(wanted);
            if (SpawnWait.Wait(DateTime.UtcNow - this.spotArrivedAt, dead, engaged))
            {
                this.Status = $"出現点 {this.spotIndex + 1}/{this.spots.Count} で湧きを待っています（倒れている {dead}・他人と戦っている {engaged}）";
                return TaskResult.Running;
            }
        }

        if (this.spotArrivedAt != DateTime.MinValue)
            this.spotIndex = this.NextSpot(wanted);

        // 地図（ナビメッシュ）の準備ができるまで待つ（以前は準備前だと床が見つからず、出現点を全部
        // 「床が無い」として毎フレーム飛ばし続けた。読み込みを頼むのは移動の作業の中だけで、そこまで届かなかった）
        if (!ctx.Navmesh.IsReady())
        {
            var progress = ctx.Navmesh.BuildProgress();
            if (progress is not (>= 0 and < 1) && !this.navReloadRequested)
            {
                this.navReloadRequested = true;
                ctx.Navmesh.Reload();
                ctx.Log.Debug("戦闘", "ナビメッシュの準備ができていないので、読み込みを頼みました");
            }

            this.Status = progress is >= 0 and < 1 ? $"ナビメッシュ構築中 {progress * 100:0}%" : "ナビメッシュの準備を待っています";
            return TaskResult.Running;
        }

        var spot = this.spots[this.spotIndex];
        var world = MapCoords.ToWorld(this.territory, spot.X, spot.Y);
        // 出現点の床：今の高さの近く → 少し上から下へ → 地図の上（高さ 1024）から下へ（vnavmesh が地図の旗から床を求めるのと同じ）。
        // 今の高さだけに頼らない（不具合の例：アジス・ラーで島の 500m 下まで降りてしまい、どの出現点も「床が無い」として
        // 表示も移動も無いまま毎フレーム飛ばし続け、3分以上止まった）
        // 行き先は、本来の地面とつながっている床の点だけから選ぶ（vnavmesh で行けない場所は分かる。
        // 以前は岩の上・物の中など、たどり着けない床の点も選び、経路を探し続けて止まった）
        var onFloor = ctx.Navmesh.NearestPointReachable(new Vector3(world.X, Me.Position.Y, world.Z), 10f, 300f)
                      ?? ctx.Navmesh.PointOnFloor(new Vector3(world.X, Me.Position.Y + 100f, world.Z), false, 10f)
                      ?? ctx.Navmesh.PointOnFloor(new Vector3(world.X, 1024f, world.Z), false, 10f);
        if (onFloor == null)
        {
            // 床が見つからない出現点は飛ばす。続けて全部の出現点で見つからなければ、回り続けずに止める
            this.spotIndex = this.NextSpot(wanted);
            this.spotArrivedAt = DateTime.MinValue;
            this.floorMisses++;
            this.Status = $"出現点の床が地図（ナビメッシュ）に見つかりません（{this.floorMisses}/{this.spots.Count}）";
            if (this.floorMisses >= this.spots.Count)
                return this.Fail($"{TeleportTask.TerritoryName(this.territory)} のどの出現点も、床が地図（ナビメッシュ）に見つかりません。"
                                 + "vnavmesh の地図を作り直してから（/vnav rebuild）、もう一度始めてください");
            return TaskResult.Running;
        }

        this.floorMisses = 0;

        // もう着いている点なら、移動を作らずに着いた扱いにする（以前は移動を作っては即座に終わる、を毎フレーム繰り返した：記録で6千回）。
        // 続けて着いたままなら、湧きを待つ間をあける（全部の点が近いときに空回りしない）
        if (Vector2.Distance(new Vector2(Me.Position.X, Me.Position.Z), new Vector2(onFloor.Value.X, onFloor.Value.Z)) <= 8f)
        {
            if (DateTime.UtcNow - this.lastInPlaceAt < TimeSpan.FromSeconds(3))
            {
                this.Status = $"出現点 {this.spotIndex + 1}/{this.spots.Count} の近くで湧きを待っています";
                return TaskResult.Running;
            }

            this.lastInPlaceAt = DateTime.UtcNow;
            this.spotArrivedAt = DateTime.UtcNow;
            return TaskResult.Running;
        }

        // 出現点の巡回は、近い点へ順に回るので1回の移動が短い（50m 前後）。60m 未満は歩く決まりのままだと、ずっと歩いた
        // （不具合の例：高地ドラヴァニアで、倒した後は徒歩で次の出現点へ向かい、マウントに乗らなかった）。15m を超えれば乗って飛ぶ
        this.moving = new MoveToTask(onFloor.Value, 8f, $"出現点 {spot.X:0.0},{spot.Y:0.0}", TimeSpan.FromMinutes(3), mountOver: PatrolMountOver);
        this.spotArrivedAt = DateTime.MinValue;
        return TaskResult.Running;
    }

    /// <summary>
    /// 出現点への移動が終わった後の、次に見る出現点と「着いた時刻」。着けたら同じ出現点で湧きを見る（進めるのは湧きを見た後）。
    /// 着けなければ次の出現点へ1つだけ進め、着いた印は付けない（ゲームを起動せずに試せるように分けた）。
    /// </summary>
    public static (int Index, DateTime ArrivedAt) SpotAfterMove(int index, int count, bool failed, DateTime now)
        => failed ? ((index + 1) % count, DateTime.MinValue) : (index, now);

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
        this.comeBack?.Cleanup(ctx);
        this.comeBack = null;
        MobSightings.Save(force: true);
        ctx.CombatInProgress = false;
        this.CancelMove(ctx);
        var approachDest = this.engage.ApproachIssued ? this.engage.ApproachDestination : null;
        if ((this.engage.ApproachIssued || this.pausedByUi) && ctx.Navmesh.IsMoving())
            ctx.Navmesh.Stop();
        if (approachDest is { } dest)
            MoveToTask.WatchPendingPath(ctx, dest, 2.5f);
        this.engage.StopApproach(ctx.Navmesh);
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

    internal static bool IsAlive(IBattleNpc npc)
        => npc.IsValid() && !npc.IsDead && npc.CurrentHp > 0 && npc.IsTargetable;

    /// <summary>
    /// FATE の敵か（GameObject.FateId が 0 でない。ClientStructs の GameObject.cs で確認）。
    /// RSR は既定の設定（IgnoreNonFateInFate）で、自分が入っていない FATE の敵を殴らない（FATE の中では FATE の敵しか殴らない）ので、
    /// 狙っても HP が減らず 45 秒むだになる（RSR の ObjectHelper.cs）。戦闘の間はこの設定を OFF にする（RotationSolverIpc.HenchedFalseSettings）ので
    /// 殴れるようになるが、OFF にできなかったとき（RSR の版の違い等。記録と注意が出る）に備え、FATE の敵は素材集めでは狙わない
    /// （FATE の敵は同じ名前でも FATE 用の個体で、素材を落とすかも分からない）。攻撃してくる敵なら反撃はする。
    /// </summary>
    private static unsafe bool IsFateMob(IBattleNpc npc)
        => ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address)->FateId != 0;

    /// <summary>近くの指定モンスターのうち、倒れている個体と、他人と戦っている個体の数（出現点で待つかの判断：SpawnWait）。</summary>
    private static (int Dead, int Engaged) NearbyWanted(HashSet<uint> wanted)
    {
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        var dead = 0;
        var engaged = 0;
        foreach (var o in Svc.Objects.OfType<IBattleNpc>())
        {
            if (o.BattleNpcKind != BattleNpcSubKind.Combatant || !wanted.Contains(o.NameId) || Vector3.Distance(o.Position, Me.Position) >= 60f)
                continue;
            if (o.IsDead || o.CurrentHp == 0)
                dead++;
            else if (o.StatusFlags.HasFlag(StatusFlags.InCombat) && o.TargetObjectId != meId)
                engaged++;
        }

        return (dead, engaged);
    }

    /// <summary>近くの指定モンスター（生きている・ターゲットできる・他人と戦っていない・FATE の敵でない）。</summary>
    private static IBattleNpc? FindMob(HashSet<uint> wanted, HashSet<ulong> giveUp)
    {
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        return Svc.Objects
            .OfType<IBattleNpc>()
            .Where(o => o.BattleNpcKind == BattleNpcSubKind.Combatant && wanted.Contains(o.NameId) && IsAlive(o) && !giveUp.Contains(o.GameObjectId))
            .Where(o => !o.StatusFlags.HasFlag(StatusFlags.InCombat) || o.TargetObjectId == meId)
            .Where(o => !IsFateMob(o))
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

        var haters = HaterIds();
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

    /// <summary>その敵がこちらと戦っているか（敵視リストに載っているか、自分を狙っている）。</summary>
    internal static bool IsHater(IBattleNpc npc)
    {
        var meId = Svc.Objects.LocalPlayer?.GameObjectId ?? 0;
        return GameUi.InCombat && (npc.TargetObjectId == meId || HaterIds().Contains(npc.EntityId));
    }

    /// <summary>敵視リスト（UIState.Hater）に載っている敵の EntityId。</summary>
    private static unsafe HashSet<uint> HaterIds()
    {
        var haters = new HashSet<uint>();
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (ui != null)
        {
            ref var hater = ref ui->Hater;
            for (var i = 0; i < hater.HaterCount && i < hater.Haters.Length; i++)
                if (hater.Haters[i].EntityId != 0)
                    haters.Add(hater.Haters[i].EntityId);
        }

        return haters;
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

    public static List<(uint Territory, List<CombatNeed> Needs, List<(Vector2 Spot, uint Mob)> Spots)> Plan(
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
        var result = new List<(uint, List<CombatNeed>, List<(Vector2 Spot, uint Mob)>)>();
        while (remaining.Count > 0)
        {
            var best = remaining
                .SelectMany(i => options[i].Keys)
                .Distinct()
                .Select(t => (Terr: t, Count: remaining.Count(i => options[i].ContainsKey(t))))
                .OrderByDescending(x => x.Count)
                .First();

            var needs = new List<CombatNeed>();
            var spots = new List<(Vector2 Spot, uint Mob)>();
            foreach (var item in remaining.Where(i => options[i].ContainsKey(best.Terr)).ToList())
            {
                var mobs = options[item][best.Terr];
                needs.Add(new CombatNeed(item, inv.CountAll(item) + shortfalls[item], mobs));
                foreach (var mob in mobs)
                    spots.AddRange(sources.SpawnsOf(mob).Where(s => s.Territory == best.Terr).Select(s => (new Vector2(s.MapX, s.MapY), mob)));
                remaining.Remove(item);
            }

            result.Add((best.Terr, needs, spots));
        }

        return result;
    }
}
