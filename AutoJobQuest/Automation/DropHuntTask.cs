using System;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;

namespace AutoJobQuest.Automation;

/// <summary>
/// デバッグ：指定のモンスターを倒して、指定の素材を指定の数だけ集めたら止まる（デバッグタブで各モンスターの
/// 名前を押したら倒しに行き、指定の数を得たら止まる）。素材集めの戦闘と同じ部品（戦闘のジョブへ着替え → そのエリアへテレポ → CombatTask）を
/// 使うので、降り方・近づき方・RSR の扱いも本番と同じになる。数は「押した時点の所持数＋指定の数」に届いたら止まる。上限は 30 分。
/// </summary>
public sealed class DropHuntTask : AutoTask
{
    private readonly DropHuntEntry entry;
    private readonly DropHuntMob mob;
    private readonly int count;
    private SequenceTask? child;
    private int target;

    public DropHuntTask(DropHuntEntry entry, DropHuntMob mob, int count)
    {
        this.entry = entry;
        this.mob = mob;
        this.count = Math.Max(1, count);
    }

    public override string Name => $"デバッグ：{this.mob.Name}（{this.mob.TerritoryName}）を倒して {this.entry.ItemName} を {this.count} 個";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        // 素材集めの戦闘の前の確かめと同じ（JobQuestFlow の 3)）
        if (!ctx.Rotation.IsLoaded)
            return this.Fail("RotationSolverReborn が読み込まれていません（攻撃は RSR に任せます）");
        if (Ipc.RsrStateReader.ReadTargetFreelyOverride() != false)
            return this.Fail("RSR の外部ターゲット指定が有効、または読めません。指定外を狙わないと確認できるまで戦闘は始められません");
        if (CombatJobPicker.Pick() is not { } job)
            return this.Fail("戦闘に使えるジョブ（ギアセットのある戦闘ジョブ）がありません");
        if (this.mob.Spots.Count == 0)
            return this.Fail($"{this.mob.Name} の出現点が分かりません");

        var owned = Inventory.CountNow(this.entry.ItemId);
        this.target = owned + this.count;
        var spots = this.mob.Spots;
        var first = MapCoords.ToWorld(this.mob.Territory, spots[0].Spot.X, spots[0].Spot.Y);
        ctx.Log.Write("デバッグ", $"{this.mob.Name}（{this.mob.TerritoryName}・出現点 {spots.Count} か所）を倒して、{this.entry.ItemName} を集めます"
                                 + $"（今 {owned} 個 → {this.target} 個になったら止めます。戦闘のジョブ {Jobs.Name(job.ClassJob)} Lv{job.Level}）");
        this.child = new SequenceTask(this.Name,
        [
            _ => new EquipJobTask(job.ClassJob),
            _ => new TeleportTask(this.mob.Territory, first),
            _ => new CombatTask(this.mob.Territory, [new CombatNeed(this.entry.ItemId, this.target, this.mob.NameIds)], spots, TimeSpan.FromMinutes(30)),
        ]);
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        var r = this.child!.Step(ctx);
        this.Status = this.child.Status;
        if (r == TaskResult.Failed)
            return this.Fail(this.child.FailReason ?? "デバッグの戦闘が失敗しました");
        if (r == TaskResult.Done)
        {
            var now = Inventory.CountNow(this.entry.ItemId);
            if (now >= this.target)
                ctx.Log.Write("デバッグ", $"{this.entry.ItemName} が {now} 個になったので止めます（目標 {this.target} 個）");
            else
                ctx.Log.Warn("デバッグ", $"{this.entry.ItemName} は {now} 個で、目標の {this.target} 個に届かないまま戦闘が終わりました（30分の上限・倒せる個体がいない等。記録の「戦闘」を見てください）");
        }

        return r;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.child?.Cleanup(ctx);
        base.Cleanup(ctx);
    }
}
