using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>開始時に1回だけ呼び鈴を開き、全員の実在庫を読み、必要分を引き出す。</summary>
public sealed unsafe class RetainerStockTask(Func<JobQuestPlan> makePlan) : AutoTask
{
    private enum Phase { Bell, List, Menu, Inventory, CloseInventory, Quit, Context, Numeric, Verify, CloseBell }
    private Phase phase;
    private DateTime changed = DateTime.UtcNow;
    private DateTime opened = DateTime.MinValue;
    private AutoTask? travel;
    private bool visitedInn;
    private bool withdrawing;
    private readonly List<ulong> retainers = [];
    private int index;
    private readonly RetainerPlan.Stock total = new();
    private RetainerPlan.Stock? needed;
    private RetainerSlot? pending;
    private int amount;
    private int beforeBag;
    private int beforeSource;
    private readonly Ipc.RetainerControl control = new();
    private HashSet<string> bellNames = [];
    private DateTime lastBellTry;
    private static readonly InventoryType[] Pages = [InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5,
        InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals];
    public override string Name => "開始時のリテイナー在庫確認・引き出し";
    private sealed record RetainerSlot(InventoryType Container, int Slot, uint Item, bool Hq, int Count);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (!ctx.Ownership.Registered || !GameUi.PlayerFree())
            return this.Fail("画面を閉じ、自由に動ける状態で開始してください");
        ctx.YesAlready.Suppress();
        if (!this.control.Take(ctx.Config))
            return this.Fail("AutoRetainerが動作中、または抑制状態を確認できません");
        if (!ctx.TextAdvance.EnsureTurnInControl())
            return this.Fail("呼び鈴の会話を操作するTextAdvanceの操作権を取得できません");
        var ids = Svc.Data.GetExcelSheet<EObjName>(ClientLanguage.Japanese)
            .Where(x => x.Singular.ExtractText() is "呼び鈴" or "リテイナーベル").Select(x => x.RowId).ToHashSet();
        this.bellNames = Svc.Data.GetExcelSheet<EObjName>().Where(x => ids.Contains(x.RowId)).Select(x => x.Singular.ExtractText()).ToHashSet();
        return TaskResult.Running;
    }

    private void Next(Phase value) { this.phase = value; this.changed = DateTime.UtcNow; }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.Elapsed > TimeSpan.FromMinutes(20)) return this.Fail("リテイナー処理が20分以内に完了しませんでした");
        if (this.phase != Phase.Bell && DateTime.UtcNow - this.changed > TimeSpan.FromSeconds(30))
            return this.Fail($"リテイナーの状態を確認できません（{this.phase}）。不明な引き出しは再送しません");
        if (!this.control.Keep()) return this.Fail("AutoRetainerとの競合を検出しました");
        if (!ctx.TextAdvance.EnsureTurnInControl()) return this.Fail("呼び鈴の会話の操作権が失われました");
        if (this.phase is Phase.Inventory or Phase.Context or Phase.Numeric or Phase.Verify or Phase.CloseInventory or Phase.Quit)
        {
            var manager = RetainerManager.Instance();
            if (manager == null || this.index >= this.retainers.Count || manager->LastSelectedRetainerId != this.retainers[this.index])
                return this.Fail("操作中のリテイナーが変わったため止めました");
        }
        this.Status = $"{(this.withdrawing ? "必要分の引き出し" : "在庫の読出し")} {this.index + 1}/{this.retainers.Count}：{this.phase}";
        if (this.travel != null)
        {
            var r = this.travel.Step(ctx);
            this.Status = this.travel.Status;
            if (r == TaskResult.Running) return r;
            var error = this.travel.FailReason;
            this.travel.Cleanup(ctx); this.travel = null;
            if (r == TaskResult.Failed) return this.Fail(error ?? "呼び鈴へ移動できません");
        }
        switch (this.phase)
        {
            case Phase.Bell:
            {
                // 名前はクライアント言語のシートから求める。ゲームオブジェクトIDは固定しない。
                var bell = Svc.Objects.Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject
                    && x.IsTargetable && this.bellNames.Contains(x.Name.TextValue)).OrderBy(x => Vector3.Distance(x.Position, Me.Position)).FirstOrDefault();
                if (bell == null)
                {
                    if (this.visitedInn) return this.Fail("宿屋の呼び鈴を見つけられません");
                    this.visitedInn = true; this.travel = new GoToInnTask(); return TaskResult.Running;
                }
                if (Vector3.Distance(bell.Position, Me.Position) > 4f)
                { this.travel = new MoveToTask(bell.Position, 4f, "呼び鈴"); return TaskResult.Running; }
                if (GameUi.IsReady("RetainerList", out _)) return this.Fail("開始前から呼び鈴が開いています。閉じて開始してください");
                if (DateTime.UtcNow - this.lastBellTry < TimeSpan.FromSeconds(1)) return TaskResult.Running;
                this.lastBellTry = DateTime.UtcNow;
                this.opened = DateTime.UtcNow;
                if (GameUi.Interact(bell, checkLineOfSight: true))
                { ctx.InOwnConversation = true; this.Next(Phase.List); }
                return TaskResult.Running;
            }
            case Phase.List:
            {
                if (!ctx.Ownership.TryGetOwnedSince("RetainerList", this.opened, out var list, out _)) return TaskResult.Running;
                var manager = RetainerManager.Instance();
                if (manager == null || !manager->IsReady) return TaskResult.Running;
                if (this.retainers.Count == 0)
                    for (uint i = 0; i < manager->Retainers.Length; i++)
                    {
                        var retainer = manager->GetRetainerBySortedIndex(i);
                        if (retainer != null && retainer->RetainerId != 0 && retainer->Available) this.retainers.Add(retainer->RetainerId);
                    }
                if (this.index >= this.retainers.Count)
                {
                    if (!this.withdrawing)
                    {
                        var bags = Inventory.Snapshot();
                        this.needed = RetainerPlan.Build(ctx.Data.Planner!, this.Targets(ctx, bags), bags, this.total);
                        this.withdrawing = true; this.index = 0;
                        ctx.Log.Write("リテイナー", $"実在庫 {this.total.Counts.Count} 種類を確認。必要な引き出しは {this.needed.Counts.Count} 種類です");
                        if (this.retainers.Count > 0 && this.needed.Counts.Count > 0) return TaskResult.Running;
                    }
                    if (this.needed?.Counts.Values.Any(n => n > 0) == true) return this.Fail("確認したリテイナー在庫と引出結果が一致しません");
                    GameUi.Fire(list, true, -1); this.Next(Phase.CloseBell); return TaskResult.Running;
                }
                for (uint i = 0; i < manager->Retainers.Length; i++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(i);
                    if (retainer != null && retainer->RetainerId == this.retainers[this.index] && retainer->Available)
                    {
                        // ECommons/ArtisanのRetainerListと同じイベント。後続引数は型未設定。
                        var args = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[4];
                        for (var n = 0; n < 4; n++) args[n] = default;
                        args[0].SetInt(2); args[1].SetUInt(i); list->FireCallback(4, args, true);
                        this.Next(Phase.Menu); return TaskResult.Running;
                    }
                }
                return this.Fail("対象リテイナーを選べません");
            }
            case Phase.Menu:
                if (RetainerManager.Instance()->LastSelectedRetainerId != this.retainers[this.index]) return TaskResult.Running;
                if (SelectMenu(ctx, 2378)) this.Next(Phase.Inventory);
                return TaskResult.Running;
            case Phase.Inventory:
            {
                if (!AgentRetainer.Instance()->IsAgentActive()) return TaskResult.Running;
                var slots = ReadSlots();
                if (slots == null) return TaskResult.Running;
                if (!this.withdrawing)
                {
                    foreach (var s in slots) this.total.Counts[(s.Item, s.Hq)] = this.total.Counts.GetValueOrDefault((s.Item, s.Hq)) + s.Count;
                    this.Next(Phase.CloseInventory); return TaskResult.Running;
                }
                this.pending = slots.FirstOrDefault(s => this.needed!.Counts.GetValueOrDefault((s.Item, s.Hq)) > 0);
                if (this.pending is not { } p) { this.Next(Phase.CloseInventory); return TaskResult.Running; }
                this.amount = Math.Min(p.Count, this.needed!.Counts[(p.Item, p.Hq)]);
                this.beforeSource = p.Count; this.beforeBag = BagCount(p);
                if (GameUi.IsReady("ContextMenu", out _) || GameUi.IsReady("InputNumeric", out _)) return this.Fail("別の品の操作窓が開いています");
                this.Next(Phase.Context);
                AgentInventoryContext.Instance()->OpenForItemSlot(p.Container, p.Slot, 0, AgentRetainer.Instance()->GetAddonId()); return TaskResult.Running;
            }
            case Phase.Context:
            {
                if (!this.SourceUnchanged()) return this.Fail("引き出す前にリテイナーの品が変わりました");
                if (!ctx.Ownership.TryGetOwnedSince("ContextMenu", this.changed, out var menu, out _)) return TaskResult.Running;
                var p = this.pending!;
                var label = Svc.Data.GetExcelSheet<Addon>().GetRow(p.Count == 1 || p.Container == InventoryType.RetainerCrystals ? 98u : 773u).Text.ExtractText();
                var context = AgentInventoryContext.Instance();
                var labels = new List<string>();
                foreach (var value in context->EventParams)
                    if (value.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String)
                        labels.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
                var idx = QuestMenuChoice.Unique(labels, label);
                if (idx < 0 || context->IsContextItemDisabled(idx)) return this.Fail("有効な引き出しの選択肢を一意に読めません");
                this.Next(p.Count == 1 ? Phase.Verify : Phase.Numeric);
                GameUi.Fire(menu, true, 0, idx, 0, 0, 0); return TaskResult.Running;
            }
            case Phase.Numeric:
                if (!this.SourceUnchanged()) return this.Fail("数量を送る前にリテイナーの品が変わりました");
                if (ctx.Ownership.TryGetOwnedSince("InputNumeric", this.changed, out var numeric, out _))
                { GameUi.Fire(numeric, true, this.amount); this.Next(Phase.Verify); }
                return TaskResult.Running;
            case Phase.Verify:
            {
                var p = this.pending!;
                var slots = ReadSlots();
                if (slots == null) return TaskResult.Running;
                var now = slots.FirstOrDefault(x => x.Container == p.Container && x.Slot == p.Slot);
                var sourceCount = now == null ? 0 : now.Item == p.Item && now.Hq == p.Hq ? now.Count : -1;
                if (!TransferConfirmed(this.beforeBag, BagCount(p), this.beforeSource, sourceCount, this.amount)) return TaskResult.Running;
                this.needed!.Counts[(p.Item, p.Hq)] -= this.amount;
                ctx.Log.Write("リテイナー", $"{CraftPlanner.ItemName(p.Item)}{(p.Hq ? " HQ" : "")} ×{this.amount} の移動を両側の在庫で確認しました");
                this.Next(Phase.Inventory); return TaskResult.Running;
            }
            case Phase.CloseInventory:
                AgentRetainer.Instance()->Hide(); this.Next(Phase.Quit); return TaskResult.Running;
            case Phase.Quit:
                if (AgentRetainer.Instance()->IsAgentActive()) return TaskResult.Running;
                if (SelectMenu(ctx, 2383)) { this.index++; this.Next(Phase.List); }
                return TaskResult.Running;
            case Phase.CloseBell:
                if (GameUi.IsReady("RetainerList", out _) || !GameUi.PlayerFree()) return TaskResult.Running;
                return TaskResult.Done;
        }
        return TaskResult.Running;
    }

    private static bool SelectMenu(TaskContext ctx, uint addonText)
    {
        var entries = GameUi.MenuEntries(out var menu);
        if (entries == null || !ctx.Ownership.TryGetOwned("SelectString", out var owned) || owned != menu) return false;
        var i = QuestMenuChoice.Unique(entries, Svc.Data.GetExcelSheet<Addon>().GetRow(addonText).Text.ExtractText());
        if (i < 0) return false;
        GameUi.Fire(menu, true, i); return true;
    }

    private static List<RetainerSlot>? ReadSlots()
    {
        var result = new List<RetainerSlot>();
        var im = InventoryManager.Instance();
        if (im == null) return null;
        foreach (var page in Pages)
        {
            var c = im->GetInventoryContainer(page);
            if (c == null || !c->IsLoaded) return null;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && s->Quantity > 0 && !s->IsSymbolic && !s->IsCollectable())
                    result.Add(new(page, i, s->ItemId, (s->Flags & InventoryItem.ItemFlags.HighQuality) != 0, s->Quantity));
            }
        }
        return result;
    }

    private static int BagCount(RetainerSlot slot)
    {
        var inv = Inventory.Snapshot();
        return slot.Hq ? inv.CountHq(slot.Item) : inv.CountNq(slot.Item);
    }

    private List<QuestItemReq> Targets(TaskContext ctx, Inventory bags)
    {
        var plan = makePlan();
        var targets = new List<QuestItemReq>(plan.RetainerTargets);
        foreach (var materia in plan.Materia.Where(m => !m.AlreadyMelded))
        {
            var id = materia.MateriaItemId ?? MateriaCatalog.ResolveAny(ctx.Config.AnyMateriaItemId, materia.TargetItemId, out _);
            if (id is { } item) targets.Add(new(item, 1, false, "装着するマテリア"));
        }
        // 引き出せる完成品・中間素材を差し引いた後でも必要な秘伝書だけを見る。
        var combined = new RetainerPlan.Combined(bags, this.total);
        var craft = ctx.Data.Planner!.Build(targets, combined, PlanBuilder.IsBookUnlocked);
        var tomes = craft.LockedBySecretBook.Select(c => c.SecretRecipeBookId).ToHashSet();
        if (tomes.Count == 0) return targets;
        var books = ctx.Data.Books ?? throw new InvalidOperationException("秘伝書の計画を読めません");
        if (books.CollectableItemId != ctx.Config.ScripCollectableItemId)
            throw new InvalidOperationException("収集品の設定が変わりました。ゲームデータを再読込してください");
        var offers = books.Offers.Values.Where(o => tomes.Contains(o.TomeId)).ToList();
        if (offers.Select(o => o.TomeId).Distinct().Count() != tomes.Count)
            throw new InvalidOperationException("必要な秘伝書の価格を確認できません");
        foreach (var offer in offers) targets.Add(new(offer.BookItemId, 1, false, "必要な秘伝書"));
        var price = offers.Where(o => combined.CountNq(o.BookItemId) + combined.CountHq(o.BookItemId) == 0).Sum(o => (int)o.Price);
        var count = BookMath.CollectablesNeeded(price, Inventory.CountSpecialCurrency(books.RewardSpecialCurrencyId, out _), books.RewardLow);
        count = Math.Max(0, count - Inventory.CountCollectables(books.CollectableItemId, books.MinCollectability));
        if (count > 0) targets.Add(new(books.CollectableItemId, count, false, "秘伝書交換用の収集品の材料"));
        return targets;
    }

    public static bool TransferConfirmed(int bagBefore, int bagNow, int sourceBefore, int sourceNow, int amount)
        => amount > 0 && sourceNow >= 0 && bagNow - bagBefore == amount && sourceBefore - sourceNow == amount;

    private bool SourceUnchanged()
    {
        var p = this.pending!;
        var context = AgentInventoryContext.Instance();
        if (context == null || context->TargetInventoryId != p.Container || context->TargetInventorySlotId != p.Slot
            || context->OwnerAddonId != AgentRetainer.Instance()->GetAddonId()) return false;
        var now = ReadSlots()?.FirstOrDefault(x => x.Container == p.Container && x.Slot == p.Slot);
        return now != null && now.Item == p.Item && now.Hq == p.Hq && now.Count == this.beforeSource && BagCount(p) == this.beforeBag;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.travel?.Cleanup(ctx); this.travel = null;
        if (this.opened != DateTime.MinValue)
        {
            foreach (var name in new[] { "InputNumeric", "ContextMenu", "InventoryRetainer", "InventoryRetainerLarge", "SelectString", "Talk", "RetainerList" })
                if (ctx.Ownership.TryGetOwnedSince(name, this.opened, out var addon, out _)) addon->Close(true);
        }
        ctx.InOwnConversation = false;
        this.control.Release(ctx.Config);
        ctx.TextAdvance.ReleaseControl();
        ctx.YesAlready.Release();
    }
}
