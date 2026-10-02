using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoJobQuest.Automation;

/// <summary>精選で集める1品目。</summary>
/// <param name="ItemId">欲しい品（霊砂など）。</param>
/// <param name="TargetOwned">持っていたい総数（今の所持数＋不足数）。</param>
/// <param name="Sources">精選の元にする採集品（採れる見込みの高い順）。</param>
/// <param name="CanBuyInstead">
/// マーケットで代わりに買えるか（手段にマーケットが残っている）。true なら、元の収集品の採集点が今どれも出ていないときは精選せず、
/// 次の周回でマーケットに回す。false なら、今までどおり出るのを待って採る（ほかに手段が無いので）。
/// </param>
public sealed record ReduceNeed(uint ItemId, int TargetOwned, List<uint> Sources, bool CanBuyInstead = false);

/// <summary>
/// 収集品を GBR に採らせ、こちらで精選して、欲しい品（霊砂など）を集める。
///
/// GBR の自動精選に任せない理由（GBR のソースで確認）:
///  ・GBR の精選の設定（DoReduce）は既定で OFF（AutoGather.Config.cs:27）。
///  ・ON でも、精選するのは「空き枠が 20 未満」か「空き枠 0」のときだけ（AutoGather.cs:680-714, 890）。
///    空きの多いキャラでは、採集の途中で精選されない。
///  ・GBR は精選できる収集品を全部精選する（利用者が別の目的で持っている収集品まで。AutoGather.Purify.cs:13-48）。
///
/// こちらのやり方（AutoHook の Tasks/AetherialReduction.cs と同じ）:
///  ・AgentPurify.ReduceItem(カバンの枠) で、元の収集品の枠だけを1個ずつ精選する（選択の窓を通さない）。
///  ・結果の窓（PurifyResult）は「閉じる」（node 20。ECommons・GBR の AddonMaster と同じ）で閉じる。
///    閉じるのは自分の精選の後に開いたものだけ。
///  ・採らせている間は、GBR 自身の自動精選（DoReduce）を切る（GatherTask）。
///  ・1個ごとに「元の収集品が減った AND 欲しい品が増えた」を確かめる。欲しい数に届いたらそこでやめる
///    （1個から出る数は1〜4で幅があるので、精選しすぎない）。
///  ・元の収集品が無ければ、GBR に少しずつ採らせる（足りない数の半分ずつ）。採っている途中でも、
///    欲しい数に届いたら GBR を止める。
///    採っている間は GBR の収集品の自動納品を切る（納品されると精選できないため）。
/// </summary>
public sealed unsafe class ReduceTask : AutoTask
{
    private enum ReduceStep { Decide, Gather, Reduce, WaitResult }

    /// <summary>精選の結果の窓の「閉じる」ボタン（ECommons・GBR の AddonMaster.PurifyResult と同じ）。</summary>
    private const uint PurifyResultCloseNode = 20;

    private const int MaxGatherCycles = 8;

    private readonly ReduceNeed need;
    private readonly List<uint> sources;

    private ReduceStep step = ReduceStep.Decide;
    private GatherTask? gather;
    private uint gatheringSource;
    private int gatherCycles;

    private uint reducingItem;
    private int sourceBefore;
    private int wantedBefore;
    private DateTime reducedAt = DateTime.MinValue;
    private DateTime lastClose = DateTime.MinValue;

    // 精選したときに結果の窓が開いていたか（開いていなかったなら、その後に開いた結果の窓は自分の精選のもの）
    private bool resultOpenAtReduce = true;

    // 自分の結果の窓を閉じ始めた時刻（閉じられないまま待ち続けない）
    private DateTime closingSince = DateTime.MinValue;
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(10);
    private DateTime lastDismount = DateTime.MinValue;
    private int oddResults;
    private int reducedCount;

    /// <summary>集めきれなかった品目（呼び出し側が次の手段を選ぶのに使う）。</summary>
    public List<uint> Unfinished { get; } = [];

    /// <summary>
    /// 元の収集品の採集点が今どれも出ていないので精選しなかった。この場合は精選の手段を外さない
    /// （時間の問題で「精選できない」ではないので、次の周回で時刻を見て決め直す）。
    /// </summary>
    public bool OutOfTime { get; private set; }

    public ReduceTask(ReduceNeed need)
    {
        this.need = need;
        this.sources = [.. need.Sources];
    }

    public override string Name => $"精選: {CraftPlanner.ItemName(this.need.ItemId)}";

    private int Owned => Inventory.CountNow(this.need.ItemId);

    /// <summary>精選が解放済みか（一般アクション 21 の解放条件をゲームに聞く。Unlocks.IsUnlocked）。</summary>
    public static bool IsUnlocked() => Unlocks.IsUnlocked(Unlocks.Reduction);

    /// <summary>
    /// 精選を使える見込みがあるか：解放済み、または解放クエストを自動で進められる（前提のメインクエスト等が済んでいる）
    /// うえに、この実行で解放をあきらめていない。実行の最初の「機能の解放」の段で解放する。
    /// </summary>
    public static bool Usable()
    {
        if (IsUnlocked())
            return true;
        if (Unlocks.UnlockStagePassed || Unlocks.GaveUp.Contains(Unlocks.Reduction))
            return false;
        var quest = Unlocks.UnlockQuest(Unlocks.Reduction);
        if (quest == 0)
            return false;
        Unlocks.ChainToRun(quest, out var blocked);
        return blocked == null;
    }

    /// <summary>
    /// 精選の元のうち、いまの採掘師・園芸師で採れるもの（採れる見込みの高い順）：採集点のレベルが足りて、その職の「収集品採集」が
    /// 使える（ゲームデータ：収集品採集はクエスト「職人の新たなお仕事」で解放。未完了だと GBR は収集品を見た時点で
    /// 採集をやめる＝GBR の AutoGather.cs。以前は確かめず、精選の周回がむだになりえた）。使えるか確かめられないときは外さない。
    /// </summary>
    /// <param name="assumeCollect">
    /// 「収集品採集」をこれから解放する（<see cref="CollectUnlockChain"/> を進める）として見るか。機能の解放の段で、精選の解放を試すかを決めるときに使う。
    /// </param>
    public static List<uint> UsableSources(SourceIndex sources, uint itemId, bool assumeCollect = false)
        => sources.Get(itemId).ReducedFrom
            .Where(src => sources.Get(src).Gather.Any(g =>
                Jobs.Level(g.Mining ? Jobs.Gatherers[0] : Jobs.Gatherers[1]) >= g.GatheringLevel
                // GBR はギアセットで着替えて採るので、ギアセットの無い採集職では採れない
                && GearCheck.HasGearset(g.Mining ? Jobs.Gatherers[0] : Jobs.Gatherers[1])
                && (assumeCollect || GatherAbilities.Usable(g.Mining ? GatherAbilities.MinerCollect : GatherAbilities.BotanistCollect) != false)))
            .ToList();

    /// <summary>
    /// 精選の解放クエスト（「生命、精選、もうひとつの答え」）を自動で進められるか（解放済みなら true）。前提が止まらず、連鎖のどのクエストにも
    /// 受けられる職（レベルとギアセット）がある。「職人の新たなお仕事」を前倒しするかを決めるときに見る
    /// （以前は精選の解放クエストを受けられる職が無いのに、「職人の新たなお仕事」だけ進めることがあった）。
    /// </summary>
    public static bool ReductionRunnable()
    {
        if (IsUnlocked())
            return true;
        var quest = Unlocks.UnlockQuest(Unlocks.Reduction);
        if (quest == 0)
            return false;
        var chain = Unlocks.ChainToRun(quest, out var blocked);
        return blocked == null && chain.All(id => Unlocks.PickJobFor(id) != null);
    }

    /// <summary>
    /// 機能の解放の段より前の計画で、「収集品採集」をこれから解放する（「職人の新たなお仕事」を前倒しする）として精選の元を数えるか。
    /// 解放の段（JobQuestFlow.StartUnlock）と同じ見込みにそろえる（以前は計画で精選を外し、開始の確認に霊砂を
    /// 「マーケットで買う」と出したのに、実際はクエストを進めてから精選していた）。解放の段の後・この実行で精選をあきらめた後は false。
    /// </summary>
    public static bool CollectAssumable()
        => !Unlocks.UnlockStagePassed && !Unlocks.GaveUp.Contains(Unlocks.Reduction) && ReductionRunnable() && CollectUnlockChain() is { Count: > 0 };

    /// <summary>
    /// 精選の元の収集品を採る「収集品採集」を解放するクエスト（ゲームデータ：アクションの解放条件 Action.UnlockLink。採掘師・園芸師とも
    /// 「職人の新たなお仕事」67631・Lv50・クラフター/ギャザラー）のうち未完了のものを、前提ごと進める順に並べたもの。
    /// 済んでいれば空、自動で進められない（前提のメインクエスト等が未完了・受けられる職が無い）なら null。
    /// 受けられる順に「職人の新たなお仕事」（Lv50）→「生命、精選、もうひとつの答え」（Lv56）で受ける。
    /// 以前は「収集品採集」が使えないと精選の元を採れないとみなして、精選の解放そのものを試さず、霊砂をマーケットで買っていた。
    /// </summary>
    public static List<uint>? CollectUnlockChain()
    {
        var chain = new List<uint>();
        var quests = new[] { GatherAbilities.MinerCollect, GatherAbilities.BotanistCollect }
            .Select(GatherAbilities.UnlockQuest)
            .Where(q => q != 0 && !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q))
            .Distinct();
        foreach (var q in quests)
        {
            var list = Unlocks.ChainToRun(q, out var blocked);
            if (blocked != null || list.Any(id => Unlocks.PickJobFor(id) == null))
                return null;
            foreach (var id in list.Where(id => !chain.Contains(id)))
                chain.Add(id);
        }

        return chain;
    }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (!IsUnlocked())
        {
            this.Unfinished.Add(this.need.ItemId);
            ctx.Log.Warn("精選", "精選が未解放なので、精選では集められません");
            return TaskResult.Done;
        }

        ctx.Log.Write("精選", $"{CraftPlanner.ItemName(this.need.ItemId)} を {this.need.TargetOwned} 個まで集めます（今 {this.Owned} 個）。元にする収集品：{string.Join("、", this.sources.Select(CraftPlanner.ItemName))}");

        // 前から持っている元の収集品は精選しない（以前は利用者が別の目的で持っている収集品まで使っていた。
        // GBR の自動精選を切った理由と同じ）。始めた時点の数を控え、それより増えた分（この作業で採った分）だけを精選する
        var held = Inventory.HeldCollectables();
        foreach (var src in this.need.Sources)
        {
            var n = held.GetValueOrDefault(src);
            this.keepCollectables[src] = n;
            if (n > 0)
                ctx.Log.Write("精選", $"前から持っている {CraftPlanner.ItemName(src)}（収集品）{n} 個は精選しません");
        }

        // 精選の結果の窓は、自分の精選で開いたものだけ閉じる（記録を始める）
        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        return TaskResult.Running;
    }

    // 始めた時点で持っていた元の収集品の数（元の品 → 数。これを超えた分だけ精選する）
    private readonly Dictionary<uint, int> keepCollectables = [];

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.Elapsed > TimeSpan.FromMinutes(120))
        {
            this.Unfinished.Add(this.need.ItemId);
            ctx.Log.Warn("精選", $"2時間たっても {CraftPlanner.ItemName(this.need.ItemId)} が集まりませんでした（{this.Owned}/{this.need.TargetOwned}）");
            return TaskResult.Done;
        }

        return this.step switch
        {
            ReduceStep.Decide => this.TickDecide(ctx),
            ReduceStep.Gather => this.TickGather(ctx),
            ReduceStep.Reduce => this.TickReduce(ctx),
            _ => this.TickWaitResult(ctx),
        };
    }

    private TaskResult TickDecide(TaskContext ctx)
    {
        if (this.Owned >= this.need.TargetOwned)
        {
            ctx.Log.Write("精選", $"{CraftPlanner.ItemName(this.need.ItemId)} がそろいました（{this.Owned}/{this.need.TargetOwned}、精選 {this.reducedCount} 回）");
            return TaskResult.Done;
        }

        // 元の収集品を持っていれば精選する
        if (this.FindSourceSlot() != null)
        {
            this.Go(ReduceStep.Reduce, "精選します");
            return TaskResult.Running;
        }

        // 持っていなければ採らせる
        if (this.sources.Count == 0 || this.gatherCycles >= MaxGatherCycles)
        {
            this.Unfinished.Add(this.need.ItemId);
            ctx.Log.Warn("精選", this.sources.Count == 0
                ? $"精選の元にする収集品を採れませんでした（{this.Owned}/{this.need.TargetOwned}）"
                : $"{MaxGatherCycles} 回採りに行っても {CraftPlanner.ItemName(this.need.ItemId)} がそろいませんでした（{this.Owned}/{this.need.TargetOwned}）");
            return TaskResult.Done;
        }

        // 今その採集点が出ていて間に合う元の収集品を選ぶ（時限の品は採れるなら採る、採れなければマーケット。
        // 霊砂の元の収集品は未知の採集場所（エリアごとに ET 4 時間ずつずれて出る）のものが多い）。どれも今は採れなければ、精選では
        // 集めず、次の周回でマーケットに回す。以前は採集点のレベルの低い順の先頭を選び、出るまで待つことがあった
        var hour = EorzeaTime.Hour();
        var unlocked = AreaAccess.UnlockedNow();
        var upNow = ctx.Data.Sources is { } idx
            ? this.sources.Select(s => (Item: s, Rem: PlanBuilder.GatherUpRemaining(idx.Get(s), unlocked, Jobs.Level, GearCheck.HasGearset, hour)))
                .Where(x => x.Rem >= PlanBuilder.MinUpHours).ToList()
            : this.sources.Select(s => (Item: s, Rem: 24.0)).ToList();

        // マーケットで代わりに買えないときは、今までどおり先頭の元の収集品を、出るのを待って採る（ほかに手段が無い）
        if (upNow.Count == 0 && !this.need.CanBuyInstead)
            upNow.Add((this.sources[0], 24.0));
        if (upNow.Count == 0)
        {
            this.OutOfTime = true;
            this.Unfinished.Add(this.need.ItemId);
            ctx.Log.Warn("精選", $"精選の元にする収集品（{string.Join("・", this.sources.Select(CraftPlanner.ItemName))}）の採集点が、今はどれも出ていない（間に合わない）ので、"
                               + $"精選では集めません（次の周回でマーケットで買います。{this.Owned}/{this.need.TargetOwned}）");
            return TaskResult.Done;
        }

        this.gatherCycles++;
        this.gatheringSource = upNow[0].Item;
        var gatherLimit = upNow[0].Rem >= 24 ? TimeSpan.FromMinutes(60) : TimeSpan.FromSeconds(Math.Min(60 * 60, upNow[0].Rem * EorzeaTime.SecondsPerHour + 60));
        var remaining = this.need.TargetOwned - this.Owned;
        var count = Math.Max(1, (int)Math.Ceiling(remaining / 2.0)); // 1個から1〜4個出るので、半分ずつ採っては精選して確かめる
        var target = this.need.TargetOwned;
        var wanted = this.need.ItemId;
        this.gather = new GatherTask(
            [new GatherNeed(this.gatheringSource, 0, ExtraFromNow: count)], null,
            $"精選用の {CraftPlanner.ItemName(this.gatheringSource)}", gatherLimit, Route.Reduce)
        {
            StopWhen = () => Inventory.CountNow(wanted) >= target,
            KeepCollectables = true,
        };
        ctx.Log.Write("精選", $"{CraftPlanner.ItemName(this.gatheringSource)} を {count} 個採ります（{this.gatherCycles} 回目。{CraftPlanner.ItemName(wanted)} は あと {remaining} 個）");
        this.Go(ReduceStep.Gather, "精選用の収集品を採っています");
        return TaskResult.Running;
    }

    private TaskResult TickGather(TaskContext ctx)
    {
        var r = this.gather!.Step(ctx);
        this.Status = this.gather.Status;
        if (r == TaskResult.Running)
            return TaskResult.Running;

        this.gather.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.gather.FailReason : null;
        var unfinished = this.gather.Unfinished.Contains(this.gatheringSource);
        this.gather = null;
        if (failed != null)
            return this.Fail(failed);

        // 1個も採れずに止まった元は候補から外す（次の元を試す）
        if (unfinished && this.FindSourceSlot() == null)
        {
            ctx.Log.Warn("精選", $"{CraftPlanner.ItemName(this.gatheringSource)} を採れなかったので、別の元を試します");
            this.sources.Remove(this.gatheringSource);
        }

        this.Go(ReduceStep.Decide, string.Empty);
        return TaskResult.Running;
    }

    private TaskResult TickReduce(TaskContext ctx)
    {
        // 前の結果の窓が残っていれば先に閉じる。自分の精選で開いたものでなければ触らず、閉じられるのを待つ
        // （以前は名前だけで閉じていたので、始める前から開いていた窓まで閉じえた）
        if (GameUi.IsReady("PurifyResult", out _))
        {
            if (this.TryGetOwnResult(ctx, out var own))
                return this.CloseResult(own);

            this.Status = "精選の結果の窓が開いています（自分の精選のものではないので閉じません）";
            return this.TimedOut(TimeSpan.FromSeconds(60))
                ? this.Fail("自分の精選のものではない精選の結果の窓が開いたままです。閉じてからやり直してください")
                : TaskResult.Running;
        }

        this.closingSince = DateTime.MinValue;
        this.resultWaitSince = null;

        // 騎乗中は精選できない（GBR も騎乗中は精選しない：AutoGather.Purify.cs:15）
        if (GameUi.Mounted)
        {
            if (DateTime.UtcNow - this.lastDismount >= TimeSpan.FromSeconds(1))
            {
                this.lastDismount = DateTime.UtcNow;
                GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：ゲームデータで確認）
            }

            this.Status = "マウントから降りています";
            return TaskResult.Running;
        }

        if (!GameUi.PlayerFree() || Svc.Condition[ConditionFlag.Occupied39])
        {
            this.Status = "動ける状態になるのを待っています";
            return this.TimedOut(TimeSpan.FromSeconds(60)) ? this.Fail("キャラクターが動ける状態にならないため、精選できません") : TaskResult.Running;
        }

        var slot = this.FindSourceSlot();
        if (slot == null)
        {
            this.Go(ReduceStep.Decide, string.Empty);
            return TaskResult.Running;
        }

        var agent = AgentPurify.Instance();
        if (agent == null)
            return this.Fail("精選の仕組み（AgentPurify）に届きません");

        this.reducingItem = slot->GetBaseItemId();
        this.sourceBefore = Inventory.HeldCollectables().GetValueOrDefault(this.reducingItem);
        this.wantedBefore = this.Owned;
        DebugLog.Current?.Line("操作", $"精選: {CraftPlanner.ItemName(this.reducingItem)}（{slot->Container} の {slot->Slot} 番、収集価値 {slot->GetCollectability()}）");
        // 時刻は精選する前に取る（同じフレームで開いた結果の窓も「精選の後に開いた」に入るように）
        this.reducedAt = DateTime.UtcNow;
        this.resultOpenAtReduce = GameUi.IsVisible("PurifyResult");
        agent->ReduceItem(slot);
        this.Go(ReduceStep.WaitResult, $"{CraftPlanner.ItemName(this.reducingItem)} を精選しています");
        return TaskResult.Running;
    }

    private TaskResult TickWaitResult(TaskContext ctx)
    {
        if (this.TryGetOwnResult(ctx, out var result))
            return this.CloseResult(result);
        this.closingSince = DateTime.MinValue;
        this.resultWaitSince = null;

        var sourceNow = Inventory.HeldCollectables().GetValueOrDefault(this.reducingItem);
        var wantedNow = this.Owned;
        var busy = Svc.Condition[ConditionFlag.Occupied39] || !GameUi.PlayerFree();

        // 減った AND 増えた
        if (!busy && sourceNow < this.sourceBefore && wantedNow > this.wantedBefore)
        {
            this.reducedCount++;
            this.oddResults = 0;
            ctx.Log.Write("精選", $"{CraftPlanner.ItemName(this.reducingItem)} を精選しました → {CraftPlanner.ItemName(this.need.ItemId)} +{wantedNow - this.wantedBefore}（{wantedNow}/{this.need.TargetOwned}）");
            this.Go(ReduceStep.Decide, string.Empty);
            return TaskResult.Running;
        }

        if (this.PhaseElapsed < TimeSpan.FromSeconds(15))
            return TaskResult.Running;

        if (sourceNow < this.sourceBefore)
        {
            // 精選はされたが、欲しい品が増えなかった（別の品だけが出た等。精選の結果は品が決まっていない）。
            // 続けるが、続けて4回（成功で数え直す。以前は累計だった）出なければ止める
            if (++this.oddResults > 3)
                return this.Fail($"精選しても {CraftPlanner.ItemName(this.need.ItemId)} が出ないことが {this.oddResults} 回続きました（精選の対応表が違う可能性）");
            ctx.Log.Warn("精選", $"{CraftPlanner.ItemName(this.reducingItem)} を精選しましたが、{CraftPlanner.ItemName(this.need.ItemId)} は増えませんでした");
            this.Go(ReduceStep.Decide, string.Empty);
            return TaskResult.Running;
        }

        return this.Fail($"精選が受け付けられませんでした（{CraftPlanner.ItemName(this.reducingItem)} {this.sourceBefore}→{sourceNow}、{CraftPlanner.ItemName(this.need.ItemId)} {this.wantedBefore}→{wantedNow}）");
    }

    /// <summary>
    /// 自分の精選（最後に精選した時刻より後）で開いた結果の窓。持ち主の記録で確かめ、記録が無ければ
    /// 「精選したときは閉じていて、いま開いている」ことで確かめる（YesAlready は結果の窓を PostUpdate で扱っていて、
    /// 開くたびに作り直されるとは限らない。作り直されないと記録が付かず、2回目から自分の窓と分からなくなる）。
    /// </summary>
    private bool TryGetOwnResult(TaskContext ctx, out FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* result)
    {
        result = null;
        if (this.reducedAt == DateTime.MinValue)
            return false;
        if (ctx.Ownership.TryGetOwnedSince("PurifyResult", this.reducedAt, out result))
            return true;
        return !this.resultOpenAtReduce && GameUi.IsReady("PurifyResult", out result);
    }

    /// <summary>
    /// 自分の結果の窓を閉じる（0.5 秒おき）。上限を過ぎても閉じられなければ止める。
    /// 閉じるのは、結果が表示され（AddonPurifyResult の ResultsMode が 0 でない）、ゲームの処理中の印（Occupied39）が下りてから
    /// （YesAlready・GBR と同じ。以前は窓を見つけるとすぐ閉じにいき、結果の表示の前に閉じる合図を撃ちえた。
    /// そのとき何が起きるかは、どこにも記録が無い）。10秒待っても表示されなければ、これまでどおり閉じる。
    /// </summary>
    private TaskResult CloseResult(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* result)
    {
        if (this.closingSince == DateTime.MinValue)
        {
            var shown = ResultShown(result) && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39];
            if (!shown)
            {
                this.resultWaitSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - this.resultWaitSince.Value < TimeSpan.FromSeconds(10))
                {
                    this.Status = "精選の結果が表示されるのを待っています";
                    return TaskResult.Running;
                }

                DebugLog.Current?.Line("精選", "精選の結果が10秒たっても表示されないので、そのまま閉じます");
            }

            // 自分の精選の結果か（エージェントの結果の品。記録に残す：精選の結果は元の品ごとに数種類ありうる）
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentPurify.Instance();
            if (agent != null)
                DebugLog.Current?.Line("精選", $"精選の結果：{CraftPlanner.ItemName(agent->ResultItemId)}（ResultItemId {agent->ResultItemId}）");
            this.resultWaitSince = null;
            this.closingSince = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - this.closingSince > CloseLimit)
            return this.Fail($"精選の結果の窓を {CloseLimit.TotalSeconds:0} 秒たっても閉じられませんでした。手で閉じてからやり直してください");

        this.Status = "精選の結果の窓を閉じています";
        if (DateTime.UtcNow - this.lastClose < TimeSpan.FromMilliseconds(500))
            return TaskResult.Running;
        this.lastClose = DateTime.UtcNow;
        if (!GameUi.ClickButton(result, PurifyResultCloseNode))
            GameUi.Fire(result, true, -1);
        return TaskResult.Running;
    }

    // 結果が表示されるのを待ち始めた時刻
    private DateTime? resultWaitSince;

    /// <summary>精選の結果の窓に結果が表示されているか（ResultsMode が 0 でない。読めなければ true＝待たない）。</summary>
    private static bool ResultShown(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* result)
    {
        if (result->AtkValuesCount <= 13 || result->AtkValues == null)
            return true;
        var rm = ((FFXIVClientStructs.FFXIV.Client.UI.AddonPurifyResult*)result)->TypedAtkValues->ResultsMode;
        return rm.Int != 0;
    }

    /// <summary>
    /// カバンの中の、精選の元にする収集品の枠。始めた時点より増えた元の品だけを対象にし、収集価値の低い個体から使う
    /// （前から持っていた分と見分けられないので数で守る。価値の高い個体を残す）。無ければ null。
    /// </summary>
    private InventoryItem* FindSourceSlot()
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return null;

        var held = Inventory.HeldCollectables();
        InventoryItem* best = null;
        foreach (var type in Inventory.Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !s->IsCollectable())
                    continue;
                var src = s->GetBaseItemId();
                if (!this.need.Sources.Contains(src) || held.GetValueOrDefault(src) <= this.keepCollectables.GetValueOrDefault(src))
                    continue;
                if (best == null || s->SpiritbondOrCollectability < best->SpiritbondOrCollectability)
                    best = s;
            }
        }

        return best;
    }

    private void Go(ReduceStep s, string status)
    {
        this.step = s;
        this.NextPhase(status);
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.gather?.Cleanup(ctx);
        this.gather = null;

        // 自分の精選で出た結果の窓が残っていれば閉じる
        if (this.TryGetOwnResult(ctx, out var result))
        {
            DebugLog.Current?.Line("操作", "止めたので精選の結果の窓を閉じます");
            if (!GameUi.ClickButton(result, PurifyResultCloseNode))
                GameUi.Fire(result, true, -1);
        }

        ctx.Ownership.Clear();
    }
}
