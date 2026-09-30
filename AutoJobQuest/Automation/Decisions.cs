using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>まだ選べたと言えない（待つ。上限を過ぎたら呼び出し側が止める）。</summary>
        Wait,
    }

    /// <summary>
    /// 右の一覧の行数＝目的の品の所持数、のうえで、次のどれかが言えるときだけ撃つ：
    ///  ①同じ所持数の別の収集品が一覧に無い（行数で区別できる）
    ///  ②選ぶ前の行数から、目的の品の所持数へ変わったのを見た（選択が効いた）
    ///  ③この画面で前に確かめた選択が同じ行のまま（自分の納品で数が1つ減っただけ）
    ///  ④右の一覧の行の品名が、目的の品と一致した（品名が読めたとき。読めて違う品なら、ほかの条件がそろっても撃たない）
    /// 【納品ボタンを待たない】以前は納品ボタン（node 51）が見えて押せるまで待っていた。実機記録では、
    /// 選択が効いて右の一覧が変わってもボタンが出ないことがあり、ボタンを待たずに Fire(15, 0u) を撃つ方式に
    /// 変えている（画面定義でも node 51 の初期状態は見えない）。こちらも選択を確かめたら撃つ。
    /// </summary>
    /// <param name="rows">いまの右の一覧の行数（読めなければ null）。</param>
    /// <param name="ownedBefore">目的の品の所持数（選ぶ前）。</param>
    /// <param name="rowsBeforeSelect">選ぶ前の右の一覧の行数（読めなければ -1）。</param>
    /// <param name="ambiguous">同じ所持数の別の収集品が一覧にあるか。</param>
    /// <param name="sameAsConfirmed">この画面で前に確かめた選択と同じ行か。</param>
    /// <param name="nameMatches">右の一覧の行の品名が目的の品と一致したか（読めなければ null）。</param>
    public static Verdict Decide(int? rows, int ownedBefore, int rowsBeforeSelect, bool ambiguous, bool sameAsConfirmed, bool? nameMatches)
    {
        if (rows is not { } r || r <= 0 || r != ownedBefore)
            return Verdict.Wait;
        if (nameMatches == false)
            return Verdict.Wait;

        var changed = rowsBeforeSelect >= 0 && rowsBeforeSelect != r;
        if (ambiguous && !changed && !sameAsConfirmed && nameMatches != true)
            return Verdict.Wait;

        return Verdict.Fire;
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

        /// <summary>
        /// 品を使う段の途中まで進んだ（先の段を終えた・同じ段の一部の相手に渡した）。残りの相手の品を用意する（<see cref="QuestItemNeeds"/>）。
        /// 相手ごとの品が分からないクエストでは、段だけではどの品を使い終えたか分からないので、手持ちで進める（作り足さない）。
        /// </summary>
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
/// クエストの今の段と、相手ごとの渡し済み（日誌の✓）から、まだ要る納品物を決める。
///
/// 【以前】段だけで決めていた（QuestItemStage）。
///  ・Q65677（品を使う最初の段＝最後の段＝2、段2で2人に別々の品）：1人に渡した後に止めて再開すると「全部要る」になり、渡した品を作り直すか
///    「0/1 しかありません」で止まった。
///  ・品を使う段の途中は「手持ちの数まで」（作り足さない）だったので、売った・捨てた品があると、渡す相手の前で止まった。
/// 【いま】相手ごとの品（JobQuest.Handovers）があれば、今の段〜品を使う最後の段の相手のうち、まだ渡していない相手の品を「全部の数」要るとする
/// （無ければ作る）。同じ段に相手が2人以上いるときだけ、日誌の✓（ゲームの判定 QuestTodo.IsChecked）で渡し済みかを見る
/// （相手が1人の段は、その相手に渡せば段が進むので、今の段にいる＝まだ渡していない）。✓が読めない相手の品は、今までどおり手持ちの数まで。
/// 相手ごとの品が無い・今の段の相手が見つからないときは、今までの段だけの判断のまま。
/// </summary>
public static class QuestItemNeeds
{
    /// <param name="Stage">段の区分（画面の注記・マテリア装着の判断に使う）。</param>
    /// <param name="Needed">まだ要る納品物（数は用意する数）。</param>
    /// <param name="Detail">相手ごとの状態（記録用。無ければ空）。</param>
    public sealed record Result(QuestItemStage.Stage Stage, List<QuestItemReq> Needed, string Detail);

    /// <param name="items">クエストの納品物。</param>
    /// <param name="currentSeq">今の段（受けていなければ 0）。</param>
    /// <param name="firstItemSeq">品を使う最初の段。</param>
    /// <param name="lastItemSeq">品を使う最後の段。</param>
    /// <param name="handovers">相手ごとの段・手順・品。</param>
    /// <param name="todoDone">日誌の手順に✓が付いているか（読めなければ null）。</param>
    /// <param name="held">手持ちの数（品, HQ だけ数えるか）。</param>
    public static Result Decide(IReadOnlyList<QuestItemReq> items, byte currentSeq, byte firstItemSeq, byte lastItemSeq,
        IReadOnlyList<QuestHandover> handovers, Func<byte, bool?> todoDone, Func<uint, bool, int> held)
    {
        var stage = QuestItemStage.Decide(currentSeq, firstItemSeq, lastItemSeq);
        if (stage == QuestItemStage.Stage.None)
            return new Result(stage, [], string.Empty);

        // 今の段の相手（2人以上なら、それぞれ渡し済みかを日誌の✓で見る）
        var here = handovers.Where(h => h.Seq == currentSeq).ToList();
        var done = here.ToDictionary(h => h, h => here.Count >= 2 ? todoDone(h.Todo) : false);
        var detail = here.Count >= 2
            ? string.Join("・", here.Select(h => $"手順{h.Todo}（相手 {h.Npc}）：{(done[h] switch { true => "渡し済み", false => "まだ", _ => "読めない" })}"))
            : string.Empty;

        if (stage == QuestItemStage.Stage.All)
        {
            if (currentSeq == 0 || currentSeq < firstItemSeq || !done.Values.Any(d => d == true))
                return new Result(stage, items.ToList(), detail);
            stage = QuestItemStage.Stage.HeldOnly;
        }

        // 相手ごとの品で決める。今の段の相手が見つからなければ（データが合わない）、今までの手持ちで進める判断に戻る
        var range = handovers.Where(h => h.Seq >= currentSeq && h.Seq <= lastItemSeq).ToList();
        if (range.Count == 0)
            return new Result(stage, QuestItemStage.StillNeeded(items, stage, held), detail);

        var full = new HashSet<uint>();
        var heldOnly = new HashSet<uint>();
        foreach (var h in range)
        {
            var d = h.Seq == currentSeq ? done[h] : false;
            if (d == true)
                continue;
            foreach (var it in h.Items)
                (d == null ? heldOnly : full).Add(it);
        }

        var needed = new List<QuestItemReq>();
        foreach (var r in items)
        {
            if (full.Contains(r.ItemId))
            {
                needed.Add(r);
            }
            else if (heldOnly.Contains(r.ItemId))
            {
                var n = Math.Min(r.Count, held(r.ItemId, r.Hq));
                if (n > 0)
                    needed.Add(r with { Count = n });
            }
        }

        return new Result(stage, needed, detail);
    }
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
/// 足りなければ理由を返す（空ならそろっている）。
/// GBR の UseAutoHook と釣果送信の同意は、釣りの間だけこちらで ON にする（<see cref="GbrRequiredSettings"/>。
/// 同意は、止まってしまうくらいなら ON にする。以前は「変えずに、始める前に止める」だった）ので、ここでは見ない。
/// </summary>
public static class RequiredCapabilities
{
    /// <summary>釣り（GBR ＋ AutoHook）。</summary>
    /// <param name="autoHookLoaded">AutoHook が読み込まれているか。</param>
    public static List<string> Fishing(bool autoHookLoaded)
    {
        var list = new List<string>();
        if (!autoHookLoaded)
            list.Add("AutoHook が読み込まれていません（釣りに使います）");
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
    /// <param name="excessCost">必要数を超えて買う分の額（(個数 − 必要数) × 単価。余りが無ければ 0）。</param>
    /// <param name="excessLimit">余りの額がこれを超えたら確かめる（0 なら確かめない）。</param>
    public static List<string> ConfirmReasons(long total, uint unitPrice, long perPurchaseLimit, bool listingApproved,
        double? marketUnit, double ratio, long spentThisRun, long runApprovedUpTo, long excessCost = 0, long excessLimit = 0)
    {
        var reasons = new List<string>();
        // 4) 必要数より大きいまとまりしか無く、余りの分の額が大きい（例：
        //    1個ほしいのに 99個×5,000＝495,000 ギルの出品しか無いと、1回の額の確認〔50万〕にかからずに余りごと買っていた）
        if (!listingApproved && excessLimit > 0 && excessCost > excessLimit)
            reasons.Add($"必要な数より多く買う分（余り）に {excessCost:N0} ギルかかります（{excessLimit:N0} ギルを超えています）");
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

/// <summary>購入の応答（InfoProxyItemSearch.ProcessPurchaseResponse）の種類。</summary>
public enum PurchaseResponse
{
    /// <summary>送った出品への応答がまだ来ていない。</summary>
    None,

    /// <summary>ゲームが成功と返した（errorMessageId が 0）。</summary>
    Success,

    /// <summary>ゲームが断った（errorMessageId が 0 でない＝断られた理由の LogMessage の行番号）。</summary>
    Rejected,
}

/// <summary>断られた理由ごとの扱い（文言はゲームデータの LogMessage で確認）。</summary>
public enum RejectKind
{
    /// <summary>一覧を取り直して別の出品を選ぶ（4523「販売状況が変化した」・386/390「購入ができませんでした」ほか）。</summary>
    Reselect,

    /// <summary>少し待って同じように取り直す（387/391「しばらく待ってから」）。</summary>
    WaitAndRetry,

    /// <summary>続けても変わらないので止める（388「所持品がいっぱい」・392「ギルがたりません」）。</summary>
    StopAll,

    /// <summary>その品はあきらめる（389「RARE をすでに所持」・4249「マーケットでは売買できない」）。</summary>
    GiveUpItem,

    /// <summary>その出品だけ外して選び直す（468「自分が雇ったリテイナーの出品」・4525「マネキンでセット販売」）。</summary>
    ExcludeListing,

    /// <summary>支払いが済んで品が届いていないかもしれない（460/461「入手完了できていない」）。買い直さずに、結果が分からないとして止める。</summary>
    MaybePaid,
}

/// <summary>購入の応答の読み方。</summary>
public static class PurchaseReply
{
    /// <summary>断られた理由（errorMessageId＝LogMessage の行番号）ごとの扱い。</summary>
    public static RejectKind Classify(uint errorMessageId) => errorMessageId switch
    {
        387 or 391 => RejectKind.WaitAndRetry,
        388 or 392 => RejectKind.StopAll,
        389 or 4249 => RejectKind.GiveUpItem,
        468 or 4525 => RejectKind.ExcludeListing,
        460 or 461 => RejectKind.MaybePaid,
        _ => RejectKind.Reselect,
    };

    /// <summary>
    /// 応答の品目 ID の HQ の印（＋1,000,000）を外す（ゲームの ProcessPurchaseResponse と同じ直し方：1,000,001〜1,999,999 なら −1,000,000）。
    /// </summary>
    public static uint NormalizeItem(uint itemId) => itemId is > 1_000_000 and < 2_000_000 ? itemId - 1_000_000 : itemId;

    /// <summary>
    /// 送った出品への応答を、応答の記録から探す（送った後の応答で、出品の番号が一致するもの）。
    /// 品目 ID ではなく出品の番号で照合する（HQ の品目 ID のずれ・同じ品の別の要求への応答を取り違えない）。
    /// </summary>
    public static (PurchaseResponse Response, uint Message) Find(IEnumerable<(int Serial, ulong ListingId, uint Message)> log, int serialBefore, ulong listingId)
    {
        foreach (var r in log)
        {
            if (r.Serial <= serialBefore || r.ListingId != listingId)
                continue;
            return (r.Message == 0 ? PurchaseResponse.Success : PurchaseResponse.Rejected, r.Message);
        }

        return (PurchaseResponse.None, 0);
    }
}

/// <summary>
/// 出品一覧が「いま頼んだ検索の分として」そろったか。
///
/// ゲームは件数の応答を受けた時点で件数（ListingCount）を新しい値に書き換えるが、一覧の行は最初のページが届くまで前回のまま。
/// 同じ品を検索し直すと、前回の行が「その品で単価が入っている」を満たしてしまう。そこで、件数の応答で振られた番号（CurrentRequestId）と
/// Dalamud の出品一覧の通知の番号（RequestId の下位1バイト。ゲームも1バイトで比べる）が一致するページの数を数え、
/// 今回のページがそろい、ゲームが受け取った行数（EntryCount）が件数に届くまで待つ。
/// </summary>
public static class ListingReceipt
{
    public enum Verdict
    {
        /// <summary>まだ待つ。</summary>
        Wait,

        /// <summary>そろった。</summary>
        Complete,

        /// <summary>出品が0件（ゲームは一覧を空にする）。</summary>
        Empty,
    }

    /// <summary>ページの数（1ページ10件。ゲームが送る上限は100件）。</summary>
    public static int PagesFor(int count) => (Math.Min(count, 100) + 9) / 10;

    /// <param name="count">件数の応答の件数。</param>
    /// <param name="pages">今回の番号のページを受け取った数。</param>
    /// <param name="currentSeq">ゲームがいま受け付けているページの番号（CurrentRequestId）。</param>
    /// <param name="seqAtResult">件数の応答の直後に控えた番号。</param>
    /// <param name="entryCount">ゲームが受け取った行数（EntryCount。最初のページで 0 に戻り、ページごとに +10）。</param>
    /// <param name="filled">先頭から件数ぶんのうち、いま検索した品で単価が入っている行の数。</param>
    public static Verdict Decide(int count, int pages, byte currentSeq, byte seqAtResult, uint entryCount, int filled)
    {
        if (count <= 0)
            return Verdict.Empty;
        var expected = Math.Min(count, 100);
        return pages >= PagesFor(count) && currentSeq == seqAtResult && entryCount >= expected && filled >= expected
            ? Verdict.Complete
            : Verdict.Wait;
    }
}

/// <summary>
/// マーケットで購入の要求を送った後の判断（二重購入を防ぐ）。
/// 成功は「ギルが減った AND 品が増えた」。
/// 【買い直すのは、はっきり断られたときだけ】通知が来ないことは「買えなかった」の証明にならない（通知の取りこぼし・反映の遅れでも
/// 同じに見える）。以前は「15秒たっても何も変わらず通知も無い」を買い直しにしていたので、反映が遅いと1出品ぶん多く買いえた。
/// いまは、ゲームが購入の応答で「断った」と返し（InfoProxyItemSearch.ProcessPurchaseResponse の errorMessageId が 0 でない）、
/// しかも少し待ってもギルも所持も変わらないときだけ買い直す。それ以外で何も変わらないまま上限まで待ったら「分からない」として止める。
///
/// 以前は「断った」を Dalamud の「買えた」の通知（ItemPurchased）と組み合わせていたが、この通知は購入の応答の
/// パケットなら成功でも失敗でも出る（Dalamud は errorMessageId を読み捨てる：MarketBoardPurchase.cs）。そのため「断った かつ 買えた通知なし」が
/// 常に偽で、断られても毎回60秒待って「分からない」で止まっていた。いまは応答の errorMessageId だけで
/// 成功・断られたを決め、送った出品とは出品の番号で照合する（<see cref="PurchaseReply.Find"/>）。
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
    /// <param name="response">送った出品への購入の応答（<see cref="PurchaseReply.Find"/>）。</param>
    /// <param name="reject">断られたときの理由の扱い（<see cref="PurchaseReply.Classify"/>）。</param>
    /// <param name="waited">送ってからの時間。</param>
    public static Verdict Decide(long gilBefore, long gil, int countBefore, int count, PurchaseResponse response, RejectKind reject, TimeSpan waited)
    {
        if (gil < gilBefore && count > countBefore)
            return Verdict.Bought;

        // 片方だけ変わった＝もう片方の反映を少し待つ。それでもそろわなければ、買えたかどうか分からない
        if (gil != gilBefore || count != countBefore)
            return waited < RetryAfter ? Verdict.Wait : Verdict.Unknown;

        // 何も変わっていない。はっきり断られていて、少し待っても変わらなければ買い直してよい。
        // ただし「入手完了できていない」（460/461）は支払いが済んでいるかもしれないので、買い直さずに分からないとして止める
        if (response == PurchaseResponse.Rejected)
        {
            if (reject == RejectKind.MaybePaid)
                return waited < RetryAfter ? Verdict.Wait : Verdict.Unknown;
            return waited < RejectSettle ? Verdict.Wait : Verdict.Retry;
        }

        // 成功と返った（反映待ち）か、応答が無い。上限まで待って、変わらなければ買い直さずに止める
        return waited < ConfirmedLimit ? Verdict.Wait : Verdict.Unknown;
    }
}

/// <summary>
/// HQ 指定の品が HQ にならなかった回数を、品目ごとに数える（以前は製作の残り回数の合計だけで
/// 「進まない周回」を数えていたので、ほかの品が進んでいる間は、同じ品の HQ 失敗の繰り返しが見えなかった）。
/// 上限は設定の「HQ 指定の品が NQ になったとき、止めずに作り直す回数」（HqRetryRounds）＋1（既定は1回目で止める）。
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

/// <summary>
/// Artisan の連続製作（CraftItem）が予定の回数の途中で止まったときに、残りを頼み直すか。
///
/// CraftItem は Artisan の中では「連続製作（Endurance）」として動く。Artisan の設定「NQ ができたら止める」（EnduranceStopNQ）・
/// 「失敗したら止める」（EnduranceStopFail）は連続製作にだけ効き（Artisan の EnduranceCraftWatcher.cs。Crafting List には効かない）、
/// ON だと1回ごとに止まる。以前はそのたびに製作の段を立て直していた（遅いだけで止まりはしない）。
/// いまは同じ作業の中で残りを頼み直すので、この設定の ON/OFF で進み方が変わらない。
///
/// 頼み直すのは、直前の頼みで1つ以上できていて（進んでいる）、まだ予定に届かず、材料が1回分以上あるときだけ。
/// 1回もできなかった頼みの後は頼み直さない（Artisan が作れない理由があるので、今までどおり結果で判断する）。
/// 頼み直しの回数は、予定の製作回数までで打ち切る（1回の頼みで必ず1つ以上進むので、それ以上は要らない）。
/// </summary>
/// <summary>
/// 製作の失敗の数え方（ゲームを起動せずに試せるように分けた）。
/// 製作は、始めたときに材料（クリスタルを含む）が減り、終わったときに品が増える。失敗すると品は増えない。
/// だから「始めた回数（減った材料 ÷ 1回分）」と「できた回数（増えた品 ÷ 1回にできる数）」の差が、失敗の回数になる（作っている最中の1回は除く）。
/// </summary>
public static class CraftFailure
{
    /// <summary>始めた回数：材料ごとに「減った数 ÷ 1回分」を求めて、一番大きいもの。</summary>
    public static int Attempts(IEnumerable<(uint Item, int Amount)> perCraft, Func<uint, int> used)
        => perCraft.Where(x => x.Amount > 0).Select(x => Math.Max(0, used(x.Item)) / x.Amount).DefaultIfEmpty(0).Max();

    /// <summary>失敗の回数：始めた回数 − できた回数 − 作っている最中の1回（<paramref name="inProgress"/>）。</summary>
    public static int Failed(int attempts, int made, int yield, bool inProgress)
        => Math.Max(0, attempts - (Math.Max(0, made) / Math.Max(1, yield)) - (inProgress ? 1 : 0));
}

public static class CraftResume
{
    /// <summary>頼み直す製作回数。頼み直さないなら 0。</summary>
    /// <param name="made">この作業でできた数（合計）。</param>
    /// <param name="expected">この作業で作るはずの数。</param>
    /// <param name="gainedLast">直前の頼みでできた数。</param>
    /// <param name="resumed">これまでに頼み直した回数。</param>
    /// <param name="plannedCrafts">この作業の予定の製作回数。</param>
    /// <param name="craftable">いまの材料で作れる回数。</param>
    /// <param name="yield">1回でできる数。</param>
    public static int Decide(int made, int expected, int gainedLast, int resumed, int plannedCrafts, int craftable, int yield)
    {
        if (made >= expected || gainedLast <= 0 || resumed >= plannedCrafts || craftable <= 0)
            return 0;
        var remaining = (expected - made + Math.Max(1, yield) - 1) / Math.Max(1, yield);
        return Math.Min(remaining, craftable);
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

    /// <summary>いまの手持ちで、そのレシピを何回作れるか（材料ごとの「持っている数 ÷ 1回に使う数」の最小）。材料が無いレシピは int.MaxValue。</summary>
    public static int Craftable(IEnumerable<(uint Item, int Amount)> ingredients, IInventoryView inv)
    {
        var min = int.MaxValue;
        foreach (var (item, amount) in ingredients)
        {
            if (amount <= 0)
                continue;
            min = Math.Min(min, (inv.CountNq(item) + inv.CountHq(item)) / amount);
        }

        return min;
    }

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

/// <summary>
/// テレポを頼んだ後の見方（以前は「何かの詠唱中かエリア移動中」でなければ 10 秒ごとに頼み直していた）。
/// Lifestream 自身の確実なテレポも「自分がテレポ（Action 5）を詠唱中か」で成否を決め、始まらなければ間隔を空けて頼み直す
/// （Lifestream の TeleportService.ReliableTeleportToAetheryte）。詠唱が頼んだのと同じフレームで始まるかはゲームの中（ネイティブ）で読めないので、
/// 詠唱が一度も始まらないときだけ上限（NoCastLimit）で頼み直す。詠唱を見た後に途切れた（動いた・攻撃された）ときは、動けるようになった時点ですぐ頼み直す。
/// </summary>
public static class TeleportWatch
{
    public enum Verdict
    {
        /// <summary>テレポの詠唱中かエリア移動中。</summary>
        InProgress,

        /// <summary>待つ（詠唱がまだ始まらない／詠唱の後の動けない間）。</summary>
        Wait,

        /// <summary>頼み直す。</summary>
        Retry,
    }

    /// <summary>頼んでから詠唱が一度も始まらないときの上限（この間は待つ）。</summary>
    public static readonly TimeSpan NoCastLimit = TimeSpan.FromSeconds(10);

    /// <param name="castingTeleport">自分がテレポ（Action 5）を詠唱中か。</param>
    /// <param name="betweenAreas">エリア移動中か。</param>
    /// <param name="sawCast">頼んだ後にテレポの詠唱を一度でも見たか。</param>
    /// <param name="playerFree">動ける状態か（詠唱の後の動けない間・会話中などは false）。</param>
    /// <param name="teleportStatus">テレポがいま使えるか（GetActionStatus(Action, 5)：0＝使える、それ以外＝使えない理由の LogMessage の行番号）。</param>
    /// <param name="sinceRequest">頼んでからの時間。</param>
    public static Verdict Decide(bool castingTeleport, bool betweenAreas, bool sawCast, bool playerFree, uint teleportStatus, TimeSpan sinceRequest)
    {
        if (castingTeleport || betweenAreas)
            return Verdict.InProgress;

        // 詠唱を見た後：動けてテレポがまた使えるなら、詠唱が途切れた（テレポしなかった）。まだ動けない・使えないなら、詠唱が終わった後の間なので待つ
        if (sawCast)
            return playerFree && teleportStatus == 0 ? Verdict.Retry : Verdict.Wait;

        return sinceRequest < NoCastLimit ? Verdict.Wait : Verdict.Retry;
    }
}

/// <summary>
/// 出現点で湧きを待つか。湧きの時刻はサーバー側で、ゲームデータにも無いので、見えるものから決める：
/// 目当ての死体（倒されて湧き直しが近い）か、他人と戦っている個体（倒されれば湧き直す）が近くにいれば、上限まで待つ。
/// 1体も見えなければ待たない（以前は見えるものに関係なく 20 秒待っていた）。
/// </summary>
public static class SpawnWait
{
    /// <summary>待つ上限。</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <param name="sinceArrive">出現点に着いてからの時間。</param>
    /// <param name="deadNearby">近くの目当ての死体の数。</param>
    /// <param name="engagedByOthers">近くで他人と戦っている目当ての個体の数。</param>
    public static bool Wait(TimeSpan sinceArrive, int deadNearby, int engagedByOthers)
        => (deadNearby > 0 || engagedByOthers > 0) && sinceArrive < Limit;
}

/// <summary>
/// ギアセットでの着替えの頼み直し（以前は頼んでから 3 秒たっても変わらなければ頼み直していた）。
/// EquipGearset は「受け付けた 0／断った -1」を返す（FFXIVClientStructs の RaptureGearsetModule）。
/// 受け付けたら、ジョブが変わるのを長い上限まで待つだけ（頼み直さない）。断られたら、同じフレームで何度も頼まないよう間隔を空けて頼み直す。
/// </summary>
public static class EquipRetry
{
    public enum Verdict
    {
        /// <summary>待つ。</summary>
        Wait,

        /// <summary>頼む（頼み直す）。</summary>
        Request,

        /// <summary>頼める回数を使い切った。</summary>
        Fail,
    }

    /// <summary>受け付けた（0）のにジョブが変わらないときの上限（サーバーの応答が届かなかった場合に備える）。</summary>
    public static readonly TimeSpan AcceptedLimit = TimeSpan.FromSeconds(10);

    /// <summary>断られた（-1）あとの頼み直しの間隔（同じフレームで何度も頼まない）。</summary>
    public static readonly TimeSpan RefusedSpacing = TimeSpan.FromSeconds(1);

    /// <summary>頼む回数の上限。</summary>
    public const int MaxRequests = 5;

    /// <param name="lastResult">前に頼んだときの戻り値（まだ頼んでいなければ null）。</param>
    /// <param name="sinceRequest">前に頼んでからの時間。</param>
    /// <param name="requests">これまでに頼んだ回数。</param>
    public static Verdict Decide(int? lastResult, TimeSpan sinceRequest, int requests)
    {
        if (lastResult is { } r && sinceRequest < (r == 0 ? AcceptedLimit : RefusedSpacing))
            return Verdict.Wait;

        return requests >= MaxRequests ? Verdict.Fail : Verdict.Request;
    }
}

/// <summary>
/// Lifestream に宿屋への移動を頼んだ直後の見方（以前は IsBusy が立つのを 5 秒待っていた）。
/// Lifestream の受け付けは同じフレームで終わる：忙しければエラーを出して戻る・プレイヤーが無ければ戻る、それ以外はその場で作業を積むので
/// IsBusy（作業が積まれている）がすぐ true になる（Lifestream の TaskPropertyShortcut.Enqueue・ECommons の TaskManager.IsBusy）。
/// </summary>
public static class InnRequest
{
    public enum Verdict
    {
        /// <summary>受け付けた（Lifestream が動き出した）。</summary>
        Accepted,

        /// <summary>受け付けなかった。</summary>
        Refused,

        /// <summary>状態を読めない（受け付けたか分からない。頼み直すと二重になるので、読めるまで待つ）。</summary>
        Unknown,
    }

    /// <param name="busyAfter">頼んだ直後の Lifestream の IsBusy（読めなければ null）。</param>
    public static Verdict AfterRequest(bool? busyAfter) => busyAfter switch
    {
        true => Verdict.Accepted,
        false => Verdict.Refused,
        null => Verdict.Unknown,
    };
}

/// <summary>
/// Questionable の「アイテムの購入」の手順で出る購入の確認（SelectYesno。ゲームデータ Addon#3406
/// 「〈品〉×〈数〉を、〈値段〉で購入します。よろしいですか？」）に、こちらで「OK」と答えるか。
///
/// 不具合の例：漁師ジョブクエのエサ（ピルバグ×99）の購入の確認を誰も押さず止まった。
/// Questionable は自分で押す作り（YesNoChoiceHandler：購入を頼んだ後に「確認待ち」の旗を立て、本文を Addon#3406 から作った正規表現で照合）だが、
/// 日本語の文面は固定の部分にギルの記号（私用領域の文字 U+E049）を含み、Questionable が読む本文と合わずに照合が外れる見込み
/// （記録では購入を頼んだ 0.1 秒後に確認が開き、Questionable の処理は購入の分岐を通らなかった）。
/// こちらはクエストの間 YesAlready を止めているので、YesAlready も押さない。そこでこちらで押す。
///
/// 押すのは全部そろったときだけ：Questionable の今の手順が購入・店（Shop）が開いている・本文が購入の確認の文面に合う・
/// 品がそのクエストの購入の手順の品・数がその手順の数以下・値段が設定の「1回の購入額の確認」の額以下・危ない語を含まない。
/// 文面の照合は、私用領域の文字と空白を除いてから行う（ギルの記号・改行の違いに左右されない）。
/// </summary>
public static class QuestPurchaseConfirm
{
    public enum Verdict
    {
        /// <summary>押す。</summary>
        Press,

        /// <summary>Questionable の今の手順が購入ではない。</summary>
        NotPurchaseStep,

        /// <summary>店が開いていない。</summary>
        NoShop,

        /// <summary>本文がまだ読めない。</summary>
        Unreadable,

        /// <summary>購入の確認の文面ではない。</summary>
        NotPurchase,

        /// <summary>そのクエストの購入の手順の品ではない。</summary>
        UnknownItem,

        /// <summary>手順の数より多く買おうとしている。</summary>
        TooMany,

        /// <summary>値段が設定の額を超える。</summary>
        TooExpensive,

        /// <summary>危ない語を含む。</summary>
        Dangerous,
    }

    /// <summary>文面の部品（固定の文字か、差し込み）。差し込みの種類は item（品）・count（数）・price（値段）・br（改行）・空（そのほか）。</summary>
    public sealed record Part(bool IsText, string Text, string Kind);

    /// <summary>照合の形と、品・数・値段のグループ番号。</summary>
    public sealed record Pattern(Regex Regex, int ItemGroup, int CountGroup, int PriceGroup);

    /// <summary>私用領域の文字（ギルの記号など）と空白・改行を除く。</summary>
    public static string Normalize(string s)
        => new(s.Where(c => !char.IsWhiteSpace(c) && c is not (>= '\uE000' and <= '\uF8FF')).ToArray());

    /// <summary>文面の部品から照合の形を作る。品・数・値段の差し込みがそろっていなければ null。</summary>
    public static Pattern? Build(IReadOnlyList<Part> parts)
    {
        var sb = new System.Text.StringBuilder("^");
        int group = 0, item = 0, count = 0, price = 0;
        foreach (var p in parts)
        {
            if (p.IsText)
            {
                sb.Append(Regex.Escape(Normalize(p.Text)));
                continue;
            }

            switch (p.Kind)
            {
                case "item":
                    item = ++group;
                    sb.Append("(.+?)");
                    break;
                case "count":
                    count = ++group;
                    sb.Append("([0-9,，]+)");
                    break;
                case "price":
                    price = ++group;
                    sb.Append("([0-9,，]+)");
                    break;
                case "br":
                    break;
                default:
                    sb.Append("(?:.*?)");
                    break;
            }
        }

        sb.Append('$');
        return item == 0 || count == 0 || price == 0 ? null : new Pattern(new Regex(sb.ToString(), RegexOptions.CultureInvariant), item, count, price);
    }

    /// <summary>本文から品・数・値段を読む。購入の確認の文面でなければ null。</summary>
    public static (string Item, int Count, long Price)? Parse(Pattern pattern, string body)
    {
        var m = pattern.Regex.Match(Normalize(body));
        if (!m.Success)
            return null;
        static string Digits(string v) => v.Replace(",", string.Empty).Replace("，", string.Empty);
        if (!int.TryParse(Digits(m.Groups[pattern.CountGroup].Value), out var count) || !long.TryParse(Digits(m.Groups[pattern.PriceGroup].Value), out var price))
            return null;
        return (m.Groups[pattern.ItemGroup].Value, count, price);
    }

    /// <param name="purchaseStep">Questionable が動いていて、今の手順が購入（PurchaseItem）か。</param>
    /// <param name="shopOpen">店（Shop）が開いているか。</param>
    /// <param name="body">確認の本文。</param>
    /// <param name="parsed">本文から読んだ品・数・値段（購入の確認の文面でなければ null）。</param>
    /// <param name="expected">そのクエストの購入の手順の品の名前と数。</param>
    /// <param name="maxGil">この額を超える購入は押さない（0 以下なら見ない）。</param>
    public static Verdict Decide(bool purchaseStep, bool shopOpen, string body, (string Item, int Count, long Price)? parsed,
        IReadOnlyList<(string Name, int? Count)> expected, long maxGil)
    {
        if (!purchaseStep)
            return Verdict.NotPurchaseStep;
        if (!shopOpen)
            return Verdict.NoShop;
        if (string.IsNullOrWhiteSpace(body))
            return Verdict.Unreadable;
        if (ConfirmPolicy.DangerousWord(body) != null)
            return Verdict.Dangerous;
        if (parsed is not { } p)
            return Verdict.NotPurchase;
        var hits = expected.Where(e => Normalize(e.Name) == Normalize(p.Item)).ToList();
        if (hits.Count == 0)
            return Verdict.UnknownItem;
        if (hits.All(e => e.Count is { } c && p.Count > c))
            return Verdict.TooMany;
        if (maxGil > 0 && p.Price > maxGil)
            return Verdict.TooExpensive;
        return Verdict.Press;
    }
}

/// <summary>
/// Questionable の見張り（このプラグインが ON の間は Questionable を OFF のままにしない。勝手に OFF になったら ON に戻す）。
/// クエストの作業中（こちらが自分で作業している間を除く）に Questionable が止まっていたら、何度でも動かし直す（以前は3回やり直したら全体を止めていた）。
/// すぐ止まる状況で頼み続けないよう、動かし直すたびに間を空ける（すぐ → 5秒 → 10秒 → 20秒 → 40秒 → 以降1分おき）。
/// 上限はクエスト全体の時間の上限（30分・釣りや採集のあるクエストは90分）だけ。
/// 実機の記録では、Questionable がクエストの途中で自分から止まったことは無く、止まったのは完了のとき（1本ずつ頼むので正しい動き）と、
/// こちらが止めたとき（停止ボタン・失敗の後片付け・こちらで行う作業の前）だけだった。
/// </summary>
public static class QuestionableKeepAlive
{
    /// <summary>間の単位（既定5秒。検証の仕組みでは短くする）。</summary>
    public static TimeSpan Unit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>n 回目（1 から）に動かし直した後、次に動かし直してよいまでの間。</summary>
    public static TimeSpan WaitAfter(int restarts) => restarts switch
    {
        <= 0 => TimeSpan.Zero,
        1 => Unit,
        2 => Unit * 2,
        3 => Unit * 4,
        4 => Unit * 8,
        _ => Unit * 12,
    };

    /// <summary>チャットにも知らせる回（1回目と5回ごと。毎回だとチャットが埋まる）。</summary>
    public static bool Notify(int restarts) => restarts == 1 || (restarts > 0 && restarts % 5 == 0);
}

/// <summary>
/// クエストの完了の直後の後片付けの判断（NpcLeftovers の説明）。
/// 完了の後、NPC が続けて出す窓を閉じ、動ける状態がしばらく続いたら（NPC の会話が終わったとみなして）終える。
/// </summary>
public static class AfterQuestWindDown
{
    public enum Verdict
    {
        /// <summary>NPC の窓が開いている。閉じる。</summary>
        Close,

        /// <summary>窓は無いが、まだ動けない（会話の途中など）。待つ。</summary>
        WaitFree,

        /// <summary>動けるが、NPC の会話が続かないかを見ている。</summary>
        Settling,

        /// <summary>終わった。</summary>
        Done,

        /// <summary>上限を過ぎた。後は次の作業の前の見張り（LeftoverWindowWatch）に任せて終える。</summary>
        GiveUp,
    }

    /// <summary>後片付けの上限。</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static Verdict Decide(NpcLeftovers.Kind open, bool playerFree, TimeSpan freeFor, TimeSpan elapsed, TimeSpan settle)
    {
        if (elapsed > Limit)
            return Verdict.GiveUp;
        if (open != NpcLeftovers.Kind.None)
            return Verdict.Close;
        if (!playerFree)
            return Verdict.WaitFree;
        return freeFor < settle ? Verdict.Settling : Verdict.Done;
    }
}

/// <summary>
/// 次のクエストを Questionable に頼む前、動ける状態を待つ間の見張り（クエスト完了後に止まったように見えることがあった。
/// 実際は Questionable が止まったのではなく、開いたままの NPC の選択肢で「動ける状態」にならず、こちらが次のクエストを頼めずに待ち続けていた）。
/// 開いたままの NPC の窓（受注の窓・選択肢）があり、Questionable が止まっていて、こちらの会話でもなければ、少し様子を見てから閉じる
/// （閉じれば動ける状態になり、次のクエストを頼む）。会話の窓は流れ全体の ForeignTalk が扱う。
/// 閉じられる窓が無いまま上限まで動けなければ、黙って待ち続けずに理由を出して止める。
/// </summary>
public static class LeftoverWindowWatch
{
    public enum Verdict
    {
        /// <summary>待つ。</summary>
        Wait,

        /// <summary>開いたままの NPC の窓を閉じる。</summary>
        Close,

        /// <summary>上限まで動けない。止める。</summary>
        GiveUp,
    }

    /// <summary>閉じる前に様子を見る時間（ほかの操作が閉じるか・利用者が選んでいる途中か）。検証の仕組みでは短くする。</summary>
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>動けないまま待つ上限。検証の仕組みでは短くする。</summary>
    public static TimeSpan Limit { get; set; } = TimeSpan.FromMinutes(3);

    public static Verdict Decide(NpcLeftovers.Kind open, bool questionableRunning, bool ownConversation, TimeSpan notFreeFor)
    {
        if (notFreeFor > Limit)
            return Verdict.GiveUp;
        if (open is NpcLeftovers.Kind.None or NpcLeftovers.Kind.Talk || questionableRunning || ownConversation)
            return Verdict.Wait;
        return notFreeFor < Grace ? Verdict.Wait : Verdict.Close;
    }
}

/// <summary>
/// Questionable の釣りの手順の前に、手順の指定の餌をこちらで付けておくかの判断（不具合の例：漁師 Lv15「キキルン族の思い出の味」で、
/// 買ったラットの尾でなくピルバグのまま釣り続け、目当ての魚が釣れなかった）。
/// Questionable は釣りの手順で、AutoHook を有効にし、プリセット（餌の強制切り替え）を渡し、/ahstart を同じフレームで送る（Fish.cs の Start）。
/// AutoHook は無効の間は所持数の控えを数え直さない（FishingManager の毎フレームの処理が、無効なら更新の前に戻る）ので、
/// 無効の間に買った餌を「カバンに無い」として替えずに釣り始める（記録：Failed to change bait for forced bait swap. Result: NotInInventory）。
/// 強制切り替えは釣り始めの1回だけで、その後は同じ餌で投げ直し続ける。
/// 付けている餌がすでに同じなら、AutoHook は所持数を見ずに進む（AlreadyEquipped を先に判定する）ので、先に付けておけば通る。
/// </summary>
public static class FishBaitPrep
{
    public enum Verdict
    {
        /// <summary>今の手順から後に、同じ段の釣りの手順（餌の指定つき）が無い。</summary>
        NoFishStep,

        /// <summary>もう手順の餌を付けている。</summary>
        AlreadyEquipped,

        /// <summary>手順の餌をまだ持っていない（Questionable が買う手順の前など）。</summary>
        NotOwned,

        /// <summary>付け替えられない状態（糸を垂らしている・詠唱中・戦闘中・エリア移動中・会話中など）。</summary>
        Busy,

        /// <summary>付ける。</summary>
        Equip,
    }

    /// <summary>今の手順（段・番号）から後で、同じ段にある最初の釣りの手順の餌（無ければ null）。</summary>
    public static uint? TargetBait(IReadOnlyList<QuestionableStep> steps, int sequence, int stepIndex)
        => steps.Where(s => s.Sequence == sequence && s.Index >= stepIndex && s.Type == "Fish" && s.BaitId is > 0)
                .OrderBy(s => s.Index)
                .FirstOrDefault()?.BaitId;

    public static Verdict Decide(uint? target, uint equipped, int owned, bool busy)
    {
        if (target is not { } t)
            return Verdict.NoFishStep;
        if (equipped == t)
            return Verdict.AlreadyEquipped;
        if (owned <= 0)
            return Verdict.NotOwned;
        if (busy)
            return Verdict.Busy;
        return Verdict.Equip;
    }
}
