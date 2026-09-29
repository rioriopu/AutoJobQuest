using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;

namespace AutoJobQuest.Planning;

/// <summary>リテイナーの合計在庫を仮に手持ちへ足し、上位品から材料需要を減らして引出数を求める。</summary>
public static class RetainerPlan
{
    public sealed class Stock : IInventoryView
    {
        public Dictionary<(uint Item, bool Hq), int> Counts { get; } = [];
        public int CountNq(uint id) => this.Counts.GetValueOrDefault((id, false));
        public int CountHq(uint id) => this.Counts.GetValueOrDefault((id, true));
    }

    public sealed class Combined(IInventoryView bags, IInventoryView retainers) : IInventoryView
    {
        public int CountNq(uint id) => bags.CountNq(id) + retainers.CountNq(id);
        public int CountHq(uint id) => bags.CountHq(id) + retainers.CountHq(id);
    }

    public static Stock Build(CraftPlanner planner, IEnumerable<QuestItemReq> targets, IInventoryView bags, IInventoryView retainers)
    {
        var plan = planner.Build(targets, new Combined(bags, retainers), _ => true);
        if (plan.Problems.Count != 0)
            throw new InvalidOperationException(string.Join(" / ", plan.Problems));
        var result = new Stock();
        foreach (var (id, demand) in plan.StockDemand)
        {
            var hq = Math.Min(retainers.CountHq(id), Math.Max(0, demand.Hq - bags.CountHq(id)));
            var any = Math.Max(0, demand.Any - bags.CountNq(id) - Math.Max(0, bags.CountHq(id) + hq - demand.Hq));
            var nq = Math.Min(retainers.CountNq(id), any);
            hq += Math.Min(retainers.CountHq(id) - hq, any - nq);
            // HQ納品物を親の材料にも使う場合、製作計画と同じHQ優先消費を満たす。
            // 引出総数は増やさず、品質不問の引出分だけNQからHQへ差し替える。
            if (demand.Hq > 0 && demand.Parent > 0)
            {
                var prefer = Math.Max(0, demand.Hq + demand.Parent - bags.CountHq(id) - hq);
                var swap = Math.Min(nq, Math.Min(prefer, retainers.CountHq(id) - hq));
                hq += swap;
                nq -= swap;
            }
            if (nq > 0) result.Counts[(id, false)] = nq;
            if (hq > 0) result.Counts[(id, true)] = hq;
        }
        return result;
    }
}
