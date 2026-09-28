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
/// <param name="Shortfall">あと何個要るか。</param>
public sealed record GatherNeed(uint ItemId, int Shortfall);

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
    private bool prepared;

    /// <summary>集めきれなかった品目（呼び出し側が次の手段を選ぶのに使う）。</summary>
    public List<uint> Unfinished { get; } = [];

    public GatherTask(IEnumerable<GatherNeed> needs, uint? preferredTerritory, string label, TimeSpan limit)
    {
        this.needs = needs.Where(x => x.Shortfall > 0).ToList();
        this.preferredTerritory = preferredTerritory;
        this.label = label;
        this.limit = limit;
    }

    public override string Name => $"採集: {this.label}";

    public static unsafe int GbrCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId, false, false, false, 0);
    }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.needs.Count == 0)
            return TaskResult.Done;

        if (!ctx.GatherBuddy.IsLoaded)
            return this.Fail("GatherBuddyReborn が読み込まれていません");

        if (ctx.GatherBuddy.IsAutoGatherEnabled() == true)
            return this.Fail("GBR の自動採集がすでに動いています（利用者の操作を横取りしないため止めました）");

        foreach (var n in this.needs)
            this.targets[n.ItemId] = (uint)(GbrCount(n.ItemId) + n.Shortfall);

        // 終わったとき・待機のときに勝手に帰宅（テレポ）しないように
        if (!ctx.Gbr.OverrideBool("GoHomeWhenDone", false) || !ctx.Gbr.OverrideBool("GoHomeWhenIdle", false))
            ctx.Log.Warn("採集", $"GBR の帰宅設定を一時的に切れませんでした: {ctx.Gbr.LastError}");

        var entries = this.targets.Select(t => new GbrGatherEntry(t.Key, t.Value, this.preferredTerritory)).ToList();
        if (!ctx.Gbr.PrepareGatherList(entries, out var unsupported))
            return this.Fail(ctx.Gbr.LastError ?? "GBR のリストを用意できませんでした");

        this.prepared = true;
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
        var remaining = this.targets.Where(t => GbrCount(t.Key) < t.Value).ToList();
        if (remaining.Count == 0)
        {
            ctx.Log.Write("採集", $"{this.label}: そろいました");
            return TaskResult.Done;
        }

        var on = ctx.GatherBuddy.IsAutoGatherEnabled();
        if (!this.enabledByMe)
        {
            if (!ctx.GatherBuddy.SetAutoGatherEnabled(true))
            {
                if (this.PhaseElapsed > TimeSpan.FromSeconds(10))
                    return this.Fail($"GBR の自動採集が ON になりません（{ctx.GatherBuddy.StatusText()}）。知覚不足・ギアセット無しなどが考えられます");
                return TaskResult.Running;
            }

            this.enabledByMe = true;
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
            return this.Unfinished.Count == this.needs.Count
                ? this.Fail($"GBR が止まりました: {ctx.GatherBuddy.StatusText()}")
                : TaskResult.Done;
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
        if (this.enabledByMe && ctx.GatherBuddy.IsAutoGatherEnabled() == true)
            ctx.GatherBuddy.SetAutoGatherEnabled(false);
        this.enabledByMe = false;

        if (this.prepared)
            ctx.Gbr.RestoreGatherLists();
        ctx.Gbr.RestoreConfig();
    }
}

/// <summary>NPC から買う1品目。</summary>
public sealed record VendorNeed(uint ItemId, int Shortfall);

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
    private Guid? listId;
    private bool started;
    private int retries;

    /// <summary>買えなかった品目（次の周回で別の手段にする）。</summary>
    public List<uint> Unfinished { get; } = [];

    public VendorTask(IEnumerable<VendorNeed> needs)
    {
        this.needs = needs.Where(x => x.Shortfall > 0).ToList();
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

        foreach (var n in this.needs)
            this.targets[n.ItemId] = VendorCount(n.ItemId) + n.Shortfall;

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
        if (busy == true)
        {
            if (this.Elapsed > TimeSpan.FromMinutes(25))
            {
                ctx.Gbr.StopVendor();
                ctx.Log.Warn("購入", "25分たっても購入が終わらないので止めました");
                return this.Finish(ctx);
            }

            this.Status = $"GBR: {ctx.Gbr.VendorStatusText()}";
            return TaskResult.Running;
        }

        return this.Finish(ctx);
    }

    private TaskResult Finish(TaskContext ctx)
    {
        foreach (var (id, target) in this.targets)
        {
            var now = VendorCount(id);
            if (now < target)
            {
                this.Unfinished.Add(id);
                ctx.Log.Warn("購入", $"{CraftPlanner.ItemName(id)} を買いきれませんでした（{now}/{target}）");
            }
        }

        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.started && ctx.Gbr.VendorIsBusy() == true)
            ctx.Gbr.StopVendor();
        ctx.Gbr.RestoreConfig();
    }
}
