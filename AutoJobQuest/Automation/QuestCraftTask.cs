using System;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 受注後にクエストの材料から作る品（Lv61〜70 の製作職の32本）を、HQ になるまで作る。
///
/// 【なぜ Questionable に任せきりにしないか】
///  ・納品は HQ が要る（クエストのスクリプトの受け渡しがすべて hq=True）。Questionable の製作手順は Artisan の既製リストを1回動かすだけで、
///    品質を見ない（NQ でも済んだことにする）。NQ ができると、受け取る相手が応じず、その先へ進めない。
///  ・材料はクエストがくれる1回分だけ。材料が0個になると、くれた相手（<see cref="QuestCraft.Giver"/>）が配り直す
///    （木工 Lv63 のスクリプトで確認。残り31本は同じ形と見込み：実機未確認）。
/// そこで、Questionable がこの品の製作手順に入ったら止め、こちらで作る。製作そのものは Artisan に任せる（HQ 化・最適化は Artisan）。
/// HQ にならなければ、材料をくれた相手に話しかけて材料をもらい直し、作り直す（設定の作り直しの回数まで）。
/// HQ がそろったら Questionable に戻す（製作手順は「持っていれば飛ばす」付きなので、作った品を見て飛ばす）。
/// </summary>
public sealed class QuestCraftTask : AutoTask
{
    private readonly JobQuest quest;
    private readonly QuestCraft qc;
    private readonly System.Collections.Generic.IReadOnlyList<QuestionableStep>? paths;
    private AutoTask? sub;
    private CraftOneTask? craft;
    private NpcStepTask? giver;
    private int redo;

    public QuestCraftTask(JobQuest quest, QuestCraft qc, System.Collections.Generic.IReadOnlyList<QuestionableStep>? paths)
    {
        this.quest = quest;
        this.qc = qc;
        this.paths = paths;
    }

    public override string Name => $"受注後の製作: {CraftPlanner.ItemName(this.qc.ItemId)}{(this.qc.Hq ? "（HQ）" : string.Empty)}×{this.qc.Count}";

    private int Held => this.qc.Hq ? Inventory.Snapshot().CountHq(this.qc.ItemId) : Inventory.Snapshot().CountAll(this.qc.ItemId);

    /// <summary>クエストがくれる材料で、あと何回作れるか。</summary>
    private int MaterialCrafts(Recipe recipe)
    {
        var inv = Inventory.Snapshot();
        var times = int.MaxValue;
        foreach (var (item, amount) in CraftPlanner.Ingredients(recipe).Where(x => this.qc.Materials.Contains(x.Item)))
            times = Math.Min(times, inv.CountAll(item) / Math.Max(1, amount));
        return times == int.MaxValue ? 0 : times;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        // 材料をもらえた（会話が終わり、材料で作れるようになった）ら、相手との手順をそこで終える（材料を渡してもクエストの段・変数は
        // 変わらない見込みなので、以前は計3回話しかけ、「既に済んだ相手の可能性」という誤った注意を作り直しのたびに出していた）
        if (this.giver != null && this.sub == this.giver && GameUi.PlayerFree()
            && Svc.Data.GetExcelSheet<Recipe>().TryGetRow(this.qc.RecipeId, out var given) && this.MaterialCrafts(given) > 0)
        {
            this.sub.Cleanup(ctx);
            this.sub = null;
            this.giver = null;
            ctx.Log.Write("クエスト", $"{NpcStepTask.NpcName(this.qc.Giver)} から {CraftPlanner.ItemName(this.qc.ItemId)} の材料を受け取りました");
        }

        if (this.sub != null)
        {
            var r = this.sub.Step(ctx);
            this.Status = this.sub.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
            this.sub.Cleanup(ctx);
            this.sub = null;
            if (failed != null)
                return this.Fail(failed);
            this.AfterSub(ctx);
            if (this.pendingFail != null)
                return this.Fail(this.pendingFail);
        }

        if (this.Held >= this.qc.Count)
        {
            ctx.Log.Write("クエスト", $"{CraftPlanner.ItemName(this.qc.ItemId)}{(this.qc.Hq ? " HQ" : string.Empty)} が {this.qc.Count} 個そろいました。Questionable に戻します");
            return TaskResult.Done;
        }

        if (!Svc.Data.GetExcelSheet<Recipe>().TryGetRow(this.qc.RecipeId, out var recipe))
            return this.Fail($"{CraftPlanner.ItemName(this.qc.ItemId)} のレシピ（{this.qc.RecipeId}）をゲームデータから引けません");

        // 事前に用意したクリスタル（1回分）
        var inv = Inventory.Snapshot();
        var lacking = this.qc.Prepared.Where(p => inv.CountAll(p.Item) < p.Amount).ToList();
        if (lacking.Count > 0)
            return this.Fail($"受注後に作る {CraftPlanner.ItemName(this.qc.ItemId)} の材料が足りません："
                             + string.Join("、", lacking.Select(p => $"{CraftPlanner.ItemName(p.Item)} {inv.CountAll(p.Item)}/{p.Amount}"))
                             + "。手に入れてから再開してください（クエストは受注したままで続きから進みます）");

        var times = this.MaterialCrafts(recipe);
        if (times > 0)
        {
            var yield = Math.Max(1, (int)recipe.AmountResult);
            var crafts = Math.Min(times, (this.qc.Count - this.Held + yield - 1) / yield);
            var job = Jobs.CraftTypeToClassJob(recipe.CraftType.RowId);
            var planned = new PlannedCraft(recipe.RowId, this.qc.ItemId, job, recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0,
                crafts, yield, this.qc.Hq, 0, 0, HqTarget: this.qc.Hq ? this.qc.Count : 0);
            this.craft = new CraftOneTask(planned);
            this.sub = new SequenceTask(this.Name, [_ => this.craft, _ => new ExitCraftStanceTask()]);
            return TaskResult.Running;
        }

        // 材料が無い：HQ にならなかった（材料を使い切った）。くれた相手に話しかけてもらい直す
        if (this.redo >= Math.Max(0, ctx.Config.QuestCraftRetryRounds))
            return this.Fail($"{CraftPlanner.ItemName(this.qc.ItemId)} を {this.redo + 1} 回作っても HQ になりませんでした。"
                             + "装備・食事・Artisan のソルバーの設定を見直し、材料をくれた相手"
                             + (this.qc.Giver != 0 ? $"（{NpcStepTask.NpcName(this.qc.Giver)}）" : string.Empty)
                             + "に話しかけて材料をもらってから、もう一度開始してください（クエストは受注したままで続きから進みます）");
        if (this.qc.Giver == 0 || GiverStep(this.quest, this.qc, this.paths) is not { } step)
            return this.Fail($"{CraftPlanner.ItemName(this.qc.ItemId)} が HQ になりませんでした。材料をくれる相手の場所をゲームデータから決められないので、"
                             + "手で材料をもらい直してから、もう一度開始してください");

        this.redo++;
        ctx.Log.Warn("クエスト", $"{CraftPlanner.ItemName(this.qc.ItemId)} が HQ になりませんでした。{NpcStepTask.NpcName(this.qc.Giver)} に話しかけて材料をもらい直し、作り直します（{this.redo}/{ctx.Config.QuestCraftRetryRounds} 回目）");
        this.giver = new NpcStepTask(step, this.quest.RowId);
        this.sub = this.giver;
        return TaskResult.Running;
    }

    private void AfterSub(TaskContext ctx)
    {
        if (this.craft != null)
        {
            if (this.craft.Finished && this.qc.Hq && this.craft.MadeHq < this.craft.Made)
                ctx.Log.Warn("クエスト", $"{CraftPlanner.ItemName(this.qc.ItemId)}：{this.craft.Made} 個中 HQ {this.craft.MadeHq} 個でした（NQ の品は納品に使えません。捨ててかまいません）");
            this.craft = null;
        }

        if (this.giver != null)
        {
            this.giver = null;
            if (Svc.Data.GetExcelSheet<Recipe>().TryGetRow(this.qc.RecipeId, out var recipe) && this.MaterialCrafts(recipe) == 0)
                this.pendingFail = $"{NpcStepTask.NpcName(this.qc.Giver)} に話しかけても、{CraftPlanner.ItemName(this.qc.ItemId)} の材料をもらえませんでした。手で材料をもらい直してから、もう一度開始してください";
        }
    }

    private string? pendingFail;

    protected override TaskResult OnStart(TaskContext ctx) => TaskResult.Running;

    /// <summary>
    /// 材料をくれる相手の手順（位置・エリア）。同じクエストの経路データにその相手の手順があればそれ、無ければゲームデータの Level 表
    /// （Object がその相手の行）から（経路に位置の無い9本：彫金4・調理4・鍛冶 Lv70）。
    /// </summary>
    public static QuestionableStep? GiverStep(JobQuest quest, QuestCraft qc, System.Collections.Generic.IReadOnlyList<QuestionableStep>? paths)
    {
        var fromPath = paths?.FirstOrDefault(s => s.DataId == qc.Giver && s.Position != null);
        if (fromPath != null)
            return new QuestionableStep(qc.GiverSeq, 0, "Interact", qc.Giver, fromPath.Territory, fromPath.Position, null);

        foreach (var lv in Svc.Data.GetExcelSheet<Level>())
        {
            if (lv.Object.RowId != qc.Giver || lv.Territory.RowId == 0)
                continue;
            return new QuestionableStep(qc.GiverSeq, 0, "Interact", qc.Giver, lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z), null);
        }

        return null;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
    }
}
