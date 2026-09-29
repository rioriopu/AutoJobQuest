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
///  ・本文に「この操作の確認」と言える語（交換なら通貨名と値段、秘伝書なら本の名前）があること
/// 以前は本文が想定と違っても、撃ってから10秒以内なら押していた（PressFresh）。
/// 「操作の後に開いた」は時刻の前後にすぎず、その操作が開かせた証明にならない（交換・本の使用の直後に別の確認窓が開くと、
/// 危ない語を含まないだけで押しえた）。いまは本文で分かるときだけ押し、分からない確認は押さずに止める（未知の文章は許可しない）。
/// 交換の確認の文面はゲームデータの Addon#8894「〈品〉×〈数〉を／〈品〉×〈数〉と／交換しますか？」（通貨名と値段が入る）。
/// 秘伝書を使うときの確認の文面は、ゲームデータの Addon・LogMessage に見当たらない（ふつうは出ないと見込む。実機で確かめる項目）。
/// </summary>
public static class ConfirmPolicy
{
    public enum Verdict
    {
        /// <summary>本文で、この操作の確認と分かった。「はい」と答える。</summary>
        PressByText,

        /// <summary>自分の操作で開いた確認ではない。押さない（触らない）。</summary>
        NotOurs,

        /// <summary>危ない語を含む。押さない。</summary>
        Dangerous,

        /// <summary>本文がまだ読めない（空）。押さずに待つ。</summary>
        Unreadable,

        /// <summary>この操作の確認と判断できない。押さない（止める）。</summary>
        Unrecognized,
    }

    /// <summary>本文にこれが出ていたら、交換・使用の確認ではないとみなして押さない。</summary>
    public static readonly string[] DangerousWords = ["捨て", "破棄", "削除", "分解", "精製", "売却", "ログアウト", "タイトル", "トレード"];

    /// <param name="ownedSinceAction">撃った（使った）後に自分の操作で開いた確認か。</param>
    /// <param name="body">確認の本文。</param>
    /// <param name="expectedWords">本文にあれば「この操作の確認」と言える語（全部含むこと。空なら本文で判断できない＝押さない）。</param>
    /// <param name="price">本文にあるべき値段（無ければ null）。</param>
    /// <param name="dangerous">危ない語（Dangerous のとき）。</param>
    public static Verdict Decide(bool ownedSinceAction, string body, IReadOnlyList<string> expectedWords, uint? price, out string? dangerous)
    {
        dangerous = null;
        if (!ownedSinceAction)
            return Verdict.NotOurs;

        dangerous = DangerousWord(body);
        if (dangerous != null)
            return Verdict.Dangerous;

        if (string.IsNullOrWhiteSpace(body))
            return Verdict.Unreadable;

        var byText = expectedWords.Count > 0
                     && expectedWords.All(w => w.Length > 0 && body.Contains(w, StringComparison.Ordinal))
                     && (price is not { } p || TextMatch.ContainsNumber(body, p));
        return byText ? Verdict.PressByText : Verdict.Unrecognized;
    }

    /// <summary>本文に押してはいけない語があればその語、無ければ null。</summary>
    public static string? DangerousWord(string body)
        => DangerousWords.FirstOrDefault(w => body.Contains(w, StringComparison.Ordinal));

    /// <summary>
    /// 専用の確認画面（交換の ShopExchangeItemDialog など）で、画面の文字に交換する品の名前が出ているか
    /// （専用ボタンも、読める範囲で品目を突き合わせる）。別の候補の品の名前だけが出ているなら取り違え。
    /// 空白（半角・全角）は除いて比べる。
    /// </summary>
    /// <param name="texts">画面の文字（GameUi.AllTexts）。</param>
    /// <param name="expected">交換する品の名前。</param>
    /// <param name="others">ほかの候補の品の名前（取り違えの見分けに使う）。</param>
    public static DialogMatch MatchDialog(IReadOnlyList<string> texts, string expected, IEnumerable<string> others)
    {
        static string N(string s) => s.Replace(" ", string.Empty).Replace("　", string.Empty);
        var joined = N(string.Join("\n", texts));
        if (joined.Length == 0)
            return DialogMatch.Unreadable;
        if (expected.Length > 0 && joined.Contains(N(expected), StringComparison.Ordinal))
            return DialogMatch.Match;
        return others.Any(o => o.Length > 0 && joined.Contains(N(o), StringComparison.Ordinal)) ? DialogMatch.Mismatch : DialogMatch.NotFound;
    }

    public enum DialogMatch
    {
        /// <summary>画面に交換する品の名前がある。</summary>
        Match,

        /// <summary>画面の文字がまだ読めない。</summary>
        Unreadable,

        /// <summary>画面の文字は読めたが、交換する品の名前が無い。</summary>
        NotFound,

        /// <summary>別の候補の品の名前が出ている（取り違え）。</summary>
        Mismatch,
    }
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

/// <summary>
/// クエストの今の段から、納品物がまだ要るかを決める（途中の段で渡すクエストは 120本中47本。
/// 渡した後に止めて再開すると、以前は同じ品をもう一度作っていた）。
/// 段は QuestManager.GetQuestSequence（受けていなければ 0）。品を使う段はクエスト一覧（JobQuest.FirstItemSeq・LastItemSeq）。
/// </summary>
public static class QuestItemStage
{
    public enum Stage
    {
        /// <summary>まだ品を使っていない（受けていない・品を使う段の前か、その段の途中）。全部要る。</summary>
        All,

        /// <summary>品を使う段の途中まで進んだ（段が2つ以上あるクエストで、先の段を終えた）。どの品を使い終えたかは段だけでは分からないので、手持ちで進める（作り足さない）。</summary>
        HeldOnly,

        /// <summary>品を使う最後の段を過ぎた（渡し終えた）。もう要らない。</summary>
        None,
    }

    public static Stage Decide(byte currentSeq, byte firstItemSeq, byte lastItemSeq)
    {
        if (currentSeq == 0 || currentSeq <= firstItemSeq)
            return Stage.All;
        return currentSeq > lastItemSeq ? Stage.None : Stage.HeldOnly;
    }

    /// <summary>
    /// 計画に入れる納品物。All ならそのまま、HeldOnly なら「手持ちの数まで」（作り足さない）、None なら空。
    /// </summary>
    /// <param name="held">手持ちの数（品、HQ だけ数えるか）。</param>
    public static List<QuestItemReq> StillNeeded(IReadOnlyList<QuestItemReq> items, Stage stage, Func<uint, bool, int> held)
        => stage switch
        {
            Stage.None => [],
            Stage.HeldOnly => items
                .Select(r => r with { Count = Math.Min(r.Count, held(r.ItemId, r.Hq)) })
                .Where(r => r.Count > 0)
                .ToList(),
            _ => items.ToList(),
        };
}

/// <summary>
/// こちらの会話ではない会話の窓（Talk）の扱い（表示されている会話の窓は、状況を確かめてから原則として閉じる）。
/// 以前は送らずに待ち、消えなければ止まっていた。
/// </summary>
public static class ForeignTalkPolicy
{
    public enum Verdict
    {
        /// <summary>Questionable が動いている（その会話は Questionable の進行の一部）。触らない。</summary>
        LeaveToQuestionable,

        /// <summary>出てすぐ。ほかの操作が送って消えるかを少し見る。</summary>
        Watch,

        /// <summary>TextAdvance をほかのプラグインが動かしている（そちらが送るはず）。少し待つ。</summary>
        WaitOthers,

        /// <summary>閉じる（会話を送る）。</summary>
        Close,

        /// <summary>送り続けても消えない。止める。</summary>
        GiveUp,
    }

    /// <summary>ほかの操作が送って消えるかを見る時間。</summary>
    public static readonly TimeSpan WatchTime = TimeSpan.FromSeconds(2);

    /// <summary>TextAdvance をほかのプラグインが動かしているとき、そちらに任せて待つ上限。過ぎたらこちらで閉じる。</summary>
    public static readonly TimeSpan OthersLimit = TimeSpan.FromSeconds(10);

    /// <summary>閉じ始めてから消えるまでの上限。</summary>
    public static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(30);

    /// <param name="questionableRunning">Questionable が動いているか。</param>
    /// <param name="othersDriveTextAdvance">TextAdvance をこちら以外が外部制御しているか。</param>
    /// <param name="seen">会話の窓を最初に見てからの時間。</param>
    /// <param name="closing">閉じ始めてからの時間（まだなら null）。</param>
    public static Verdict Decide(bool questionableRunning, bool othersDriveTextAdvance, TimeSpan seen, TimeSpan? closing)
    {
        if (questionableRunning)
            return Verdict.LeaveToQuestionable;
        if (closing is { } c)
            return c > CloseLimit ? Verdict.GiveUp : Verdict.Close;
        if (seen < WatchTime)
            return Verdict.Watch;
        if (othersDriveTextAdvance && seen < OthersLimit)
            return Verdict.WaitOthers;
        return Verdict.Close;
    }
}

/// <summary>
/// 攻撃されたときの反撃の決まり（RSR は、使い終わったときに途中で Off になっていたら戻さないのが基本。
/// ただし敵に攻撃されていることを検知したら、一時的に ON にする）。
/// </summary>
public static class DefensePolicy
{
    public enum Verdict
    {
        /// <summary>攻撃されていない・ほかが戦っている（戦闘の作業・Questionable）。何もしない（反撃中なら終える）。</summary>
        None,

        /// <summary>攻撃されているが、今のジョブ（製作職・採集職）では戦えない、または RSR が無い。戦闘が解けるのを待つ。</summary>
        CannotFight,

        /// <summary>攻撃されている。RSR を一時的に Henched にして、その敵を狙う。</summary>
        Defend,
    }

    /// <param name="inCombat">戦闘状態か。</param>
    /// <param name="combatTaskActive">戦闘の作業（CombatTask）の最中か（そちらが敵視リストの敵も倒す）。</param>
    /// <param name="questionableRunning">Questionable が動いているか（Questionable が自分で戦う）。</param>
    /// <param name="attacker">自分と戦闘状態の敵がいるか（敵視リスト・自分を狙っている敵）。</param>
    /// <param name="combatJob">今のジョブが戦闘ジョブか。</param>
    /// <param name="rsrLoaded">RSR が読み込まれているか。</param>
    public static Verdict Decide(bool inCombat, bool combatTaskActive, bool questionableRunning, bool attacker, bool combatJob, bool rsrLoaded)
    {
        if (!inCombat || combatTaskActive || questionableRunning || !attacker)
            return Verdict.None;
        return combatJob && rsrLoaded ? Verdict.Defend : Verdict.CannotFight;
    }
}

/// <summary>
/// 外部の処理を始める直前に、要るものがそろっているか（計画を立て直した後や、途中でプラグインを外した・設定を変えた
/// 場合にも、始める直前にもう一度確かめる。以前は釣りの同意だけを見ていて、AutoHook と GBR の UseAutoHook は開始時にしか見なかった）。
/// 同意や他のプラグインの設定は、こちらからは変えない。足りなければ理由を返す（空ならそろっている）。
/// </summary>
public static class RequiredCapabilities
{
    /// <summary>釣り（GBR ＋ AutoHook）。</summary>
    /// <param name="optIn">GBR の「Opt-in to fishing data collection」（読めなければ null）。</param>
    /// <param name="autoHookLoaded">AutoHook が読み込まれているか。</param>
    /// <param name="useAutoHook">GBR の UseAutoHook（読めなければ null）。</param>
    public static List<string> Fishing(bool? optIn, bool autoHookLoaded, bool? useAutoHook)
    {
        var list = new List<string>();
        if (optIn != true)
            list.Add(optIn == false
                ? "GBR の「Opt-in to fishing data collection」が OFF のため GBR は釣りをしません（釣果を外部へ送る同意なので、こちらからは変えません。GBR の設定画面の検索欄に「fishing data」と入れると項目が出ます）"
                : "GBR の「Opt-in to fishing data collection」の設定を読めませんでした（GBR の版が変わった可能性。記録の IPC 欄を見てください）");
        if (!autoHookLoaded)
            list.Add("AutoHook が読み込まれていません（釣りに使います）");
        if (useAutoHook != true)
            list.Add(useAutoHook == false
                ? "GBR の UseAutoHook が OFF のため釣りが始まりません"
                : "GBR の UseAutoHook の設定を読めませんでした");
        return list;
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

    /// <summary>
    /// 買う前に利用者へ確かめる理由（無ければ空）。誤ってギルを大量に使わないための確認。
    ///  1) 1回の購入額が基準を超える（基準 0 なら確かめない）。
    ///  2) 単価が、最近の取引の単価の中央値の ratio 倍を超える（相場から外れた高値。ratio 0 か取引履歴が無ければ確かめない）。
    ///  3) この実行の合計が、了承済みの額を超える（任意の上限。上限 0 なら確かめない）。
    /// 1)・2) は出品ごとの了承（同じ出品・同じ額以下）で通す。3) は実行の合計なので、出品の了承では通さない。
    /// </summary>
    /// <param name="total">この出品の合計（手数料込み）。</param>
    /// <param name="unitPrice">この出品の単価。</param>
    /// <param name="perPurchaseLimit">1回の購入額の基準（0 なら確かめない）。</param>
    /// <param name="listingApproved">この出品に了承をもらっているか（<see cref="IsApproved"/>）。</param>
    /// <param name="marketUnit">最近の取引の単価の中央値（取引履歴が無ければ null）。</param>
    /// <param name="ratio">相場の何倍を超えたら確かめるか（0 なら確かめない）。</param>
    /// <param name="spentThisRun">この実行でマーケットに払った合計。</param>
    /// <param name="runApprovedUpTo">この実行の合計を、いくらまで了承済みか（任意の上限を使わないなら 0）。</param>
    public static List<string> ConfirmReasons(long total, uint unitPrice, long perPurchaseLimit, bool listingApproved,
        double? marketUnit, double ratio, long spentThisRun, long runApprovedUpTo)
    {
        var reasons = new List<string>();
        if (!listingApproved && perPurchaseLimit > 0 && total > perPurchaseLimit)
            reasons.Add($"1回の購入額が {perPurchaseLimit:N0} ギルを超えています");
        if (!listingApproved && ratio > 0 && marketUnit is { } m && m > 0 && unitPrice > m * ratio)
            reasons.Add($"単価 {unitPrice:N0} ギルが、最近の取引の単価の中央値 {m:N0} ギルの {ratio:0.#} 倍を超えています（相場から外れた高値の可能性）");
        if (runApprovedUpTo > 0 && spentThisRun + total > runApprovedUpTo)
            reasons.Add($"この実行でマーケットに払う合計が {runApprovedUpTo:N0} ギルを超えます（これまで {spentThisRun:N0} ギル＋今回 {total:N0} ギル）");
        return reasons;
    }

    /// <summary>取引履歴の単価の中央値（履歴が無ければ null）。</summary>
    public static double? MedianUnitPrice(IReadOnlyCollection<uint> unitPrices)
    {
        if (unitPrices.Count == 0)
            return null;
        var sorted = unitPrices.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + (double)sorted[mid]) / 2;
    }
}

/// <summary>
/// マーケットで購入の要求を送った後の判断（二重購入を防ぐ）。
/// 成功は「ギルが減った AND 品が増えた」。
/// 【買い直すのは、はっきり断られたときだけ】通知が来ないことは「買えなかった」の証明にならない（通知の取りこぼし・反映の遅れでも
/// 同じに見える）。以前は「15秒たっても何も変わらず通知も無い」を買い直しにしていたので、反映が遅いと1出品ぶん多く買いえた。
/// いまは、ゲームが購入の応答で「断った」と返し（InfoProxyItemSearch.ProcessPurchaseResponse の errorMessageId が 0 でない）、
/// しかも少し待ってもギルも所持も変わらないときだけ買い直す。それ以外で何も変わらないまま上限まで待ったら「分からない」として止める。
/// </summary>
public static class PurchaseOutcome
{
    public enum Verdict
    {
        /// <summary>買えた。</summary>
        Bought,

        /// <summary>待つ。</summary>
        Wait,

        /// <summary>はっきり断られた（売り切れ等）。必要数を数え直してから買い直してよい。</summary>
        Retry,

        /// <summary>買えたかどうか分からない。二重に買わないよう止める。</summary>
        Unknown,
    }

    /// <summary>片方だけ変わったとき、もう片方の反映を待つ時間。</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    /// <summary>断られたと返ってきたあと、ギルも所持も変わらないことを確かめる時間（反映の途中で買い直さない）。</summary>
    public static readonly TimeSpan RejectSettle = TimeSpan.FromSeconds(3);

    /// <summary>何も変わらないとき、待つ上限（過ぎたら「分からない」として止める）。</summary>
    public static readonly TimeSpan ConfirmedLimit = TimeSpan.FromSeconds(60);

    /// <param name="gilBefore">送る前のギル。</param>
    /// <param name="gil">いまのギル。</param>
    /// <param name="countBefore">送る前の所持数。</param>
    /// <param name="count">いまの所持数。</param>
    /// <param name="serverConfirmed">送った後に、その品の「買えた」の通知が来たか。</param>
    /// <param name="rejected">送った後に、その品の購入の応答で「断った」と返ってきたか。</param>
    /// <param name="waited">送ってからの時間。</param>
    public static Verdict Decide(long gilBefore, long gil, int countBefore, int count, bool serverConfirmed, bool rejected, TimeSpan waited)
    {
        if (gil < gilBefore && count > countBefore)
            return Verdict.Bought;

        // 片方だけ変わった＝もう片方の反映を少し待つ。それでもそろわなければ、買えたかどうか分からない
        if (gil != gilBefore || count != countBefore)
            return waited < RetryAfter ? Verdict.Wait : Verdict.Unknown;

        // 何も変わっていない。はっきり断られていて（「買えた」の通知とは食い違っていない）、少し待っても変わらなければ買い直してよい
        if (rejected && !serverConfirmed)
            return waited < RejectSettle ? Verdict.Wait : Verdict.Retry;

        // 断られたと分からない（通知の有無にかかわらず）。上限まで待って、変わらなければ買い直さずに止める
        return waited < ConfirmedLimit ? Verdict.Wait : Verdict.Unknown;
    }
}

/// <summary>
/// HQ 指定の品が HQ にならなかった回数を、品目ごとに数える（以前は製作の残り回数の合計だけで
/// 「進まない周回」を数えていたので、ほかの品が進んでいる間は、同じ品の HQ 失敗の繰り返しが見えなかった）。
/// 上限は設定の「HQ ができなかったとき何回まで作り直すか」（MaxRetryRounds）。
/// </summary>
public sealed class HqFailureTally
{
    private readonly Dictionary<uint, int> counts = [];

    public HqFailureTally(int limit)
    {
        this.Limit = Math.Max(1, limit);
    }

    public int Limit { get; }

    /// <summary>その品の HQ 失敗の回数。</summary>
    public int Count(uint item) => this.counts.GetValueOrDefault(item);

    /// <summary>1回の製作の結果を記録する。HQ が足りなかった回数が上限に届いたら true。</summary>
    /// <param name="item">品。</param>
    /// <param name="wantHq">HQ 指定か。</param>
    /// <param name="expected">作るはずだった数。</param>
    /// <param name="madeHq">HQ でできた数。</param>
    public bool Record(uint item, bool wantHq, int expected, int madeHq)
    {
        if (!wantHq || madeHq >= expected)
            return false;
        var n = this.counts[item] = this.counts.GetValueOrDefault(item) + 1;
        return n >= this.Limit;
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
