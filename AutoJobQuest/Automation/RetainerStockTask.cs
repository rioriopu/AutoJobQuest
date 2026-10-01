using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 呼び鈴を開き、全員のリテイナーの持ち物を読み、計画に要る分だけ引き出す。
///
/// 手順は、実機で確かめたものに合わせた：
///  ・持ち物の画面が開いたかは InventoryRetainer／InventoryRetainerLarge の窓で見る
///    （RetainerPage1 は一度開くと読めたままになるので、入れ物が読めるかでは判定しない）
///  ・右クリックのメニュー（ContextMenu）は常駐の窓とみられ、開くたびに PostSetup が来ない。
///    「頼む前は出ていなかった」「メニューの対象が頼んだ枠」の2つで自分のものと見る
///  ・数の入力（InputNumeric）は「受け取る」を選んでも出ることがある（実測）。出たら入れ、出ずに増えたらそのまま
///  ・クリスタルの入れ物は読み込み済みと報告しないことがある
///  ・閉じる順：数の入力 → 持ち物（AgentRetainer.Hide）→「リテイナーを帰す」→ 一覧（-1）
///  ・引き出す前に、鞄（クリスタルは専用の欄）に入る数を確かめる（見ずに撃つと弾かれて空振りする）
///
/// 窓が自分のものかは、画面の持ち主の記録（呼び鈴に話しかける直前から記録する）か、
/// 「操作する前は出ていなかった窓が、操作の後に出た」かで見る（以前は記録を始めておらず、自分で開いた窓を1つも認識できなかった）。
/// 見分けの決まりは <see cref="RetainerWindows"/> にまとめ、ゲーム無しで試している。
///
/// 持ち物は、まず Allagan Tools の記録（リテイナーごとの所持数）から読む（以前は1巡目で全員を開いて読み、
/// 2巡目でまた上から開いて引き出していた。Allagan Tools でどこに何が何個あるか分かっているので、要るリテイナーだけ開く）。
/// 記録の無いリテイナーと、Allagan Tools が使えないときだけ、開いて読む（従来の1巡目）。合計から引き出す数を決め、
/// 引き出す品を持っているリテイナーだけを一覧の上から1回ずつ開く。
/// 引き出しは「リテイナー側が減った」かつ「手持ちが増えた」が頼んだ数と一致したときだけ済んだとする。
/// 片側だけの反映・対象の変化・応答が分からないときは送り直さずに止める。
///
/// 数えない・引き出さないもの：収集品、装備中の品（リテイナーの装備欄は読まない）、
/// 出品中の品（マーケットの欄は読まない）、マテリアの付いた品（納品で失わないように）、チョコボかばん。
/// </summary>
public sealed unsafe class RetainerStockTask : AutoTask
{
    private enum Phase
    {
        FindBell,
        TakeControl,
        Bell,
        List,
        Select,
        Menu,
        Inventory,
        Context,
        Numeric,
        Verify,
        CloseInventory,
        Quit,
        CloseList,
        Leave,
    }

    /// <summary>この距離まで近づいてから話しかける。</summary>
    private const float BellRange = 3.5f;

    /// <summary>呼び鈴に話しかける回数の上限（1回目はターゲットするだけなので、その分を含む）。</summary>
    private const int MaxBellTries = 12;

    /// <summary>各段で待つ上限（進む条件は窓・在庫の状態で見る。これは詰まったときに止めるための上限）。</summary>
    private static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(30);

    /// <summary>AutoRetainer が動作中のとき、終わるのを待つ上限。</summary>
    private static readonly TimeSpan AutoRetainerBusyLimit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 始める前に、動ける状態になるのを待つ上限（区切りの切り替えはクエストの完了の直後なので、
    /// クエストのイベントがまだ終わっていないことがある。以前はその場で失敗して全体が止まっていた）。
    /// </summary>
    private static readonly TimeSpan FreeWaitLimit = TimeSpan.FromSeconds(60);

    /// <summary>宿屋の個室に入った後、呼び鈴が物体の一覧に載るまで探し続ける上限。</summary>
    private static readonly TimeSpan InnBellSearchLimit = TimeSpan.FromSeconds(15);

    /// <summary>着いたとみる高さの差（MoveToTask と同じ見方にそろえる）。</summary>
    private const float BellHeightTolerance = 8f;

    /// <summary>リテイナーの持ち物の入れ物（通常の7ページとクリスタル）。</summary>
    public static readonly InventoryType[] Pages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    private readonly Func<JobQuestPlan> makePlan;
    private readonly Ipc.RetainerControl control = new();

    private Phase phase = Phase.FindBell;
    private DateTime phaseAt = DateTime.UtcNow;
    private bool began;
    private AutoTask? travel;
    private bool visitedInn;
    private DateTime innArrivedAt = DateTime.MinValue;
    // 呼び鈴に話しかける前に降りる（GameUi.DismountBeforeInteract）
    private DateTime? bellDismountSince;
    private DateTime bellDismountSentAt = DateTime.MinValue;

    private int bellTries;
    private DateTime lastBellTry = DateTime.MinValue;
    private DateTime? busySince;
    private bool bellOpened;
    private DateTime bellAt = DateTime.MinValue;
    private DateTime lastTalk = DateTime.MinValue;

    // AutoRetainer の抑制が外れた・動き出したので止めた（止めた後に呼び鈴を押すと AutoRetainer と取り合うので、見張りを置かない）
    private bool autoRetainerTookOver;

    // 自分の操作と、その直前に出ていた窓（前後で見るため）。OnStart で作る
    private RetainerWindows? windows;

    private HashSet<string> bellNames = new(StringComparer.OrdinalIgnoreCase);

    // リテイナー：読む巡は記録の無い人だけ（Allagan Tools が使えなければ全員）、引き出す巡は引き出す品を持っている人だけ
    private readonly List<ulong> queue = [];

    // Allagan Tools の記録から読んだ、リテイナーごとの数（記録のあるリテイナーだけ。使えなかったら null）
    private readonly Ipc.AllaganToolsIpc allagan = new();
    private Dictionary<ulong, Dictionary<(uint Item, bool Hq), int>>? recorded;
    private bool listed;
    private ulong current;
    private bool withdrawing;
    private readonly Dictionary<ulong, List<RetainerSlot>> contents = [];
    private readonly RetainerPlan.Stock total = new();
    private RetainerPlan.Stock? needed;
    private readonly List<string> skippedProblems = [];

    // 引き出し中の品
    private RetainerSlot? pending;
    private int amount;
    private int beforeBag;
    private int beforeSource;
    private DateTime firedAt = DateTime.MinValue;
    private bool numericSent;

    public RetainerStockTask(Func<JobQuestPlan> makePlan)
    {
        this.makePlan = makePlan;
    }

    public override string Name => "リテイナーの在庫の確認と引き出し";

    /// <summary>引き出した品（品, HQ か）→ 数（記録・試験用）。</summary>
    public Dictionary<(uint Item, bool Hq), int> Withdrawn { get; } = [];

    /// <summary>リテイナーの持ち物の1枠。</summary>
    public sealed record RetainerSlot(InventoryType Container, int Slot, uint Item, bool Hq, int Count);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (!ctx.Ownership.Registered)
            return this.Fail("画面の開閉の知らせを受け取れないため、リテイナーの窓を見分けられません");

        var ownership = ctx.Ownership;
        this.windows = new RetainerWindows(
            GameUi.IsVisible,
            (name, since) => ownership.TryGetOwnedSince(name, since, out var owned) ? (nint)owned : 0,
            name => GameUi.IsReady(name, out var addon) ? (nint)addon : 0);

        this.bellNames = BellNames();
        if (this.bellNames.Count == 0)
            return this.Fail("呼び鈴の名前をゲームデータから引けません");

        // YesAlready の選択肢の自動選択が、リテイナーのメニューを押さないように止めてもらう（設定は変えない）
        ctx.YesAlready.Suppress();
        return TaskResult.Running;
    }

    /// <summary>
    /// 呼び鈴の名前（クライアント言語）。EObjName で日本語の名前が「呼び鈴」の行と、ハウジングの家具（Item）で日本語の名前が
    /// 「リテイナーベル」の品（家に置いた呼び鈴はこの名前で出る：AutoRetainer の Lang.BellName と同じ考え方）。
    /// 比べるときは大文字と小文字を区別しない（英語などでは "summoning bell"）。
    /// </summary>
    public static HashSet<string> BellNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eobjJa = Svc.Data.GetExcelSheet<EObjName>(Dalamud.Game.ClientLanguage.Japanese);
        var eobj = Svc.Data.GetExcelSheet<EObjName>();
        foreach (var row in eobjJa.Where(x => x.Singular.ExtractText() == "呼び鈴"))
            if (eobj.TryGetRow(row.RowId, out var local) && local.Singular.ExtractText() is { Length: > 0 } n)
                names.Add(n);

        var itemJa = Svc.Data.GetExcelSheet<Item>(Dalamud.Game.ClientLanguage.Japanese);
        var item = Svc.Data.GetExcelSheet<Item>();
        foreach (var row in itemJa.Where(x => x.Name.ExtractText() == "リテイナーベル"))
            if (item.TryGetRow(row.RowId, out var local) && local.Name.ExtractText() is { Length: > 0 } n)
                names.Add(n);
        return names;
    }

    private void Next(Phase value)
    {
        this.phase = value;
        this.phaseAt = DateTime.UtcNow;
    }

    /// <summary>操作の直前に呼ぶ。その時点で出ていた窓を控える（前後で自分の窓を見分けるため）。操作の後には呼ばない。</summary>
    private void MarkAct() => this.windows!.MarkAct();

    /// <summary>直前の自分の操作の後に出た窓か（持ち主の記録か、操作の前は出ていなかったか）。</summary>
    private bool Fresh(string name, out AtkUnitBase* addon)
    {
        addon = (AtkUnitBase*)this.windows!.Fresh(name);
        return addon != null;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.Elapsed > TimeSpan.FromMinutes(20))
            return this.Fail("リテイナーの処理が20分以内に終わりませんでした");

        // 動ける状態になってから始める（クエストの完了の直後は、イベントがまだ終わっていないことがある）
        if (!this.began)
        {
            if (!GameUi.PlayerFree())
            {
                this.Status = "動ける状態になるのを待っています";
                return this.Elapsed > FreeWaitLimit
                    ? this.Fail($"{FreeWaitLimit.TotalSeconds:0} 秒たっても動ける状態になりません。画面を閉じ、自由に動ける状態で開始してください")
                    : TaskResult.Running;
            }

            if (GameUi.IsVisible("RetainerList") || Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                return this.Fail("開始前から呼び鈴が開いています。閉じてから開始してください");
            this.began = true;
        }

        if (this.travel != null)
        {
            var r = this.travel.Step(ctx);
            this.Status = this.travel.Status;
            if (r == TaskResult.Running)
                return r;
            var error = this.travel.FailReason;
            var wasInn = this.travel is GoToInnTask;
            this.travel.Cleanup(ctx);
            this.travel = null;
            if (r == TaskResult.Failed)
                return this.Fail(error ?? "呼び鈴へ移動できません");
            if (wasInn)
                this.innArrivedAt = DateTime.UtcNow;
            this.Next(Phase.FindBell);
        }

        // 呼び鈴を開いた後だけ AutoRetainer を見る（移動中は Lifestream の移動も AutoRetainer の IsBusy に入るので見ない）
        if (this.bellOpened && this.phase != Phase.Leave && !this.control.Keep())
        {
            this.autoRetainerTookOver = true;
            return this.Fail("AutoRetainer の抑制が外れたか、AutoRetainer が動き出しました。取り合いを避けるため止めます");
        }

        if (this.phase is not (Phase.FindBell or Phase.TakeControl or Phase.Bell) && DateTime.UtcNow - this.phaseAt > PhaseLimit)
            return this.Fail($"リテイナーの画面が {PhaseLimit.TotalSeconds:0} 秒進みません（{PhaseLabel(this.phase)}）。結果の分からない引き出しは送り直しません");

        // 呼び鈴の会話（リテイナーのあいさつ等）は自分で進める。TextAdvance には頼まない
        if (this.bellOpened && this.AdvanceOwnTalk(ctx))
            return TaskResult.Running;

        if (this.phase is (Phase.Inventory or Phase.Context or Phase.Numeric or Phase.Verify) && GameMemory.LastSelectedRetainer != this.current)
            return this.Fail("操作中のリテイナーが変わったため止めました");

        this.Status = $"{(this.withdrawing ? "引き出し" : "在庫の読み取り")}：{PhaseLabel(this.phase)}（残り {this.queue.Count} 人）";
        return this.phase switch
        {
            Phase.FindBell => this.TickFindBell(ctx),
            Phase.TakeControl => this.TickTakeControl(ctx),
            Phase.Bell => this.TickBell(ctx),
            Phase.List => this.TickList(ctx),
            Phase.Select => this.TickSelect(ctx),
            Phase.Menu => this.TickMenu(ctx, 2378, Phase.Inventory),
            Phase.Inventory => this.TickInventory(ctx),
            Phase.Context => this.TickContext(ctx),
            Phase.Numeric => this.TickNumeric(ctx),
            Phase.Verify => this.TickVerify(ctx),
            Phase.CloseInventory => this.TickCloseInventory(),
            Phase.Quit => this.TickMenu(ctx, 2383, Phase.List),
            Phase.CloseList => this.TickCloseList(ctx),
            Phase.Leave => this.TickLeave(ctx),
            _ => TaskResult.Running,
        };
    }

    private static string PhaseLabel(Phase p) => p switch
    {
        Phase.FindBell => "呼び鈴を探す",
        Phase.TakeControl => "AutoRetainer の一時停止",
        Phase.Bell => "呼び鈴に話しかける",
        Phase.List => "リテイナーの一覧",
        Phase.Select => "リテイナーを選ぶ",
        Phase.Menu => "アイテムの受け渡しを選ぶ",
        Phase.Inventory => "持ち物を読む",
        Phase.Context => "受け取るを選ぶ",
        Phase.Numeric => "数を入れる",
        Phase.Verify => "両側の在庫で確かめる",
        Phase.CloseInventory => "持ち物を閉じる",
        Phase.Quit => "リテイナーを帰す",
        Phase.CloseList => "一覧を閉じる",
        Phase.Leave => "呼び鈴を離れる",
        _ => p.ToString(),
    };

    // ---- 呼び鈴 ----

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindBell()
        => Svc.Objects
            .Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject && x.IsTargetable && this.bellNames.Contains(x.Name.TextValue))
            .OrderBy(x => Vector3.Distance(x.Position, Me.Position))
            .FirstOrDefault();

    /// <summary>
    /// 呼び鈴の近くにいるか。MoveToTask の「着いた」と同じ見方（水平の距離と高さの差）にそろえる（
    /// 以前は3次元の距離で見ていたので、高さに差がある呼び鈴では MoveToTask がすぐ着いたと返し、こちらは遠いとみて作り直し続けた）。
    /// </summary>
    public static bool NearBell(Vector3 me, Vector3 bell)
        => Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(bell.X, bell.Z)) <= BellRange && MathF.Abs(me.Y - bell.Y) < BellHeightTolerance;

    private TaskResult TickFindBell(TaskContext ctx)
    {
        var bell = this.FindBell();
        if (bell == null)
        {
            // 近くに無ければ、グリダニアの宿屋の個室へ（個室に呼び鈴がある：ゲームデータの配置で確認）
            if (this.visitedInn)
            {
                // 個室に入った直後は、呼び鈴がまだ物体の一覧に載っていないことがある。上限まで探し続ける
                if (DateTime.UtcNow - this.innArrivedAt < InnBellSearchLimit)
                {
                    this.Status = "宿屋の個室で呼び鈴を探しています";
                    return TaskResult.Running;
                }

                return this.Fail($"宿屋の個室で呼び鈴を {InnBellSearchLimit.TotalSeconds:0} 秒探しても見つけられません");
            }

            this.visitedInn = true;
            ctx.Log.Write("リテイナー", "近くに呼び鈴が無いので、グリダニアの宿屋の個室へ向かいます");
            this.travel = new GoToInnTask();
            return TaskResult.Running;
        }

        if (!NearBell(Me.Position, bell.Position))
        {
            this.travel = new MoveToTask(bell.Position, BellRange - 1f, "呼び鈴");
            return TaskResult.Running;
        }

        this.Next(Phase.TakeControl);
        return TaskResult.Running;
    }

    private TaskResult TickTakeControl(TaskContext ctx)
    {
        var taken = this.control.Take(ctx.Config);
        if (this.control.StaleNotice.Length > 0)
            ctx.Log.Warn("リテイナー", this.control.StaleNotice);
        switch (taken)
        {
            case Ipc.RetainerControl.TakeResult.Taken:
                this.busySince = null;
                this.Next(Phase.Bell);
                return TaskResult.Running;
            case Ipc.RetainerControl.TakeResult.Busy:
                this.busySince ??= DateTime.UtcNow;
                this.Status = "AutoRetainer が動作中です。終わるのを待っています";
                return DateTime.UtcNow - this.busySince.Value > AutoRetainerBusyLimit
                    ? this.Fail($"AutoRetainer が {AutoRetainerBusyLimit.TotalSeconds:0} 秒たっても動作中です。取り合いを避けるため止めます")
                    : TaskResult.Running;
            default:
                return this.Fail($"AutoRetainer を一時停止できません（{this.control.LastProblem}）。取り合いを避けるため止めます");
        }
    }

    private TaskResult TickBell(TaskContext ctx)
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && this.bellOpened)
        {
            this.Next(Phase.List);
            return TaskResult.Running;
        }

        if (!this.bellOpened && GameUi.IsVisible("RetainerList"))
            return this.Fail("自分が話しかける前に呼び鈴の一覧が開きました（ほかの操作と取り合わないよう止めます）");

        var bell = this.FindBell();
        if (bell == null || !NearBell(Me.Position, bell.Position))
        {
            this.Next(Phase.FindBell);
            return TaskResult.Running;
        }

        if (GameUi.DismountBeforeInteract(ref this.bellDismountSince, ref this.bellDismountSentAt))
        {
            this.Status = "呼び鈴に話しかける前に、マウントから降りています";
            return TaskResult.Running;
        }

        // 話しかけの間隔（送りすぎの防止。進む条件は「呼び鈴を使っている状態になったか」で見る）
        if (DateTime.UtcNow - this.lastBellTry < TimeSpan.FromSeconds(1))
            return TaskResult.Running;
        if (this.bellTries >= MaxBellTries)
        {
            var height = MathF.Abs(Me.Position.Y - bell.Position.Y);
            return this.Fail($"呼び鈴に {MaxBellTries} 回話しかけても開きませんでした"
                             + (height > 2f ? $"（呼び鈴との高さの差が {height:0.0}m あります。同じ高さにある呼び鈴の近くで開始してください）" : string.Empty));
        }
        this.lastBellTry = DateTime.UtcNow;

        if (!this.bellOpened)
        {
            // ここから開いた窓を自分のものとして記録する
            ctx.Ownership.Clear();
            ctx.Ownership.IsClaiming = true;
            ctx.InOwnConversation = true;
            this.bellAt = DateTime.UtcNow;
        }

        this.MarkAct();
        this.bellTries++;
        if (GameUi.Interact(bell))
            this.bellOpened = true;

        return TaskResult.Running;
    }

    /// <summary>
    /// 呼び鈴を使っている間の会話の窓（リテイナーのあいさつ・見送り）を進める。進めたら true。
    /// 呼び鈴を開いているのはこちらの操作なので、その間の会話はこちらのもの（AutoRetainer の MiniTA も呼び鈴の間の会話を送っている）。
    /// </summary>
    private bool AdvanceOwnTalk(TaskContext ctx)
    {
        if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell] || !GameUi.IsReady("Talk", out _))
            return false;
        if (DateTime.UtcNow - this.lastTalk < TimeSpan.FromMilliseconds(300))
            return true;
        this.lastTalk = DateTime.UtcNow;
        GameUi.AdvanceTalk();
        return true;
    }

    // ---- 一覧・選択 ----

    private TaskResult TickList(TaskContext ctx)
    {
        if (!this.ListReady(ctx, out var list))
            return TaskResult.Running;
        if (!GameMemory.RetainersReady)
            return TaskResult.Running;

        if (!this.listed)
        {
            this.listed = true;
            var available = GameMemory.Retainers().Where(r => r.Available).ToList();
            if (available.Count == 0)
            {
                ctx.Log.Warn("リテイナー", "呼べるリテイナーがいません。手持ちだけで計画します");
                return this.StartClose(list);
            }

            if (this.ReadFromAllagan(ctx, available, out var unrecorded))
            {
                this.queue.AddRange(unrecorded);
            }
            else
            {
                this.queue.AddRange(available.Select(r => r.Id));
                ctx.Log.Write("リテイナー", $"リテイナー {this.queue.Count} 人の持ち物を開いて読みます");
            }
        }

        if (this.queue.Count == 0)
        {
            if (!this.withdrawing)
                return this.BeginWithdraw(ctx, list);

            // 引き出しきれなかった品（Allagan Tools の記録が古かった・収集品やマテリア付きで引き出さない品だった等）を記録に残す。
            // 足りない分は、引き出した後に作り直す計画で、ほかの手段（マーケット・採集など）に回る
            var left = this.needed!.Counts.Where(kv => kv.Value > 0).ToList();
            if (left.Count > 0)
                ctx.Log.Warn("リテイナー", $"引き出しきれなかった品があります：{string.Join("、", left.Select(kv => $"{CraftPlanner.ItemName(kv.Key.Item)}{(kv.Key.Hq ? " HQ" : string.Empty)}×{kv.Value}"))}"
                                          + "（引き出した後の計画で、ほかの手段に回します）");
            return this.StartClose(list);
        }

        this.current = this.queue[0];
        this.queue.RemoveAt(0);
        this.Next(Phase.Select);
        return TaskResult.Running;
    }

    private bool ListReady(TaskContext ctx, out AtkUnitBase* list)
        => ctx.Ownership.TryGetOwnedSince("RetainerList", this.bellAt, out list)
           || (this.bellOpened && GameUi.IsReady("RetainerList", out list) && Svc.Condition[ConditionFlag.OccupiedSummoningBell]);

    private TaskResult TickSelect(TaskContext ctx)
    {
        if (!this.ListReady(ctx, out var list))
            return TaskResult.Running;
        var name = RetainerName(this.current);
        var index = name == null ? -1 : GameMemory.RetainerListIndex(list, name);
        if (index < 0)
        {
            ctx.Log.Warn("リテイナー", $"リテイナー（{name ?? this.current.ToString()}）を一覧に見つけられないので飛ばします");
            this.current = 0;
            this.Next(Phase.List);
            return TaskResult.Running;
        }

        // ECommons の AddonMaster.RetainerList.Entry.Select と同じ（2, 一覧の番号, 型なし, 型なし）
        this.windows!.ForgetInventory();
        this.MarkAct();
        GameMemory.SelectRetainer(list, index);
        this.Next(Phase.Menu);
        return TaskResult.Running;
    }

    /// <summary>リテイナーの名前（RetainerManager から）。</summary>
    private static string? RetainerName(ulong id)
        => GameMemory.Retainers().Where(r => r.Id == id).Select(r => r.Name).FirstOrDefault();

    /// <summary>リテイナーのメニュー（SelectString）で、Addon の文言が先頭に来る項目を1つだけ選ぶ。</summary>
    private TaskResult TickMenu(TaskContext ctx, uint addonRow, Phase then)
    {
        if (this.phase == Phase.Menu && GameMemory.LastSelectedRetainer != this.current)
            return TaskResult.Running;
        if (GameUi.MenuEntries(out var menu) is not { } entries || !this.Fresh("SelectString", out var owned) || owned != menu)
            return TaskResult.Running;

        var index = MenuChoice.ByAddonPrefix(entries, AddonPrefix(addonRow));
        if (index < 0)
            return this.Fail($"リテイナーのメニューで「{AddonPrefix(addonRow)}」を一意に選べません：{string.Join(" / ", entries)}");

        this.MarkAct();
        GameUi.Fire(menu, true, index);
        if (then == Phase.List)
        {
            // リテイナーを帰した。一覧へ戻る
            this.current = 0;
        }

        this.Next(then);
        return TaskResult.Running;
    }

    /// <summary>
    /// Addon の文言のうち、最初の差し込み（マクロ）より前の部分。
    /// 2378「アイテムの受け渡し　[預託中：&lt;kilo(lnum1,\,)&gt;枠]」→「アイテムの受け渡し　[預託中：」。
    /// 実際の項目は「アイテムの受け渡し　[預託中：15枠]」なので、完全一致では選べない（AutoRetainer は GetText(onlyFirst) の先頭一致）。
    /// </summary>
    public static string AddonPrefix(uint row)
    {
        var macro = Svc.Data.GetExcelSheet<Addon>().GetRow(row).Text.ToMacroString();
        var cut = macro.IndexOf('<');
        return (cut < 0 ? macro : macro[..cut]).Trim();
    }

    // ---- 持ち物 ----

    private TaskResult TickInventory(TaskContext ctx)
    {
        // 持ち物の窓は、同じリテイナーの間は最初に認めた窓を認め続ける
        if (!GameMemory.RetainerInventoryActive || !this.windows!.InventoryOpen(this.current))
            return TaskResult.Running;
        var slots = GameMemory.RetainerSlots();
        if (slots == null)
            return TaskResult.Running;

        if (!this.withdrawing)
        {
            this.contents[this.current] = slots;
            foreach (var s in slots)
                this.total.Counts[(s.Item, s.Hq)] = this.total.Counts.GetValueOrDefault((s.Item, s.Hq)) + s.Count;
            this.Next(Phase.CloseInventory);
            return TaskResult.Running;
        }

        var want = slots.FirstOrDefault(s => this.needed!.Counts.GetValueOrDefault((s.Item, s.Hq)) > 0);
        if (want == null)
        {
            this.Next(Phase.CloseInventory);
            return TaskResult.Running;
        }

        var need = this.needed!.Counts[(want.Item, want.Hq)];
        var fits = Room(want);
        var take = Math.Min(Math.Min(want.Count, need), fits.Amount);
        if (take <= 0)
            return this.Fail($"{CraftPlanner.ItemName(want.Item)}{(want.Hq ? " HQ" : string.Empty)} を引き出せません：{fits.Reason}。鞄を空けてから再開してください");

        if (GameUi.IsVisible("ContextMenu") || GameUi.IsVisible("InputNumeric"))
            return this.Fail("別の品の右クリックのメニューか数の入力が開いています（触らずに止めます）");

        this.pending = want;
        this.amount = take;
        this.beforeSource = want.Count;
        this.beforeBag = BagCount(want);
        this.numericSent = false;
        this.MarkAct();
        this.firedAt = DateTime.UtcNow;
        GameMemory.OpenRetainerItemMenu(want.Container, want.Slot);
        this.Next(Phase.Context);
        return TaskResult.Running;
    }

    private TaskResult TickContext(TaskContext ctx)
    {
        if (!this.SourceUnchanged())
            return this.Fail("引き出す前にリテイナーの品か手持ちが変わりました");

        // メニューは常駐の窓とみられる。頼む前は出ていなかった＋メニューの対象が頼んだ枠、で自分のものと見る
        if (this.windows!.WasOpenAtAct("ContextMenu") || !GameUi.IsReady("ContextMenu", out var menu))
            return TaskResult.Running;

        var p = this.pending!;
        var labels = GameMemory.ContextMenuLabels();

        // 全部取るなら「リテイナーから受け取る」（98）、一部なら「個数指定」（773）。クリスタルには個数指定が出ない
        // （Artisan と同じ）。望んだほうが無ければもう一方にする
        var all = Svc.Data.GetExcelSheet<Addon>().GetRow(98).Text.ExtractText();
        var some = Svc.Data.GetExcelSheet<Addon>().GetRow(773).Text.ExtractText();
        var useAll = this.amount >= p.Count;
        var idx = MenuChoice.Exact(labels, useAll ? all : some);
        if (idx < 0)
            idx = MenuChoice.Exact(labels, useAll ? some : all);
        if (idx < 0 || GameMemory.ContextMenuItemDisabled(idx))
        {
            GameUi.Close(menu);
            return this.Fail($"右クリックのメニューで「受け取る」を一意に選べません：{string.Join(" / ", labels)}");
        }

        this.MarkAct();
        GameUi.Fire(menu, true, 0, idx, 0, 0, 0);
        this.Next(Phase.Numeric);
        return TaskResult.Running;
    }

    /// <summary>
    /// 数の入力。出るかどうかは品で変わる（実測：クリスタルは「受け取る」でも出た）。
    /// 出たら入れる。出ないまま手持ちが増えたら、そのまま確かめへ進む。
    /// </summary>
    private TaskResult TickNumeric(TaskContext ctx)
    {
        if (!this.numericSent && this.Fresh("InputNumeric", out var numeric))
        {
            if (!this.SourceStillThere())
                return this.Fail("数を入れる前にリテイナーの品が変わりました");
            this.numericSent = true;
            GameUi.Fire(numeric, true, this.amount);
            this.Next(Phase.Verify);
            return TaskResult.Running;
        }

        if (BagCount(this.pending!) != this.beforeBag)
            this.Next(Phase.Verify);
        return TaskResult.Running;
    }

    private TaskResult TickVerify(TaskContext ctx)
    {
        var p = this.pending!;
        var slots = GameMemory.RetainerSlots();
        if (slots == null)
            return TaskResult.Running;
        var now = slots.FirstOrDefault(x => x.Container == p.Container && x.Slot == p.Slot);
        var sourceNow = now == null ? 0 : now.Item == p.Item && now.Hq == p.Hq ? now.Count : -1;
        var bagNow = BagCount(p);
        var got = bagNow - this.beforeBag;
        if (!TransferConfirmed(this.beforeBag, bagNow, this.beforeSource, sourceNow, this.amount))
        {
            // 頼んだ数より多く移ったら、送り直さずに止める（手持ちだけ先に増えてリテイナー側が古い数のままの一瞬は待つ）
            if (got > this.amount)
                return this.Fail($"{CraftPlanner.ItemName(p.Item)} の引き出しの結果が頼んだ数と合いません（頼んだ {this.amount}・手持ち +{got}）。送り直さずに止めます");
            return TaskResult.Running;
        }

        this.needed!.Counts[(p.Item, p.Hq)] -= this.amount;
        this.Withdrawn[(p.Item, p.Hq)] = this.Withdrawn.GetValueOrDefault((p.Item, p.Hq)) + this.amount;
        ctx.Log.Write("リテイナー", $"{CraftPlanner.ItemName(p.Item)}{(p.Hq ? " HQ" : string.Empty)} ×{this.amount} を引き出しました（リテイナー側の減少と手持ちの増加で確認）");
        this.pending = null;
        // ここでは控えを取り直さない（操作していない。持ち物の窓は同じ窓を認め続ける）
        this.Next(Phase.Inventory);
        return TaskResult.Running;
    }

    private TaskResult TickCloseInventory()
    {
        if (GameMemory.RetainerInventoryActive)
        {
            if (DateTime.UtcNow - this.windows!.ActedAt > TimeSpan.FromMilliseconds(600))
            {
                this.MarkAct();
                GameMemory.HideRetainerInventory();
            }

            return TaskResult.Running;
        }

        // 閉じた後に控えを取り直さない。取り直すと、出直したリテイナーのメニューを「前から出ていた窓」とみてしまう。
        // 帰す段は、閉じる直前（Hide の前）の控えを基準に見る
        this.windows!.ForgetInventory();
        this.Next(Phase.Quit);
        return TaskResult.Running;
    }

    // ---- 2巡目と後片付け ----

    private TaskResult BeginWithdraw(TaskContext ctx, AtkUnitBase* list)
    {
        // ギアセットが使う品のうち手持ちに無い分は、リテイナーに預けた利用者の装備とみて、引き出しの対象に数えない
        foreach (var (key, keep) in Inventory.GearsetKeepOutsideBags())
        {
            if (!this.total.Counts.TryGetValue(key, out var have) || have <= 0)
                continue;
            this.total.Counts[key] = Math.Max(0, have - keep);
            ctx.Log.Write("リテイナー", $"{CraftPlanner.ItemName(key.Item)}{(key.Hq ? " HQ" : string.Empty)} ×{Math.Min(have, keep)} はギアセットの品なので、引き出しの対象に数えません");
        }

        var bags = Inventory.Snapshot();
        var (targets, notes) = this.Targets(ctx, bags);
        foreach (var n in notes)
            ctx.Log.Warn("リテイナー", n);
        var built = RetainerPlan.Build(ctx.Data.Planner!, targets, bags, this.total, CraftAbility.FromGame());
        foreach (var n in built.Problems)
            ctx.Log.Warn("リテイナー", $"引き出しの計算から外した品：{n}");
        this.needed = built.Pull;
        this.withdrawing = true;

        var kinds = this.needed.Counts.Where(kv => kv.Value > 0).ToList();
        ctx.Log.Write("リテイナー", $"リテイナーの在庫 {this.total.Counts.Count} 種類を読みました。引き出すのは {kinds.Count} 種類です"
            + (kinds.Count > 0 ? $"：{string.Join("、", kinds.Select(kv => $"{CraftPlanner.ItemName(kv.Key.Item)}{(kv.Key.Hq ? " HQ" : string.Empty)}×{kv.Value}"))}" : string.Empty));

        // 鞄に入りきるかを先に確かめる（入らないまま始めると、途中で弾かれて止まる）
        var slotsNeeded = BagSlotsNeeded(kinds.Select(kv => (kv.Key.Item, kv.Key.Hq, kv.Value, IsCrystal(kv.Key.Item))), Inventory.StackRoom, ItemStack);
        var free = Inventory.FreeBagSlots();
        if (slotsNeeded > free - ctx.Config.KeepFreeBagSlots)
            return this.Fail($"引き出す品に鞄の枠が {slotsNeeded} 枠要りますが、空きは {free} 枠です（残しておく空き {ctx.Config.KeepFreeBagSlots} 枠を除くと足りません）。"
                             + "鞄を空けるか、選ぶ職を減らしてから開始してください（残しておく空きは設定タブで減らせます）");

        // 引き出す巡は、引き出す品を持っている人だけ（開いて読んだ人は読んだ持ち物で、ほかは Allagan Tools の記録で見る）。一覧の上から1回ずつ開く
        foreach (var (id, list2) in this.contents)
            if (list2.Any(s => this.needed.Counts.GetValueOrDefault((s.Item, s.Hq)) > 0))
                this.queue.Add(id);
        if (this.recorded != null)
        {
            foreach (var (id, counts) in this.recorded)
                if (!this.queue.Contains(id) && counts.Any(kv => kv.Value > 0 && this.needed.Counts.GetValueOrDefault(kv.Key) > 0))
                    this.queue.Add(id);
        }

        var order = GameMemory.Retainers().Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        this.queue.Sort((a, b) => order.GetValueOrDefault(a, int.MaxValue).CompareTo(order.GetValueOrDefault(b, int.MaxValue)));
        if (this.queue.Count == 0)
            return this.StartClose(list);
        ctx.Log.Write("リテイナー", $"引き出す品を持っているリテイナー {this.queue.Count} 人だけを開きます：{string.Join("、", this.queue.Select(id => RetainerName(id) ?? id.ToString()))}");
        this.current = 0;
        return TaskResult.Running;
    }

    /// <summary>
    /// Allagan Tools の記録から、計画に関係しうる品のリテイナーごとの数を読み、合計（total）に足す。関係しうる品は、手持ちもリテイナーの在庫も
    /// 無いとみたときの計画が在庫を見る品（レシピの木の全部と納品物・マテリア・秘伝書）。記録の無いリテイナーは <paramref name="unrecorded"/> に返す
    /// （開いて読む）。Allagan Tools が使えない・読めないときは false（従来どおり全員を開いて読む）。
    /// </summary>
    private bool ReadFromAllagan(TaskContext ctx, List<(ulong Id, string Name, bool Available)> available, out List<ulong> unrecorded)
    {
        unrecorded = [];
        if (!this.allagan.IsInitialized())
        {
            ctx.Log.Write("リテイナー", "Allagan Tools が使えないので、全員の持ち物を開いて読みます");
            return false;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (targets, _) = this.Targets(ctx, Inventory.Snapshot());
        var items = ctx.Data.Planner!.Build(targets, new RetainerPlan.Stock(), _ => true, CraftAbility.FromGame()).StockDemand.Keys
            .Concat(targets.Select(t => t.ItemId))
            .Distinct()
            .ToList();

        var read = new Dictionary<ulong, Dictionary<(uint Item, bool Hq), int>>();
        foreach (var r in available)
        {
            var has = this.allagan.HasRecord(r.Id);
            if (has == null)
            {
                ctx.Log.Warn("リテイナー", $"Allagan Tools の記録を読めません（{string.Join(" / ", this.allagan.LastErrors.Values)}）。全員の持ち物を開いて読みます");
                return false;
            }

            if (has == false)
            {
                unrecorded.Add(r.Id);
                continue;
            }

            var counts = new Dictionary<(uint Item, bool Hq), int>();
            foreach (var item in items)
            {
                if (this.allagan.RetainerCount(item, r.Id, IsCrystal(item)) is not { } c)
                {
                    ctx.Log.Warn("リテイナー", $"Allagan Tools の記録を読めません（{string.Join(" / ", this.allagan.LastErrors.Values)}）。全員の持ち物を開いて読みます");
                    return false;
                }

                if (c.All - c.Hq > 0)
                    counts[(item, false)] = c.All - c.Hq;
                if (c.Hq > 0)
                    counts[(item, true)] = c.Hq;
            }

            read[r.Id] = counts;
        }

        this.recorded = read;
        foreach (var counts in read.Values)
            foreach (var (key, n) in counts)
                this.total.Counts[key] = this.total.Counts.GetValueOrDefault(key) + n;
        ctx.Log.Write("リテイナー", $"Allagan Tools の記録から、リテイナー {read.Count} 人の持ち物（関係する品 {items.Count} 種類）を読みました（{watch.ElapsedMilliseconds}ms）"
                                  + (unrecorded.Count > 0 ? $"。記録の無い {unrecorded.Count} 人だけ開いて読みます：{string.Join("、", unrecorded.Select(id => RetainerName(id) ?? id.ToString()))}" : "。開いて読む人はいません"));
        return true;
    }

    private TaskResult StartClose(AtkUnitBase* list)
    {
        this.MarkAct();
        GameUi.Fire(list, true, -1);
        this.Next(Phase.CloseList);
        return TaskResult.Running;
    }

    private TaskResult TickCloseList(TaskContext ctx)
    {
        if (GameUi.IsVisible("RetainerList"))
        {
            if (DateTime.UtcNow - this.windows!.ActedAt > TimeSpan.FromSeconds(1) && this.ListReady(ctx, out var list))
            {
                this.MarkAct();
                GameUi.Fire(list, true, -1);
            }

            return TaskResult.Running;
        }

        this.Next(Phase.Leave);
        return TaskResult.Running;
    }

    private TaskResult TickLeave(TaskContext ctx)
    {
        // 呼び鈴を使っている状態が終わってから AutoRetainer の一時停止を戻す（呼び鈴を開いたまま戻すと、AutoRetainer が動き出す）
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell] || !GameUi.PlayerFree())
            return TaskResult.Running;
        this.control.Release(ctx.Config);
        if (ctx.Config.RetainerSuppressionPendingRestore)
        {
            // 戻せなかった（IPC の失敗）。実行中は再試行しないので、ここで知らせる。次の区切りの呼び鈴の前（Take）と、止まった後（Services）で戻し直す
            const string msg = "AutoRetainer の一時停止を戻せませんでした（次の呼び鈴の前と、止まった後に戻し直します）";
            ctx.Log.Warn("リテイナー", msg);
            Svc.Chat.Print($"[AutoJobQuest] {msg}");
        }

        ctx.Ownership.Clear();
        ctx.InOwnConversation = false;
        var left = this.needed?.Counts.Where(kv => kv.Value > 0).ToList() ?? [];
        if (left.Count > 0)
            return this.Fail($"リテイナーから引き出しきれませんでした：{string.Join("、", left.Select(kv => $"{CraftPlanner.ItemName(kv.Key.Item)}×{kv.Value}"))}");
        return TaskResult.Done;
    }

    // ---- 読み取り（リテイナーの持ち物は GameMemory.RetainerSlots）----

    /// <summary>
    /// 引き出しの確かめに使う手持ちの数。ギアセットの引き算をしない生の数で数える（引き算をすると、
    /// ギアセットに登録された品を受け取っても増えたと見えず、誤った理由で止まっていた）。
    /// </summary>
    private static int BagCount(RetainerSlot slot)
    {
        var inv = Inventory.Snapshot(subtractGearsets: false);
        return slot.Hq ? inv.CountHq(slot.Item) : inv.CountNq(slot.Item);
    }

    private static bool IsCrystal(uint item) => Inventory.IsCrystalItem(item);

    private static int ItemStack(uint item)
        => Svc.Data.GetExcelSheet<Item>().TryGetRow(item, out var row) ? (int)Math.Max(1u, row.StackSize) : 1;

    /// <summary>手持ちに入る数（クリスタルは専用の欄の上限まで、ほかは鞄の空き枠と同じ品の山の余り。1つしか持てない品は持っていなければ1）。</summary>
    private static (int Amount, string Reason) Room(RetainerSlot slot)
    {
        var stack = ItemStack(slot.Item);
        if (Svc.Data.GetExcelSheet<Item>().TryGetRow(slot.Item, out var row) && row.IsUnique)
            return Inventory.CountNow(slot.Item) > 0 ? (0, "1つしか持てない品で、もう持っています") : (1, string.Empty);
        if (slot.Container == InventoryType.RetainerCrystals || IsCrystal(slot.Item))
        {
            var room = Math.Max(0, stack - Inventory.CrystalCount(slot.Item));
            return (room, room > 0 ? string.Empty : $"クリスタルの欄が上限（{stack}）です");
        }

        var amount = Inventory.StackRoom(slot.Item, slot.Hq, stack) + (Inventory.FreeBagSlots() * stack);
        return (amount, amount > 0 ? string.Empty : "鞄に空きがありません");
    }

    /// <summary>
    /// 引き出す品に要る鞄の新しい枠の数（クリスタルは鞄を使わない）。既存の同じ品の山に積める分は枠を使わない。
    /// </summary>
    public static int BagSlotsNeeded(IEnumerable<(uint Item, bool Hq, int Amount, bool Crystal)> pulls, Func<uint, bool, int, int> stackRoom, Func<uint, int> stackSize)
    {
        var slots = 0;
        foreach (var (item, hq, amount, crystal) in pulls)
        {
            if (crystal || amount <= 0)
                continue;
            var stack = Math.Max(1, stackSize(item));
            var rest = Math.Max(0, amount - stackRoom(item, hq, stack));
            slots += (rest + stack - 1) / stack;
        }

        return slots;
    }

    /// <summary>引き出す対象（計画の納品物・中間素材・装着するマテリア・要る秘伝書・紫貨の収集品の材料）。</summary>
    private (List<QuestItemReq> Targets, List<string> Notes) Targets(TaskContext ctx, Inventory bags)
    {
        var notes = new List<string>();
        var plan = this.makePlan();
        var targets = new List<QuestItemReq>(plan.RetainerTargets);
        foreach (var materia in plan.Materia.Where(m => !m.AlreadyMelded))
        {
            var id = materia.MateriaItemId ?? MateriaCatalog.ResolveAny(ctx.Config.AnyMateriaItemId, materia.TargetItemId, out _);
            if (id is { } item)
                targets.Add(new(item, 1, false, "装着するマテリア"));
        }

        // 引き出せる完成品・中間素材を差し引いた後でも要る秘伝書だけを見る
        var combined = new RetainerPlan.Combined(bags, this.total);
        var craft = ctx.Data.Planner!.Build(targets, combined, PlanBuilder.IsBookUnlocked, CraftAbility.FromGame());
        var tomes = craft.LockedBySecretBook.Select(c => c.SecretRecipeBookId).ToHashSet();
        if (tomes.Count == 0)
            return (targets, notes);

        // 秘伝書の表が欠けていても、引き出しは止めない（秘伝書の段で改めて確かめて止める）
        var books = ctx.Data.Books;
        if (books == null || books.CollectableItemId != ctx.Config.ScripCollectableItemId)
        {
            notes.Add("秘伝書の表を読めないため、秘伝書と紫貨の収集品の材料は引き出しの対象に入れません");
            return (targets, notes);
        }

        var offers = books.Offers.Values.Where(o => tomes.Contains(o.TomeId)).ToList();
        foreach (var offer in offers)
            targets.Add(new(offer.BookItemId, 1, false, "要る秘伝書"));
        if (offers.Select(o => o.TomeId).Distinct().Count() != tomes.Count)
        {
            notes.Add("要る秘伝書の値段を確かめられないため、紫貨の収集品の材料は引き出しの対象に入れません");
            return (targets, notes);
        }

        var price = offers.Where(o => combined.CountNq(o.BookItemId) + combined.CountHq(o.BookItemId) == 0).Sum(o => (int)o.Price);
        var count = BookMath.CollectablesNeeded(price, Inventory.CountSpecialCurrency(books.RewardSpecialCurrencyId, out _), books.RewardLow);
        count = Math.Max(0, count - Inventory.CountCollectables(books.CollectableItemId, books.MinCollectability));
        if (count > 0)
            targets.Add(new(books.CollectableItemId, count, false, "紫貨のための収集品の材料"));
        return (targets, notes);
    }

    /// <summary>止めた後の見張りの名前。</summary>
    public const string DismissWatchName = "呼び出したリテイナーを帰して呼び鈴を閉じる";

    /// <summary>
    /// 止めたときにリテイナーを呼んだまま・呼び鈴を開いたままなら、止まった後に「リテイナーを帰す」を選び、一覧を閉じる
    /// （以前は持ち物を閉じるだけで、リテイナーのメニューと呼び鈴が開いたまま残り、AutoRetainer の一時停止も戻らなかった。
    /// もう一度開始すると「開始前から呼び鈴が開いています」で止まった）。
    /// 押すのは、自分が呼んだリテイナーのメニューと、呼び鈴の一覧と、その間の会話だけ。20秒で閉じきれなければ、手で閉じるよう知らせる。
    /// 呼び鈴が閉じれば、AutoRetainer の一時停止は Services が戻す。
    /// </summary>
    private static (string Name, DateTime Until, Func<bool> Step) DismissWatch(ulong retainer, bool inventoryMayOpen, Ipc.RetainerControl control)
    {
        var prefix = AddonPrefix(2383);
        var started = DateTime.UtcNow;
        var giveUpAt = started + TimeSpan.FromSeconds(20);

        // 最初の押しは1秒後（後始末の押しが効くのを待つ。同じフレームで同じ一覧に2回送らない）
        var lastPress = started;
        var hidInventory = false;
        return (DismissWatchName, started + TimeSpan.FromSeconds(25), () =>
        {
            if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                return true;
            if (DateTime.UtcNow > giveUpAt)
            {
                Svc.Chat.Print("[AutoJobQuest] 呼び鈴を開いたまま止まりました。リテイナーの持ち物が開いていれば閉じ、メニューで「リテイナーを帰す」を選び、一覧を閉じてください"
                               + "（閉じると AutoRetainer の一時停止を戻します）");
                return true;
            }

            // 押す間隔（送りすぎの防止。終わりは「呼び鈴を使っている状態が解けたか」で見る）
            if (DateTime.UtcNow - lastPress < TimeSpan.FromSeconds(1))
                return false;

            // AutoRetainer の抑制が外れた・動き出したなら押さない（取り合わない）
            if (!control.Keep())
            {
                Svc.Chat.Print("[AutoJobQuest] AutoRetainer が動き出したので、呼び鈴の後始末をやめました。AutoRetainer が終わったら、呼び鈴を閉じてください");
                return true;
            }

            // 利用者が別のリテイナーを呼んだら、利用者の操作とみて押さない
            var last = GameMemory.LastSelectedRetainer;
            if (last is { } selected && selected != 0 && selected != retainer)
            {
                Core.DebugLog.Current?.Line("リテイナー", "止めた後に別のリテイナーが呼ばれたので、呼び鈴の後始末をやめました（利用者の操作とみなします）");
                return true;
            }

            if (GameMemory.RetainerInventoryActive)
            {
                // 「アイテムの受け渡し」を押した直後に止めると、持ち物の窓は止めた後に開く。自分が呼んだリテイナーなら1回だけ閉じる
                if (inventoryMayOpen && !hidInventory && last == retainer && DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
                {
                    hidInventory = true;
                    lastPress = DateTime.UtcNow;
                    GameMemory.HideRetainerInventory();
                }

                return false;
            }

            if (GameUi.IsReady("Talk", out _))
            {
                lastPress = DateTime.UtcNow;
                GameUi.AdvanceTalk();
                return false;
            }

            if (GameUi.MenuEntries(out var menu) is { } entries)
            {
                var index = MenuChoice.ByAddonPrefix(entries, prefix);
                if (retainer == 0 || last != retainer || index < 0)
                    return false;
                lastPress = DateTime.UtcNow;
                GameUi.Fire(menu, true, index);
                return false;
            }

            if (GameUi.IsReady("RetainerList", out var list))
            {
                lastPress = DateTime.UtcNow;
                GameUi.Fire(list, true, -1);
            }

            return false;
        });
    }

    /// <summary>引き出しが済んだか：リテイナー側の減少と手持ちの増加が、どちらも頼んだ数と一致する。</summary>
    public static bool TransferConfirmed(int bagBefore, int bagNow, int sourceBefore, int sourceNow, int amount)
        => amount > 0 && sourceNow >= 0 && bagNow - bagBefore == amount && sourceBefore - sourceNow == amount;

    /// <summary>メニューを出す前後で、頼んだ枠と手持ちが変わっていないか（メニューの対象が頼んだ枠か、も見る）。</summary>
    private bool SourceUnchanged()
    {
        var p = this.pending!;
        if (!GameMemory.ContextMenuTargets(p.Container, p.Slot))
            return false;
        return this.SourceStillThere();
    }

    private bool SourceStillThere()
    {
        var p = this.pending!;
        var now = GameMemory.RetainerSlots()?.FirstOrDefault(x => x.Container == p.Container && x.Slot == p.Slot);
        return now != null && now.Item == p.Item && now.Hq == p.Hq && now.Count == this.beforeSource && BagCount(p) == this.beforeBag;
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.travel?.Cleanup(ctx);
        this.travel = null;

        if (this.bellOpened)
        {
            // 自分が開いた窓だけを、内側から外側の順に閉じる
            if (this.firedAt != DateTime.MinValue && GameUi.IsReady("InputNumeric", out var numeric) && this.phase is Phase.Numeric or Phase.Verify)
                GameUi.Close(numeric);
            if (this.phase == Phase.Context && GameUi.IsReady("ContextMenu", out var menu) && this.windows?.WasOpenAtAct("ContextMenu") == false)
                GameUi.Close(menu);
            if (GameMemory.RetainerInventoryActive)
                GameMemory.HideRetainerInventory();
            if (ctx.Ownership.TryGetOwnedSince("RetainerList", this.bellAt, out var list)
                || (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && GameUi.IsReady("RetainerList", out list)))
                GameUi.Fire(list, true, -1);

            // リテイナーを呼んだまま・呼び鈴を開いたままなら、止まった後にリテイナーを帰して一覧を閉じる。
            // AutoRetainer が動き出して止めたときは押さない（取り合う）。手で閉じるよう知らせるだけにする
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                ctx.AfterStop.RemoveAll(a => a.Name == DismissWatchName);
                if (this.autoRetainerTookOver)
                    Svc.Chat.Print("[AutoJobQuest] AutoRetainer が動き出したので、呼び鈴はそのままにしました。AutoRetainer が終わったら、呼び鈴を閉じてください");
                else
                    ctx.AfterStop.Add(DismissWatch(this.current, this.phase is Phase.Menu or Phase.Inventory, this.control));
            }
        }

        // 一時停止を戻す。呼び鈴がまだ開いていれば戻さない（開いたまま戻すと AutoRetainer が呼び鈴で動き出す）。
        // 控えが残るので、呼び鈴が閉じてから Services が戻す
        if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            this.control.Release(ctx.Config);
        ctx.Ownership.Clear();
        ctx.InOwnConversation = false;
        ctx.YesAlready.Release();
    }
}

/// <summary>メニューの項目の選び方（ゲームを起動せずに試せるように分けた）。</summary>
public static class MenuChoice
{
    /// <summary>先頭が <paramref name="prefix"/> の項目がちょうど1つならその番号、そうでなければ -1。</summary>
    public static int ByAddonPrefix(IReadOnlyList<string> entries, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return -1;
        var found = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Trim().StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (found >= 0)
                return -1;
            found = i;
        }

        return found;
    }

    /// <summary>完全一致の項目がちょうど1つならその番号、そうでなければ -1。</summary>
    public static int Exact(IReadOnlyList<string> entries, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return -1;
        var found = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(entries[i].Trim(), text.Trim(), StringComparison.Ordinal))
                continue;
            if (found >= 0)
                return -1;
            found = i;
        }

        return found;
    }
}
