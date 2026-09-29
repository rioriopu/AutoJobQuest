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

/// <summary>
/// 確認窓（SelectYesno）に「はい」と答えてよいか（交換・秘伝書で同じ決まりを1か所にまとめた）。
///  ・撃った（使った）後に自分の操作で開いた確認であること
///  ・危ない語（捨て・売却・ログアウト等）を含まないこと
///  ・そのうえで、本文に「この操作の確認」と言える語（と値段）があるか、撃ってから <see cref="FreshWindow"/> 以内のときだけ
/// </summary>
public static class ConfirmPolicy
{
    public enum Verdict
    {
        /// <summary>本文で、この操作の確認と分かった。「はい」と答える。</summary>
        PressByText,

        /// <summary>本文は想定と違うが、撃った直後に自分の操作で開いた確認なので答える（本文は記録に残す）。</summary>
        PressFresh,

        /// <summary>自分の操作で開いた確認ではない。押さない。</summary>
        NotOurs,

        /// <summary>危ない語を含む。押さない。</summary>
        Dangerous,

        /// <summary>この操作の確認と判断できない。押さない。</summary>
        Unrecognized,
    }

    /// <summary>本文が想定と違っても答えてよい、撃ってからの時間。</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(10);

    /// <summary>本文にこれが出ていたら、交換・使用の確認ではないとみなして押さない。</summary>
    public static readonly string[] DangerousWords = ["捨て", "破棄", "削除", "分解", "精製", "売却", "ログアウト", "タイトル", "トレード"];

    /// <param name="ownedSinceAction">撃った（使った）後に自分の操作で開いた確認か。</param>
    /// <param name="body">確認の本文。</param>
    /// <param name="expectedWords">本文にあれば「この操作の確認」と言える語（全部含むこと。空なら本文では判断しない）。</param>
    /// <param name="price">本文にあるべき値段（無ければ null）。</param>
    /// <param name="sinceAction">撃ってからの時間。</param>
    /// <param name="dangerous">危ない語（Dangerous のとき）。</param>
    public static Verdict Decide(bool ownedSinceAction, string body, IReadOnlyList<string> expectedWords, uint? price, TimeSpan sinceAction, out string? dangerous)
    {
        dangerous = null;
        if (!ownedSinceAction)
            return Verdict.NotOurs;

        dangerous = DangerousWord(body);
        if (dangerous != null)
            return Verdict.Dangerous;

        var byText = expectedWords.Count > 0
                     && expectedWords.All(w => w.Length > 0 && body.Contains(w, StringComparison.Ordinal))
                     && (price is not { } p || TextMatch.ContainsNumber(body, p));
        if (byText)
            return Verdict.PressByText;

        return sinceAction <= FreshWindow ? Verdict.PressFresh : Verdict.Unrecognized;
    }

    /// <summary>本文に押してはいけない語があればその語、無ければ null。</summary>
    public static string? DangerousWord(string body)
        => DangerousWords.FirstOrDefault(w => body.Contains(w, StringComparison.Ordinal));
}

/// <summary>秘伝書のための紫貨と収集品の計算。</summary>
public static class BookMath
{
    /// <summary>
    /// 納品で貯めたい紫貨＝まだ読んでいなくて、手元にも無い秘伝書の値段の合計。
    /// 読んだ・持っている秘伝書のぶんは交換しないので数えない。
    /// </summary>
    public static int ScripTarget(IEnumerable<(bool Learned, bool Owned, int Price)> books)
        => books.Where(b => !b.Learned && !b.Owned).Sum(b => b.Price);

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

    /// <summary>数え直す（作る物がいったん全部そろったとき）。</summary>
    public void Reset()
    {
        this.last = int.MaxValue;
        this.Stalled = 0;
    }
}

/// <summary>
/// 素材集めと製作の周回の上限の決まり（1か所にまとめた）。
///  ・素材集め：「続けて集めきれなかった周回」を数える。集める物が無くなった（集めきれた）ら 0 に戻す。
///    以前は成功した周回も数え、製作が進んだときしか戻さなかったので、HQ の作り直しで材料を集め直すたびに増え、
///    製作の上限（7回）より先に素材集めの上限（5回）に当たって「何度集めても足りない素材」という違う理由で止まっていた。
///  ・製作：「進まなかった周回」を数える（残りの製作回数が減らない）。進んだら 0 に戻す。作る物が全部そろったら数え直す。
/// </summary>
public sealed class RoundPolicy
{
    public enum Verdict
    {
        /// <summary>続ける。</summary>
        Proceed,

        /// <summary>続けて集めきれない周回が上限に届いた。</summary>
        AcquireExceeded,

        /// <summary>製作が進まない周回が上限に届いた。</summary>
        CraftExceeded,
    }

    private readonly ProgressRounds craft = new();

    /// <param name="acquireLimit">続けて集めきれない周回の上限（既定の設定で 5）。</param>
    /// <param name="craftLimit">製作が進まない周回の上限（既定の設定で 7）。</param>
    public RoundPolicy(int acquireLimit, int craftLimit)
    {
        this.AcquireLimit = acquireLimit;
        this.CraftLimit = craftLimit;
    }

    public int AcquireLimit { get; }

    public int CraftLimit { get; }

    /// <summary>続けて集めきれなかった周回の数（今の周回を含む）。</summary>
    public int AcquireRounds { get; private set; }

    /// <summary>製作が進まなかった周回の数。</summary>
    public int CraftStalled => this.craft.Stalled;

    /// <summary>素材集めの段に入った。集める物があるか（無ければ集めきれたので数え直す）。</summary>
    public Verdict EnterAcquire(bool somethingToCollect)
    {
        if (!somethingToCollect)
        {
            this.AcquireRounds = 0;
            return Verdict.Proceed;
        }

        return this.AcquireRounds++ >= this.AcquireLimit ? Verdict.AcquireExceeded : Verdict.Proceed;
    }

    /// <summary>製作の段で、作る物がある（残りの製作回数 remaining）。</summary>
    public Verdict EnterCraft(int remaining)
    {
        this.craft.Observe(remaining);
        return this.craft.Exceeded(this.CraftLimit) ? Verdict.CraftExceeded : Verdict.Proceed;
    }

    /// <summary>作る物が無くなった（全部そろった）。次に作り直しが要っても、そこから数え直す。</summary>
    public void CraftsDone() => this.craft.Reset();
}

/// <summary>
/// Questionable にクエストを頼む前に、Questionable が止まっているのを確かめる。
/// 前のクエストが終わった直後は、Questionable がまだ後片付け（受注・完了の後の待ち）で動いていることがある。
/// 以前はそこで「すでに動いている」と即座に止めていた。少し待って止まれば頼み、止まらなければ利用者の操作とみなして止める。
/// </summary>
public static class QuestionableIdle
{
    public enum Verdict
    {
        /// <summary>止まっている（または読めない：頼む側で失敗が分かる）。頼んでよい。</summary>
        Start,

        /// <summary>まだ動いている。待つ。</summary>
        Wait,

        /// <summary>待っても止まらない。利用者の操作を横取りしないため止める。</summary>
        Fail,
    }

    /// <summary>動いたまま待つ上限。</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <param name="running">Questionable の IsRunning（読めなければ null）。</param>
    /// <param name="waited">動いているのを最初に見てからの時間。</param>
    public static Verdict Decide(bool? running, TimeSpan waited)
        => running != true ? Verdict.Start : waited < Limit ? Verdict.Wait : Verdict.Fail;
}

/// <summary>
/// 止めた後の、計算中だった経路の見張りの判断。
/// vnavmesh の Path.Stop はたどっている経路しか止められず、計算中の経路は計算が終わると遅れて動き出す。
/// そこで止めた後しばらく見張り、動き出した経路の終点が「こちらの行き先」のときだけ止める
/// （Path.Stop は全体に効くので、他人の移動は止めない）。
/// </summary>
public static class PendingPath
{
    public enum Verdict
    {
        /// <summary>まだ計算中。見張りを続ける。</summary>
        Wait,

        /// <summary>こちらの行き先の経路が動き出した。止めて見張りを終える。</summary>
        StopAndFinish,

        /// <summary>見張りを終える（計算が終わって動いていない・他人の経路が動いている）。</summary>
        Finish,
    }

    /// <param name="following">経路をたどっている最中か（Path.IsRunning）。</param>
    /// <param name="lastWaypoint">たどっている経路の終点（読めなければ null）。</param>
    /// <param name="computing">経路の計算が進行中か（読めなければ null）。</param>
    /// <param name="destination">こちらが頼んだ行き先。</param>
    /// <param name="tolerance">終点がこの距離以内なら、こちらの経路とみなす。</param>
    public static Verdict Decide(bool following, System.Numerics.Vector3? lastWaypoint, bool? computing, System.Numerics.Vector3 destination, float tolerance)
    {
        if (following)
        {
            return lastWaypoint is { } p && System.Numerics.Vector3.Distance(p, destination) <= tolerance
                ? Verdict.StopAndFinish
                : Verdict.Finish;
        }

        return computing == false ? Verdict.Finish : Verdict.Wait;
    }
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

/// <summary>
/// マーケットで購入の要求を送った後の判断（二重購入を防ぐ）。
/// 成功は「ギルが減った AND 品が増えた」。どちらも変わらないまま 15 秒たったら買い直すが、
/// サーバーから「買えた」の通知（Dalamud の IMarketBoard.ItemPurchased）が来ていれば、反映が遅れているだけなので買い直さずに待つ。
/// </summary>
public static class PurchaseOutcome
{
    public enum Verdict
    {
        /// <summary>買えた。</summary>
        Bought,

        /// <summary>待つ。</summary>
        Wait,

        /// <summary>買えなかった（売り切れ・混雑等）。買い直してよい。</summary>
        Retry,

        /// <summary>買えたかどうか分からない。二重に買わないよう止める。</summary>
        Unknown,
    }

    /// <summary>何も変わらないとき、買い直すまで待つ時間。</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    /// <summary>「買えた」の通知が来たのに所持が変わらないとき、待つ上限。</summary>
    public static readonly TimeSpan ConfirmedLimit = TimeSpan.FromSeconds(60);

    /// <param name="gilBefore">送る前のギル。</param>
    /// <param name="gil">いまのギル。</param>
    /// <param name="countBefore">送る前の所持数。</param>
    /// <param name="count">いまの所持数。</param>
    /// <param name="serverConfirmed">送った後に、その品の「買えた」の通知が来たか。</param>
    /// <param name="waited">送ってからの時間。</param>
    public static Verdict Decide(long gilBefore, long gil, int countBefore, int count, bool serverConfirmed, TimeSpan waited)
    {
        if (gil < gilBefore && count > countBefore)
            return Verdict.Bought;
        if (waited < RetryAfter)
            return Verdict.Wait;

        // 片方だけ変わった＝買えたかどうか分からない
        if (gil != gilBefore || count != countBefore)
            return Verdict.Unknown;

        // 買えた通知が来ている＝反映が遅れているだけ。待つ（上限を過ぎたら、買い直さずに止める）
        if (serverConfirmed)
            return waited < ConfirmedLimit ? Verdict.Wait : Verdict.Unknown;

        return Verdict.Retry;
    }
}

/// <summary>採集・購入で、始めるときに何個集めるか。</summary>
public static class AcquireMath
{
    /// <summary>持っていたい総数 − いまの所持数（0 未満なら 0）。</summary>
    public static int Additional(int targetOwned, int current) => Math.Max(0, targetOwned - current);

    /// <summary>
    /// GBR に渡す目標。GBR は自分の数え方（GBR の所持数）で「何個になるまで」と受け取るので、
    /// 「GBR の数え方での今の数 ＋ こちらの数え方で足りない数」を渡す（数え方の違いで取りすぎ・取り足りないを防ぐ）。
    /// </summary>
    /// <param name="gbrCountNow">GBR の数え方での今の数。</param>
    /// <param name="targetOwned">こちらの数え方で持っていたい総数。</param>
    /// <param name="ourCountNow">こちらの数え方での今の数。</param>
    /// <param name="extraFromNow">総数ではなく「今から何個」で頼むとき（0 なら targetOwned を使う）。</param>
    /// <returns>足す数（0 なら頼まない）と、GBR に渡す目標。</returns>
    public static (int Add, int Target) GbrTarget(int gbrCountNow, int targetOwned, int ourCountNow, int extraFromNow = 0)
    {
        var add = extraFromNow > 0 ? extraFromNow : Additional(targetOwned, ourCountNow);
        return (add, gbrCountNow + add);
    }
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

    /// <summary>
    /// 待たされていた時間を数えない（ショップ等の画面が閉じるのを待った後に呼ぶ）。HP はそのまま、時計だけ今から数え直す
    /// （以前は画面を待った時間も「HP が減らない時間」に入り、閉じた直後に敵を諦めていた）。
    /// </summary>
    public void Resume(DateTime now) => this.lastProgress = now;

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
