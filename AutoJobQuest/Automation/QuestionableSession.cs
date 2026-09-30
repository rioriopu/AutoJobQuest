using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// Questionable の優先リストを、こちらのクエストだけに一時的に差し替える（「優先的にクリアする」フラグで
/// 別のクエストに誘導されないように）。
///
/// Questionable の単体進行は、受注した後は毎フレーム「いまのクエスト」を選び直し、それが自分のクエストと違うと「単体は終わり」として止まる。
/// 選び直しの材料はゲームの ToDo リスト（先頭）・受注できる優先クエスト・受注済みのメインクエストで、利用者が受けている別のクエストが
/// 先頭にあるとそちらへ移って止まっていた。優先リストの先頭で「受注可能か受注済み」のクエストは、これらのどれよりも優先される
/// （Questionable の QuestController.cs:633-644）。そこでクエストの間だけ、優先リストをこちらのクエスト1本にする。
///
/// 【戻し方】Questionable の優先リストは保存されない（メモリだけ）。始める前に書き出した文字列を控え、終わったら空にしてから読み込む
/// （Import は追加なので、先に空にする）。IPC の戻り値は当てにならないので、書き出して確かめる。
/// 戻す時に中身がこちらの設定でなくなっていたら（利用者が変えた・優先リストの窓の「Job Quests」プリセットが書き換えた）、戻さずに残す。
/// 「受注のみ」の印は書き出しに含まれないので戻せない（事前点検で知らせる）。
/// </summary>
public sealed class QuestionablePriorityGuard
{
    private string? saved;
    private string? mine;
    private uint questRowId;

    /// <summary>差し替え中か。</summary>
    public bool Active => this.mine != null;

    /// <summary>空の優先リストの書き出し。</summary>
    public const string Empty = "qst:priority:";

    /// <summary>
    /// 優先リストをこのクエストだけにする。できなければ理由（できたら null）。
    /// </summary>
    public string? Take(QuestionableIpc q, uint questRowId, RunLog log)
    {
        var export = q.ExportQuestPriority();
        if (export == null)
            return "Questionable の優先リストを読めません（IPC ExportQuestPriority）";

        var expected = QuestionableIpc.EncodePriority([QuestionableIpc.ToQuestId(questRowId)]);
        this.saved ??= export;
        this.questRowId = questRowId;
        if (export != expected)
        {
            q.ClearQuestPriority();
            q.AddQuestPriority(questRowId);
            var now = q.ExportQuestPriority();
            if (now != expected)
            {
                // 入れられなかった（経路データが無い等で Add が黙って何もしない）。空にしたリストを、確かめずにすぐ元へ戻す
                // （Restore は「こちらの設定のままか」を確かめるので、ここでは使わない）
                var back = this.saved ?? Empty;
                this.saved = null;
                q.ClearQuestPriority();
                if (back != Empty)
                    q.ImportQuestPriority(back);
                return $"Questionable の優先リストをこのクエストだけにできませんでした（書き出し：{now ?? "読めない"}。元のリストに戻しました）";
            }

            log.Write("クエスト", export == Empty
                ? "Questionable の優先リストを、このクエストだけにしました（別のクエストへ移らないように。終わったら空に戻します）"
                : "Questionable の優先リストを一時的にこのクエストだけにしました（別のクエストへ移らないように。終わったら元のリストに戻します）");
        }

        this.mine = expected;
        return null;
    }

    /// <summary>
    /// まだこちらの設定のままか確かめ、変わっていたら入れ直す（優先リストの窓のプリセットが職の変更で書き換える等）。
    /// 入れ直せなければ理由を返す。
    /// </summary>
    public string? Keep(QuestionableIpc q, RunLog log)
    {
        if (this.mine == null)
            return null;
        var now = q.ExportQuestPriority();
        if (now == null)
            return "Questionable の優先リストを読めないので、進行を止めます";
        if (now == this.mine)
            return null;

        log.Warn("クエスト", "Questionable の優先リストが途中で書き換わっていたので、このクエストだけに入れ直します（優先リストの窓の「Job Quests」プリセットを開いたままだと、職が変わるたびに書き換わります）");
        q.ClearQuestPriority();
        q.AddQuestPriority(this.questRowId);
        return q.ExportQuestPriority() == this.mine ? null : "Questionable の優先リストを入れ直せませんでした";
    }

    /// <summary>元に戻す（差し替えていなければ何もしない）。記録に出す文言を返す（無ければ空）。</summary>
    public string Restore(QuestionableIpc q, RunLog log)
    {
        if (this.mine == null)
            return string.Empty;

        var saved = this.saved ?? Empty;
        var mine = this.mine;
        this.mine = null;
        this.saved = null;

        var now = q.ExportQuestPriority();
        string msg;
        if (now == null)
            msg = "Questionable の優先リストを読めないので、元に戻せませんでした（Questionable の画面で確かめてください）";
        else if (now != mine && now != Empty && now.Length > 0)
            msg = "Questionable の優先リストが途中で変わっていたので、元に戻さずに今の中身を残しました";
        else
        {
            q.ClearQuestPriority();
            if (saved != Empty)
                q.ImportQuestPriority(saved);
            var after = q.ExportQuestPriority();
            msg = after == saved
                ? (saved == Empty ? "Questionable の優先リストを空に戻しました" : "Questionable の優先リストを元に戻しました")
                : "Questionable の優先リストを元に戻しましたが、中身が控えと一致しません（Questionable の画面で確かめてください）";
        }

        log.Write("クエスト", msg);
        return msg;
    }
}

/// <summary>
/// Questionable の単体進行を、こちらで引き継ぐかの判断。
///
/// Questionable の製作職クラスクエの経路には、段の中に「NPC から材料を買う」（PurchaseItem）と「作る」（Craft）がある。
///  ・作る品の指定が無い Craft（木工 Lv1〜25 の6本・調理 Lv53〜60 の4本）は、在庫に関係なく Artisan の既製リストを動かす
///    （Questionable の Craft.cs。設定・IPC・コマンドで飛ばす手段は無い）。こちらで作った納品物があっても、追加で作る。
///  ・材料の購入は「材料そのものの所持数」だけで飛ばす（StepIf.Item）。納品物を先に作ってあると、材料は使わないのに買い足す。
/// どちらも、こちらで納品物を作ってある以上は要らない手順。そこで Questionable がその段の購入・製作の手順に入ったら止め、
/// その段の「最後の購入・製作より後」の手順（NPC と話す・報告する）をこちらで行う。段が進めば Questionable に戻す。
///
/// 止めるのが間に合う理由：Questionable は1フレームに作業を1つだけ始め、Craft 手順は「降りる」作業の次に作る作業を置く
/// （MiniTaskController.UpdateCurrentTask・Craft.Factory）。Craft 手順に入ってから既製リストを呼ぶまで少なくとも1フレームあり、
/// 購入の手順は NPC まで歩くところから始まる。
/// </summary>
public static class QuestTakeOver
{
    /// <summary>こちらで行える手順の種類（ほかの種類が残る段は引き継がない）。</summary>
    public static readonly string[] OwnTypes = ["Interact", "CompleteQuest", "WalkTo", "None", "WaitForManualProgress"];

    /// <param name="steps">そのクエストの全手順（経路データ）。</param>
    /// <param name="sequence">Questionable がいま進めている段。</param>
    /// <param name="stepIndex">Questionable がいま進めている手順の番号。</param>
    /// <param name="type">その手順の種類。</param>
    /// <param name="crafts">その段の Craft 手順（品・数・HQ）。</param>
    /// <param name="held">品を持っているか（品, 数, HQ で数えるか）。</param>
    /// <param name="doneRecipient">その相手に渡し済みか（渡し済みの相手との手順はこちらでも行わない）。省略時は誰も渡し済みでない。</param>
    /// <returns>こちらで行う手順（引き継がないなら null）。</returns>
    public static List<QuestionableStep>? Decide(IReadOnlyList<QuestionableStep> steps, int sequence, int stepIndex, string type,
        IReadOnlyList<QuestionableCraftStep> crafts, Func<uint, int, bool, bool> held, Func<uint, bool>? doneRecipient = null)
    {
        if (type is not ("PurchaseItem" or "Craft"))
            return null;

        var inSeq = steps.Where(s => s.Sequence == sequence).OrderBy(s => s.Index).ToList();
        var buyOrCraft = inSeq.Where(s => s.Type is "PurchaseItem" or "Craft").ToList();
        if (buyOrCraft.Count == 0)
            return null;
        var last = buyOrCraft.Max(s => s.Index);
        if (stepIndex > last)
            return null;

        var seqCrafts = crafts.Where(c => c.Sequence == sequence).ToList();

        // 製作の無い段（購入だけ：釣り餌など）は引き取らない（以前は漁師 Lv63・65 で「作る品を全部持っている」が空で真になり、
        // 餌を買わずに進めて後の釣りで詰まった。Lv1〜60 の漁師6本・採掘1本も同じ形。購入の手順は「持っていれば飛ばす」付きなので二重には買わない）
        if (seqCrafts.Count == 0)
            return null;
        var premade = seqCrafts.Any(c => c.ItemId == null);
        var buys = buyOrCraft.Any(s => s.Type == "PurchaseItem");
        var allHeld = seqCrafts.Where(c => c.ItemId != null).All(c => held(c.ItemId!.Value, Math.Max(1, c.ItemCount), c.Hq));

        // 既製リストが動く段か、作る品を全部持っているのに材料を買い足す段だけ引き継ぐ。
        // 作る品を持っていない（こちらの準備が足りない）ときは Questionable に任せる（買って作る＝進みはする）
        if (!premade && !(buys && allHeld))
            return null;
        if (premade && !allHeld)
            return null;

        var rest = inSeq.Where(s => s.Index > last).ToList();
        if (rest.Count == 0 || rest.Any(s => !OwnTypes.Contains(s.Type)))
            return null;
        return WithoutDone(rest, doneRecipient);
    }

    /// <summary>
    /// 受注後の品（Lv61〜70 の製作職）の製作手順を引き取るか。引き取るならその品、引き取らないなら null。
    /// 必要数を（HQ が要るなら HQ で）持っていれば引き取らない。Questionable の製作手順は「持っていれば飛ばす」付きなので、
    /// こちらが作り終えて戻した後は Questionable が飛ばして先へ進む。
    /// 以前は持ち物を見ずに引き取ったので、戻した直後の数フレーム（Questionable が飛ばすまで）に製作手順をまた引き取り、
    /// 戻す→引き取るを繰り返して30分の上限で止まっていた（Lv61〜70 の製作32本すべて）。
    /// </summary>
    /// <param name="type">Questionable のいまの手順の種類。</param>
    /// <param name="stepItem">その手順の品（経路データ。無ければ null）。</param>
    /// <param name="crafts">そのクエストの受注後に作る品。</param>
    /// <param name="held">持っている数（品, HQ で数えるか）。</param>
    public static QuestCraft? QuestCraftToTake(string type, uint? stepItem, IReadOnlyList<QuestCraft> crafts, Func<uint, bool, int> held)
    {
        if (type != "Craft" || crafts.Count == 0)
            return null;
        var qc = crafts.FirstOrDefault(c => stepItem == null || c.ItemId == stepItem);
        if (qc == null)
            return null;
        return held(qc.ItemId, qc.Hq) >= qc.Count ? null : qc;
    }

    /// <summary>
    /// 手で行う手順（Instruction）の後に、同じ段でこちらが行える手順（話しかける等）だけが残り、その段で渡す品がそろったら、
    /// 残りの手順を返す（こちらで行う）。そろっていない・ほかの種類が残るなら null。
    /// 漁師 Lv68「減少を食い止めろ」の段5は、刺突漁で大方士を獲った後にワワラゴへ渡して初めて段が進む。
    /// Questionable の Instruction は段が変わるまで待ち続け、先へ進めるのは Questionable の画面の Skip だけなので、
    /// 以前は獲り終えても進まず、60分の上限で止まっていた。
    /// </summary>
    /// <param name="steps">そのクエストの全手順（経路データ）。</param>
    /// <param name="sequence">Questionable がいま進めている段。</param>
    /// <param name="stepIndex">Questionable がいま進めている手順の番号。</param>
    /// <param name="type">その手順の種類。</param>
    /// <param name="itemsReady">この段で渡す品が、必要数そろっているか。</param>
    public static List<QuestionableStep>? AfterInstruction(IReadOnlyList<QuestionableStep> steps, int sequence, int stepIndex, string type, bool itemsReady)
    {
        if (type != "Instruction" || !itemsReady)
            return null;
        var rest = steps.Where(s => s.Sequence == sequence && s.Index > stepIndex).OrderBy(s => s.Index).ToList();
        if (rest.Count == 0 || rest.Any(s => !OwnTypes.Contains(s.Type)) || !rest.Any(s => s.Type is "Interact" or "CompleteQuest"))
            return null;
        return rest;
    }

    /// <summary>
    /// 受注後の品の製作の段にいるのに、その品が（HQ が要るなら HQ で）足りないなら、その品（引き取って作る）。そうでなければ null。
    /// Questionable の製作の手順の「持っていれば飛ばす」は品質を見ない（NQ と HQ の合計で数える）。そのため NQ だけを持って製作の段に来ると、
    /// 製作の手順を飛ばして受け取る相手へ行き、相手は HQ が無いので応じず、30分の上限で止まっていた（こちらの引き取りは手順の種類が製作のときだけだった）。
    /// そこで、その段のどの手順にいても、HQ が足りなければ引き取る。
    /// </summary>
    /// <param name="sequence">Questionable がいま進めている段。</param>
    /// <param name="steps">そのクエストの全手順（経路データ）。</param>
    /// <param name="crafts">そのクエストの受注後に作る品。</param>
    /// <param name="held">持っている数（品, HQ で数えるか）。</param>
    public static QuestCraft? QuestCraftInSequence(int sequence, IReadOnlyList<QuestionableStep> steps, IReadOnlyList<QuestCraft> crafts, Func<uint, bool, int> held)
    {
        foreach (var qc in crafts)
        {
            var inThisSequence = steps.Any(s => s.Sequence == sequence && s.Type == "Craft" && (s.ItemId == null || s.ItemId == qc.ItemId));
            if (inThisSequence && held(qc.ItemId, qc.Hq) < qc.Count)
                return qc;
        }

        return null;
    }

    /// <summary>
    /// 手で行う手順の段で渡す品（受注後にしか手に入らない品のうち、その段が品を使う段の範囲にあるもの）。
    /// 漁師 Lv68 なら大方士×3（クエストの本文から読んだ数）。
    /// </summary>
    public static List<QuestItemReq> InstructionItems(JobQuest quest, int sequence)
    {
        if (sequence < quest.FirstItemSeq || (quest.LastItemSeq != 255 && sequence > quest.LastItemSeq))
            return [];
        return quest.Items.Where(i => quest.AfterAcceptItems.Contains(i.ItemId)).ToList();
    }

    /// <summary>
    /// Questionable が、同じ段の渡し済みの相手との手順（Interact）に来たら引き継ぐ。
    /// 経路データの「話しかける」手順には、渡し済みで飛ばす条件（CompletionQuestVariablesFlags）が無いので（Q65601・Q65677 の段2）、
    /// Questionable は渡し済みの相手の前で頭上のマーカーを待ち続けると見込まれる。
    /// そこで止めて、その段の残りのうち、まだの相手との手順だけをこちらで行う。
    /// </summary>
    /// <param name="steps">そのクエストの全手順（経路データ）。</param>
    /// <param name="sequence">Questionable がいま進めている段。</param>
    /// <param name="stepIndex">Questionable がいま進めている手順の番号。</param>
    /// <param name="type">その手順の種類。</param>
    /// <param name="doneRecipient">その相手に渡し済みか。</param>
    /// <returns>こちらで行う手順（引き継がないなら null）。</returns>
    public static List<QuestionableStep>? SkipDone(IReadOnlyList<QuestionableStep> steps, int sequence, int stepIndex, string type, Func<uint, bool> doneRecipient)
    {
        if (type != "Interact")
            return null;
        var current = steps.FirstOrDefault(s => s.Sequence == sequence && s.Index == stepIndex);
        if (current?.DataId is not { } npc || !doneRecipient(npc))
            return null;

        var rest = steps.Where(s => s.Sequence == sequence && s.Index >= stepIndex).OrderBy(s => s.Index).ToList();
        if (rest.Any(s => !OwnTypes.Contains(s.Type)))
            return null;
        return WithoutDone(rest, doneRecipient);
    }

    /// <summary>ギアセット・装備を変える手順の種類（Questionable の新しいクラス向けの準備）。</summary>
    public static readonly string[] GearsetSetupTypes = ["EquipItem", "CreateGearset", "EquipRecommended", "UpdateGearset"];

    /// <summary>
    /// 受注前の段（段0）に、新しいクラス向けの準備（道具の装備・ギアセットの作成・推奨装備での上書き）があるなら、その段の受注の手順。無ければ null。
    /// Lv1 の「My First ～」（製作8職・採集3職）がこの形（経路で確認：飛ばす条件は NG+ だけ）。
    /// Lv70 の職では要らず、利用者のギアセットを1つ増やして書き換える。漁師と製作8職は Lv1 の道具が無いと例外で止まる。
    /// そこで段0 はこちらで受注だけ行う（受注の相手に話しかける）。
    /// </summary>
    public static QuestionableStep? GearsetSetupAccept(IReadOnlyList<QuestionableStep> steps)
    {
        var seq0 = steps.Where(s => s.Sequence == 0).ToList();
        if (!seq0.Any(s => GearsetSetupTypes.Contains(s.Type)))
            return null;
        return seq0.FirstOrDefault(s => s.Type == "AcceptQuest" && s.DataId != null && s.Position != null);
    }

    private static List<QuestionableStep> WithoutDone(List<QuestionableStep> rest, Func<uint, bool>? doneRecipient)
        => doneRecipient == null ? rest : rest.Where(s => !(s.Type == "Interact" && s.DataId is { } id && doneRecipient(id))).ToList();
}

/// <summary>
/// 行き先（エリアと位置）へ行く（こちらで行う手順のため）。同じエリアなら歩く。違うエリアなら、
/// 行き先の最寄りのエーテルネットの中継点（シャード）を、ゲームデータのエーテライト表（位置は Level）から選び、
///  ・同じエーテルネットの組（AethernetGroup）のエリアにいれば、近くのエーテライト／シャードまで歩いて Lifestream の都市内転送で飛ぶ
///    （IPC AethernetTeleportById：エーテライトかシャードの近くでないと使えない。Lifestream の IPCProvider.cs）
///  ・違う組なら、先にその組のエーテライトへテレポする
/// 行き先が本体のエーテライトのあるエリアなら、テレポで直接行く。
/// 進む判断はエリアと位置（状態）で行う。
/// </summary>
public sealed class GoToTask : AutoTask
{
    private readonly uint territory;
    private readonly Vector3 position;
    private readonly float range;
    private readonly string label;
    private AutoTask? sub;
    private bool aethernetRequested;
    private int aethernetTries;

    // 同じエリアの中のテレポを試したか（1回だけ）
    private bool shortcutTried;

    // 中継点のそばで、そのオブジェクトを探し始めた時刻（テレポ直後はまだ載っていないことがある）
    private DateTime? nodeSearchSince;

    /// <summary>中継点のそばで、そのオブジェクトが載るのを待つ上限。</summary>
    private static readonly TimeSpan NodeSearchLimit = TimeSpan.FromSeconds(10);

    /// <summary>同じエリアの中でテレポを考える距離（今の位置から行き先まで、水平でこれ以上あるとき）。</summary>
    public const float ShortcutMinDistance = 400f;

    /// <summary>同じエリアの中でテレポするのは、エーテライトから行き先までが今より これだけ以上近いとき。</summary>
    public const float ShortcutGain = 300f;

    public GoToTask(uint territory, Vector3 position, float range, string label)
    {
        this.territory = territory;
        this.position = position;
        this.range = range;
        this.label = label;
    }

    public override string Name => $"移動: {this.label}";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.sub != null)
        {
            var r = this.sub.Step(ctx);
            this.Status = this.sub.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.sub.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
            this.sub = null;
            if (failed != null)
                return this.Fail(failed);
        }

        if (this.WorkElapsed > TimeSpan.FromMinutes(6))
            return this.Fail($"{this.label} へ6分以内に着けませんでした");

        if (GameUi.BetweenAreas || !GameUi.PlayerFree())
        {
            this.Status = "移動中";
            return TaskResult.Running;
        }

        if (Me.Territory == this.territory)
        {
            this.aethernetRequested = false;
            // 着いたかは MoveToTask と同じ見方（水平の距離が範囲内で、高さの差が 8 未満）。以前は3次元の距離で見ていたので、
            // 高さに差がある行き先では MoveToTask がすぐ「着いた」と返し、こちらは着いていないとみなして作り直し続けた
            var flat = Vector2.Distance(new Vector2(Me.Position.X, Me.Position.Z), new Vector2(this.position.X, this.position.Z));
            if (flat <= this.range && MathF.Abs(Me.Position.Y - this.position.Y) < 8f)
                return TaskResult.Done;

            // 同じエリアでも、行き先に近い解放済みのエーテライトがあればテレポする（1回だけ。漁師 Lv68 のワワラゴは同じエリアの約1300m 先で、
            // 歩くと4分の上限に当たりえた。Questionable も経路の AetheryteShortcut でナマイへ飛ぶ）
            if (!this.shortcutTried && SameTerritoryShortcut(this.territory, Me.Position, this.position, IsNodeUnlocked) is { } shortcut)
            {
                this.shortcutTried = true;
                ctx.Log.Write("移動", $"{this.label} は同じエリアの遠く（水平 {flat:0}m）なので、近くのエーテライト（{AetherytePlaces.Name(shortcut)}）へテレポします");
                this.sub = new TeleportTask(this.territory, this.position, shortcut);
                return TaskResult.Running;
            }

            this.sub = new MoveToTask(this.position, this.range, this.label);
            return TaskResult.Running;
        }

        // 都市内転送を頼んだ後：Lifestream が動いている間は待つ
        if (this.aethernetRequested)
        {
            if (ctx.Lifestream.IsBusy() == true || this.PhaseElapsed < TimeSpan.FromSeconds(3))
            {
                this.Status = "都市内転送を待っています";
                return TaskResult.Running;
            }

            this.aethernetRequested = false;
        }

        var target = NearestNode(this.territory, this.position, IsNodeUnlocked);
        if (target == null)
        {
            // 行き先のエリアに解放済みの中継点が1つも無い（以前は理由が「見つかりません」だけで、未交感だと伝わらなかった）
            var any = NearestNode(this.territory, this.position);
            return this.Fail(any is { } n
                ? $"{TeleportTask.TerritoryName(this.territory)} のエーテライト・エーテルネットの中継点（最寄りは「{AetherytePlaces.Name(n)}」）が未交感です。交感してから再開してください"
                : $"{TeleportTask.TerritoryName(this.territory)} へ行くエーテライト・エーテルネットの中継点が見つかりません");
        }

        var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var t = sheet.GetRow(target.Value);
        if (t.IsAetheryte)
        {
            this.sub = new TeleportTask(this.territory, this.position);
            return TaskResult.Running;
        }

        // 行き先の組に、いまのエリアの中継点・エーテライトが入っているか
        // 見えない行（都市の門：野外側の着地点で、中継点のオブジェクトは無い）は外す（以前は野外の門へ歩いてから止まっていた。
        // 外すと、野外からは組の本体のエーテライトへテレポしてから中継点へ歩く）
        var here = NodesHere(Me.Territory, t.AethernetGroup, IsNodeUnlocked);
        if (here.Count == 0)
        {
            var main = sheet.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == t.AethernetGroup && IsNodeUnlocked(a.RowId));
            if (main.RowId == 0)
                return this.Fail($"{TeleportTask.TerritoryName(this.territory)} のエーテルネットの親のエーテライトが見つかりません");
            this.sub = new TeleportTask(main.Territory.RowId);
            return TaskResult.Running;
        }

        // 近くの「解放済みの」エーテライト／中継点まで歩いてから都市内転送（以前は一番近い中継点を、解放済みかを見ずに選んでいた。
        // 未交感の中継点だと Lifestream が交感だけして転送せず、利用者の交感の状態も勝手に変えた）。
        // 位置は地図の印から求める（エーテライト表の Level の行はほとんど無く、以前は位置が引けずに必ず止まっていた）。
        // 地図の印からは高さが分からないので、距離は水平で見る。近くのエーテライトのオブジェクトと、求めた位置が10m以内かで照らす
        // （Lifestream の TryGetTinyAetheryteFromIGameObject と同じ見方）
        var nodes = here.Select(a => (Row: a.RowId, Place: AetherytePlaces.Of(a.RowId))).Where(x => x.Place != null)
            .Select(x => (x.Row, Flat: x.Place!.Value.Flat, x.Place.Value.Height)).ToList();
        if (nodes.Count == 0)
            return this.Fail($"{TeleportTask.TerritoryName(Me.Territory)} の解放済みの中継点（{string.Join("、", here.Select(a => AetherytePlaces.Name(a.RowId)))}）の位置を、"
                             + "ゲームデータから求められません。近くの中継点まで手で移動してから再開してください");
        var me2 = new Vector2(Me.Position.X, Me.Position.Z);
        var node = nodes.OrderBy(x => Vector2.Distance(x.Flat, me2)).First();
        var near = Svc.Objects.Where(o => o.ObjectKind == ObjectKind.Aetheryte && Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), node.Flat) < 10f)
            .OrderBy(o => Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), node.Flat)).FirstOrDefault();
        if (near == null)
        {
            // まだ読み込まれていない（遠い）なら、その位置へ歩く。近づけば読み込まれる。
            // 高さが分からなければ、vnavmesh で床に合わせる（以前は今の高さのまま向かい、高低差が 8m 以上だと着いたと判定されずに止まった。
            // CombatTask の地図の座標と同じやり方）
            if (Vector2.Distance(node.Flat, me2) > 7f)
            {
                this.nodeSearchSince = null;
                var guess = new Vector3(node.Flat.X, node.Height ?? Me.Position.Y, node.Flat.Y);
                var dest = node.Height != null
                    ? guess
                    : ctx.Navmesh.NearestPoint(guess, 10f, 300f) ?? ctx.Navmesh.PointOnFloor(new Vector3(guess.X, Me.Position.Y + 100f, guess.Z), false, 10f);
                if (dest == null)
                    return this.Fail($"エーテルネットの中継点（{AetherytePlaces.Name(node.Row)}）の足元の位置が分かりません（vnavmesh の経路の地図に床が見つかりません）");
                this.sub = new MoveToTask(dest.Value, 5f, $"エーテルネットの中継点（{AetherytePlaces.Name(node.Row)}）");
                return TaskResult.Running;
            }

            // テレポで着いた直後は、そのオブジェクトがまだ載っていないことがある。上限まで探し続ける
            this.nodeSearchSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - this.nodeSearchSince.Value < NodeSearchLimit)
            {
                this.Status = $"エーテルネットの中継点（{AetherytePlaces.Name(node.Row)}）を探しています";
                return TaskResult.Running;
            }

            return this.Fail($"エーテルネットの中継点（{AetherytePlaces.Name(node.Row)}）の近くに来ましたが、{NodeSearchLimit.TotalSeconds:0} 秒探してもそのオブジェクトが見つかりません");
        }

        this.nodeSearchSince = null;

        // 近いかは MoveToTask の「着いた」と同じ見方（水平 5m 以内かつ高さの差 8m 未満）にそろえる（以前は3次元の 7m で見ていたので、
        // 段の上の中継点では MoveToTask がすぐ着いたと返し、こちらは遠いとみて作り直し続けた。Lifestream が都市内転送を受け付けるのは水平 11m・3次元 15m 未満）
        var nearFlat = Vector2.Distance(new Vector2(near.Position.X, near.Position.Z), me2);
        if (nearFlat > 5f || MathF.Abs(near.Position.Y - Me.Position.Y) >= 8f)
        {
            this.sub = new MoveToTask(near.Position, 5f, "エーテルネットの中継点");
            return TaskResult.Running;
        }

        if (this.aethernetTries++ >= 3)
            return this.Fail($"都市内転送で {TeleportTask.TerritoryName(this.territory)} へ行けませんでした");
        if (!ctx.Lifestream.AethernetTeleportById(target.Value))
            return this.Fail($"Lifestream に都市内転送を頼めませんでした: {string.Join(" / ", ctx.Lifestream.LastErrors.Values)}");
        this.aethernetRequested = true;
        this.NextPhase($"{TeleportTask.TerritoryName(this.territory)} へ都市内転送します");
        return TaskResult.Running;
    }

    /// <summary>
    /// そのエリアにある、同じ組の、見えている・解放済みのエーテライトと中継点（都市内転送の出発点の候補）。組が 0 なら空。
    /// 見えない行（都市の門）は外す。
    /// </summary>
    public static List<Aetheryte> NodesHere(uint territory, byte group, Func<uint, bool> unlocked)
        => group == 0
            ? []
            : Svc.Data.GetExcelSheet<Aetheryte>().Where(a => a.Territory.RowId == territory && !a.Invisible && a.AethernetGroup == group && unlocked(a.RowId)).ToList();

    /// <summary>
    /// 同じエリアの中で、行き先に近い解放済みのエーテライトへテレポするほうがよいなら、そのエーテライト（エーテライト表の行）。よくなければ null。
    /// 今の位置から行き先まで水平で <see cref="ShortcutMinDistance"/> 以上あり、エーテライトから行き先までが今より <see cref="ShortcutGain"/> 以上近いときだけ。
    /// </summary>
    public static uint? SameTerritoryShortcut(uint territory, Vector3 me, Vector3 destination, Func<uint, bool> unlocked)
    {
        var dest2 = new Vector2(destination.X, destination.Z);
        var fromMe = Vector2.Distance(new Vector2(me.X, me.Z), dest2);
        if (fromMe < ShortcutMinDistance)
            return null;
        uint? best = null;
        var bestDistance = float.MaxValue;
        foreach (var a in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!a.IsAetheryte || a.Invisible || a.Territory.RowId != territory || !unlocked(a.RowId))
                continue;
            if (AetherytePlaces.FlatDistance(a.RowId, destination) is not { } d || d >= bestDistance)
                continue;
            best = a.RowId;
            bestDistance = d;
        }

        return best != null && bestDistance + ShortcutGain <= fromMe ? best : null;
    }

    private static unsafe bool IsNodeUnlocked(uint id)
    {
        var state = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return state != null && state->IsAetheryteUnlocked(id);
    }

    /// <summary>
    /// そのエリアで、位置に一番近いエーテライト／中継点（エーテライト表の行）。無ければ null。
    /// 距離は水平で見る（位置は地図の印から求め、高さが分からないため。以前は Level の行が無いので、どの行も同じ距離になり、
    /// 行番号の一番小さい行を選んでいた）。位置が求められない行は後回しにする。
    /// </summary>
    public static uint? NearestNode(uint territory, Vector3 position, Func<uint, bool>? unlocked = null)
    {
        uint? best = null;
        var bestDist = float.MaxValue;
        foreach (var a in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (a.Territory.RowId != territory || a.Invisible || (unlocked != null && !unlocked(a.RowId)))
                continue;
            var d = AetherytePlaces.FlatDistance(a.RowId, position) ?? float.MaxValue / 2;
            if (d < bestDist)
            {
                bestDist = d;
                best = a.RowId;
            }
        }

        return best;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
        if (this.aethernetRequested && ctx.Lifestream.IsBusy() == true)
            ctx.Lifestream.Abort();
    }
}

/// <summary>
/// Questionable の経路の「NPC と話す」「報告する」手順を1つ、こちらで行う。
/// その手順の位置へ行き、相手（DataId）に話しかける。会話・受注・完了・報酬選びは TextAdvance、納品窓はこちら（QuestTask）が扱う。
/// 終わりの判断（状態）：報告なら「クエスト完了」、話すなら「段が進んだ、またはクエストの変数（6バイト）が変わった」。
/// 話しかけて会話が終わっても何も変わらなければ、もう一度話しかける（3回まで）。それでも変わらなければ「変化なし」で終える
/// （既に済んだ相手のことがある。段が進まなければ呼び出し側が止める）。
/// </summary>
public sealed unsafe class NpcStepTask : AutoTask
{
    private readonly QuestionableStep step;
    private readonly uint questRowId;
    private GoToTask? travel;
    private bool arrived;
    private int interactions;
    private int interactionAttempts;
    private DateTime interactedAt = DateTime.MinValue;
    private bool talked;
    private byte seqBefore;
    private byte[] varsBefore = [];
    private string? selectedMenu;
    private DateTime firstInteractAt = DateTime.MinValue;

    public NpcStepTask(QuestionableStep step, uint questRowId)
    {
        this.step = step;
        this.questRowId = questRowId;
    }

    /// <summary>話しかけても何も変わらなかったか（既に済んだ相手の可能性）。</summary>
    public bool NoChange { get; private set; }

    public override string Name => $"手順 {this.step.Sequence}-{this.step.Index} {this.step.Type}（{NpcName(this.step.DataId)}）";

    public static string NpcName(uint? id)
        => id is { } i && Svc.Data.GetExcelSheet<ENpcResident>().TryGetRow(i, out var r) ? r.Singular.ExtractText() : $"#{id}";

    /// <summary>クエストの段と変数（6バイト）。受けていなければ（0, 空）。</summary>
    public static (byte Seq, byte[] Vars) QuestState(uint questRowId)
    {
        var qm = QuestManager.Instance();
        if (qm == null)
            return (0, []);
        var qw = qm->GetQuestById((ushort)(questRowId & 0xFFFF));
        if (qw == null)
            return (0, []);
        return (qw->Sequence, qw->Variables.ToArray());
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.step.Type == "CompleteQuest" && QuestManager.IsQuestComplete(this.questRowId))
            return TaskResult.Done;
        if (this.step.DataId == null || this.step.Position == null)
            return this.Fail($"手順 {this.step.Sequence}-{this.step.Index}（{this.step.Type}）の相手か位置が経路データにありません");
        if (this.WorkElapsed > TimeSpan.FromMinutes(10))
            return this.Fail($"{NpcName(this.step.DataId)} との手順が10分以内に終わりませんでした");

        // カウンター越しでは終点への到達を待たず試す。拒否された場合は経路を続け、壁越しで止まり続けない。
        var nearby = Svc.Objects.Where(o => o.ObjectKind == ObjectKind.EventNpc && o.BaseId == this.step.DataId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position)).FirstOrDefault();
        if (!this.arrived && Me.Territory == this.step.Territory && GameUi.PlayerFree()
            && nearby != null && Vector3.Distance(nearby.Position, Me.Position) <= 5f
            && DateTime.UtcNow - this.interactedAt >= TimeSpan.FromSeconds(2))
        {
            this.interactedAt = DateTime.UtcNow;
            (this.seqBefore, this.varsBefore) = QuestState(this.questRowId);
            if (GameUi.Interact(nearby, checkLineOfSight: true))
            {
                this.travel?.Cleanup(ctx);
                this.travel = null;
                this.arrived = true;
                this.firstInteractAt = this.interactedAt;
                this.interactions++;
                this.selectedMenu = null;
                this.talked = false;
                this.Status = $"{NpcName(this.step.DataId)} に経路の途中から話しかけました";
                return TaskResult.Running;
            }
        }

        if (!this.arrived)
        {
            this.travel ??= new GoToTask(this.step.Territory, this.step.Position.Value, 3f, NpcName(this.step.DataId));
            var r = this.travel.Step(ctx);
            this.Status = this.travel.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.travel.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.travel.FailReason : null;
            this.travel = null;
            if (failed != null)
                return this.Fail(failed);
            this.arrived = true;
        }

        if (QuestMenuChoice.Handle(ctx, this.questRowId, this.firstInteractAt, ref this.selectedMenu, out var menuFailure))
        {
            if (menuFailure != null)
                return this.Fail(menuFailure);
            this.talked = true;
            this.Status = "選択したクエストの会話を待っています";
            return TaskResult.Running;
        }

        // 会話・納品の最中は待つ（納品窓は QuestTask が扱う）
        if (!GameUi.PlayerFree())
        {
            this.talked = true;
            this.Status = "会話・納品中";
            return TaskResult.Running;
        }

        // 話しかけた後、会話が終わった：変わったかを見る
        if (this.interactions > 0 && (this.talked || DateTime.UtcNow - this.interactedAt > TimeSpan.FromSeconds(5)))
        {
            if (this.step.Type == "CompleteQuest")
            {
                if (QuestManager.IsQuestComplete(this.questRowId))
                    return TaskResult.Done;
            }
            else
            {
                var (seq, vars) = QuestState(this.questRowId);
                if (seq != this.seqBefore || !vars.AsSpan().SequenceEqual(this.varsBefore))
                {
                    ctx.Log.Write("クエスト", $"{NpcName(this.step.DataId)} との手順を終えました（段 {this.seqBefore}→{seq}、変数 {Hex(this.varsBefore)}→{Hex(vars)}）");
                    return TaskResult.Done;
                }
            }

            if (this.interactions >= 3)
            {
                this.NoChange = true;
                ctx.Log.Warn("クエスト", $"{NpcName(this.step.DataId)} に3回話しかけても、クエストの進み具合が変わりません（既に済んだ相手の可能性）");
                return TaskResult.Done;
            }

            this.talked = false;
        }

        if (DateTime.UtcNow - this.interactedAt < TimeSpan.FromSeconds(2))
            return TaskResult.Running;

        var npc = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && o.BaseId == this.step.DataId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();
        if (npc == null)
        {
            if (this.PhaseElapsed > TimeSpan.FromSeconds(30))
                return this.Fail($"{NpcName(this.step.DataId)} が見つかりません");
            this.Status = $"{NpcName(this.step.DataId)} を探しています";
            return TaskResult.Running;
        }

        if (Vector3.Distance(npc.Position, Me.Position) > 5f)
        {
            this.arrived = false;
            this.travel = new GoToTask(Me.Territory, npc.Position, 3f, NpcName(this.step.DataId));
            return TaskResult.Running;
        }

        if (++this.interactionAttempts > 6)
            return this.Fail($"{NpcName(this.step.DataId)} に話しかけられません。距離・壁・高低差を確認してください");
        (this.seqBefore, this.varsBefore) = QuestState(this.questRowId);
        this.interactedAt = DateTime.UtcNow;

        // 着いた後は視線判定なし（Questionable と同じ。経路の終点から壁やカウンターで遮られても話しかけられるように）
        if (GameUi.Interact(npc))
        {
            this.firstInteractAt = this.interactedAt;
            this.selectedMenu = null;
            this.interactions++;
            this.talked = false;
            this.Status = $"{NpcName(this.step.DataId)} に話しかけました（{this.interactions} 回目）";
        }
        return TaskResult.Running;
    }

    internal static string Hex(byte[] v) => v.Length == 0 ? "-" : string.Join(" ", v.Select(b => b.ToString("X2")));

    public override void Cleanup(TaskContext ctx)
    {
        this.travel?.Cleanup(ctx);
        this.travel = null;
    }
}

/// <summary>
/// Questionable の単体進行の見張りの判断（受注前に「受注できない」まま頼むと、Questionable は
/// 未受注のメインクエストなど別のクエストを単体進行のまま進め続け、止まらない）。
/// 動いていて、いまのクエストが自分のクエストでない（読めない null は数えない）のを続けて <see cref="Limit"/> 回見たら「別のクエストへ移った」。
/// </summary>
public static class QuestWander
{
    /// <summary>続けて何回見たら移ったとみなすか（1フレームのずれで誤らないため。時間ではなく回数で判断する）。</summary>
    public const int Limit = 5;

    /// <param name="running">Questionable が動いているか。</param>
    /// <param name="currentId">Questionable のいまのクエスト（読めなければ null）。</param>
    /// <param name="ownId">自分のクエスト（Questionable の番号）。</param>
    /// <param name="count">これまでに続けて見た回数（更新する）。</param>
    /// <returns>移ったと決めたら true。</returns>
    public static bool Check(bool? running, string? currentId, string ownId, ref int count)
    {
        if (running != true || currentId == null || currentId == ownId)
        {
            count = 0;
            return false;
        }

        return ++count >= Limit;
    }
}

/// <summary>
/// Questionable に1本の単体進行を頼むまでの準備と、進行中の見張り（QuestTask・RunQuestTask で共通）。
///  1) 受注前なら、受けられる職（レベルが足りてギアセットのある職）に着替える。Questionable の「受注できるか」は今の職のレベルで決まるため。
///  2) Questionable が前の動作を終えるのを待つ（待っても止まらなければ、利用者の操作とみなして止める）。
///  3) 受注前なら、Questionable が「受注できる」と答えるか確かめる。だめなら理由（IsQuestLockedReason）を出して止める。
///  4) 優先リストをこのクエストだけにする（受注後に別のクエストへ移って止まらないように）。
///  5) （QuestTask のとき）TextAdvance の外部制御を取る。Questionable は自分の毎フレームの処理の中で手放すので、手放すのを状態で待つ。
///  6) 頼む。
/// 進行中は、Questionable が別のクエストへ移っていないか（<see cref="QuestWander"/>）と、優先リストが書き換わっていないかを見る。
/// </summary>
public sealed unsafe class QuestionableStarter
{
    private readonly uint questRowId;
    private readonly string label;
    private readonly bool takeTextAdvance;
    private bool jobChecked;
    private EquipJobTask? equip;
    private DateTime? busySince;
    private int wander;
    private DateTime lastKeep = DateTime.MinValue;
    private DateTime? taWaitSince;
    private bool taWarned;

    // ジャーナルの非表示の命令を最後に送った時刻（送りすぎの防止）と、送った本数（記録用）
    private DateTime lastJournalSend = DateTime.MinValue;
    private int journalHidden;
    private bool journalLogged;

    public QuestionableStarter(uint questRowId, string label, bool takeTextAdvance)
    {
        this.questRowId = questRowId;
        this.label = label;
        this.takeTextAdvance = takeTextAdvance;
    }

    /// <summary>優先リストの差し替え。</summary>
    public QuestionablePriorityGuard Priority { get; } = new();

    /// <summary>頼んだことがあるか。</summary>
    public bool Requested { get; private set; }

    /// <summary>Questionable が別のクエストへ移ったのを見たか（後始末で止める）。</summary>
    public bool SawWander { get; private set; }

    private string OwnId => QuestionableIpc.ToQuestId(this.questRowId);

    /// <summary>頼むまで進める。頼めたら Done、途中なら Running、止めるなら Failed（<paramref name="fail"/> に理由）。</summary>
    public TaskResult Start(TaskContext ctx, out string? fail, out string status)
    {
        fail = null;
        status = string.Empty;
        var qm = QuestManager.Instance();
        var accepted = qm != null && qm->IsQuestAccepted(this.questRowId);

        // 1) 受けられる職に着替える。受注済みでも合わせる（クラス・ジョブのクエストは受注した職でしか進まない。
        //    以前は受注前だけ着替えたので、止めて再開すると素材集め・製作で職が変わったまま頼み、Questionable の採集の手順が例外で止まった）
        if (!this.jobChecked)
        {
            this.jobChecked = true;
            var job = Unlocks.PickJobFor(this.questRowId);
            if (job == null)
                ctx.Log.Warn("クエスト", $"「{this.label}」を受けられる職（レベルが足りてギアセットのあるもの）が見つかりません");
            else if (job.Value != Jobs.CurrentClassJob)
            {
                ctx.Log.Write("クエスト", $"「{this.label}」を受けるため、{Jobs.Name(job.Value)} に着替えます（Questionable は今の職のレベルで受注できるかを決めるため）");
                this.equip = new EquipJobTask(job.Value);
            }
        }

        if (this.equip != null)
        {
            var r = this.equip.Step(ctx);
            status = this.equip.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.equip.Cleanup(ctx);
            var failed = r == TaskResult.Failed ? this.equip.FailReason : null;
            this.equip = null;
            if (failed != null)
            {
                fail = failed;
                return TaskResult.Failed;
            }
        }

        // 2)〜3) Questionable が前の動作を終える・動ける・ジャーナルの空き・受注できるか
        var ready = this.ReadyToAccept(ctx, accepted, out fail, out status);
        if (ready != TaskResult.Done)
            return ready;

        // 3.5) 受注中のほかのクエストをジャーナルで非表示にする（設定で切れる。控えを保存してから、1本ずつ間を空けて送る）
        if (ctx.Config.HideOtherQuestsDuringRun && this.JournalStep(ctx, out var journalStatus))
        {
            status = journalStatus;
            return TaskResult.Running;
        }

        // 4) 優先リストをこのクエストだけにする
        if (!this.Priority.Active && this.Priority.Take(ctx.Questionable, this.questRowId, ctx.Log) is { } priorityFail)
        {
            fail = priorityFail;
            return TaskResult.Failed;
        }

        // 5) TextAdvance の外部制御（QuestTask のとき）。他者（止まる途中の Questionable 等）が持っていれば、手放すのを待つ（10秒まで）
        if (this.takeTextAdvance && !ctx.TextAdvance.OwnsControl)
        {
            if (this.WaitTextAdvance(ctx, out status))
                return TaskResult.Running;

            if (!ctx.TextAdvance.TakeControlForTurnIn() && !this.taWarned)
            {
                this.taWarned = true;
                ctx.Log.Warn("クエスト", ctx.TextAdvance.IsInExternalControl() == true
                    ? "TextAdvance はほかのプラグインが外部制御しています（納品窓に TextAdvance が品を入れ、こちらの入力と取り合う可能性）"
                    : "TextAdvance の外部制御を取れませんでした（納品窓の入力が取り合いになる可能性）");
            }
        }

        // 再依頼でも必ず直前に照合する（定期監視の間隔に依存しない）。
        if (this.Priority.Keep(ctx.Questionable, ctx.Log) is { } changedPriority)
        {
            fail = changedPriority;
            return TaskResult.Failed;
        }
        if (this.takeTextAdvance && !ctx.TextAdvance.VerifyTurnInControlNow())
        {
            fail = $"TextAdvance の操作権を確保できません（{ctx.TextAdvance.LossReason ?? "理由を読めません"}）。納品入力の競合を避けるため止めました";
            return TaskResult.Failed;
        }

        // 6) 頼む
        if (!ctx.Questionable.StartSingleQuest(this.questRowId))
        {
            fail = $"Questionable が「{this.label}」を始められませんでした（経路データが無い等）";
            return TaskResult.Failed;
        }

        this.Requested = true;
        this.wander = 0;
        return TaskResult.Done;
    }

    /// <summary>
    /// Questionable が前の動作を終えるのを待ち、動ける状態を待ち、受注前ならジャーナルの空きと受注できるかを確かめる。
    /// 済めば Done、待つなら Running、止めるなら Failed（<paramref name="fail"/> に理由）。
    /// </summary>
    private TaskResult ReadyToAccept(TaskContext ctx, bool accepted, out string? fail, out string status)
    {
        fail = null;
        status = string.Empty;
        var running = ctx.Questionable.IsRunning();
        if (running == true)
            this.busySince ??= DateTime.UtcNow;
        else
            this.busySince = null;
        switch (QuestionableIdle.Decide(running, this.busySince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero))
        {
            case QuestionableIdle.Verdict.Wait:
                status = "Questionable が前の動作を終えるのを待っています";
                return TaskResult.Running;
            case QuestionableIdle.Verdict.Fail:
                fail = $"Questionable が {QuestionableIdle.Limit.TotalSeconds:0} 秒たっても動いたままです（利用者の操作を横取りしないため止めました）";
                return TaskResult.Failed;
        }

        if (!GameUi.PlayerFree())
        {
            status = "動ける状態になるのを待っています";
            return TaskResult.Running;
        }

        // 上限でも受注済みのクエストは進められる。
        var journal = QuestManager.Instance();
        if (!accepted && journal != null && journal->NumAcceptedQuests >= journal->NormalQuests.Length)
        {
            fail = $"「{this.label}」を新しく受注する空きがありません。ジャーナルの空きを作って再開してください";
            return TaskResult.Failed;
        }

        // 受注前なら、受注できるか
        if (!accepted && ctx.Questionable.IsReadyToAcceptQuest(this.questRowId) != true)
        {
            var reason = ctx.Questionable.IsQuestLockedReason(this.questRowId) is { } lr && lr.Reason.Length > 0 ? lr.Reason : "理由を読めません";
            fail = $"Questionable が「{this.label}」を受注できないと答えました（{reason}）。受注できないまま頼むと、Questionable が別のクエストを進め続けるため止めました";
            return TaskResult.Failed;
        }

        return TaskResult.Done;
    }

    /// <summary>TextAdvance の外部制御がほかの者にあれば、手放すのを待つ（10秒まで）。待つなら true。</summary>
    private bool WaitTextAdvance(TaskContext ctx, out string status)
    {
        status = string.Empty;
        if (ctx.TextAdvance.CanOwn() != false)
            return false;
        this.taWaitSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - this.taWaitSince.Value >= TimeSpan.FromSeconds(10))
            return false;
        status = "TextAdvance の外部制御が空くのを待っています";
        return true;
    }

    /// <summary>
    /// 受注をこちらで行う前の確かめ（Lv1 の「My First ～」）。Questionable に頼むときと同じ確かめ
    /// （Questionable が止まっている・動ける・ジャーナルの空き・受注できるか・TextAdvance の外部制御が空くのを10秒まで待つ）を通し、
    /// 同じ文言で止める。以前はこれを通らずに話しかけたので、ジャーナルが上限でも「段が進みません」という誤った理由で止まった。
    /// 済めば Done（TextAdvance の外部制御も取る）、待つなら Running、止めるなら Failed。
    /// </summary>
    public TaskResult ReadyForOwnAccept(TaskContext ctx, out string? fail, out string status)
    {
        var ready = this.ReadyToAccept(ctx, accepted: false, out fail, out status);
        if (ready != TaskResult.Done)
            return ready;
        if (this.takeTextAdvance && !ctx.TextAdvance.OwnsControl)
        {
            if (this.WaitTextAdvance(ctx, out status))
                return TaskResult.Running;
            ctx.TextAdvance.TakeControlForTurnIn();
        }

        return TaskResult.Done;
    }

    /// <summary>
    /// ジャーナルの非表示を1歩進める（JournalHide）。送った・間隔を待っているなら true（まだ頼まない）、終わったら false。
    /// 対象のクエストを前にこちらが隠していたら先に元へ戻し、そのあと受注中のほかのクエストを1本ずつ非表示にする。
    /// 控え（元の状態）は送る前に保存する（読み込みの解除をまたいでも戻せるように）。送れなくても控えは残す（同じクエストに送り続けない。戻すときに「非表示ではない」として消える）。
    /// </summary>
    private bool JournalStep(TaskContext ctx, out string status)
    {
        status = string.Empty;
        var records = ctx.Config.JournalHiddenByMe;
        var own = (ushort)(this.questRowId & 0xFFFF);
        var slots = JournalHide.ReadSlots();

        JournalHide.Slot? target = null;
        byte state;
        if (JournalHide.OwnRestore(slots, own, records) is { } back)
        {
            target = slots.First(s => s.QuestId == own);
            state = back;
        }
        else
        {
            var hide = JournalHide.ToHide(slots, own, records);
            if (hide.Count == 0)
            {
                if (!this.journalLogged && this.journalHidden > 0)
                {
                    this.journalLogged = true;
                    ctx.Log.Write("ジャーナル", $"受注中のほかのクエスト {this.journalHidden} 本を、実行の間だけジャーナルで非表示にしました（終わったら元の状態に戻します）");
                }

                return false;
            }

            target = hide[0];
            state = JournalHide.Hidden;
        }

        if (DateTime.UtcNow - this.lastJournalSend < JournalHide.SendSpacing)
        {
            status = "ジャーナルでほかのクエストを非表示にしています";
            return true;
        }

        var t = target.Value;
        if (state == JournalHide.Hidden)
            records[t.QuestId] = t.State;
        else
            records.Remove(t.QuestId);
        ctx.Config.Save();

        this.lastJournalSend = DateTime.UtcNow;
        var ok = JournalHide.Send(t.Index, state);
        if (state == JournalHide.Hidden)
            this.journalHidden++;
        ctx.Log.Debug("ジャーナル", state == JournalHide.Hidden
            ? $"「{JournalHide.Name(t.QuestId)}」を非表示にする命令を{(ok ? "送りました" : "送れませんでした")}（元の状態 {t.State}・受注枠 {t.Index}）"
            : $"このクエスト「{this.label}」を、前に非表示にしていたので元の状態 {state} に戻す命令を{(ok ? "送りました" : "送れませんでした")}");
        status = "ジャーナルでほかのクエストを非表示にしています";
        return true;
    }

    /// <summary>進行中の見張り（毎フレーム）。止めるべき理由があれば返す（無ければ null）。</summary>
    public string? Watch(TaskContext ctx)
    {
        if (QuestWander.Check(ctx.Questionable.IsRunning(), ctx.Questionable.GetCurrentQuestId(), this.OwnId, ref this.wander))
        {
            this.SawWander = true;
            var other = ctx.Questionable.GetCurrentQuestId();
            ctx.Questionable.Stop(Plugin.InternalNameConst);
            var name = other != null && ushort.TryParse(other, out var sid) ? Unlocks.QuestName(sid + 65536u) : other;
            return $"Questionable が別のクエスト（{name}）へ移ったので止めました（「{this.label}」を進めていません）";
        }

        // 優先リストが書き換わっていないか・TextAdvance の外部制御がまだこちらのものか（3秒おき）
        if (DateTime.UtcNow - this.lastKeep >= TimeSpan.FromSeconds(3))
        {
            this.lastKeep = DateTime.UtcNow;
            if (this.Priority.Keep(ctx.Questionable, ctx.Log) is { } keepFail)
                return keepFail;
            if (this.takeTextAdvance && ctx.TextAdvance.OwnsControl && !ctx.TextAdvance.KeepControl())
            {
                // 利用者が TextAdvance の画面で取り消した（または読み直した）なら、取り直さずに止める
                if (ctx.TextAdvance.CancelledExternally)
                    return ctx.TextAdvance.LossReason;
                ctx.Log.Warn("クエスト", $"TextAdvance の外部制御を失いました（{ctx.TextAdvance.LossReason ?? "理由を読めません"}。納品窓の入力が取り合いになる可能性）");
            }
        }

        return null;
    }

    /// <summary>後始末：自分が始めた進行（または別のクエストへ移ったのを見た進行）が動いていれば止め、優先リストを戻す。</summary>
    public void Cleanup(TaskContext ctx)
    {
        this.equip?.Cleanup(ctx);
        this.equip = null;
        if (this.Requested && ctx.Questionable.IsRunning() == true
            && (ctx.Questionable.GetCurrentQuestId() == this.OwnId || this.SawWander))
            ctx.Questionable.Stop(Plugin.InternalNameConst);
        this.Priority.Restore(ctx.Questionable, ctx.Log);
    }
}
