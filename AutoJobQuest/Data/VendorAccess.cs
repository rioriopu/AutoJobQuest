using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>GBR が挙げる売り手1件（NPC と店の組。置き場所のエリアつき）。</summary>
/// <param name="Npc">ENpc の番号。</param>
/// <param name="Shop">ギルショップ（GilShop の行）。</param>
/// <param name="Name">NPC の名前（記録用）。</param>
/// <param name="Territory">NPC の置き場所のエリア（GBR の VendorNpcLocationCache）。</param>
public sealed record VendorCandidate(uint Npc, uint Shop, string Name, uint Territory);

/// <summary>
/// NPC 購入で、このキャラクターが使える売り手を品ごとに選ぶ（ゲームに触らない判断。状態は外から渡す）。
/// 人によって状況が異なる購入先は購入先リストに含めない。友好部族の進捗は調べられるので、
/// ゲームデータを読み、キャラクター情報から購入可能かどうか判断した上で、高地ドラヴァニアのアキンドに向かうかどうか判断する。
/// 不具合の例：オーラムレギスナゲットを、GBR が店の番号の並びで先に来るアキンド（グナース族の解放前は現れない）に割り当て、
/// 「Timed out opening アキンド's gil shop」で失敗した（同じ品はイディルシャイアの素材屋でも条件なしで売っていた）。
/// GBR は売り手を「店の番号順で置き場所の分かる最初の NPC」に決め、友好部族やクエストの条件は見ない（VendorPreferenceHelper・VendorShopResolver）。
/// </summary>
public static class VendorAccess
{
    /// <summary>
    /// その売り手で、このキャラクターがその品を買えない理由（買えるなら null）。次をすべて満たせば買える：
    ///  ・店と品の条件（GilShop.Quest・GilShopItem.QuestRequired・アチーブメント）
    ///  ・NPC が現れている（Story の条件：アキンドなら「名なしのグナース族」の完了）
    ///  ・NPC のエリアに行ける（入口のエーテライトが解放済み。判断できないエリアは行けるとみなす）
    /// </summary>
    /// <param name="isComplete">クエストが完了しているか（ゲームでは QuestManager.IsQuestComplete）。</param>
    /// <param name="reachable">エリアに行けるか（ゲームでは AreaAccess.Reachable。null＝判断できない）。</param>
    public static string? WhyNot(SourceIndex sources, uint item, VendorCandidate c, Func<uint, bool> isComplete, Func<uint, bool?> reachable)
    {
        var where = $"{c.Name}（{AreaAccess.Name(c.Territory)}）";

        // 店と品の条件。その店の行がデータに無ければ（GBR だけが知る店）、条件は分からないので見ない
        var offers = sources.Get(item).VendorOffers.Where(o => o.Shop == c.Shop).ToList();
        if (offers.Count > 0 && !offers.Any(o => o.Conditions(isComplete)))
        {
            var q = offers.SelectMany(o => o.Quests).FirstOrDefault(q => !isComplete(q));
            return q != 0
                ? $"{where}：この品を買うのにクエスト「{Unlocks.QuestName(q)}」の完了が要ります"
                : $"{where}：確かめられない条件（アチーブメント）が付いています";
        }

        if (GateReason(sources.NpcGate(c.Npc), isComplete) is { } hidden)
            return $"{where}は、{hidden}";

        if (reachable(c.Territory) == false)
            return $"{where}：エリアの入口のエーテライトが未解放です";

        return null;
    }

    /// <summary>NPC が現れていない理由（現れていれば null）。</summary>
    public static string? GateReason(ShopNpc? gate, Func<uint, bool> isComplete)
    {
        if (gate == null || gate.Visible(isComplete))
            return null;
        if (gate.Never)
            return "いつも現れる NPC ではありません（ゲームデータの Story の条件を読み取れません）";
        var missing = gate.Gate.Where(q => !isComplete(q)).ToList();
        return $"クエスト「{string.Join(gate.GateAll ? "」と「" : "」か「", missing.Select(Unlocks.QuestName))}」を終えるまで現れません（友好部族・クエストの進み具合）";
    }

    /// <summary>
    /// 品ごとに、買える売り手を1人ずつ選ぶ。選ぶ順：
    ///  1) 指定の NPC（<paramref name="preferredNpc"/>：万能ルアーのよろず屋など）が買えるなら、その NPC
    ///  2) 同じ NPC・店でまとめて買える品の多い売り手（GBR は同じ NPC・店の品を続けて買う。移動が減る）
    ///  3) 同じなら GBR の並び順（<paramref name="candidates"/> の順）
    /// 買える売り手がいない品は、理由つきで外す（NPC 購入をやめ、次の周回で別の手段にする）。
    /// </summary>
    /// <param name="candidates">品 → GBR が挙げる売り手（GBR の並び順）。</param>
    /// <param name="whyNot">その品をその売り手で買えない理由（買えるなら null）。</param>
    public static (Dictionary<uint, VendorCandidate> Chosen, Dictionary<uint, string> Excluded) Choose(
        IReadOnlyDictionary<uint, List<VendorCandidate>> candidates, Func<uint, VendorCandidate, string?> whyNot, IReadOnlyDictionary<uint, uint>? preferredNpc = null)
    {
        var chosen = new Dictionary<uint, VendorCandidate>();
        var excluded = new Dictionary<uint, string>();
        var usable = new Dictionary<uint, List<VendorCandidate>>();
        foreach (var (item, list) in candidates)
        {
            var ok = new List<VendorCandidate>();
            var reasons = new List<string>();
            foreach (var c in list)
            {
                if (whyNot(item, c) is { } why)
                    reasons.Add(why);
                else
                    ok.Add(c);
            }

            if (ok.Count > 0)
                usable[item] = ok;
            else
                excluded[item] = list.Count == 0 ? "GBR が自動で買える売り手がいません" : string.Join(" / ", reasons.Distinct());
        }

        // NPC・店の組ごとに、まとめて買える品の数
        var cover = usable.Values.SelectMany(l => l.Select(c => (c.Npc, c.Shop)).Distinct())
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (item, ok) in usable)
        {
            if (preferredNpc != null && preferredNpc.TryGetValue(item, out var want) && ok.FirstOrDefault(c => c.Npc == want) is { } wanted)
            {
                chosen[item] = wanted;
                continue;
            }

            chosen[item] = ok
                .Select((c, i) => (c, i))
                .OrderByDescending(x => cover[(x.c.Npc, x.c.Shop)])
                .ThenBy(x => x.i)
                .First().c;
        }

        return (chosen, excluded);
    }

    /// <summary>
    /// 計画の段で、どの店でも買えないときの理由（NPC 購入を手段から外すときの文。PlanBuilder.RouteBlockers）。
    /// 店と品の条件は満たしているのに、店を開く NPC がみな現れていない店があれば、その NPC と要るクエストを出す。
    /// </summary>
    public static string BlockedReason(IReadOnlyList<VendorOffer> offers, Func<uint, bool> isComplete)
    {
        foreach (var o in offers.Where(o => o.Conditions(isComplete) && o.Npcs is { Length: > 0 }))
        {
            var hidden = o.Npcs!.FirstOrDefault(n => !n.Visible(isComplete));
            if (hidden != null && GateReason(hidden, isComplete) is { } why)
                return $"売っている NPC（{NpcName(hidden.Npc)}）は、{why}";
        }

        var q = offers.SelectMany(o => o.Quests).FirstOrDefault(q => !isComplete(q));
        return q != 0
            ? $"売っている店に、クエスト「{Unlocks.QuestName(q)}」の完了が要ります"
            : "売っている店に、確かめられない条件（アチーブメント等）が付いています";
    }

    /// <summary>NPC の名前（ENpcResident。読めなければ番号）。</summary>
    public static string NpcName(uint npc)
        => Svc.Data.GetExcelSheet<ENpcResident>().TryGetRow(npc, out var r) && r.Singular.ExtractText() is { Length: > 0 } n ? n : $"NPC {npc}";
}
