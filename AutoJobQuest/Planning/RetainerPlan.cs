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

    /// <summary>引き出す数と、計算で見つかった問題（計画の注意：マイスター専用のレシピ・循環など）。</summary>
    public sealed record Result(Stock Pull, IReadOnlyList<string> Problems);

    /// <summary>
    /// 引き出す数を求める。計画の注意（CraftPlan.Problems）があっても例外で止めない（以前は事前点検より前に
    /// 「例外で止まりました」になった）。注意は呼び出し側が記録に出し、その品も分かる範囲で引き出す。
    /// </summary>
    public static Result Build(CraftPlanner planner, IEnumerable<QuestItemReq> targets, IInventoryView bags, IInventoryView retainers)
    {
        var plan = planner.Build(targets, new Combined(bags, retainers), _ => true);
        var result = new Stock();
        foreach (var (id, demand) in plan.StockDemand)
        {
            var hq = Math.Min(retainers.CountHq(id), Math.Max(0, demand.Hq - bags.CountHq(id)));
            var any = Math.Max(0, demand.Any - bags.CountNq(id) - Math.Max(0, bags.CountHq(id) + hq - demand.Hq));
            var nq = Math.Min(retainers.CountNq(id), any);
            hq += Math.Min(retainers.CountHq(id) - hq, any - nq);
            // HQ で納品し、しかも親の材料にもなる品は、製作計画と同じく「親は HQ を先に使う」前提にそろえる：
            // 「HQ の納品＋親の材料」までは、リテイナーの HQ を引き出す（増やした HQ の分だけ NQ を減らす）。
            // 以前は品質不問の引き出し分を振り替えるだけだったので、鞄の NQ だけで品質不問の分が足りていると HQ を1つも引き出さず、
            // 引き出した後の計画で取り置きの製作が増え、材料を集め直していた（乱数の在庫1000回中265回。ウォルナット材の例）
            if (demand.Hq > 0 && demand.Parent > 0)
            {
                var prefer = Math.Max(0, demand.Hq + demand.Parent - bags.CountHq(id) - hq);
                var add = Math.Min(prefer, retainers.CountHq(id) - hq);
                hq += add;
                nq = Math.Max(0, nq - add);
            }
            if (nq > 0) result.Counts[(id, false)] = nq;
            if (hq > 0) result.Counts[(id, true)] = hq;
        }
        return new Result(result, plan.Problems.ToList());
    }
}
