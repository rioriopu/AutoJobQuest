using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using AutoJobQuest.Planning;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// グリダニアの宿屋へ入る（素材がそろったらグリダニアの宿屋で製作する）。
///
/// Lifestream の EnqueueLocalInnShortcut(2)。2 はグリダニア（LifestreamIpc の説明を参照）。
/// 着いたかは「いまのエリアが宿屋の個室（TerritoryIntendedUse=2）で、グリダニアと同じ地域」で確かめる
/// （177/178/179 の宿屋はどれも TerritoryIntendedUse=2。ゲームデータで実測）。
/// Lifestream は忙しいと何もせずに戻る（IPC は void）ので、呼んだあと IsBusy が立ったかで受け付けを確かめる。
/// </summary>
public sealed class GoToInnTask : AutoTask
{
    /// <summary>宿屋の個室を表す TerritoryIntendedUse の値（177/178/179 で実測）。</summary>
    private const uint InnIntendedUse = 2;

    /// <summary>グリダニア：新市街（Lifestream の宿屋番号 2 の街）。宿屋が同じ地域かの判定に使う。</summary>
    private const uint NewGridania = 132;

    private bool requested;
    private bool sawBusy;
    private int attempts;

    public override string Name => "グリダニアの宿屋へ";

    public static bool InGridaniaInn()
    {
        var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
        if (!sheet.TryGetRow(Me.Territory, out var here) || !sheet.TryGetRow(NewGridania, out var city))
            return false;
        return here.TerritoryIntendedUse.RowId == InnIntendedUse && here.PlaceNameZone.RowId == city.PlaceNameZone.RowId;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (InGridaniaInn() && GameUi.PlayerFree())
            return TaskResult.Done;

        if (this.Elapsed > TimeSpan.FromMinutes(4))
            return this.Fail("4分以内にグリダニアの宿屋へ入れませんでした");

        if (GameUi.IsShopOrMarketOpen())
        {
            this.Status = "ショップ等の画面が開いているので待っています";
            return TaskResult.Running;
        }

        var busy = ctx.Lifestream.IsBusy();
        if (this.requested)
        {
            if (busy == true || GameUi.BetweenAreas)
            {
                this.sawBusy = true;
                this.Status = "Lifestream が宿屋へ向かっています";
                return TaskResult.Running;
            }

            // 受け付けられなかった（IsBusy が一度も立たない）か、終わったのに宿屋でない
            if (!this.sawBusy && this.PhaseElapsed < TimeSpan.FromSeconds(5))
                return TaskResult.Running;

            this.requested = false;
        }

        if (!GameUi.PlayerFree() || GameUi.InCombat)
        {
            this.Status = "動ける状態になるのを待っています";
            return TaskResult.Running;
        }

        if (this.attempts++ >= 3)
            return this.Fail("Lifestream で宿屋へ入れませんでした（宿屋が未解放の可能性）");

        if (!ctx.Lifestream.EnqueueLocalInn(LifestreamIpc.GridaniaInnIndex))
            return this.Fail("Lifestream に宿屋への移動を頼めませんでした");

        this.requested = true;
        this.sawBusy = false;
        this.NextPhase("Lifestream に宿屋への移動を頼みました");
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // こちらが頼んだ宿屋への移動がまだ動いていれば止める（止めたのに宿屋へ向かい続けないように）
        if (this.requested && !InGridaniaInn() && ctx.Lifestream.IsBusy() == true)
            ctx.Lifestream.Abort();
    }
}

/// <summary>
/// 計画どおりに Artisan で作る（1レシピずつ CraftItem）。
///
///  ・作る前に秘伝書を確かめる（Artisan の CraftItem は確かめずに進まなくなる）。
///  ・作る前後で所持数を比べ、「材料が減った AND 完成品が増えた」で成功とみなす。
///  ・HQ 指定の品は HQ の数を数える。NQ しかできなければ記録し、次の周回で作り直す。
///  ・職の持ち替えは Artisan が自分で行う（ギアセットが必要）。
/// </summary>
public sealed class CraftOneTask : AutoTask
{
    private readonly PlannedCraft craft;
    private int beforeAll;
    private int beforeHq;
    private int expected;
    private bool requested;
    private bool sawBusy;
    private bool collectable;

    // 作る前の材料の所持数（「材料が減った」を確かめるため）
    private readonly Dictionary<uint, int> ingredientsBefore = [];

    public CraftOneTask(PlannedCraft craft)
    {
        this.craft = craft;
    }

    public override string Name => $"製作: {CraftPlanner.ItemName(this.craft.ItemId)} ×{this.craft.Crafts}回";

    /// <summary>作れた数（完成品の増えた数）。</summary>
    public int Made { get; private set; }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.craft.SecretRecipeBookId != 0 && !PlanBuilder.IsBookUnlocked(this.craft.SecretRecipeBookId))
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} の秘伝書が未読です");

        // 収集品（シーダーロングボウなど）は通常の数え方では 0 になるので、カバンの枠を直接数える
        this.collectable = Svc.Data.GetExcelSheet<Item>().TryGetRow(this.craft.ItemId, out var itemRow) && itemRow.AlwaysCollectable;

        var inv = Inventory.Snapshot();
        this.beforeAll = this.CountMade(inv);
        this.beforeHq = inv.CountHq(this.craft.ItemId);
        this.expected = this.craft.Crafts * this.craft.Yield;

        // 材料が足りるかを先に確かめる（足りないまま頼むと Artisan は材料切れで止まるだけ）
        var recipe = Svc.Data.GetExcelSheet<Recipe>().GetRow(this.craft.RecipeId);
        foreach (var (ing, amount) in CraftPlanner.Ingredients(recipe))
        {
            var have = inv.CountAll(ing);
            this.ingredientsBefore[ing] = have;
            if (have < amount * this.craft.Crafts)
                ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(ing)} が {have}/{amount * this.craft.Crafts} しかありません（作れる分だけ作ります）");
        }

        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        var busy = ctx.Artisan.IsBusy();

        if (!this.requested)
        {
            if (busy != false)
            {
                // 読めない（null）ときも処理中とみなして待つ
                this.Status = "Artisan が空くのを待っています";
                if (this.PhaseElapsed > TimeSpan.FromMinutes(1))
                    return this.Fail("Artisan が他の処理をしていて空きません");
                return TaskResult.Running;
            }

            if (!ctx.Artisan.CraftItem((ushort)this.craft.RecipeId, this.craft.Crafts))
                return this.Fail($"Artisan に製作を頼めませんでした: {string.Join(" / ", ctx.Artisan.LastErrors.Values)}");

            this.requested = true;
            this.NextPhase("Artisan が製作中");
            return TaskResult.Running;
        }

        if (busy == true)
        {
            this.sawBusy = true;
            var made = this.CountMade(Inventory.Snapshot()) - this.beforeAll;
            this.Status = $"Artisan が製作中（{Math.Max(0, made)}/{this.expected}個）";

            var limit = TimeSpan.FromSeconds(60 + 90 * this.craft.Crafts);
            if (this.PhaseElapsed > limit)
            {
                ctx.Artisan.SetEndurance(false);
                return this.Fail($"{limit.TotalMinutes:0}分たっても製作が終わりません");
            }

            return TaskResult.Running;
        }

        // 頼んだ直後は Endurance がまだ OFF（レシピ選択の後で ON になる）。IsBusy が立つか、品が増えるまで待つ。
        // 60 秒は「動き出さなかった」と判断する上限（15 秒では開始の遅い環境で別の理由の失敗になる）
        if (!this.sawBusy && this.CountMade(Inventory.Snapshot()) <= this.beforeAll && this.PhaseElapsed < TimeSpan.FromSeconds(60))
            return TaskResult.Running;

        var inv = Inventory.Snapshot();
        this.Made = this.CountMade(inv) - this.beforeAll;
        var madeHq = inv.CountHq(this.craft.ItemId) - this.beforeHq;

        // 減った AND 増えた（完成品の数だけでは、手持ちの移動などを製作と取り違える）
        var used = this.ingredientsBefore.Where(kv => inv.CountAll(kv.Key) < kv.Value)
            .Select(kv => $"{CraftPlanner.ItemName(kv.Key)} {kv.Value}→{inv.CountAll(kv.Key)}").ToList();

        if (this.Made <= 0)
        {
            return this.Fail(used.Count > 0
                ? $"{CraftPlanner.ItemName(this.craft.ItemId)} が1つも増えませんでしたが、材料は減っています（製作の失敗の可能性：{string.Join("、", used)}）"
                : $"{CraftPlanner.ItemName(this.craft.ItemId)} が1つも増えませんでした（材料不足か、Artisan が止まった可能性）");
        }

        if (used.Count == 0 && this.ingredientsBefore.Count > 0)
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} は {this.Made} 個増えましたが、材料が減っていません（製作ではない増え方。想定外なので止めます）");

        if (this.Made < this.expected)
            ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} は {this.Made}/{this.expected} 個でした");

        if (this.craft.WantHq)
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}: {this.Made}個（うち HQ {madeHq}個）");
        else
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}: {this.Made}個");

        return TaskResult.Done;
    }

    private int CountMade(Inventory inv)
        => this.collectable ? Inventory.CountCollectables(this.craft.ItemId, 0) : inv.CountAll(this.craft.ItemId);

    public override void Cleanup(TaskContext ctx)
    {
        // こちらが頼んだ製作がまだ動いていれば止める（Endurance を OFF）
        if (!this.requested || ctx.Artisan.IsBusy() == false)
            return;

        if (ctx.Artisan.IsEndurance() == true)
            ctx.Artisan.SetEndurance(false);

        // Artisan の CraftItem は「レシピ選択 → Endurance を ON」を内部の順番待ちに積むので、
        // 止めた直後に遅れて Endurance が ON になり、製作が進むことがある。
        // Artisan が空く（IsBusy が false）まで見張り、その間に ON になったら OFF にする。
        var artisan = ctx.Artisan;
        ctx.AfterStop.Add(("Artisan の製作を止め切る", DateTime.UtcNow.AddSeconds(30), () =>
        {
            if (artisan.IsEndurance() == true)
                artisan.SetEndurance(false);
            return artisan.IsBusy() == false;
        }));
    }
}
