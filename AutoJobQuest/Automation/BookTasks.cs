using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// ジョブクエ以外のクエストを Questionable で完了させる（秘伝書の流れの「職人の新たなお仕事」用）。
/// </summary>
public sealed class RunQuestTask : AutoTask
{
    private readonly uint questRowId;
    private readonly string label;
    private bool started;
    private int restarts;
    private int notRunningFrames;

    public RunQuestTask(uint questRowId, string label)
    {
        this.questRowId = questRowId;
        this.label = label;
    }

    public override string Name => $"クエスト: {this.label}";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (QuestManager.IsQuestComplete(this.questRowId))
            return TaskResult.Done;

        if (this.Elapsed > TimeSpan.FromMinutes(30))
            return this.Fail("30分たってもクエストが完了しません");

        if (!this.started)
        {
            if (ctx.Questionable.IsRunning() == true)
                return this.Fail("Questionable がすでに動いています");
            if (!GameUi.PlayerFree())
                return TaskResult.Running;
            if (!ctx.Questionable.StartSingleQuest(this.questRowId))
                return this.Fail($"Questionable が「{this.label}」を始められませんでした（前提クエスト未完了・経路データ無しなど）");
            this.started = true;
            return TaskResult.Running;
        }

        // QuestTask と同じ：続けて3回 false を見たら止まったとみなす（時間ではなく状態で判断）
        if (ctx.Questionable.IsRunning() == false)
        {
            if (++this.notRunningFrames >= 3)
            {
                if (this.restarts++ >= 3)
                    return this.Fail("Questionable が途中で止まりました");
                this.started = false;
                this.notRunningFrames = 0;
            }
        }
        else
        {
            this.notRunningFrames = 0;
            this.Status = ctx.Questionable.GetCurrentStepData() is { } sd ? $"Questionable: {sd.InteractionType}" : "Questionable が進めています";
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.started && ctx.Questionable.IsRunning() == true
            && ctx.Questionable.GetCurrentQuestId() == QuestionableIpc.ToQuestId(this.questRowId))
            ctx.Questionable.Stop(Plugin.InternalNameConst);
    }
}

/// <summary>
/// NPC の会話メニュー（SelectString / SelectIconString）で、手がかりの文言に合う項目を選ぶ
///  ・番号では選ばない（並びはクエストの進み具合で変わる）。
///  ・空白（半角・全角）を除いて、完全一致を優先し、無ければ部分一致。**ちょうど1件のときだけ**選ぶ。
/// </summary>
public static class MenuPicker
{
    public enum Failure { None, NotFound, Ambiguous }

    public static int Resolve(IReadOnlyList<string> entries, string hint, out Failure failure)
    {
        failure = Failure.None;
        var target = GameUi.Normalize(hint);
        if (target.Length == 0)
        {
            failure = Failure.NotFound;
            return -1;
        }

        var exact = new List<int>();
        var partial = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = GameUi.Normalize(entries[i]);
            if (e.Length == 0)
                continue;
            if (string.Equals(e, target, StringComparison.OrdinalIgnoreCase))
                exact.Add(i);
            else if (e.Contains(target, StringComparison.OrdinalIgnoreCase) || target.Contains(e, StringComparison.OrdinalIgnoreCase))
                partial.Add(i);
        }

        var candidates = exact.Count > 0 ? exact : partial;
        switch (candidates.Count)
        {
            case 1:
                return candidates[0];
            case 0:
                failure = Failure.NotFound;
                return -1;
            default:
                failure = Failure.Ambiguous;
                return -1;
        }
    }
}

/// <summary>
/// NPC のところへ行って話しかけ、目的の画面が開くまで進める
/// （TickTeleport → TickNavigate → TickInteract → TickMenu の流れ）。
///
///  ・移動中でも、NPC が話しかけられる距離（5.5m）に入ったら経路の終わりを待たずに話しかける
///    （カウンターの向こうの NPC だと、経路が NPC の足元へ向かって走り続けるため）。
///  ・配置データの座標と実際の NPC の位置が 3m 以上ずれていたら、実際の位置へ経路を引き直す（3回まで）。
///  ・話しかけ：動けない状態なら待つ → ターゲット → 1秒おきに話しかける（視線判定なし）。遠ければ近づき直す（3回まで）。
///  ・会話ウィンドウ（Talk）は 0.3 秒おきに進める。
///  ・選択肢：手がかりの順に試す。同じ選択を4回・合計9回選んでも進まなければ失敗。選択肢が変わらないまま
///    2秒たっても合うものが無ければ失敗。メニューが閉じたら1度だけ話しかけ直す。
///  ・マウントに乗っていたら降りてから話しかける。
/// </summary>
public sealed unsafe class TalkToNpcTask : AutoTask
{
    private enum TalkStep { Teleport, Navigate, Interact, Menu }

    /// <summary>話しかけられる距離（5.5m。ゲームの判定は非公開なので「明らかに遠い」だけを判定する）。</summary>
    public const float InteractRange = 5.5f;

    private static readonly TimeSpan MenuSettle = TimeSpan.FromSeconds(2);

    private readonly NpcSpot spot;
    private readonly Func<bool> opened;
    private readonly string label;
    private readonly List<string> hints;

    private TalkStep step = TalkStep.Teleport;
    private AutoTask? sub;
    private Vector3 destination;
    private int destinationUpdates;
    private int reapproaches;
    private DateTime stepDeadline = DateTime.MaxValue;

    private DateTime lastTalk = DateTime.MinValue;
    private DateTime lastInteract = DateTime.MinValue;
    private DateTime lastMenu = DateTime.MinValue;
    private DateTime lastReapproach = DateTime.MinValue;
    private DateTime lastDismount = DateTime.MinValue;

    private int menuBounces;
    private int menuSelections;
    private int sameMenuSelections;
    private string lastMenuSelection = string.Empty;
    private string lastMenuSignature = string.Empty;
    private DateTime menuSignatureSince = DateTime.MinValue;

    /// <param name="hints">会話メニューが出たときに選ぶ手がかり（試す順）。</param>
    public TalkToNpcTask(NpcSpot spot, Func<bool> opened, string label, IEnumerable<string>? hints = null)
    {
        this.spot = spot;
        this.opened = opened;
        this.label = label;
        this.hints = hints?.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList() ?? [];
        this.destination = spot.Position;
    }

    public override string Name => $"話しかけ: {this.label}";

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.opened())
            return TaskResult.Done;

        if (this.Elapsed > TimeSpan.FromMinutes(10))
            return this.Fail($"{this.label} の画面を10分以内に開けませんでした");

        return this.step switch
        {
            TalkStep.Teleport => this.TickTeleport(ctx),
            TalkStep.Navigate => this.TickNavigate(ctx),
            TalkStep.Interact => this.TickInteract(ctx),
            _ => this.TickMenu(ctx),
        };
    }

    private TaskResult TickTeleport(TaskContext ctx)
    {
        this.sub ??= new TeleportTask(this.spot.Territory, this.spot.Position);
        var r = this.sub.Step(ctx);
        this.Status = this.sub.Status;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        this.sub.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
        this.sub = null;
        if (failed != null)
            return this.Fail(failed);

        this.GoTo(TalkStep.Navigate, $"{this.label} へ移動します", TimeSpan.FromMinutes(4));
        return TaskResult.Running;
    }

    private TaskResult TickNavigate(TaskContext ctx)
    {
        var live = FindNpc(this.spot.NpcId);
        if (live != null)
        {
            // 届く距離なら、経路の終わりを待たずに話しかける
            if (Vector3.Distance(live.Position, Me.Position) <= InteractRange)
            {
                this.StopSub(ctx);
                this.GoTo(TalkStep.Interact, $"{live.Name} に話しかけます", TimeSpan.FromSeconds(30));
                return TaskResult.Running;
            }

            // 配置データの座標と実際の位置が大きくずれていれば、実際の位置へ引き直す
            if (Vector3.Distance(live.Position, this.destination) > 3f && this.destinationUpdates < 3)
            {
                this.destinationUpdates++;
                this.destination = live.Position;
                this.StopSub(ctx);
                ctx.Log.Debug("会話", $"{this.label} の実際の位置へ経路を引き直します（{this.destinationUpdates} 回目）");
            }
        }

        this.sub ??= new MoveToTask(this.destination, 3f, this.label);
        var r = this.sub.Step(ctx);
        this.Status = this.sub.Status;
        if (r == TaskResult.Running)
        {
            if (DateTime.UtcNow > this.stepDeadline)
                return this.Fail($"{this.label} へ移動できませんでした（時間切れ）");
            return TaskResult.Running;
        }

        this.sub.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
        this.sub = null;

        // 届いていなくても、話しかけ側で近づき直す（それでも駄目なら失敗）
        if (failed != null)
            ctx.Log.Debug("会話", $"{this.label} への移動が途中で終わりました（{failed}）。話しかけ側で近づき直します");
        this.GoTo(TalkStep.Interact, $"{this.label} に話しかけます", TimeSpan.FromSeconds(30));
        return TaskResult.Running;
    }

    private TaskResult TickInteract(TaskContext ctx)
    {
        if (this.TryAdvanceTalk())
        {
            this.Status = "会話を進めています";
            return TaskResult.Running;
        }

        if (GameUi.MenuEntries(out _) != null)
        {
            this.GoTo(TalkStep.Menu, "会話の選択肢を選んでいます", TimeSpan.FromSeconds(45));
            return TaskResult.Running;
        }

        var npc = FindNpc(this.spot.NpcId);
        if (npc == null)
        {
            if (DateTime.UtcNow > this.stepDeadline)
                return this.Fail($"{this.label} の NPC が見つかりません");
            this.Status = $"{this.label} の NPC を探しています";
            return TaskResult.Running;
        }

        // 遠いと「話しかけられない距離です」と出るだけで進まない。実際の位置へ近づき直す
        if (Vector3.Distance(npc.Position, Me.Position) > InteractRange)
        {
            if (this.reapproaches >= 3)
                return this.Fail($"{this.label} に近づけませんでした");
            if (DateTime.UtcNow - this.lastReapproach < TimeSpan.FromSeconds(3))
                return TaskResult.Running;

            this.lastReapproach = DateTime.UtcNow;
            this.reapproaches++;
            ctx.Log.Debug("会話", $"{this.label} から離れているので近づき直します（{this.reapproaches} 回目）");
            this.destination = npc.Position;
            this.StopSub(ctx);
            this.sub = new MoveToTask(npc.Position, 2.5f, this.label);
            this.GoTo(TalkStep.Navigate, $"{this.label} へ近づき直しています", TimeSpan.FromSeconds(60));
            return TaskResult.Running;
        }

        // マウントに乗ったままなら降りる（降りきるまで待つ）
        if (GameUi.Mounted)
        {
            if (DateTime.UtcNow - this.lastDismount >= TimeSpan.FromSeconds(1))
            {
                this.lastDismount = DateTime.UtcNow;
                GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：ゲームデータで確認。CombatTask と同じ）
            }

            this.Status = "マウントから降りています";
            return TaskResult.Running;
        }

        // 動けない状態（会話の開始待ち・詠唱など）なら待つ。ターゲット → 次の呼び出しで話しかけ、を1秒おきに
        if (GameUi.PlayerFree() && DateTime.UtcNow - this.lastInteract >= TimeSpan.FromSeconds(1))
        {
            this.lastInteract = DateTime.UtcNow;
            GameUi.Interact(npc);
            this.Status = $"{npc.Name} に話しかけています";
        }

        if (DateTime.UtcNow > this.stepDeadline)
            return this.Fail($"{this.label} に話しかけても画面が開きませんでした");
        return TaskResult.Running;
    }

    private TaskResult TickMenu(TaskContext ctx)
    {
        // 選んだあとに会話が挟まることがある
        if (this.TryAdvanceTalk())
        {
            this.Status = "会話を進めています";
            return TaskResult.Running;
        }

        var entries = GameUi.MenuEntries(out var menu);
        if (entries == null)
        {
            // 選択肢が閉じただけかもしれないので、話しかけからやり直す。ただし1度だけ
            // （利用者が手で閉じた・選ぶべき項目が無い場合に、話しかけ直して永久に往復しないため）
            if (++this.menuBounces > 1)
                return this.Fail($"会話を抜けられませんでした（{this.menuBounces} 回やり直し）");
            this.GoTo(TalkStep.Interact, "話しかけ直します", TimeSpan.FromSeconds(30));
            return TaskResult.Running;
        }

        var ambiguous = false;
        foreach (var hint in this.hints)
        {
            var idx = MenuPicker.Resolve(entries, hint, out var failure);
            if (idx < 0)
            {
                ambiguous |= failure == MenuPicker.Failure.Ambiguous;
                continue;
            }

            if (DateTime.UtcNow - this.lastMenu < TimeSpan.FromMilliseconds(500))
                return TaskResult.Running;
            this.lastMenu = DateTime.UtcNow;

            // 選んでも画面が進まない（選択肢の先がまた選択肢・条件を満たさず戻される）ときに、同じ項目を選び続けない
            if (hint == this.lastMenuSelection)
            {
                this.sameMenuSelections++;
            }
            else
            {
                this.lastMenuSelection = hint;
                this.sameMenuSelections = 1;
            }

            if (++this.menuSelections > 8 || this.sameMenuSelections > 3)
            {
                return this.Fail($"「{hint}」を選んでも先へ進みませんでした（同じ選択 {this.sameMenuSelections} 回 / 合計 {this.menuSelections} 回）。"
                                 + $"選択肢: {string.Join(" / ", entries)}");
            }

            ctx.Log.Write("会話", $"選択肢「{entries[idx]}」を選びます（手がかり「{hint}」・{this.menuSelections} 回目）");
            GameUi.Fire(menu, true, idx);
            this.Status = $"「{entries[idx]}」を選びました";
            return TaskResult.Running;
        }

        // 合うものが無い。選択肢が切り替わる途中なら待つが、変わらないまま2秒たったら失敗にする
        var signature = string.Join("\u0001", entries);
        if (signature != this.lastMenuSignature)
        {
            this.lastMenuSignature = signature;
            this.menuSignatureSince = DateTime.UtcNow;
            return TaskResult.Running;
        }

        if (DateTime.UtcNow - this.menuSignatureSince <= MenuSettle && DateTime.UtcNow <= this.stepDeadline)
            return TaskResult.Running;

        return this.Fail(ambiguous
            ? $"選択肢を1つに絞れませんでした。手がかり: {string.Join(" / ", this.hints)} / 選択肢: {string.Join(" / ", entries)}"
            : $"合う選択肢がありません。手がかり: {string.Join(" / ", this.hints)} / 選択肢: {string.Join(" / ", entries)}");
    }

    private bool TryAdvanceTalk()
    {
        if (!GameUi.IsReady("Talk", out _))
            return false;
        if (DateTime.UtcNow - this.lastTalk >= TimeSpan.FromMilliseconds(300))
        {
            this.lastTalk = DateTime.UtcNow;
            GameUi.AdvanceTalk();
        }

        return true;
    }

    private void GoTo(TalkStep s, string status, TimeSpan limit)
    {
        this.step = s;
        this.stepDeadline = DateTime.UtcNow + limit;
        this.NextPhase(status);
    }

    private void StopSub(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
    }

    /// <summary>その NPC（BaseId）のうち、ターゲットできる一番近いもの。</summary>
    public static IGameObject? FindNpc(uint baseId)
        => Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && o.BaseId == baseId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, Me.Position))
            .FirstOrDefault();

    public override void Cleanup(TaskContext ctx)
    {
        this.StopSub(ctx);

        // 失敗して止まったとき、自分の操作で開いた選択肢が残っていれば閉じる（
        // 残ると次の実行のテレポが「ショップ等の画面が開いている」で待ち続ける）
        if (!this.opened())
        {
            foreach (var name in new[] { "SelectString", "SelectIconString" })
            {
                if (ctx.Ownership.TryGetOwned(name, out var own))
                {
                    DebugLog.Current?.Line("操作", $"止めたので {name} を閉じます");
                    GameUi.Fire(own, true, -1);
                }
            }
        }
    }
}

/// <summary>
/// 収集品を納品して紫貨を得る。
///
///  ・納品画面は開いた時点の「今のジョブ」のタブが出るので、先にその品のタブのジョブ（木工）に着替えてから話しかける。
///  ・一覧：AtkValues[20]＝表示行数（見出しを含む。走査の上限にだけ使う）、[33+i×11]＝行番号、[34+i×11]＝ItemId+500000。
///    行番号が 0 からの連番でない・収集品の形でない値がある・同じ行番号が2回出る、のどれかなら配置ずれとみなして撃たない。
///  ・選ぶ：Fire(12, (uint)行番号)。選べたかは右の一覧（node 31）の行数＝その品の所持数で確かめる
///    （左の一覧の選択位置は動かないので見ない）。確かめられなければ納品ボタン（node 51）が押せる状態かで代える。2秒で諦める。
///  ・渡す：Fire(15, 0u)。確認ダイアログは出ず、1回で1個。
///  ・成功は「その収集品が減った AND 紫貨が増えた」（2.5秒まで待つ）。狙っていない収集品が減ったら即停止。
///    変わらなければ1度だけ撃ち直す。
///  ・終わったら（成功でも失敗でも）自分が開いた画面だけを閉じる（1手目 Close、2手目以降 Fire(-1)。0.8秒おき）。
/// </summary>
public sealed unsafe class DeliverCollectablesTask : AutoTask
{
    private enum DeliverStep { Equip, Talk, Select, WaitTrade, Verify, Close }

    private const uint CollectableOffset = 500000;
    private const int DisplayRowCountIndex = 20;
    private const int FirstEntry = 33;
    private const int EntryStride = 11;
    private const uint HeldListNodeId = 31;
    private const uint TradeButtonNodeId = 51;

    private static readonly TimeSpan TradeReadyLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TradeReadyDumpAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VerifyLimit = TimeSpan.FromMilliseconds(2500);

    private readonly BookData data;
    private readonly NpcSpot npc;
    private DeliverStep step = DeliverStep.Equip;
    private AutoTask? sub;

    private int rowIndex;
    private int ownedBefore;
    private Dictionary<uint, int> heldBefore = [];
    private int scripsBefore;
    private DateTime selectedAt;
    private DateTime verifyUntil;
    private bool dumped;
    private bool retried;
    private int observedReward;
    private string lastButton = "未読";
    private string lastSelection = "未確認";

    private DateTime lastClose = DateTime.MinValue;
    private int closeAttempts;

    public int Delivered { get; private set; }

    public DeliverCollectablesTask(BookData data, NpcSpot npc)
    {
        this.data = data;
        this.npc = npc;
    }

    public override string Name => "収集品の納品";

    private uint Item => this.data.CollectableItemId;

    /// <summary>納品の下限を満たす手持ち。</summary>
    private int Deliverable => Inventory.CountCollectables(this.Item, this.data.MinCollectability);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        // 報酬の通貨をアイテムに直せないと、納品が成立しても紫貨の増加を読めない（片方だけ反映に見えて止まる）。先に理由をはっきりさせる
        if (SpecialCurrency.ItemId(this.data.RewardSpecialCurrencyId) == 0)
            return this.Fail($"納品の報酬の特殊通貨（番号 {this.data.RewardSpecialCurrencyId}）をアイテムに直せません（クライアントの表にも、設定の控えにもありません）");

        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.step)
        {
            case DeliverStep.Equip:
                if (this.Deliverable == 0)
                {
                    ctx.Log.Write("納品", $"納品できる {CraftPlanner.ItemName(this.Item)}（収集価値 {this.data.MinCollectability} 以上）がありません");
                    return TaskResult.Done;
                }

                this.sub ??= new EquipJobTask(this.data.CollectableTabClassJob);
                return this.RunSub(ctx, DeliverStep.Talk);

            case DeliverStep.Talk:
                this.sub ??= new TalkToNpcTask(this.npc, () => GameUi.IsReady("CollectablesShop", out _), "収集品納品窓口", [this.data.CollectablesShopName]);
                return this.RunSub(ctx, DeliverStep.Select);

            case DeliverStep.Close:
                return this.Close(ctx);
        }

        // ここから先は納品画面が開いている前提。閉じたら撃ち続けない
        if (!GameUi.IsReady("CollectablesShop", out var addon))
            return this.Fail($"納品画面が閉じました（{this.Delivered} 個まで納品済み）");

        return this.step switch
        {
            DeliverStep.Select => this.TickSelect(ctx, addon),
            DeliverStep.WaitTrade => this.TickWaitTrade(ctx, addon),
            _ => this.TickVerify(ctx),
        };
    }

    private TaskResult RunSub(TaskContext ctx, DeliverStep next)
    {
        var r = this.sub!.Step(ctx);
        this.Status = this.sub.Status;
        if (r == TaskResult.Running)
            return TaskResult.Running;
        this.sub.Cleanup(ctx);
        var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
        this.sub = null;
        if (failed != null)
            return this.Fail(failed);
        this.Go(next, string.Empty);
        return TaskResult.Running;
    }

    private void Go(DeliverStep s, string status)
    {
        this.step = s;
        this.NextPhase(status);
    }

    private TaskResult TickSelect(TaskContext ctx, AtkUnitBase* addon)
    {
        if (!TryReadOffers(addon, out var offers, out var readFailure))
            return this.Fail($"納品画面の一覧を読めません：{readFailure}");

        var held = Inventory.HeldCollectables();
        var owned = held.GetValueOrDefault(this.Item);
        if (owned <= 0 || this.Deliverable == 0)
        {
            ctx.Log.Write("納品", $"納品を終えました（{this.Delivered} 個）");
            this.Go(DeliverStep.Close, "納品画面を閉じます");
            return TaskResult.Running;
        }

        // 溢れた分は捨てられるので、一番多くもらえる場合で上限を見る
        var scrips = Inventory.CountSpecialCurrency(this.data.RewardSpecialCurrencyId, out _);
        var cap = ScripCap(this.data.RewardSpecialCurrencyId);
        var gain = Math.Max(this.observedReward, this.data.RewardHigh);
        if (cap > 0 && scrips + gain > cap)
        {
            ctx.Log.Warn("納品", $"紫貨が上限に近いので納品をやめます（{scrips}/{cap}、次の納品で最大 +{gain}）");
            this.Go(DeliverStep.Close, "納品画面を閉じます");
            return TaskResult.Running;
        }

        var offer = offers.FirstOrDefault(o => o.ItemId == this.Item);
        if (offer.ItemId == 0)
            return this.Fail($"納品画面に {CraftPlanner.ItemName(this.Item)} が出ていません（ジョブのタブが違う可能性。読めた品：{string.Join("、", offers.Select(o => CraftPlanner.ItemName(o.ItemId)))}）");

        this.rowIndex = offer.Row;
        this.ownedBefore = owned;
        this.heldBefore = held;
        this.scripsBefore = scrips;
        this.selectedAt = DateTime.UtcNow;
        this.lastButton = "未読";
        this.lastSelection = "未確認";

        ctx.Log.Write("納品", $"{CraftPlanner.ItemName(this.Item)} を選びます（行 {offer.Row}、所持 {owned}）");
        GameUi.Fire(addon, true, 12, (uint)offer.Row); // 実測は UInt（Int だと型が食い違う）
        this.Go(DeliverStep.WaitTrade, "選択が効くのを待っています");
        return TaskResult.Running;
    }

    private TaskResult TickWaitTrade(TaskContext ctx, AtkUnitBase* addon)
    {
        // 右の一覧（node 31）の行数＝その品の所持数なら、狙った品が選ばれている
        var comp = addon->GetComponentByNodeId(HeldListNodeId);
        if (comp != null && comp->GetComponentType() == ComponentType.List)
        {
            var rows = ((AtkComponentList*)comp)->ListLength;
            this.lastSelection = $"手持ちの一覧 {rows} 行 / 所持 {this.ownedBefore}";
            if (rows > 0 && rows == this.ownedBefore)
                return this.FireDelivery(ctx, addon, this.lastSelection);
        }
        else
        {
            this.lastSelection = $"手持ちの一覧（node {HeldListNodeId}）を取れません";
        }

        // 確かめられない間は、納品ボタンが押せる状態かで代える
        var btn = addon->GetComponentByNodeId(TradeButtonNodeId);
        if (btn != null && btn->GetComponentType() == ComponentType.Button && btn->OwnerNode != null)
        {
            var visible = btn->OwnerNode->AtkResNode.IsVisible();
            var enabled = ((AtkComponentButton*)btn)->IsEnabled;
            this.lastButton = $"見える={visible} 押せる={enabled}";
            if (visible && enabled)
                return this.FireDelivery(ctx, addon, $"納品ボタンが押せる状態（{this.lastButton}）");
        }

        var waited = DateTime.UtcNow - this.selectedAt;
        if (waited >= TradeReadyDumpAfter && !this.dumped)
        {
            this.dumped = true;
            DebugLog.Current?.Block("納品", "選んだあとの納品画面", AddonRecorder.Describe(addon));
        }

        if (waited >= TradeReadyLimit)
            return this.Fail($"{CraftPlanner.ItemName(this.Item)} を選びましたが納品できる状態になりませんでした（ボタン: {this.lastButton} / 選択: {this.lastSelection}）");
        return TaskResult.Running;
    }

    private TaskResult FireDelivery(TaskContext ctx, AtkUnitBase* addon, string note)
    {
        ctx.Log.Debug("納品", $"納品を撃ちます（{note}）");
        GameUi.Fire(addon, true, 15, 0u); // 実測：第2引数は行番号に関わらず 0u
        this.verifyUntil = DateTime.UtcNow + VerifyLimit;
        this.Go(DeliverStep.Verify, "納品の反映を待っています");
        return TaskResult.Running;
    }

    private TaskResult TickVerify(TaskContext ctx)
    {
        var heldAfter = Inventory.HeldCollectables();
        var ownedAfter = heldAfter.GetValueOrDefault(this.Item);

        // 狙っていない収集品が減った＝別の品を渡した。続けると被害が積み上がるので止める
        if (heldAfter.Count > 0)
        {
            foreach (var (id, before) in this.heldBefore)
            {
                if (id != this.Item && heldAfter.GetValueOrDefault(id) < before)
                    return this.Fail($"狙っていない収集品が減りました（{CraftPlanner.ItemName(id)} {before}→{heldAfter.GetValueOrDefault(id)}）。取り違えの恐れがあるため止めました");
            }
        }

        var scrips = Inventory.CountSpecialCurrency(this.data.RewardSpecialCurrencyId, out _);
        var ok = ownedAfter < this.ownedBefore && scrips > this.scripsBefore;
        if (!ok && DateTime.UtcNow < this.verifyUntil)
            return TaskResult.Running;

        if (!ok)
        {
            if (ownedAfter < this.ownedBefore || scrips != this.scripsBefore)
                return this.Fail($"納品の結果が片方しか反映されていません（{CraftPlanner.ItemName(this.Item)} {this.ownedBefore}→{ownedAfter}、紫貨 {this.scripsBefore}→{scrips}）");

            if (!this.retried)
            {
                this.retried = true;
                ctx.Log.Warn("納品", $"{CraftPlanner.ItemName(this.Item)} が納品されなかったので、もう一度試します");
                this.Go(DeliverStep.Select, "選び直します");
                return TaskResult.Running;
            }

            var values = string.Join(" / ", Inventory.Collectabilities(this.Item));
            return this.Fail($"{CraftPlanner.ItemName(this.Item)} を納品できませんでした（所持 {this.ownedBefore} のまま・紫貨の増加なし。収集価値 {values} / ボタン: {this.lastButton} / 選択: {this.lastSelection}）");
        }

        this.Delivered++;
        this.retried = false;
        this.observedReward = scrips - this.scripsBefore;
        ctx.Log.Write("納品", $"{CraftPlanner.ItemName(this.Item)} を納品しました（{this.ownedBefore}→{ownedAfter}、紫貨 {this.scripsBefore}→{scrips} ＋{this.observedReward}）");
        this.Go(DeliverStep.Select, string.Empty);
        return TaskResult.Running;
    }

    private TaskResult Close(TaskContext ctx)
    {
        if (!GameUi.IsVisible("CollectablesShop"))
        {
            if (this.closeAttempts > 0)
                ctx.Log.Debug("納品", $"納品画面を閉じました（{this.closeAttempts} 手目）");
            return TaskResult.Done;
        }

        // 自分が開いたものでなければ触らない
        if (!ctx.Ownership.TryGetOwned("CollectablesShop", out var addon))
        {
            ctx.Log.Warn("納品", "納品画面は自分が開いたものではないため閉じません");
            return TaskResult.Done;
        }

        if (this.TimedOut(TimeSpan.FromSeconds(10)))
        {
            ctx.Log.Warn("納品", "納品画面を閉じられませんでした。手で閉じてください");
            return TaskResult.Done;
        }

        if (DateTime.UtcNow - this.lastClose < TimeSpan.FromMilliseconds(800))
            return TaskResult.Running;
        this.lastClose = DateTime.UtcNow;

        if (++this.closeAttempts == 1)
            addon->Close(true);
        else
            GameUi.Fire(addon, true, -1);
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;
        if (ctx.Ownership.TryGetOwned("CollectablesShop", out var addon))
        {
            DebugLog.Current?.Line("操作", "止めたので納品画面を閉じます");
            addon->Close(true);
        }

        ctx.Ownership.Clear();
    }

    private readonly record struct Offer(int Row, uint ItemId);

    /// <summary>
    /// 納品画面の一覧を読む。
    /// 欠けたまま返すと行番号を取り違えて別の品を渡すので、形が崩れていたら失敗にする。
    /// </summary>
    private static bool TryReadOffers(AtkUnitBase* addon, out List<Offer> offers, out string failure)
    {
        offers = [];
        failure = string.Empty;
        var total = addon->AtkValuesCount;
        if (addon->AtkValues == null || total <= DisplayRowCountIndex)
        {
            failure = "画面の値を読み取れませんでした";
            return false;
        }

        var displayRows = ReadUInt(addon->AtkValues[DisplayRowCountIndex]);
        if (displayRows == 0)
        {
            failure = "納品できる品がありません";
            return false;
        }

        var seen = new HashSet<uint>();
        var emptyRun = 0;
        for (var i = 0u; i < displayRows * 2; i++)
        {
            var indexAt = FirstEntry + (int)(i * EntryStride);
            var itemAt = indexAt + 1;
            if (itemAt >= total)
                break;

            var row = ReadUInt(addon->AtkValues[indexAt]);
            var raw = ReadUInt(addon->AtkValues[itemAt]);
            if (raw == 0)
            {
                // 見出しの行（次の品の番号だけが入る）。一覧の終わりにも続くので、続いたら打ち切る
                if (offers.Count > 0 && ++emptyRun >= 3)
                    break;
                continue;
            }

            emptyRun = 0;
            if (raw < CollectableOffset)
            {
                failure = $"位置 {i} のアイテムが収集品の形をしていません（値 {raw}）";
                return false;
            }

            if (!seen.Add(row))
            {
                failure = $"行番号 {row} が複数の位置にあります";
                return false;
            }

            offers.Add(new Offer((int)row, raw - CollectableOffset));
            if (offers.Count >= displayRows)
            {
                failure = $"品目の件数が表示行数 {displayRows} を超えました";
                return false;
            }
        }

        if (offers.Count == 0)
        {
            failure = "納品できる品を1件も読み取れませんでした";
            return false;
        }

        for (var i = 0; i < offers.Count; i++)
        {
            if (offers[i].Row != i)
            {
                failure = $"行番号が連番になっていません（{i} 番目の行番号が {offers[i].Row}）";
                return false;
            }
        }

        return true;
    }

    private static uint ReadUInt(AtkValue v) => v.Type switch
    {
        AtkValueType.UInt => v.UInt,
        AtkValueType.Int => v.Int < 0 ? 0u : (uint)v.Int,
        _ => 0u,
    };

    public static int ScripCap(byte specialId)
    {
        var cm = CurrencyManager.Instance();
        var id = SpecialCurrency.ItemId(specialId);
        if (cm == null || id == 0)
            return 0;
        if (cm->IsItemLimited(id))
        {
            var max = cm->GetItemMaxCount(id);
            if (max > 0)
                return (int)max;
        }

        return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var row) ? (int)row.StackSize : 0;
    }
}

/// <summary>
/// 紫貨で秘伝書を交換する（アイテム交換画面 InclusionShop）。
///
///  ・系統：いま開いている店の系統のうち、秘伝書の店（SpecialShop）を含む系列の系統を、行 ID で選ぶ（500ms おき）。
///  ・種別：いま選ばれている系列の中で、その店が何番目か（InclusionShopSeries の subrow）をシートから求め、+1 したタブを選ぶ
///    （先頭に「選択してください」が入るため。700ms おき）。一覧に目的の品が出たのを見てから撃つ。
///  ・撃つ前：確認窓などが残っていない／index に重複が無い／目的の品がちょうど1件／値段がゲームデータと同じ／
///    払う通貨が紫貨／画面の通貨の所持数がカバンの紫貨と同じ（ずれていれば少し待つ）／紫貨が足りる／カバンに空きがある。
///  ・撃つ：Fire(14, (uint)index, 1u)。続いて ShopExchangeItemDialog の「交換する」（node 18）→ 品によって SelectYesno。
///    SelectYesno は「本文に通貨名と値段がある」か「撃った後に自分の操作で開いたもので、危ない語を含まない」ときだけ「はい」。
///  ・成功は「秘伝書が増えた AND 紫貨が減った」（15秒まで）。何も動かなければ（習得済みなど）その巻を飛ばす。片方だけなら止める。
/// </summary>
public sealed unsafe class ExchangeBooksTask : AutoTask
{
    private enum ExStep { Talk, Select, Fire, Outcome, Close }

    private const int PinnedCurrencyIndex = 297;
    private const int ItemCountIndex = 298;
    private const int ItemsBase = 299;
    private const int ItemStride = 18;
    private const uint ExchangeButtonNode = 18; // ShopExchangeItemDialog の「交換する」（ECommons と同じ）

    private static readonly TimeSpan OutcomeLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OwnedDialogWindow = TimeSpan.FromSeconds(10);

    /// <summary>撃つ前に開いていてはいけない画面。</summary>
    private static readonly string[] BlockingAddons =
        ["ShopExchangeCurrencyDialog", "ShopExchangeItemDialog", "SelectYesno", "SelectString", "SelectIconString", "Talk", "_TextInput"];

    /// <summary>本文にこれが出ていたら、交換の確認ではないとみなして押さない。</summary>
    private static readonly string[] DangerousWords = ["捨て", "破棄", "削除", "分解", "精製", "売却", "ログアウト", "タイトル", "トレード"];

    private readonly BookData data;
    private readonly NpcSpot npc;
    private readonly Queue<BookOffer> queue;

    private ExStep step = ExStep.Talk;
    private AutoTask? sub;
    private BookOffer? current;
    private uint scripItemId;
    private int beforeBooks;
    private int beforeScrips;
    private DateTime firedAt = DateTime.MinValue;
    private DateTime lastClick = DateTime.MinValue;
    private DateTime lastCategory = DateTime.MinValue;
    private DateTime lastSubCategory = DateTime.MinValue;
    private DateTime lastUnmatchedLog = DateTime.MinValue;
    private DateTime lastClose = DateTime.MinValue;
    private int closeAttempts;

    public ExchangeBooksTask(BookData data, NpcSpot npc, IEnumerable<BookOffer> books)
    {
        this.data = data;
        this.npc = npc;
        this.queue = new Queue<BookOffer>(books);
    }

    public override string Name => "秘伝書の交換";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        foreach (var o in this.queue)
        {
            if (SpecialCurrency.ItemId(o.SpecialCurrencyId) == 0)
                return this.Fail($"{CraftPlanner.ItemName(o.BookItemId)} の値段の特殊通貨（番号 {o.SpecialCurrencyId}）をアイテムに直せません（クライアントの表にも、設定の控えにもありません）");
        }

        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.step)
        {
            case ExStep.Talk:
            {
                if (!this.PickNext(ctx))
                    return TaskResult.Done;

                this.sub ??= new TalkToNpcTask(this.npc, IsShopReady, "スクリップ取引窓口", this.MenuHints());
                var r = this.sub.Step(ctx);
                this.Status = this.sub.Status;
                if (r == TaskResult.Running)
                    return TaskResult.Running;
                this.sub.Cleanup(ctx);
                var failed = r == TaskResult.Failed ? this.sub.FailReason : null;
                this.sub = null;
                if (failed != null)
                    return this.Fail(failed);
                this.Go(ExStep.Select, "系統と種別を選びます");
                return TaskResult.Running;
            }

            case ExStep.Select:
                return this.TickSelect(ctx);

            case ExStep.Fire:
                return this.TickFire(ctx);

            case ExStep.Outcome:
                return this.TickOutcome(ctx);

            default:
                return this.Close(ctx);
        }
    }

    private void Go(ExStep s, string status)
    {
        this.step = s;
        this.NextPhase(status);
    }

    /// <summary>次に交換する巻を決める。習得済み・所持済みは飛ばす。無ければ false。</summary>
    private bool PickNext(TaskContext ctx)
    {
        while (this.current == null || IsLearned(this.current.TomeId) || Inventory.CountNow(this.current.BookItemId) > 0)
        {
            if (this.current != null)
                ctx.Log.Debug("交換", $"{CraftPlanner.ItemName(this.current.BookItemId)} は習得済みか所持済みなので飛ばします");
            if (this.queue.Count == 0)
            {
                this.current = null;
                return false;
            }

            this.current = this.queue.Dequeue();
        }

        return true;
    }

    /// <summary>会話メニューの手がかり：店の名前（SpecialShop.Name）→ 系統の名前の順。</summary>
    private IEnumerable<string> MenuHints()
    {
        var shops = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SpecialShop>();
        var cats = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.InclusionShopCategory>();
        var offers = new List<BookOffer>();
        if (this.current != null)
            offers.Add(this.current);
        offers.AddRange(this.queue);

        foreach (var o in offers)
            if (shops.TryGetRow(o.ShopId, out var s))
                yield return s.Name.ExtractText();

        foreach (var c in this.data.BookCategories)
            if (cats.TryGetRow(c, out var row))
                yield return row.Name.ExtractText();
    }

    private static bool IsShopReady()
    {
        if (!GameUi.IsReady("InclusionShop", out _))
            return false;
        var agent = AgentInclusionShop.Instance();
        return agent != null && agent->IsAgentActive() && agent->Data != null && agent->Data->IsShopReady;
    }

    // ---- 系統・種別 ----

    private TaskResult TickSelect(TaskContext ctx)
    {
        if (!this.PickNext(ctx))
        {
            this.Go(ExStep.Close, "交換画面を閉じます");
            return TaskResult.Running;
        }

        if (!IsShopReady())
        {
            if (this.TimedOut(TimeSpan.FromSeconds(30)))
                return this.Fail("アイテム交換の画面が開いていません");
            return TaskResult.Running;
        }

        var agent = AgentInclusionShop.Instance();
        var d = agent->Data;
        var shopId = this.current!.ShopId;

        // いま開いている店の系統のうち、秘伝書の店を含む系列の系統（表示位置）
        var series = Svc.Data.GetSubrowExcelSheet<Lumina.Excel.Sheets.InclusionShopSeries>();
        int targetPos = -1;
        ushort targetSeries = 0;
        for (byte i = 0; i < d->CategoryCount && i < 30; i++)
        {
            var mapped = d->CategoryIndexMap[i];
            if (mapped >= 30)
                continue;
            var cat = d->Categories[mapped];
            if (SeriesIndexOf(series, cat.InclusionShopSeriesId, shopId) >= 0)
            {
                targetPos = i;
                targetSeries = cat.InclusionShopSeriesId;
                break;
            }
        }

        // 画面の準備ができた直後は系統の一覧がまだ空のことがある（推測）ので、上限まで待ってから諦める（30 秒）
        if (targetPos < 0)
        {
            this.Status = "交換画面の系統が出そろうのを待っています";
            return this.TimedOut(TimeSpan.FromSeconds(30))
                ? this.Fail($"この窓口の交換画面に {CraftPlanner.ItemName(this.current.BookItemId)} の店（SpecialShop {shopId}）を含む系統がありません")
                : TaskResult.Running;
        }

        if (d->SelectedCategoryIndex != targetPos)
        {
            if (DateTime.UtcNow - this.lastCategory >= TimeSpan.FromMilliseconds(500))
            {
                this.lastCategory = DateTime.UtcNow;
                ctx.Log.Debug("交換", $"系統を選びます（表示位置 {targetPos}）");
                agent->SelectCategory((byte)targetPos);
            }

            return this.TimedOut(TimeSpan.FromSeconds(30)) ? this.Fail("系統を切り替えられませんでした") : TaskResult.Running;
        }

        // 系統は合っている。目的の品が一覧に出ていれば撃つ段へ
        if (GameUi.IsReady("InclusionShop", out var addon) && ReadEntries(addon, out var entries, out _, out _)
            && entries.Any(e => e.ItemId == this.current.BookItemId))
        {
            this.Go(ExStep.Fire, "交換の直前確認をしています");
            return TaskResult.Running;
        }

        // 出ていなければ種別を切り替える（切り替えたことを成功にせず、一覧が入れ替わるのを見てから判断する）
        if (DateTime.UtcNow - this.lastSubCategory >= TimeSpan.FromMilliseconds(700))
        {
            this.lastSubCategory = DateTime.UtcNow;
            var index = SeriesIndexOf(series, targetSeries, shopId);
            var tab = (byte)(index + 1); // 先頭の「選択してください」の分ずらす
            if (index < 0 || tab >= d->VisibleSubCategoryCount)
            {
                ctx.Log.Debug("交換", $"種別 {tab} は選べません（表示 {d->VisibleSubCategoryCount} 件）");
            }
            else if (d->SelectedSubCategoryTab != tab)
            {
                ctx.Log.Debug("交換", $"種別を選びます（タブ {tab}）");
                d->SelectSubCategory(tab);
            }
        }

        if (this.TimedOut(TimeSpan.FromSeconds(30)))
            return this.Fail($"アイテム交換画面に {CraftPlanner.ItemName(this.current.BookItemId)} が出てきませんでした");
        return TaskResult.Running;
    }

    /// <summary>系列（InclusionShopSeries の行）の中で、その店が何番目か。無ければ -1。</summary>
    private static int SeriesIndexOf(Lumina.Excel.SubrowExcelSheet<Lumina.Excel.Sheets.InclusionShopSeries> sheet, uint seriesId, uint shopId)
    {
        if (!sheet.TryGetRow(seriesId, out var rows))
            return -1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].SpecialShop.RowId == shopId)
                return i;
        return -1;
    }

    // ---- 撃つ ----

    private TaskResult TickFire(TaskContext ctx)
    {
        var offer = this.current!;

        // 残っている確認窓などがあれば、前の巻を撃った後に自分の操作で開いたものだけ閉じる。それ以外は止める
        // （移動・会話の間に利用者や他のプラグインが出した確認まで「自分のもの」に含まれうるため、撃った時刻で絞る）
        foreach (var name in BlockingAddons)
        {
            if (!GameUi.IsReady(name, out _))
                continue;
            if (this.firedAt != DateTime.MinValue && ctx.Ownership.TryGetOwnedSince(name, this.firedAt, out var own))
            {
                if (DateTime.UtcNow - this.lastClose >= TimeSpan.FromMilliseconds(500))
                {
                    this.lastClose = DateTime.UtcNow;
                    DebugLog.Current?.Line("操作", $"撃つ前に残っていた自分の {name} を閉じます");
                    GameUi.Fire(own, true, -1);
                }

                return this.TimedOut(TimeSpan.FromSeconds(10)) ? this.Fail($"{name} が閉じません") : TaskResult.Running;
            }

            return this.Fail($"{name} が開いています（交換で自分が開いたものではないので触りません）。閉じてから始めてください");
        }

        if (!GameUi.IsReady("InclusionShop", out var addon))
            return this.Fail("アイテム交換の画面が閉じられました");
        if (!ReadEntries(addon, out var entries, out var currencyOnScreen, out var readFailure))
            return this.Fail(readFailure);

        if (entries.Select(e => e.Index).Distinct().Count() != entries.Count)
            return this.Fail("交換画面の index に重複があります（配置がずれている可能性）");

        var matches = entries.Where(e => e.ItemId == offer.BookItemId).ToList();
        if (matches.Count != 1)
            return this.Fail(matches.Count == 0
                ? $"交換画面に {CraftPlanner.ItemName(offer.BookItemId)} がありません（読めたのは {entries.Count} 件）"
                : $"交換画面に {CraftPlanner.ItemName(offer.BookItemId)} が {matches.Count} 件あり、どれか決められません");

        var entry = matches[0];
        if (entry.Cost != offer.Price)
            return this.Fail($"値段がゲームデータと違います（画面 {entry.Cost} / データ {offer.Price}）");

        var scrips = Inventory.CountSpecialCurrency(offer.SpecialCurrencyId, out this.scripItemId);
        if (ResolveCurrency(entry.CostItem) is not { } costItem || costItem != this.scripItemId)
            return this.Fail($"払う通貨が紫貨ではありません（画面の値 {entry.CostItem}）");

        // 画面の所持通貨がカバンの紫貨と同じか（別の通貨の画面を撃たないため）。更新が遅れることがあるので少し待つ
        if (currencyOnScreen != scrips)
        {
            this.Status = $"画面の通貨（{currencyOnScreen}）とカバンの紫貨（{scrips}）がそろうのを待っています";
            return this.TimedOut(TimeSpan.FromSeconds(5))
                ? this.Fail($"画面の通貨（{currencyOnScreen}）がカバンの紫貨（{scrips}）と合いません")
                : TaskResult.Running;
        }

        if (scrips < offer.Price)
            return this.Fail($"紫貨が足りません（{scrips}/{offer.Price}）");
        // 2枠は残す（空きが足りないキャラは AutoRetainer が処理から外すため）
        if (Inventory.FreeBagSlots() < 3)
            return this.Fail("カバンの空きが足りません（交換のあとも2枠残るよう、3枠以上空けてください）");

        this.beforeBooks = Inventory.CountNow(offer.BookItemId);
        this.beforeScrips = scrips;
        ctx.Log.Write("交換", $"{CraftPlanner.ItemName(offer.BookItemId)} を交換します（紫貨 {offer.Price}・index {entry.Index}）");
        GameUi.Fire(addon, true, 14, entry.Index, 1u); // 実測：コマンドは Int、index と個数は UInt
        this.firedAt = DateTime.UtcNow;
        this.Go(ExStep.Outcome, "確認に答えています");
        return TaskResult.Running;
    }

    // ---- 確認と結果 ----

    private TaskResult TickOutcome(TaskContext ctx)
    {
        var offer = this.current!;
        var books = Inventory.CountNow(offer.BookItemId);
        var scrips = Inventory.CountSpecialCurrency(offer.SpecialCurrencyId, out _);

        if (books > this.beforeBooks && scrips <= this.beforeScrips - (int)offer.Price)
        {
            ctx.Log.Write("交換", $"{CraftPlanner.ItemName(offer.BookItemId)} を交換しました（紫貨 {this.beforeScrips}→{scrips}）");
            this.current = null;
            this.Go(ExStep.Select, string.Empty);
            return TaskResult.Running;
        }

        // 個数を選ぶ画面は想定外（1個ずつしか撃たない）。閉じて止める
        if (GameUi.IsReady("ShopExchangeCurrencyDialog", out var qty))
        {
            GameUi.Fire(qty, true, -1);
            return this.Fail("個数を選ぶ画面が出ました（想定外）。閉じて止めました");
        }

        // 交換の確認（撃った直後に出る）。押しても消えないまま時間が来たら、閉じて止める
        if (GameUi.IsReady("ShopExchangeItemDialog", out var dialog))
        {
            if (DateTime.UtcNow - this.firedAt >= OutcomeLimit)
            {
                DebugLog.Current?.Block("交換", "消えない交換の確認", AddonRecorder.Describe(dialog));
                GameUi.Fire(dialog, true, -1);
                return this.Fail("交換の確認（交換する）を押しても進みませんでした。確認を閉じて止めました");
            }

            if (DateTime.UtcNow - this.lastClick >= TimeSpan.FromMilliseconds(400))
            {
                this.lastClick = DateTime.UtcNow;
                GameUi.ClickButton(dialog, ExchangeButtonNode);
            }

            return TaskResult.Running;
        }

        // 品によっては、さらに「はい／いいえ」が続く
        if (this.TryFindConfirm(ctx, offer, out var yesno, out var body))
        {
            if (DateTime.UtcNow - this.firedAt >= OutcomeLimit)
                return this.Fail($"確認に「はい」と答えても進みませんでした：{body}");

            if (DateTime.UtcNow - this.lastClick >= TimeSpan.FromMilliseconds(500))
            {
                this.lastClick = DateTime.UtcNow;
                ctx.Log.Write("交換", $"確認に「はい」と答えます：{body}");
                if (!GameUi.ClickYes(yesno))
                    return this.Fail($"確認の「はい」が押せる状態ではありません：{body}");
            }

            return TaskResult.Running;
        }

        // 画面が閉じた（話しかけられる距離から外れた等）なら、待っても結果は出ない
        if (!GameUi.IsVisible("InclusionShop"))
            return this.Fail("交換の途中でアイテム交換の画面が閉じました（話しかけられる距離から外れた可能性）");

        if (DateTime.UtcNow - this.firedAt < OutcomeLimit)
            return TaskResult.Running;

        if (books == this.beforeBooks && scrips == this.beforeScrips)
        {
            // 何も動いていない＝ゲームが受け付けなかった（習得済みの秘伝書は、確認まで通って何も起きない：実測）
            ctx.Log.Warn("交換", $"{CraftPlanner.ItemName(offer.BookItemId)} は交換されませんでした（所持数が動いていません。習得済みの可能性）。この巻は飛ばします");
            this.current = null;
            this.Go(ExStep.Select, string.Empty);
            return TaskResult.Running;
        }

        return this.Fail($"交換の結果が片方しか反映されていません（{CraftPlanner.ItemName(offer.BookItemId)} {this.beforeBooks}→{books}、紫貨 {this.beforeScrips}→{scrips}）");
    }

    /// <summary>
    /// 押してよい SelectYesno を2段で探す。
    ///  1) 本文に通貨名と値段の両方がある。
    ///  2) 撃った後に自分の操作で開いたもので、撃ってから 10 秒以内、かつ危ない語を含まない（本文は記録に残す）。
    /// </summary>
    private bool TryFindConfirm(TaskContext ctx, BookOffer offer, out AtkUnitBase* addon, out string body)
    {
        addon = null;
        body = GameUi.YesnoText(out var yesno) ?? string.Empty;
        if (yesno == null)
            return false;

        var currencyName = CraftPlanner.ItemName(this.scripItemId);
        var shown = body;
        var priceTexts = new[] { offer.Price.ToString(), offer.Price.ToString("N0") };
        if (currencyName.Length > 0 && shown.Contains(currencyName, StringComparison.Ordinal)
            && priceTexts.Any(p => shown.Contains(p, StringComparison.Ordinal)))
        {
            addon = yesno;
            return true;
        }

        if (DateTime.UtcNow - this.firedAt <= OwnedDialogWindow && ctx.Ownership.TryGetOwnedSince("SelectYesno", this.firedAt, out var own))
        {
            var bad = DangerousWord(body);
            if (bad != null)
            {
                ctx.Log.Warn("交換", $"確認に「{bad}」が含まれるので押しません：{body}");
                return false;
            }

            ctx.Log.Warn("交換", $"確認の本文が想定と違いますが、撃った直後に自分の操作で開いたものなので答えます：{body}（探した語：{currencyName}・{offer.Price}）");
            addon = own;
            return true;
        }

        if (DateTime.UtcNow - this.lastUnmatchedLog >= TimeSpan.FromSeconds(5))
        {
            this.lastUnmatchedLog = DateTime.UtcNow;
            ctx.Log.Warn("交換", $"確認が出ていますが、交換のものと判断できないので押しません：{body}");
        }

        return false;
    }

    // ---- 閉じる ----

    private TaskResult Close(TaskContext ctx)
    {
        if (!GameUi.IsVisible("InclusionShop"))
            return TaskResult.Done;

        if (!ctx.Ownership.TryGetOwned("InclusionShop", out var addon))
        {
            ctx.Log.Warn("交換", "アイテム交換の画面は自分が開いたものではないため閉じません");
            return TaskResult.Done;
        }

        if (this.TimedOut(TimeSpan.FromSeconds(10)))
        {
            ctx.Log.Warn("交換", "アイテム交換の画面を閉じられませんでした。手で閉じてください");
            return TaskResult.Done;
        }

        if (DateTime.UtcNow - this.lastClose < TimeSpan.FromMilliseconds(800))
            return TaskResult.Running;
        this.lastClose = DateTime.UtcNow;

        if (++this.closeAttempts == 1)
            addon->Close(true);
        else
            GameUi.Fire(addon, true, -1);
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.sub?.Cleanup(ctx);
        this.sub = null;

        // 自分が開いたものだけを閉じる（確認窓は「いいえ」相当の -1、交換画面は Close）
        foreach (var name in new[] { "SelectYesno", "ShopExchangeCurrencyDialog", "ShopExchangeItemDialog" })
        {
            if (ctx.Ownership.TryGetOwned(name, out var own))
            {
                DebugLog.Current?.Line("操作", $"止めたので {name} を閉じます");
                GameUi.Fire(own, true, -1);
            }
        }

        if (ctx.Ownership.TryGetOwned("InclusionShop", out var shop))
        {
            DebugLog.Current?.Line("操作", "止めたのでアイテム交換の画面を閉じます");
            shop->Close(true);
        }

        ctx.Ownership.Clear();
    }

    // ---- 画面の読み取り ----

    private readonly record struct Entry(uint ItemId, uint CostItem, uint Cost, uint Index);

    /// <summary>
    /// 交換画面の品を読む（AddonInclusionShop の配置）。
    /// [297]＝表示中の通貨の所持数、[298]＝件数、[299+i×18 +1]＝ItemId、[+6]＝払う通貨、[+12]＝値段、[+17]＝撃つ index。
    /// </summary>
    private static bool ReadEntries(AtkUnitBase* addon, out List<Entry> entries, out long currencyOnScreen, out string failure)
    {
        entries = [];
        currencyOnScreen = GameUi.AtkInt(addon, PinnedCurrencyIndex) ?? -1;
        failure = string.Empty;

        if (GameUi.AtkInt(addon, ItemCountIndex) is not { } count)
        {
            failure = "交換画面の件数を読めません";
            return false;
        }

        for (var i = 0; i < Math.Min(count, 60); i++)
        {
            var b = ItemsBase + (i * ItemStride);
            var item = GameUi.AtkInt(addon, b + 1);
            if (item is null or 0)
                continue;
            var costItem = GameUi.AtkInt(addon, b + 6);
            var cost = GameUi.AtkInt(addon, b + 12);
            var index = GameUi.AtkInt(addon, b + 17);
            if (costItem == null || cost == null || index == null)
                continue;
            entries.Add(new Entry((uint)item.Value, (uint)costItem.Value, (uint)cost.Value, (uint)index.Value));
        }

        return true;
    }

    /// <summary>本文に押してはいけない語（捨てる・売る・ログアウト等）があればその語、無ければ null。</summary>
    public static string? DangerousWord(string body)
        => DangerousWords.FirstOrDefault(w => body.Contains(w, StringComparison.Ordinal));

    /// <summary>画面の「払う通貨」の値をアイテム ID に直す（8 以上ならそのまま、未満なら特殊通貨の番号）。</summary>
    private static uint? ResolveCurrency(uint value)
    {
        if (value >= 8)
            return value;
        var id = SpecialCurrency.ItemId((byte)value);
        return id == 0 ? null : id;
    }

    public static bool IsLearned(uint tomeId)
    {
        try
        {
            var ps = PlayerState.Instance();
            return ps == null || ps->IsSecretRecipeBookUnlocked(tomeId);
        }
        catch
        {
            return true; // 読めないときは「習得済み」側（＝撃たない側）
        }
    }
}

/// <summary>
/// 秘伝書を使って習得する。確認（SelectYesno）が出たら、本の名前を含むときだけ「はい」（押せる状態のボタンだけを押す）。
/// 成功は「習得済みになった AND 本が減った」。
/// </summary>
public sealed unsafe class UseBooksTask : AutoTask
{
    private readonly Queue<BookOffer> queue;
    private BookOffer? current;
    private int ownedBefore;
    private DateTime usedAt = DateTime.MinValue;
    private DateTime lastClick = DateTime.MinValue;
    private DateTime lastUnmatchedLog = DateTime.MinValue;

    public UseBooksTask(IEnumerable<BookOffer> books)
    {
        this.queue = new Queue<BookOffer>(books);
    }

    public override string Name => "秘伝書を読む";

    protected override TaskResult OnStart(TaskContext ctx)
    {
        ctx.Ownership.Clear();
        ctx.Ownership.IsClaiming = true;
        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.current == null)
        {
            if (this.queue.Count == 0)
                return TaskResult.Done;
            this.current = this.queue.Dequeue();
            this.usedAt = DateTime.MinValue;
            this.NextPhase(string.Empty);
        }

        var name = CraftPlanner.ItemName(this.current.BookItemId);
        var learned = ExchangeBooksTask.IsLearned(this.current.TomeId);
        var owned = Inventory.CountNow(this.current.BookItemId);

        if (this.usedAt == DateTime.MinValue)
        {
            if (learned)
            {
                ctx.Log.Debug("秘伝書", $"{name} は習得済みです");
                this.current = null;
                return TaskResult.Running;
            }

            if (owned == 0)
                return this.Fail($"{name} を持っていません");

            if (!GameUi.PlayerFree())
            {
                this.Status = "動ける状態になるのを待っています";
                return this.TimedOut(TimeSpan.FromSeconds(60)) ? this.Fail("キャラクターが動ける状態にならないため、秘伝書を使えません") : TaskResult.Running;
            }

            this.ownedBefore = owned;
            ctx.Log.Write("秘伝書", $"{name} を使います");
            AgentInventoryContext.Instance()->UseItem(this.current.BookItemId);
            this.usedAt = DateTime.UtcNow;
            return TaskResult.Running;
        }

        // 習得済みになった AND 本が減った
        if (learned && owned < this.ownedBefore)
        {
            ctx.Log.Write("秘伝書", $"{name} を読みました");
            this.current = null;
            return TaskResult.Running;
        }

        // 確認（SelectYesno）に答えるのは、交換と同じ2段の判定を通ったものだけ（本の名前だけでは、
        // 利用者が出した「〇〇秘伝書を捨てますか？」にも「はい」を押しうる）。
        //  1) 本文に本の名前がある、または 2) 本を使った後に自分の操作で開いた確認で、使ってから10秒以内。
        //  どちらでも、危ない語を含むものは押さない。
        var text = GameUi.YesnoText(out var yesno);
        if (text != null && DateTime.UtcNow - this.lastClick > TimeSpan.FromMilliseconds(400))
        {
            var bad = ExchangeBooksTask.DangerousWord(text);
            var byName = text.Contains(name, StringComparison.Ordinal);
            var ownFresh = DateTime.UtcNow - this.usedAt <= TimeSpan.FromSeconds(10)
                           && ctx.Ownership.TryGetOwnedSince("SelectYesno", this.usedAt, out _);
            if (bad == null && (byName || ownFresh))
            {
                this.lastClick = DateTime.UtcNow;
                ctx.Log.Write("秘伝書", $"確認に「はい」と答えます（{(byName ? "本の名前が本文にある" : "使った直後に自分の操作で開いた確認")}）：{text}");
                if (!GameUi.ClickYes(yesno))
                    return this.Fail($"確認の「はい」が押せる状態ではありません：{text}");
                return TaskResult.Running;
            }

            if (DateTime.UtcNow - this.lastUnmatchedLog >= TimeSpan.FromSeconds(5))
            {
                this.lastUnmatchedLog = DateTime.UtcNow;
                ctx.Log.Warn("秘伝書", bad != null
                    ? $"確認に「{bad}」が含まれるので押しません：{text}"
                    : $"確認が出ていますが、秘伝書のものと判断できないので押しません：{text}");
            }
        }

        if (DateTime.UtcNow - this.usedAt > TimeSpan.FromSeconds(15))
        {
            return this.Fail(learned
                ? $"{name} は習得済みになりましたが、本が減っていません（想定外）"
                : $"{name} を読めませんでした（使っても習得済みになりません）");
        }

        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // 自分が使って出た確認が残っていれば閉じる
        if (this.usedAt != DateTime.MinValue && ctx.Ownership.TryGetOwnedSince("SelectYesno", this.usedAt, out var own))
        {
            DebugLog.Current?.Line("操作", "止めたので秘伝書の確認を閉じます");
            GameUi.Fire(own, true, -1);
        }

        ctx.Ownership.Clear();
    }
}
