using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;

namespace AutoJobQuest.Automation;

// ここにあるのは、各作業の「判断」のうち、ゲームに触らない部分だけ。
// 作業（〇〇Task）はゲームから値を読んでここに渡し、結果に従ってゲームを操作する。
// ゲームを起動せずに試せるように分けてある（検証の仕組み：AutoJobQuest.Tests）。

/// <summary>収集品の納品で、選んだ品が選ばれたと言えるか。</summary>
public static class CollectableSelection
{
    public enum Verdict
    {
        /// <summary>撃ってよい。</summary>
        Fire,

        /// <summary>選べているが、納品ボタンが押せるようになるのを待つ。</summary>
        WaitButton,

        /// <summary>まだ選べたと言えない（待つ。上限を過ぎたら呼び出し側が止める）。</summary>
        Wait,
    }

    /// <summary>
    /// 右の一覧の行数＝目的の品の所持数、のうえで、次のどれかが言えるときだけ撃つ：
    ///  ①同じ所持数の別の収集品が一覧に無い（行数で区別できる）
    ///  ②選ぶ前の行数から、目的の品の所持数へ変わったのを見た（選択が効いた）
    ///  ③この画面で前に確かめた選択が同じ行のまま（自分の納品で数が1つ減っただけ）
    /// 納品ボタンが押せるだけでは撃たない。
    /// </summary>
    /// <param name="rows">いまの右の一覧の行数（読めなければ null）。</param>
    /// <param name="ownedBefore">目的の品の所持数（選ぶ前）。</param>
    /// <param name="rowsBeforeSelect">選ぶ前の右の一覧の行数（読めなければ -1）。</param>
    /// <param name="ambiguous">同じ所持数の別の収集品が一覧にあるか。</param>
    /// <param name="sameAsConfirmed">この画面で前に確かめた選択と同じ行か。</param>
    /// <param name="buttonReady">納品ボタンが見えていて押せるか。</param>
    public static Verdict Decide(int? rows, int ownedBefore, int rowsBeforeSelect, bool ambiguous, bool sameAsConfirmed, bool buttonReady)
    {
        if (rows is not { } r || r <= 0 || r != ownedBefore)
            return Verdict.Wait;

        var changed = rowsBeforeSelect >= 0 && rowsBeforeSelect != r;
        if (ambiguous && !changed && !sameAsConfirmed)
            return Verdict.Wait;

        return buttonReady ? Verdict.Fire : Verdict.WaitButton;
    }
}

/// <summary>確認窓の本文の照合。</summary>
public static class TextMatch
{
    /// <summary>
    /// 本文に、その数（3桁区切りでも可）が「前後が数字でない」形で含まれるか。
    /// 「100」が「1000」「2,100」の一部に当たらないようにする。
    /// </summary>
    public static bool ContainsNumber(string body, uint value)
    {
        foreach (var text in new[] { value.ToString(), value.ToString("N0") }.Distinct())
        {
            var at = 0;
            while ((at = body.IndexOf(text, at, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = at == 0 || !IsNumberChar(body[at - 1]);
                var end = at + text.Length;
                var afterOk = end >= body.Length || !IsNumberChar(body[end]);
                if (beforeOk && afterOk)
                    return true;
                at = end;
            }
        }

        return false;
    }

    private static bool IsNumberChar(char c) => char.IsDigit(c) || c == ',' || c == '，';
}

/// <summary>秘伝書のための紫貨と収集品の計算。</summary>
public static class BookMath
{
    /// <summary>
    /// 要る収集品の数＝ceil(足りない紫貨 ÷ 1個の最低報酬)。足りていれば 0。報酬が読めなければ 0（下準備で止めている）。
    /// </summary>
    public static int CollectablesNeeded(int totalPrice, int scrips, int rewardLow)
    {
        var need = Math.Max(0, totalPrice - scrips);
        return rewardLow > 0 ? (int)Math.Ceiling(need / (double)rewardLow) : 0;
    }

    /// <summary>納品を続けるか：交換に要る紫貨（target）に届いていなければ続ける。</summary>
    public static bool ShouldDeliver(int scrips, int target) => scrips < target;
}

/// <summary>
/// 周回の数え方：「進まなかった周回」だけを数える。
/// 残りの製作回数が前の周回より減ったら進んだとみなして 0 に戻す。進まない周回が上限に届いたら止める。
/// </summary>
public sealed class ProgressRounds
{
    private int last = int.MaxValue;

    /// <summary>進まなかった周回の数。</summary>
    public int Stalled { get; private set; }

    /// <summary>
    /// この周回の「残り」を記録する。進んだら true（呼び出し側は素材集めの周回も数え直す）。
    /// </summary>
    public bool Observe(int remaining)
    {
        var progressed = remaining < this.last;
        this.Stalled = progressed ? 0 : this.Stalled + 1;
        this.last = remaining;
        return progressed;
    }

    /// <summary>進まない周回が上限に届いたか。</summary>
    public bool Exceeded(int limit) => this.Stalled >= limit;
}

/// <summary>マーケットで送る直前の確認。</summary>
public static class PurchaseGuard
{
    public enum Verdict
    {
        /// <summary>買う。</summary>
        Buy,

        /// <summary>もう足りている（買わずに次の品へ）。</summary>
        AlreadyEnough,

        /// <summary>不足数が変わった（出品を選び直す）。</summary>
        Reselect,

        /// <summary>ギルが足りない。</summary>
        NotEnoughGil,
    }

    /// <param name="needNow">いまの不足数（カバンを数え直したもの）。</param>
    /// <param name="needAtDecide">出品を選んだときの不足数。</param>
    /// <param name="gil">いまのギル。</param>
    /// <param name="total">その出品の合計金額（手数料込み）。</param>
    public static Verdict Check(int needNow, int needAtDecide, long gil, long total)
    {
        if (needNow <= 0)
            return Verdict.AlreadyEnough;
        if (needNow != needAtDecide)
            return Verdict.Reselect;
        return total > gil ? Verdict.NotEnoughGil : Verdict.Buy;
    }

    /// <summary>確認で「はい」をもらった出品と同じで、同じ額以下か（取り直した一覧で確認をやり直さない条件）。</summary>
    public static bool IsApproved(ulong listingId, long total, ulong approvedListingId, long approvedTotal)
        => approvedListingId != 0 && listingId == approvedListingId && total <= approvedTotal;
}

/// <summary>採集・購入で、始めるときに何個集めるか。</summary>
public static class AcquireMath
{
    /// <summary>持っていたい総数 − いまの所持数（0 未満なら 0）。</summary>
    public static int Additional(int targetOwned, int current) => Math.Max(0, targetOwned - current);
}

/// <summary>
/// 戦闘の停滞の見張り：最後に HP が減った時刻から数える。
/// 回復・無敵で HP が増えたときは進展にしない。
/// </summary>
public sealed class StallWatch
{
    private DateTime lastProgress;
    private uint lastHp;

    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    /// <summary>狙い始めた（または狙い直した）。</summary>
    public void Start(uint hp, DateTime now)
    {
        this.lastHp = hp;
        this.lastProgress = now;
    }

    /// <summary>いまの HP を見る。最後に減ってから <see cref="Limit"/> を過ぎていたら true（その敵は諦める）。</summary>
    public bool Observe(uint hp, DateTime now)
    {
        if (hp < this.lastHp)
            this.lastProgress = now;
        this.lastHp = hp;
        return now - this.lastProgress > Limit;
    }
}

/// <summary>製作の列の打ち切り。</summary>
public static class CraftCut
{
    /// <summary>そのレシピを予定の回数作るのに足りない材料（品・持っている数・要る数）。足りていれば空。</summary>
    public static List<(uint Item, int Have, int Need)> Lacking(IEnumerable<(uint Item, int Amount)> ingredients, int crafts, IInventoryView inv)
        => ingredients
            .Select(x => (x.Item, Have: inv.CountNq(x.Item) + inv.CountHq(x.Item), Need: x.Amount * crafts))
            .Where(x => x.Have < x.Need)
            .ToList();

    /// <summary>計画で使う職のうち、ギアセットの無いもの（「職（品）」の並び）。全部あれば null。</summary>
    public static string? MissingGearsets(CraftPlan plan, Func<uint, bool> hasGearset)
    {
        var missing = plan.Crafts
            .GroupBy(c => c.ClassJobId)
            .Where(g => !hasGearset(g.Key))
            .Select(g => $"{Jobs.Name(g.Key)}（{string.Join("・", g.Select(c => CraftPlanner.ItemName(c.ItemId)).Distinct())}）")
            .ToList();
        return missing.Count == 0 ? null : string.Join("、", missing);
    }
}
