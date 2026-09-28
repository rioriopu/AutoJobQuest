using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// ジョブクエ以外のクエストを Questionable で完了させる（秘伝書の流れの「職人の新たなお仕事」用）。
/// </summary>
public sealed class RunQuestTask : AutoTask
{
    private readonly uint questRowId;
    private readonly string label;
    private bool started;
    private int restarts;
    private DateTime notRunningSince = DateTime.MinValue;

    public RunQuestTask(uint questRowId, string label)
    {
        this.questRowId = questRowId;
        this.label = label;
    }

    public override string Name => $"クエスト: {this.label}";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (QuestManager.IsQuestComplete(this.questRowId))
            return TaskResult.Done;

        if (this.Elapsed > TimeSpan.FromMinutes(30))
            return this.Fail("30分たってもクエストが完了しません");

        if (!this.started)
        {
            if (ctx.Questionable.IsRunning() == true)
                return this.Fail("Questionable がすでに動いています");
            if (!GameUi.PlayerFree())
                return TaskResult.Running;
            if (!ctx.Questionable.StartSingleQuest(this.questRowId))
                return this.Fail($"Questionable が「{this.label}」を始められませんでした（前提クエスト未完了・経路データ無しなど）");
            this.started = true;
            return TaskResult.Running;
        }

        if (ctx.Questionable.IsRunning() == false)
        {
            if (this.notRunningSince == DateTime.MinValue)
                this.notRunningSince = DateTime.UtcNow;
            if (DateTime.UtcNow - this.notRunningSince > TimeSpan.FromSeconds(8))
            {
                if (this.restarts++ >= 3)
                    return this.Fail("Questionable が途中で止まりました");
                this.started = false;
                this.notRunningSince = DateTime.MinValue;
            }
        }
        else
        {
            this.notRunningSince = DateTime.MinValue;
            this.Status = ctx.Questionable.GetCurrentStepData() is { } sd ? $"Questionable: {sd.InteractionType}" : "Questionable が進めています";
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.started && ctx.Questionable.IsRunning() == true
            && ctx.Questionable.GetCurrentQuestId() == QuestionableIpc.ToQuestId(this.questRowId))
            ctx.Questionable.Stop(Plugin.InternalNameConst);
    }
}

/// <summary>
/// NPC のところへ行って話しかけ、目的の画面が開くまで進める。
/// 会話の選択肢（SelectString / SelectIconString）が出たら、渡された候補の文言で選ぶ
/// （空白を除いて完全一致を優先、無ければ部分一致。ちょうど1件のときだけ選ぶ）。
/// </summary>
public sealed class TalkToNpcTask : AutoTask
{
    private readonly NpcSpot spot;
    private readonly Func<bool> opened;
    private readonly string label;
    private readonly List<string> menuHints;
    private AutoTask? sub;
    private int phase;
    private DateTime lastInteract = DateTime.MinValue;
    private DateTime lastMenu = DateTime.MinValue;
    private int interacts;

    public TalkToNpcTask(NpcSpot spot, Func<bool> opened, string label, IEnumerable<string>? menuHints = null)
    {
        this.spot = spot;
        this.opened = opened;
        this.label = label;
        this.menuHints = menuHints?.Select(GameUi.Normalize).Where(x => x.Length > 0).ToList() ?? [];
    }

    public override string Name => $"話しかけ: {this.label}";

    protected override unsafe TaskResult Tick(TaskContext ctx)
    {
        if (this.opened())
            return TaskResult.Done;

        if (this.Elapsed > TimeSpan.FromMinutes(8))
            return this.Fail($"{this.label} の画面を開けませんでした");

        // 1) テレポ → 2) 近くまで移動
        if (this.phase < 2)
        {
            this.sub ??= this.phase == 0
                ? new TeleportTask(this.spot.Territory, this.spot.Position)
                : new MoveToTask(this.spot.Position, 3f, this.label);
            var r = this.sub.Step(ctx);
            this.Status = this.sub.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.sub.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
            this.sub = null;
            if (failed != null)
                return this.Fail(failed);
            this.phase++;
            return TaskResult.Running;
        }

        // 3) 選択肢が出ていれば選ぶ
        var entries = GameUi.MenuEntries(out var menu);
        if (entries != null)
        {
            if (DateTime.UtcNow - this.lastMenu < TimeSpan.FromMilliseconds(500))
                return TaskResult.Running;
            this.lastMenu = DateTime.UtcNow;

            var idx = this.ChooseMenu(entries);
            if (idx < 0)
                return this.Fail($"会話の選択肢から選べませんでした（{string.Join(" / ", entries)}）");
            GameUi.Fire(menu, true, idx);
            this.Status = $"選択肢「{entries[idx]}」を選びました";
            return TaskResult.Running;
        }

        // 4) 話しかける
        if (!GameUi.PlayerFree())
        {
            this.Status = "会話中";
            return TaskResult.Running;
        }

        if (DateTime.UtcNow - this.lastInteract < TimeSpan.FromSeconds(2))
            return TaskResult.Running;

        var npc = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && o.BaseId == this.spot.NpcId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
        if (npc == null)
        {
            if (this.PhaseElapsed > TimeSpan.FromSeconds(20))
                return this.Fail($"{this.label} の NPC が見つかりません");
            return TaskResult.Running;
        }

        if (Vector3.Distance(npc.Position, Me.Position) > 5.5f)
        {
            this.phase = 1; // 近づき直す
            this.sub = new MoveToTask(npc.Position, 3f, this.label);
            return TaskResult.Running;
        }

        if (this.interacts++ >= 6)
            return this.Fail($"{this.label} に話しかけても画面が開きません");

        GameUi.Interact(npc);
        this.lastInteract = DateTime.UtcNow;
        this.Status = $"{npc.Name} に話しかけました";
        return TaskResult.Running;
    }

    private int ChooseMenu(List<string> entries)
    {
        var norm = entries.Select(GameUi.Normalize).ToList();
        foreach (var hint in this.menuHints)
        {
            var exact = norm.Select((e, i) => (e, i)).Where(x => x.e == hint).ToList();
            if (exact.Count == 1)
                return exact[0].i;
        }

        foreach (var hint in this.menuHints)
        {
            var partial = norm.Select((e, i) => (e, i)).Where(x => x.e.Contains(hint, StringComparison.Ordinal) || hint.Contains(x.e, StringComparison.Ordinal) && x.e.Length > 0).ToList();
            if (partial.Count == 1)
                return partial[0].i;
        }

        return -1;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
    }
}

/// <summary>
/// 収集品を納品して紫貨を得る。
///
///  ・納品画面は開いた時点の「今のジョブ」のタブが出るので、先にその品のタブのジョブ（木工）に着替えてから話しかける。
///  ・一覧：AtkValues[20]＝表示行数、[33+i×11]＝行番号、[34+i×11]＝ItemId+500000。
///  ・選ぶ：Fire(12, (uint)行番号)。渡す：Fire(15, 0u)。確認ダイアログは出ず、1回で1個。
///  ・成功は「収集品が減った AND 紫貨が増えた」。
///  ・終わったら（成功でも失敗でも）自分で開いた画面を閉じる。
/// </summary>
public sealed unsafe class DeliverCollectablesTask : AutoTask
{
    private readonly BookData data;
    private readonly NpcSpot npc;
    private AutoTask? sub;
    private int stage;
    private int beforeItems;
    private int beforeScrips;
    private DateTime firedAt = DateTime.MinValue;
    private bool selected;
    private int retries;

    public int Delivered { get; private set; }

    public DeliverCollectablesTask(BookData data, NpcSpot npc)
    {
        this.data = data;
        this.npc = npc;
    }

    public override string Name => "収集品の納品";

    private int Held => Inventory.CountCollectables(this.data.CollectableItemId, this.data.MinCollectability);

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.stage)
        {
            case 0:
                if (this.Held == 0)
                    return TaskResult.Done;
                this.sub ??= new EquipJobTask(this.data.CollectableTabClassJob);
                return this.RunSub(ctx);
            case 1:
                this.sub ??= new TalkToNpcTask(this.npc, () => GameUi.IsReady("CollectablesShop", out _), "収集品納品窓口");
                return this.RunSub(ctx);
            case 2:
                return this.Deliver(ctx);
            default:
                return this.Close();
        }
    }

    private TaskResult RunSub(TaskContext ctx)
    {
        var r = this.sub!.Step(ctx);
        this.Status = this.sub.Status;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        this.sub.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
        this.sub = null;
        if (failed != null)
            return this.Fail(failed);
        this.stage++;
        this.NextPhase(string.Empty);
        return TaskResult.Running;
    }

    private TaskResult Deliver(TaskContext ctx)
    {
        if (!GameUi.IsReady("CollectablesShop", out var addon))
            return this.Fail("納品画面が閉じられました");

        var scrips = Inventory.CountSpecialCurrency(this.data.RewardSpecialCurrencyId, out _);
        var held = this.Held;

        // 渡したあとの結果待ち
        if (this.firedAt != DateTime.MinValue)
        {
            if (held < this.beforeItems && scrips > this.beforeScrips)
            {
                this.Delivered++;
                ctx.Log.Write("納品", $"{CraftPlanner.ItemName(this.data.CollectableItemId)} を納品しました（紫貨 {this.beforeScrips}→{scrips}）");
                this.firedAt = DateTime.MinValue;
                this.selected = false;
                this.retries = 0;
                return TaskResult.Running;
            }

            if (held < this.beforeItems && scrips <= this.beforeScrips && DateTime.UtcNow - this.firedAt > TimeSpan.FromSeconds(2.5))
                return this.Fail("収集品は減ったのに紫貨が増えません（想定外。止めます）");

            if (DateTime.UtcNow - this.firedAt < TimeSpan.FromSeconds(2.5))
                return TaskResult.Running;

            this.firedAt = DateTime.MinValue;
            this.selected = false;
            if (this.retries++ >= 1)
                return this.Fail("納品が反映されません");
            return TaskResult.Running;
        }

        if (held == 0)
        {
            this.stage = 3;
            return TaskResult.Running;
        }

        // 溢れる分は捨てられるので、上限を超えるなら渡さない
        var cap = ScripCap(this.data.RewardSpecialCurrencyId);
        if (cap > 0 && scrips + this.data.RewardHigh > cap)
        {
            ctx.Log.Warn("納品", $"紫貨が上限に近いので納品をやめます（{scrips}/{cap}）");
            this.stage = 3;
            return TaskResult.Running;
        }

        var row = this.FindRow(addon);
        if (row < 0)
            return this.Fail($"納品画面に {CraftPlanner.ItemName(this.data.CollectableItemId)} が出ていません（ジョブのタブが違う可能性）");

        if (!this.selected)
        {
            GameUi.Fire(addon, true, 12, (uint)row);
            this.selected = true;
            this.NextPhase("選択しました");
            return TaskResult.Running;
        }

        // 右の一覧（node 31）に手持ちが出たら渡す（出ない場合もあるので、少し待ったら渡す）
        var list = (AtkComponentList*)addon->GetComponentByNodeId(31);
        var shown = list != null && list->ListLength > 0;
        if (!shown && this.PhaseElapsed < TimeSpan.FromSeconds(1))
            return TaskResult.Running;

        this.beforeItems = held;
        this.beforeScrips = scrips;
        GameUi.Fire(addon, true, 15, 0u);
        this.firedAt = DateTime.UtcNow;
        return TaskResult.Running;
    }

    private int FindRow(AtkUnitBase* addon)
    {
        var rows = GameUi.AtkInt(addon, 20) ?? 0;
        for (var i = 0; i < rows * 2 && i < 200; i++)
        {
            var id = GameUi.AtkInt(addon, 34 + i * 11);
            if (id == null)
                break;
            if (id == this.data.CollectableItemId + 500000)
                return (int)(GameUi.AtkInt(addon, 33 + i * 11) ?? -1);
        }

        return -1;
    }

    private TaskResult Close()
    {
        if (GameUi.Addon("CollectablesShop") is var a && a != null)
        {
            a->Close(true);
            if (this.PhaseElapsed > TimeSpan.FromSeconds(10))
                GameUi.Fire(a, true, -1);
            return TaskResult.Running;
        }

        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        if (GameUi.Addon("CollectablesShop") is var a && a != null)
            a->Close(true);
    }

    public static int ScripCap(byte specialId)
    {
        var cm = CurrencyManager.Instance();
        if (cm == null)
            return 0;
        var id = cm->GetItemIdBySpecialId(specialId);
        if (id == 0)
            return 0;
        if (cm->IsItemLimited(id))
        {
            var max = cm->GetItemMaxCount(id);
            if (max > 0)
                return (int)max;
        }

        return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row) ? (int)row.StackSize : 0;
    }
}

/// <summary>
/// 紫貨で秘伝書を交換する（アイテム交換画面 InclusionShop）。
///
///  ・系統と種別はエージェント（AgentInclusionShop）で選ぶ。
///  ・品：AtkValues[298]＝件数、[299+i×18+1]＝ItemId、[+12]＝値段、[+17]＝撃つ値（画面の index）。
///  ・撃つ：Fire(14, (uint)index, 1u) → ShopExchangeItemDialog の「交換する」（node 18）→ 出れば SelectYesno。
///  ・成功は「秘伝書が増えた AND 紫貨が減った」。習得済みの本は撃っても何も起きないので、撃つ前に弾く。
/// </summary>
public sealed unsafe class ExchangeBooksTask : AutoTask
{
    private const uint ExchangeButtonNode = 18; // ShopExchangeItemDialog の「交換する」（ECommons）

    private readonly BookData data;
    private readonly NpcSpot npc;
    private readonly Queue<BookOffer> queue;
    private AutoTask? sub;
    private int stage;
    private BookOffer? current;
    private int beforeBooks;
    private int beforeScrips;
    private DateTime firedAt = DateTime.MinValue;
    private DateTime lastClick = DateTime.MinValue;

    public ExchangeBooksTask(BookData data, NpcSpot npc, IEnumerable<BookOffer> books)
    {
        this.data = data;
        this.npc = npc;
        this.queue = new Queue<BookOffer>(books);
    }

    public override string Name => "秘伝書の交換";

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.stage)
        {
            case 0:
            {
                if (this.queue.Count == 0)
                    return TaskResult.Done;

                var hints = new List<string>();
                var cats = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.InclusionShopCategory>();
                foreach (var c in this.data.BookCategories)
                    if (cats.TryGetRow(c, out var row))
                        hints.Add(row.Name.ExtractText());
                var shops = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SpecialShop>();
                foreach (var o in this.queue)
                    if (shops.TryGetRow(o.ShopId, out var s))
                        hints.Add(s.Name.ExtractText());

                this.sub ??= new TalkToNpcTask(this.npc, IsShopReady, "スクリップ取引窓口", hints.Distinct());
                var r = this.sub.Step(ctx);
                this.Status = this.sub.Status;
                if (r == TaskResult.Running)
                    return TaskResult.Running;
                this.sub.Cleanup(ctx);
                var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
                this.sub = null;
                if (failed != null)
                    return this.Fail(failed);
                this.stage = 1;
                this.NextPhase("系統を選びます");
                return TaskResult.Running;
            }

            case 1:
                return this.SelectCategory();

            case 2:
                return this.Exchange(ctx);

            default:
                return this.Close();
        }
    }

    private static bool IsShopReady()
    {
        if (!GameUi.IsReady("InclusionShop", out _))
            return false;
        var agent = AgentInclusionShop.Instance();
        return agent != null && agent->IsAgentActive() && agent->Data != null && agent->Data->IsShopReady;
    }

    /// <summary>系統と種別を選び、目的の本が一覧に出るまで待つ。</summary>
    private TaskResult SelectCategory()
    {
        if (!IsShopReady())
        {
            if (this.TimedOut(TimeSpan.FromSeconds(10)))
                return this.Fail("アイテム交換の画面が閉じられました");
            return TaskResult.Running;
        }

        if (GameUi.IsReady("InclusionShop", out var addon) && this.FindBook(addon, this.queue.Peek().BookItemId).Index >= 0)
        {
            this.stage = 2;
            this.NextPhase("交換します");
            return TaskResult.Running;
        }

        if (this.TimedOut(TimeSpan.FromSeconds(10)))
            return this.Fail("交換画面に秘伝書が出てきません（系統・種別を選べませんでした）");

        if (DateTime.UtcNow - this.lastClick < TimeSpan.FromMilliseconds(700))
            return TaskResult.Running;
        this.lastClick = DateTime.UtcNow;

        var agent = AgentInclusionShop.Instance();
        var d = agent->Data;

        // 系統：表示位置 i → 内部の並び → 系統の行
        for (byte i = 0; i < d->CategoryCount && i < 30; i++)
        {
            var mapped = d->CategoryIndexMap[i];
            if (mapped >= 30)
                continue;
            var cat = d->Categories[mapped];
            if (!this.data.BookCategories.Contains(cat.InclusionShopRowId))
                continue;

            if (d->SelectedCategoryIndex != i)
            {
                agent->SelectCategory(i);
                return TaskResult.Running;
            }

            // 種別：その系統の系列で秘伝書の店が何番目か（先頭に「選択してください」が入るので +1）
            if (this.data.BookSeriesSubrow.TryGetValue(cat.InclusionShopSeriesId, out var subrow))
            {
                var tab = (byte)(subrow + 1);
                if (d->SelectedSubCategoryTab != tab)
                    d->SelectSubCategory(tab);
            }

            return TaskResult.Running;
        }

        return this.Fail("この窓口の交換画面に、秘伝書の系統がありません");
    }

    private (int Index, uint Price) FindBook(AtkUnitBase* addon, uint itemId)
    {
        var count = GameUi.AtkInt(addon, 298) ?? 0;
        for (var i = 0; i < count && i < 60; i++)
        {
            var baseIdx = 299 + (i * 18);
            if (GameUi.AtkInt(addon, baseIdx + 1) != itemId)
                continue;
            var price = (uint)(GameUi.AtkInt(addon, baseIdx + 12) ?? 0);
            var index = (int)(GameUi.AtkInt(addon, baseIdx + 17) ?? -1);
            return (index, price);
        }

        return (-1, 0);
    }

    private TaskResult Exchange(TaskContext ctx)
    {
        var books = this.current == null ? 0 : Inventory.CountNow(this.current.BookItemId);
        var scrips = Inventory.CountSpecialCurrency(this.data.RewardSpecialCurrencyId, out var scripItem);

        // 撃ったあとの確認と結果待ち
        if (this.firedAt != DateTime.MinValue)
        {
            if (books > this.beforeBooks && scrips < this.beforeScrips)
            {
                ctx.Log.Write("交換", $"{CraftPlanner.ItemName(this.current!.BookItemId)} を交換しました（紫貨 {this.beforeScrips}→{scrips}）");
                this.firedAt = DateTime.MinValue;
                this.current = null;
                return TaskResult.Running;
            }

            // 確認ダイアログ
            if (GameUi.IsReady("ShopExchangeItemDialog", out var dialog))
            {
                if (DateTime.UtcNow - this.lastClick > TimeSpan.FromMilliseconds(400))
                {
                    GameUi.ClickButton(dialog, ExchangeButtonNode);
                    this.lastClick = DateTime.UtcNow;
                }

                return TaskResult.Running;
            }

            var text = GameUi.YesnoText(out var yesno);
            if (text != null && DateTime.UtcNow - this.firedAt < TimeSpan.FromSeconds(10))
            {
                // 撃った直後に出たもので、通貨名と値段の両方を含むときだけ「はい」
                var scripName = CraftPlanner.ItemName(scripItem);
                if (text.Contains(scripName, StringComparison.Ordinal) && text.Contains(this.current!.Price.ToString(), StringComparison.Ordinal)
                    && DateTime.UtcNow - this.lastClick > TimeSpan.FromMilliseconds(400))
                {
                    yesno->FireCallbackInt(0);
                    this.lastClick = DateTime.UtcNow;
                }

                return TaskResult.Running;
            }

            if (DateTime.UtcNow - this.firedAt > TimeSpan.FromSeconds(15))
            {
                if (books == this.beforeBooks && scrips == this.beforeScrips)
                {
                    ctx.Log.Warn("交換", $"{CraftPlanner.ItemName(this.current!.BookItemId)} の交換がゲームに受け付けられませんでした（習得済みの可能性）。この巻は飛ばします");
                    this.firedAt = DateTime.MinValue;
                    this.current = null;
                    return TaskResult.Running;
                }

                return this.Fail("交換の結果が片方しか反映されていません（想定外。止めます）");
            }

            return TaskResult.Running;
        }

        // 次の巻
        if (this.current == null)
        {
            if (this.queue.Count == 0)
            {
                this.stage = 3;
                this.NextPhase("閉じます");
                return TaskResult.Running;
            }

            this.current = this.queue.Dequeue();
        }

        if (IsLearned(this.current.TomeId) || Inventory.CountNow(this.current.BookItemId) > 0)
        {
            this.current = null;
            return TaskResult.Running;
        }

        if (!GameUi.IsReady("InclusionShop", out var addon))
            return this.Fail("アイテム交換の画面が閉じられました");

        var (index, price) = this.FindBook(addon, this.current.BookItemId);
        if (index < 0)
            return this.Fail($"交換画面に {CraftPlanner.ItemName(this.current.BookItemId)} がありません");
        if (price != this.current.Price)
            return this.Fail($"値段がゲームデータと違います（画面 {price} / データ {this.current.Price}）");
        if (scrips < price)
            return this.Fail($"紫貨が足りません（{scrips}/{price}）");

        // 画面の所持通貨が紫貨の所持数と一致するか（別の通貨の画面を撃たないための確認）
        if (GameUi.AtkInt(addon, 297) is { } shown && shown != scrips)
            return this.Fail($"画面の通貨（{shown}）が紫貨の所持数（{scrips}）と合いません");

        if (Inventory.FreeBagSlots() < 3)
            return this.Fail("カバンの空きが足りません（3枠以上空けてください）");

        this.beforeBooks = Inventory.CountNow(this.current.BookItemId);
        this.beforeScrips = scrips;
        GameUi.Fire(addon, true, 14, (uint)index, 1u);
        this.firedAt = DateTime.UtcNow;
        return TaskResult.Running;
    }

    private TaskResult Close()
    {
        foreach (var name in new[] { "SelectYesno", "ShopExchangeItemDialog" })
        {
            if (GameUi.Addon(name) is var a && a != null)
            {
                GameUi.Fire(a, true, -1);
                return TaskResult.Running;
            }
        }

        if (GameUi.Addon("InclusionShop") is var s && s != null)
        {
            s->Close(true);
            if (this.PhaseElapsed > TimeSpan.FromSeconds(10))
                return TaskResult.Done;
            return TaskResult.Running;
        }

        return TaskResult.Done;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        foreach (var name in new[] { "SelectYesno", "ShopExchangeItemDialog" })
            if (GameUi.Addon(name) is var a && a != null)
                GameUi.Fire(a, true, -1);
        if (GameUi.Addon("InclusionShop") is var s && s != null)
            s->Close(true);
    }

    public static bool IsLearned(uint tomeId)
    {
        try
        {
            var ps = PlayerState.Instance();
            return ps == null || ps->IsSecretRecipeBookUnlocked(tomeId);
        }
        catch
        {
            return true; // 読めないときは「習得済み」側（＝撃たない側）
        }
    }
}

/// <summary>
/// 秘伝書を使って習得する。確認（SelectYesno）が出たら、本の名前を含むときだけ「はい」。
/// 成功は「習得済みになった AND 本が減った」。
/// </summary>
public sealed unsafe class UseBooksTask : AutoTask
{
    private readonly Queue<BookOffer> queue;
    private BookOffer? current;
    private DateTime usedAt = DateTime.MinValue;
    private DateTime lastClick = DateTime.MinValue;

    public UseBooksTask(IEnumerable<BookOffer> books)
    {
        this.queue = new Queue<BookOffer>(books);
    }

    public override string Name => "秘伝書を読む";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.current == null)
        {
            if (this.queue.Count == 0)
                return TaskResult.Done;
            this.current = this.queue.Dequeue();
            this.usedAt = DateTime.MinValue;
        }

        var learned = ExchangeBooksTask.IsLearned(this.current.TomeId);
        var owned = Inventory.CountNow(this.current.BookItemId);

        if (learned)
        {
            if (this.usedAt != DateTime.MinValue)
                ctx.Log.Write("秘伝書", $"{CraftPlanner.ItemName(this.current.BookItemId)} を読みました");
            this.current = null;
            return TaskResult.Running;
        }

        if (this.usedAt == DateTime.MinValue)
        {
            if (owned == 0)
                return this.Fail($"{CraftPlanner.ItemName(this.current.BookItemId)} を持っていません");
            if (!GameUi.PlayerFree())
                return TaskResult.Running;

            AgentInventoryContext.Instance()->UseItem(this.current.BookItemId);
            this.usedAt = DateTime.UtcNow;
            return TaskResult.Running;
        }

        var text = GameUi.YesnoText(out var yesno);
        if (text != null && text.Contains(CraftPlanner.ItemName(this.current.BookItemId), StringComparison.Ordinal)
            && DateTime.UtcNow - this.lastClick > TimeSpan.FromMilliseconds(400))
        {
            yesno->FireCallbackInt(0);
            this.lastClick = DateTime.UtcNow;
            return TaskResult.Running;
        }

        if (DateTime.UtcNow - this.usedAt > TimeSpan.FromSeconds(15))
            return this.Fail($"{CraftPlanner.ItemName(this.current.BookItemId)} を読めませんでした");

        return TaskResult.Running;
    }
}
