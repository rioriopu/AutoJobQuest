using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoJobQuest.Automation;

/// <summary>採集・釣りで集める1品目。</summary>
/// <param name="ItemId">アイテム。</param>
/// <param name="TargetOwned">
/// 持っていたい総数（計画と同じ数え方＝Inventory.Snapshot。収集品は数えない）。採る数は作業を始めるときに
/// 「総数 − その時点の所持数」で決める（以前は計画時の不足数を開始時の所持数に足していたので、
/// その間に別の手段で手に入った分まで余計に採っていた）。
/// </param>
/// <param name="ExtraFromNow">総数ではなく「今から何個採るか」で頼むとき（精選の元にする収集品。0 なら TargetOwned を使う）。</param>
public sealed record GatherNeed(uint ItemId, int TargetOwned, int ExtraFromNow = 0);

/// <summary>
/// GBR の自動採集で素材を集める（採掘・園芸・釣り・シャード）。
///
/// 手順:
///  1) GBR がすでに動いていたら横取りしない（利用者が使用中）。
///  2) 設定を一時的に書き換える：GoHomeWhenDone / GoHomeWhenIdle を false。
///     終わったとき・待機のときに GBR が勝手に帰宅（Lifestream /li auto ＝テレポ）しないように。
///     GBR の作業中に理由の分からないテレポが起きる主な原因と見ている。
///  3) 専用リスト「AutoJobQuest」に品目と「持っていたい総数」を入れ、他のリストは退避する。
///  4) IPC で ON。ON にならなければ（知覚不足など）失敗。
///  5) 毎フレーム所持数を見る。全部そろったら OFF。GBR が自分で OFF になったら所持数で成否を判定する
///     （GBR は完了と失敗を区別しない）。
///  6) 後始末でリストと設定を元に戻す。
///
/// 所持数は GBR と同じ数え方にする（食い違うと「GBR は終わったと言うがこちらは足りない」が起きる）:
///   GetInventoryItemCount(item, isHq:false, checkEquipped:false, checkArmory:false, minCollectability:0)
/// </summary>
public sealed class GatherTask : AutoTask
{
    private readonly List<GatherNeed> needs;
    private readonly uint? preferredTerritory;
    private readonly string label;
    private readonly TimeSpan limit;

    private readonly Dictionary<uint, uint> targets = [];
    private bool enabledByMe;

    /// <summary>集めきれなかった品目（呼び出し側が次の手段を選ぶのに使う）。</summary>
    public List<uint> Unfinished { get; } = [];

    /// <summary>
    /// これが true になったら、目標に届いていなくても採集をやめる（例：精選で霊砂が足りた。
    /// 欲しい霊砂が手に入ったら、GBR のリストから外す）。
    /// </summary>
    public Func<bool>? StopWhen { get; init; }

    /// <summary>
    /// 採った収集品を GBR に納品させない（精選に使うため）。true なら採集の間だけ GBR の
    /// 「収集品の自動納品」（CollectableConfig.AutoTurnInCollectables）を OFF にする。控えを取り、終わったら元に戻る。
    /// </summary>
    public bool KeepCollectables { get; init; }

    /// <summary>
    /// この作業がどの手段か（採集か釣りか）。集めきれなかったときに、どの手段を外すかをこれで決める
    /// （品目の性質で決めると、採集でも釣りでも取れる品で、失敗した手段と違う手段を外してしまう）。
    /// </summary>
    public Planning.Route Route { get; }

    public GatherTask(IEnumerable<GatherNeed> needs, uint? preferredTerritory, string label, TimeSpan limit, Planning.Route route = Planning.Route.Gather)
    {
        this.needs = needs.ToList();
        this.preferredTerritory = preferredTerritory;
        this.label = label;
        this.limit = limit;
        this.Route = route;
    }

    public override string Name => $"採集: {this.label}";

    /// <summary>
    /// GBR と同じ数え方の所持数（GBR の GatherableExtensions.GetInventoryCount と同じ）：
    /// 普通の品（最低収集価値 0）＋ 収集品になりうる品なら収集品（最低収集価値 1）も足す。
    /// 収集品を数えないと、GBR が目標まで採って止まったのに「0 個のまま止まった」と誤って判定する。
    /// </summary>
    public static unsafe int GbrCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var count = im->GetInventoryItemCount(itemId, false, false, false, 0);
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var row) && row.IsCollectable)
            count += im->GetInventoryItemCount(itemId, false, false, false, 1);
        return count;
    }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.needs.Count == 0)
            return TaskResult.Done;

        if (!ctx.GatherBuddy.IsLoaded)
            return this.Fail("GatherBuddyReborn が読み込まれていません");

        // 動いている・読めないときは始めない（利用者の操作を横取りしない。fail-closed）
        var running = ctx.GatherBuddy.IsAutoGatherEnabled();
        if (running != false)
            return this.Fail(running == true
                ? "GBR の自動採集がすでに動いています（利用者の操作を横取りしないため止めました）"
                : "GBR の自動採集の状態が読めません");

        // NPC 購入が動いている・読めないときも始めない（購入と採集を取り合わない）
        var vendorBusy = ctx.Gbr.VendorIsBusy();
        if (vendorBusy != false)
            return this.Fail(vendorBusy == true
                ? "GBR の NPC 購入が動いています（終わってから採集を始めます）"
                : "GBR の NPC 購入の状態が読めません（GBR の版が変わった可能性）");

        // 採る数は、いまの所持数から決める。GBR には GBR の数え方（収集品も含む）での目標を渡す
        var inv = Inventory.Snapshot();
        foreach (var n in this.needs)
        {
            var add = n.ExtraFromNow > 0 ? n.ExtraFromNow : n.TargetOwned - inv.CountAll(n.ItemId);
            if (add <= 0)
            {
                ctx.Log.Write("採集", $"{CraftPlanner.ItemName(n.ItemId)} はもう足りています（{inv.CountAll(n.ItemId)}/{n.TargetOwned}）");
                continue;
            }

            this.targets[n.ItemId] = (uint)(GbrCount(n.ItemId) + add);
        }

        if (this.targets.Count == 0)
            return TaskResult.Done;

        // 終わったとき・待機のときに勝手に帰宅（テレポ）しないように
        if (!ctx.Gbr.OverrideBool("GoHomeWhenDone", false) || !ctx.Gbr.OverrideBool("GoHomeWhenIdle", false))
            ctx.Log.Warn("採集", $"GBR の帰宅設定を一時的に切れませんでした: {ctx.Gbr.LastError}");

        // GBR の自動精選を切る（GBR の精選は、カバンにある精選できる収集品を全部対象にする
        // ＝AutoGather.Purify.cs:13-48 の HasReducibleItems は採集リストの品に限らない。利用者が別の目的で持っている
        // 収集品まで精選されないように。精選はこちらの ReduceTask が品と数を決めて行う）。切れなければ始めない
        if (!ctx.Gbr.OverrideBool("DoReduce", false))
            return this.Fail($"GBR の自動精選（DoReduce）を一時的に切れませんでした（手持ちの収集品を精選されうるため止めます）: {ctx.Gbr.LastError}");

        // 精選に使う収集品を、GBR が収集品納品窓口へ持っていかないように（GBR は収集品が溜まると納品しに行く：AutoGather.cs:1062）
        if (this.KeepCollectables && !ctx.Gbr.OverrideBool(GbrHandle.CollectablePrefix + "AutoTurnInCollectables", false))
            return this.Fail($"GBR の収集品の自動納品を一時的に切れませんでした（採った収集品を納品されてしまうため止めます）: {ctx.Gbr.LastError}");

        var entries = this.targets.Select(t => new GbrGatherEntry(t.Key, t.Value, this.preferredTerritory)).ToList();
        if (!ctx.Gbr.PrepareGatherList(entries, out var unsupported))
            return this.Fail(ctx.Gbr.LastError ?? "GBR のリストを用意できませんでした");

        foreach (var id in unsupported)
        {
            ctx.Log.Warn("採集", $"{CraftPlanner.ItemName(id)} は GBR で扱えない品目でした");
            this.Unfinished.Add(id);
            this.targets.Remove(id);
        }

        if (this.targets.Count == 0)
            return TaskResult.Done;

        ctx.Log.Write("採集", $"{this.label}: {string.Join("、", this.targets.Select(t => $"{CraftPlanner.ItemName(t.Key)} 目標{t.Value}"))}");
        this.NextPhase("GBR の自動採集を開始します");
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.StopWhen?.Invoke() == true)
        {
            ctx.Log.Write("採集", $"{this.label}: 目的のものが手に入ったので、採集をやめます");
            return TaskResult.Done;
        }

        var remaining = this.targets.Where(t => GbrCount(t.Key) < t.Value).ToList();
        if (remaining.Count == 0)
        {
            ctx.Log.Write("採集", $"{this.label}: そろいました");
            return TaskResult.Done;
        }

        var on = ctx.GatherBuddy.IsAutoGatherEnabled();
        if (!this.enabledByMe)
        {
            // ON を頼むのは1回だけ（GBR は断るときその場で断り、毎回チャットに理由を出すため、繰り返さない）
            this.enabledByMe = true;
            if (!ctx.GatherBuddy.SetAutoGatherEnabled(true))
            {
                this.enabledByMe = ctx.GatherBuddy.IsAutoGatherEnabled() != false;
                this.Unfinished.AddRange(remaining.Select(r => r.Key));
                ctx.Log.Warn("採集", $"GBR の自動採集が ON になりません（{ctx.GatherBuddy.StatusText()}）。知覚不足・ギアセット無し・エサ無しなどが考えられます。別の入手手段にします");
                return TaskResult.Done;
            }

            this.NextPhase("GBR が採集中");
            return TaskResult.Running;
        }

        if (on == false)
        {
            // GBR が自分で止まった＝完了か失敗（区別できない）。所持数で判定する
            foreach (var (id, target) in remaining)
            {
                this.Unfinished.Add(id);
                ctx.Log.Warn("採集", $"{CraftPlanner.ItemName(id)} が目標に届かないまま GBR が止まりました（{GbrCount(id)}/{target}）");
            }

            this.enabledByMe = false;
            ctx.Log.Warn("採集", $"GBR が止まりました（{ctx.GatherBuddy.StatusText()}）。集めきれなかった品目は別の入手手段にします");
            return TaskResult.Done;
        }

        if (this.Elapsed > this.limit)
        {
            this.Unfinished.AddRange(remaining.Select(x => x.Key));
            ctx.Log.Warn("採集", $"{this.label}: {this.limit.TotalMinutes:0}分たっても集めきれませんでした");
            return TaskResult.Done;
        }

        var status = ctx.GatherBuddy.StatusText();
        this.Status = $"残り {remaining.Count} 品目（{string.Join("、", remaining.Take(3).Select(r => $"{CraftPlanner.ItemName(r.Key)} {GbrCount(r.Key)}/{r.Value}"))}）{(status.Length > 0 ? $"　GBR: {status}" : string.Empty)}";
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // こちらが ON にしたなら OFF を頼む（読めないときも頼む）
        if (this.enabledByMe && ctx.GatherBuddy.IsAutoGatherEnabled() != false)
            ctx.GatherBuddy.SetAutoGatherEnabled(false);
        this.enabledByMe = false;

        // 止まったことを確かめられたときだけリストと設定を戻す。戻せなければ控えを残し、
        // 止まっている間に Services が定期的に戻す
        if (!ctx.Gbr.RestoreIfIdle(ctx.GatherBuddy.IsAutoGatherEnabled(), ctx.Gbr.VendorIsBusy()))
            ctx.Log.Warn("採集", "GBR が止まったことを確かめられないので、リストと設定は後で戻します");
    }
}

/// <summary>NPC から買う1品目。</summary>
/// <param name="ItemId">アイテム。</param>
/// <param name="TargetOwned">
/// 持っていたい総数（計画と同じ数え方＝Inventory.Snapshot）。買う数は作業を始めるときに「総数 − その時点の所持数」で決める
/// （計画時の不足数を開始時の所持数に足すと、その間に手に入った分まで余計に買ってギルを使う）。
/// </param>
public sealed record VendorNeed(uint ItemId, int TargetOwned);

/// <summary>
/// GBR の購入機能で NPC から買う（NPC で買える素材・魚は買う。ギルの店だけ）。
///
/// 手順:
///  ・専用の購入リスト「AutoJobQuest」を作り直し、ギルの店で買える品目だけを入れる
///    （TrySetTarget はギルの店が無いと軍票・特殊通貨の店に落ちるので、事前判定＋事後確認で外す）。
///  ・Start → IsBusy が false になるまで待つ（GBR に全体の制限時間は無いので自前で持つ）。
///  ・成否は所持数（手持ち＋アーマリー、NQ＋HQ）で判定する。
/// 購入中は GBR が店の画面を操作するので、こちらは何もしない。
/// </summary>
public sealed class VendorTask : AutoTask
{
    private readonly List<VendorNeed> needs;
    private readonly Dictionary<uint, int> targets = [];
    private readonly Dictionary<uint, int> before = [];
    private long gilBefore;
    private Guid? listId;
    private bool started;
    private int retries;

    // 止めるよう頼んだ（GBR の Stop は中止待ちを立てるだけなので、IsBusy が false になるまで待つ：VendorBuyListManager.cs:468-481）
    private bool stopRequested;

    // 購入の状態が読めなくなった時刻（読めないまま続けば止める。読めない＝止まった、とはみなさない）
    private DateTime unknownSince = DateTime.MinValue;

    /// <summary>買えなかった品目（次の周回で別の手段にする）。</summary>
    public List<uint> Unfinished { get; } = [];

    public VendorTask(IEnumerable<VendorNeed> needs)
    {
        this.needs = needs.ToList();
    }

    public override string Name => "NPC 購入";

    public static unsafe int VendorCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        return Math.Max(0, im->GetInventoryItemCount(itemId, false, false, true))
               + Math.Max(0, im->GetInventoryItemCount(itemId, true, false, true));
    }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.needs.Count == 0)
            return TaskResult.Done;

        // 利用者が GBR の自動採集を使っている・読めないときは始めない（fail-closed）
        if (ctx.GatherBuddy.IsAutoGatherEnabled() != false)
            return this.Fail("GBR の自動採集が動いている（または状態が読めない）ので、NPC 購入を始めません");

        // 買う数は、いまの所持数から決める。GBR には購入の数え方（手持ち＋アーマリー、NQ＋HQ）での目標を渡す
        var inv = Inventory.Snapshot();
        foreach (var n in this.needs)
        {
            var add = n.TargetOwned - inv.CountAll(n.ItemId);
            if (add <= 0)
            {
                ctx.Log.Write("購入", $"{CraftPlanner.ItemName(n.ItemId)} はもう足りています（{inv.CountAll(n.ItemId)}/{n.TargetOwned}）");
                continue;
            }

            this.before[n.ItemId] = VendorCount(n.ItemId);
            this.targets[n.ItemId] = this.before[n.ItemId] + add;
        }

        if (this.targets.Count == 0)
            return TaskResult.Done;

        this.gilBefore = Inventory.Gil();

        // 帰宅テレポの抑止（購入の後に採集へ続くことが多いので、ここでも切っておく）
        ctx.Gbr.OverrideBool("GoHomeWhenDone", false);
        ctx.Gbr.OverrideBool("GoHomeWhenIdle", false);

        this.listId = ctx.Gbr.PrepareVendorList(this.targets.Select(t => (t.Key, (uint)t.Value)).ToList(), out var notGil);
        if (this.listId == null)
            return this.Fail(ctx.Gbr.LastError ?? "GBR の購入リストを用意できませんでした");

        foreach (var id in notGil.Distinct())
        {
            ctx.Log.Warn("購入", $"{CraftPlanner.ItemName(id)} はギルの店で自動購入できません（別の手段で集めます）");
            this.Unfinished.Add(id);
            this.targets.Remove(id);
        }

        if (this.targets.Count == 0)
            return TaskResult.Done;

        ctx.Log.Write("購入", string.Join("、", this.targets.Select(t => $"{CraftPlanner.ItemName(t.Key)} 目標{t.Value}")));
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (!this.started)
        {
            var r = ctx.Gbr.StartVendor(this.listId!.Value);
            switch (r)
            {
                case "Started":
                case "WaitingForPreviousInteraction":
                    this.started = true;
                    this.NextPhase("GBR が購入中");
                    return TaskResult.Running;

                case "NoPendingEntries":
                    return this.Finish(ctx);

                case "VendorDataLoading":
                case "LocationDataLoading":
                case "AlreadyRunning":
                case "AnotherPurchaseRunning":
                    this.Status = $"GBR の準備待ち（{r}）";
                    if (this.PhaseElapsed > TimeSpan.FromMinutes(2))
                        return this.Fail($"GBR の購入を始められませんでした（{r}）");
                    return TaskResult.Running;

                case "AutomationUnavailable":
                    this.Unfinished.AddRange(this.targets.Keys);
                    ctx.Log.Warn("購入", "GBR の NPC 購入が使えません（Allagan Tools か Allagan Item Search が必要）");
                    return TaskResult.Done;

                default:
                    if (this.retries++ < 3)
                        return TaskResult.Running;
                    return this.Fail($"GBR の購入を始められませんでした（{r ?? ctx.Gbr.LastError}）");
            }
        }

        var busy = ctx.Gbr.VendorIsBusy();

        // 読めない＝止まった、とはみなさない。読めないまま 30 秒続けば止める（購入が続いているか分からないまま次へ進まない）
        if (busy == null)
        {
            if (this.unknownSince == DateTime.MinValue)
                this.unknownSince = DateTime.UtcNow;
            this.Status = "GBR の購入の状態が読めません（読めるようになるのを待っています）";
            return DateTime.UtcNow - this.unknownSince > TimeSpan.FromSeconds(30)
                ? this.Fail("GBR の NPC 購入の状態を 30 秒読めません（購入が続いているか分からないので止めます。GBR の版が変わった可能性）")
                : TaskResult.Running;
        }

        this.unknownSince = DateTime.MinValue;

        if (busy == true)
        {
            if (!this.stopRequested && this.Elapsed > TimeSpan.FromMinutes(25))
            {
                ctx.Gbr.StopVendor();
                this.stopRequested = true;
                ctx.Log.Warn("購入", "25分たっても購入が終わらないので止めるよう頼みました（止まったのを確かめてから次へ進みます）");
                this.NextPhase("GBR の購入が止まるのを待っています");
            }

            if (this.stopRequested && this.PhaseElapsed > TimeSpan.FromMinutes(1))
                return this.Fail("GBR の NPC 購入を止めるよう頼みましたが、1分たっても止まりません");

            this.Status = this.stopRequested ? "GBR の購入が止まるのを待っています" : $"GBR: {ctx.Gbr.VendorStatusText()}";
            return TaskResult.Running;
        }

        // busy == false：止まったことを確かめられた
        return this.Finish(ctx);
    }

    private TaskResult Finish(TaskContext ctx)
    {
        var gained = new List<string>();
        foreach (var (id, target) in this.targets)
        {
            var now = VendorCount(id);
            if (now > this.before.GetValueOrDefault(id))
                gained.Add($"{CraftPlanner.ItemName(id)} {this.before.GetValueOrDefault(id)}→{now}");
            if (now < target)
            {
                this.Unfinished.Add(id);
                ctx.Log.Warn("購入", $"{CraftPlanner.ItemName(id)} を買いきれませんでした（{now}/{target}）");
            }
        }

        // 減った AND 増えた：品が増えたのにギルが減っていなければ、購入ではない増え方（取り出し等）なので記録に残す
        var gil = Inventory.Gil();
        if (gained.Count > 0 && gil >= this.gilBefore)
            ctx.Log.Warn("購入", $"品は増えましたがギルが減っていません（{this.gilBefore:N0}→{gil:N0}。購入ではない増え方の可能性）：{string.Join("、", gained)}");
        else if (gained.Count > 0)
            ctx.Log.Write("購入", $"NPC から買いました（ギル {this.gilBefore:N0}→{gil:N0}）：{string.Join("、", gained)}");

        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.started && ctx.Gbr.VendorIsBusy() == true)
            ctx.Gbr.StopVendor();

        // 購入が止まっていれば、リストの品目を消し、設定を戻す（動いていれば後で戻す）
        if (ctx.Gbr.VendorIsBusy() == false)
            ctx.Gbr.ClearVendorList();
        if (!ctx.Gbr.RestoreIfIdle(ctx.GatherBuddy.IsAutoGatherEnabled(), ctx.Gbr.VendorIsBusy()))
            ctx.Log.Warn("購入", "GBR が止まったことを確かめられないので、設定は後で戻します");
    }
}
