using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;

namespace AutoJobQuest.Automation;

/// <summary>
/// 刺突漁で、魚影にだけ出る魚をそろえる（漁師 Lv68「減少を食い止めろ」の大方士×3）。
/// 移動と漁場の回り方は GBR、突くのは AutoHook（AutoGig）。こちらは「何を突くか」を AutoHook のプリセットで決め、数を見て止める。
///
///  1) 漁師に着替える。刺突漁が使える・GBR と AutoHook が読み込まれている・そのエリアの風脈がすべて開放済み
///     （沈没川船の漁場は水中。GBR は飛べるエリアでだけ潜る：GBR の ShouldFly）を確かめる。
///  2) 計画（<see cref="SpearfishPlanner"/>）：前提の魚・GBR の目標・親の漁場と魚影・欲しい魚の大きさ・速さと代わりの札。
///  3) GBR が潜水・刺突漁をできる設定になっていなければ、刺突漁の間だけ合わせる（<see cref="GbrRequiredSettings"/>：
///     vnavmesh の移動 ON・採集窓の操作 ON・UseAutoHook ON・徒歩の強制 OFF・釣果送信の同意 ON）。変えたものは記録とチャットに出し、後始末で戻す。
///     AutoHook に、刺突漁のプリセット（親の漁場＝前提の魚、魚影＝代わりの札）を取り込む。取り込むと選ばれ、刺突の自動が ON になる。
///     GBR が自分のプリセットを作って上書きしないよう、GBR の「Use existing AutoHook presets」を一時的に ON にし、
///     前提の魚と目標の魚の番号の名前の竿のプリセット（中身は既定の値）を置く（GBR は刺突漁でも、その名前の竿のプリセットがあれば自分のものを作らない）。
///     AutoHook は設定をファイルに少し遅れて書く。GBR はそのファイルを読むので、名前がファイルに出てから GBR を動かす。
///  4) GBR を動かす前に、ファゾム（漁場が見える）とトゥルー・オブ・オーシャン（魚影がナビマップに出る）を付ける。
///     どちらも永続の状態で、特性（オートファゾム・オートトゥルー・オブ・オーシャン）は「漁師にクラスチェンジした際」にしか付かない。
///     すでに漁師で状態が外れていると付かず、漁場が見えないまま GBR が水中で止まる（沈没川船で潜ったまま動かなかった原因）。
///     ファゾムを付けられなければ始めない。トゥルー・オブ・オーシャンは付かなくても進める（GBR は物の一覧でも魚影を見つける）。
///  5) GBR の目標を「魚影の別の魚」にして動かす（GatherTask）。欲しい魚が要る数になったら止める。
///     刺突の画面に出た魚の大きさ・速さは、変わるたびに記録に残す。魚影で、いつもの魚のどれとも違う値の魚を見たら、
///     それを欲しい魚とみて、プリセットを見た値に合わせ直す。動かしている間にファゾムが外れたら付け直し、1分付けられなければ止める。
///  6) 後始末：GBR を止めて設定とリストを戻す。置いた竿のプリセットを消し、前に選ばれていた竿のプリセットを選び直す。
///     刺突漁のプリセットは、消す IPC が無いので残る（知らせる）。刺突の自動の ON/OFF は、始める前の状態に戻す。
/// </summary>
public sealed class SpearfishTask : AutoTask
{
    private enum SpearStep
    {
        Equip,
        Buffs,
        Prepare,
        WaitAutoHook,
        Gather,
    }

    /// <summary>漁師（ClassJob）。</summary>
    public const uint Fisher = 18;

    /// <summary>ファゾム（Action。漁師 Lv61）。刺突漁の漁場を見えるようにする（Questionable の注記「activate Fathom」）。</summary>
    public const uint FathomAction = 7903;

    /// <summary>ファゾムの状態（Status。永続）。</summary>
    public const uint FathomStatus = 1166;

    /// <summary>トゥルー・オブ・オーシャン（Action。漁師 Lv65）。最も近い魚影の位置をナビマップに出す。</summary>
    public const uint TruthAction = 7911;

    /// <summary>トゥルー・オブ・オーシャンの状態（Status。永続）。</summary>
    public const uint TruthStatus = 1173;

    /// <summary>GBR を動かす前に、ファゾムを付けるのを待つ上限（付かなければ始めない）。</summary>
    public static readonly TimeSpan FathomSetupLimit = TimeSpan.FromSeconds(20);

    /// <summary>トゥルー・オブ・オーシャンを付けるのを待つ上限（付かなくても進める）。</summary>
    public static readonly TimeSpan TruthSetupLimit = TimeSpan.FromSeconds(10);

    /// <summary>GBR を動かしている間にファゾムが外れたとき、付け直せずに待つ上限（過ぎたら止める：漁場が見えず GBR が止まるため）。</summary>
    public static readonly TimeSpan FathomLostLimit = TimeSpan.FromMinutes(1);

    /// <summary>GBR の刺突漁を続ける上限。</summary>
    public static readonly TimeSpan GatherLimit = TimeSpan.FromMinutes(50);

    private static readonly TimeSpan AutoHookSaveLimit = TimeSpan.FromSeconds(20);

    private readonly JobQuest quest;
    private readonly uint wanted;
    private readonly int count;
    private readonly QuestionableStep instruction;

    private SpearStep step = SpearStep.Equip;
    private SpearfishPlan? plan;
    private AutoTask? equip;
    private GatherTask? gather;
    private readonly List<string> addedRodPresets = [];
    private string? previousRodPreset;
    private bool? previousAutoGig;
    private bool autoHookTouched;
    private bool gbrTouched;
    private string presetName = string.Empty;
    private DateTime nextFileCheck = DateTime.MinValue;
    private DateTime lastFathom = DateTime.MinValue;
    private DateTime lastTruth = DateTime.MinValue;
    private DateTime lastDismount = DateTime.MinValue;
    private DateTime? fathomMissingSince;
    private bool truthGaveUp;
    private DateTime nextProgressLog = DateTime.MinValue;
    private string lastFishKey = string.Empty;
    private readonly HashSet<(int, int)> triedStats = [];
    private int startCount;

    public SpearfishTask(JobQuest quest, uint wanted, int count, QuestionableStep instruction)
    {
        this.quest = quest;
        this.wanted = wanted;
        this.count = count;
        this.instruction = instruction;
    }

    public override string Name => $"刺突漁: {CraftPlanner.ItemName(this.wanted)}×{this.count}";

    /// <summary>計画（記録・試験用）。</summary>
    public SpearfishPlan? Plan => this.plan;

    /// <summary>GBR が潜水・刺突漁をするのに要る設定（違えば刺突漁の間だけ合わせる）。</summary>
    public static List<GbrRequiredSettings.Setting> RequiredSettings() => GbrRequiredSettings.For(Route.Fish, spearfish: true);

    /// <summary>
    /// 手で行う手順でそろえる品のうち、刺突漁でしか取れず、まだ足りないもの（無ければ null）。
    /// 竿でも釣れる魚は対象にしない（Questionable の釣りの手順と GBR に任せる）。
    /// </summary>
    public static (uint ItemId, int Count)? Want(IEnumerable<QuestItemReq> wanted, Func<uint, int> held, SourceIndex? sources)
    {
        if (sources == null)
            return null;
        foreach (var w in wanted)
        {
            var s = sources.Get(w.ItemId);
            if (s.Spearfish && !s.Fish && held(w.ItemId) < w.Count)
                return (w.ItemId, w.Count);
        }

        return null;
    }

    private int Held => Inventory.CountNow(this.wanted);

    protected override TaskResult OnStart(TaskContext ctx)
    {
        this.startCount = this.Held;
        if (this.startCount >= this.count)
            return TaskResult.Done;

        if (!ctx.GatherBuddy.IsLoaded)
            return this.Fail("GatherBuddyReborn が読み込まれていません");
        if (!ctx.AutoHook.IsLoaded)
            return this.Fail("AutoHook が読み込まれていません（刺突は AutoHook が行います）");
        if (GatherAbilities.Usable(GatherAbilities.Gig) == false)
            return this.Fail($"刺突漁が使えません（{GatherAbilities.Requirement(GatherAbilities.Gig)} が要ります）");

        var territory = this.instruction.Territory;
        switch (GameMemory.AetherCurrentsComplete(territory))
        {
            case false:
                return this.Fail($"「{AreaAccess.Name(territory)}」の風脈がすべて開放されていません。漁場が水中にあり、GBR は飛べるエリアでだけ潜るので、"
                                 + "風脈を開放してから再開するか、手で行ってください");
            case null:
                ctx.Log.Debug("刺突漁", $"エリア {territory} の風脈の開放を読めませんでした（風脈の無いエリアか、読み込み前）。確かめずに進めます");
                break;
        }

        var path = AutoHookData.FishListPath(ctx.AutoHook.LoadedVersion);
        if (path == null)
            return this.Fail("AutoHook の魚のデータ（Data\\FishData\\fish_list.json）が見つかりません");
        Dictionary<uint, AutoHookFish> fish;
        try
        {
            fish = AutoHookData.LoadFishList(path);
        }
        catch (Exception ex)
        {
            return this.Fail($"AutoHook の魚のデータを読めませんでした（{ex.GetType().Name}: {ex.Message}）");
        }

        var english = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>(Dalamud.Game.ClientLanguage.English).TryGetRow(this.wanted, out var en) ? en.Name.ExtractText() : string.Empty;
        var pools = SpearfishPlanner.Pools(territory);
        var near = this.instruction.Position is { } p ? SpearfishPlanner.ToNotebookCoordinates(territory, p) : null;
        this.plan = SpearfishPlanner.Build(this.wanted, this.count, territory, near, fish, pools, this.instruction.Comment, english, out var why);
        if (this.plan == null)
            return this.Fail($"刺突漁の計画を立てられません：{why}");

        var pl = this.plan;
        ctx.Log.Write("刺突漁", $"{CraftPlanner.ItemName(this.wanted)}×{this.count}（いま {this.startCount}）を刺突漁で集めます。"
                               + $"親の漁場「{PoolName(pl.Parent)}」（刺突漁の表 {pl.Parent.NotebookId}）で {CraftPlanner.ItemName(pl.Predator)} を突き、"
                               + $"魚影「{PoolName(pl.Shadow)}」（{pl.Shadow.NotebookId}）で {CraftPlanner.ItemName(this.wanted)} を突きます。GBR の目標は {CraftPlanner.ItemName(pl.GbrTarget)}");
        ctx.Log.Debug("刺突漁", $"AutoHook の魚のデータ：{path}（{fish.Count} 件）。欲しい魚のデータ：{DescribeFish(fish, this.wanted)}／前提の魚：{DescribeFish(fish, pl.Predator)}／GBR の目標：{DescribeFish(fish, pl.GbrTarget)}");
        ctx.Log.Debug("刺突漁", $"魚影のいつもの魚：{string.Join("、", pl.ShadowRegulars.Select(r => $"{CraftPlanner.ItemName(r.Item)}（{AutoHookData.Describe(r.Size, r.Speed)}）"))}");
        ctx.Log.Write("刺突漁", pl.Stats is { } st
            ? $"{CraftPlanner.ItemName(this.wanted)} の大きさ・速さは {AutoHookData.Describe(st.Size, st.Speed)}（{pl.StatsFrom}）。"
              + (pl.Proxy is { } proxy ? $"AutoHook には {CraftPlanner.ItemName(proxy)}（同じ大きさ・速さ）の名前で頼みます" : "同じ大きさ・速さの魚が AutoHook のデータに無いので、魚影で見た値で決めます")
            : $"{CraftPlanner.ItemName(this.wanted)} の大きさ・速さが分かりません（{pl.StatsFrom}）。魚影で見た値で決めます");
        if (pl.Stats is { } s2)
            this.triedStats.Add((s2.Size, s2.Speed));

        this.presetName = $"AutoJobQuest_{CraftPlanner.ItemName(this.wanted)}";
        if (Jobs.CurrentClassJob != Fisher)
            this.equip = new EquipJobTask(Fisher);
        return TaskResult.Running;
    }

    private static string PoolName(SpearfishPool pool)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SpearfishingNotebook>().TryGetRow(pool.NotebookId, out var nb) && nb.PlaceName.ValueNullable is { } pn
            ? pn.Name.ExtractText()
            : $"#{pool.NotebookId}";

    private static string DescribeFish(IReadOnlyDictionary<uint, AutoHookFish> fish, uint item)
        => fish.TryGetValue(item, out var f)
            ? $"{CraftPlanner.ItemName(item)} {AutoHookData.Describe(f.Size, f.Speed)}{(AutoHookData.IsGameSpeed(f.Speed) ? string.Empty : "（ゲームの値ではない）")}・前提 {string.Join("・", f.Predators.Select(x => $"{CraftPlanner.ItemName(x.ItemId)}×{x.Quantity}"))}"
            : $"{CraftPlanner.ItemName(item)}（データ無し）";

    protected override TaskResult Tick(TaskContext ctx)
    {
        this.ObserveFish(ctx);

        var held = this.Held;
        if (held >= this.count)
        {
            ctx.Log.Write("刺突漁", $"{CraftPlanner.ItemName(this.wanted)} が {held}/{this.count} そろいました");
            return TaskResult.Done;
        }

        switch (this.step)
        {
            case SpearStep.Equip:
                if (this.equip != null)
                {
                    var r = this.equip.Step(ctx);
                    this.Status = this.equip.Status;
                    if (r == TaskResult.Running)
                        return TaskResult.Running;
                    this.equip.Cleanup(ctx);
                    var failed = r == TaskResult.Failed ? this.equip.FailReason : null;
                    this.equip = null;
                    if (failed != null)
                        return this.Fail(failed);
                }

                this.step = SpearStep.Buffs;
                this.NextPhase("ファゾムとトゥルー・オブ・オーシャンを付けています");
                return TaskResult.Running;

            case SpearStep.Buffs:
                return this.SetupBuffs(ctx);

            case SpearStep.Prepare:
                return this.Prepare(ctx);

            case SpearStep.WaitAutoHook:
                return this.WaitAutoHook(ctx);

            default:
                return this.RunGather(ctx, held);
        }
    }

    private TaskResult Prepare(TaskContext ctx)
    {
        var pl = this.plan!;

        // GBR が潜水・刺突漁をできる設定になっていなければ、刺突漁の間だけ合わせる（
        // 要る設定は GBR のソースで確かめた：GbrRequiredSettings。戻すのは後始末＝RestoreIfIdle）。AutoHook に触る前に行う。
        // GBR が動いている・読めないときは設定に触らない（利用者が動かしている GBR の設定を途中で変えない。GatherTask と同じく止める）
        var running = ctx.GatherBuddy.IsAutoGatherEnabled();
        if (running != false)
            return this.Fail(running == true
                ? "GBR の自動採集がすでに動いています（利用者の操作を横取りしないため、GBR の設定に触らずに止めました）"
                : "GBR の自動採集の状態が読めません（GBR の設定に触らずに止めました）");

        this.gbrTouched = true;
        var required = RequiredSettings();
        ctx.Log.Debug("刺突漁", $"GBR の設定（合わせる前）：{GbrRequiredSettings.Snapshot(required, ctx.Gbr.ReadAutoGatherBool)}");
        var changed = GbrRequiredSettings.Apply(required, ctx.Gbr.ReadAutoGatherBool, ctx.Gbr.OverrideBool, out var failedSetting);
        foreach (var c in changed)
            ctx.Log.Write("刺突漁", $"GBR の設定を、刺突漁の間だけ変えました：{GbrRequiredSettings.Describe(c)}。終わったら戻します");
        if (changed.Count > 0)
        {
            var consent = GbrRequiredSettings.ConsentNote(changed);
            Svc.Chat.Print($"[AutoJobQuest] GBR が潜水・刺突漁をできるよう、設定を刺突漁の間だけ変えました：{string.Join("、", changed.Select(c => $"{c.Setting.Label} {GbrRequiredSettings.OnOff(c.Before)}→{GbrRequiredSettings.OnOff(c.Setting.Value)}"))}。"
                           + $"終わったら元に戻します{consent}");
        }

        if (failedSetting != null)
            return this.Fail(GbrRequiredSettings.FailText(failedSetting, ctx.Gbr.LastError));

        var configPath = AutoHookData.ConfigPath;
        var before = configPath == null ? null : AutoHookData.ReadConfig(configPath);
        if (before == null)
            return this.Fail("AutoHook の設定ファイル（pluginConfigs\\AutoHook.json）を読めません");
        this.previousRodPreset = before.Value.SelectedRodPreset;
        this.previousAutoGig = before.Value.AutoGigEnabled;
        ctx.Log.Debug("刺突漁", $"AutoHook の始める前の状態：竿のプリセット「{this.previousRodPreset ?? "（選ばれていない）"}」・刺突の自動 {this.previousAutoGig?.ToString() ?? "読めない"}・"
                               + $"竿のプリセット {before.Value.RodPresets.Count} 件・刺突漁のプリセット {before.Value.GigPresets.Count} 件");

        // GBR が刺突漁でも自分のプリセットを作らないように（前提の魚と目標の魚の番号の名前の竿のプリセット）
        foreach (var id in new[] { pl.Predator, pl.GbrTarget })
        {
            var name = id.ToString();
            if (before.Value.RodPresets.Contains(name))
            {
                ctx.Log.Debug("刺突漁", $"竿のプリセット「{name}」は前からあるので、それを使います（消しません）");
                continue;
            }

            this.autoHookTouched = true;
            if (!ctx.AutoHook.ImportAndSelectPreset(AutoHookData.RodPresetString(name)))
                return this.Fail($"AutoHook に竿のプリセット「{name}」を取り込めませんでした（{ctx.AutoHook.LastErrors.GetValueOrDefault("ImportAndSelectPreset") ?? "理由を読めません"}）");
            this.addedRodPresets.Add(name);
            ctx.Log.Write("刺突漁", $"AutoHook に竿のプリセット「{name}」（{CraftPlanner.ItemName(id)} の番号。GBR に刺突漁のプリセットを作らせないための札）を置きました");
        }

        if (!this.ImportGigPreset(ctx))
            return this.Fail($"AutoHook に刺突漁のプリセットを取り込めませんでした（{ctx.AutoHook.LastErrors.GetValueOrDefault("ImportAndSelectPreset") ?? "理由を読めません"}）");

        if (!ctx.Gbr.OverrideBool("UseExistingAutoHookPresets", true))
            return this.Fail($"GBR の「Use existing AutoHook presets」を一時的に ON にできませんでした（{ctx.Gbr.LastError}）");

        this.step = SpearStep.WaitAutoHook;
        this.NextPhase("AutoHook が設定を書き出すのを待っています");
        return TaskResult.Running;
    }

    private bool ImportGigPreset(TaskContext ctx)
    {
        var gigs = this.plan!.Gigs();
        var preset = AutoHookData.SpearfishPresetString(this.presetName, gigs);
        this.autoHookTouched = true;
        var ok = ctx.AutoHook.ImportAndSelectPreset(preset);
        ctx.Log.Write("刺突漁", $"AutoHook に刺突漁のプリセット「{this.presetName}」を取り込み{(ok ? "ました" : "ませんでした")}："
                               + string.Join("／", gigs.Select(g => $"{(g.Notebook == this.plan.Parent.NotebookId ? "親の漁場" : "魚影")}（{g.Notebook}）で {CraftPlanner.ItemName(g.ItemId)}")));
        ctx.Log.Debug("刺突漁", $"取り込んだ中身：{AutoHookData.DecodePresetString(preset)}");
        return ok;
    }

    private TaskResult WaitAutoHook(TaskContext ctx)
    {
        if (DateTime.UtcNow < this.nextFileCheck)
            return TaskResult.Running;
        this.nextFileCheck = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        var pl = this.plan!;
        var now = AutoHookData.ConfigPath is { } path ? AutoHookData.ReadConfig(path) : null;
        var ready = now is { } c && c.RodPresets.Contains(pl.Predator.ToString()) && c.RodPresets.Contains(pl.GbrTarget.ToString()) && c.GigPresets.Contains(this.presetName);
        if (!ready)
        {
            this.Status = "AutoHook が設定を書き出すのを待っています";
            return this.TimedOut(AutoHookSaveLimit)
                ? this.Fail($"AutoHook の設定ファイルに、取り込んだプリセットが {AutoHookSaveLimit.TotalSeconds:0} 秒たっても出ません（GBR が読むのはファイルなので、進めません）")
                : TaskResult.Running;
        }

        ctx.Log.Debug("刺突漁", "AutoHook の設定ファイルに、取り込んだプリセットが出ました。GBR を動かします");
        this.gather = new GatherTask([new GatherNeed(pl.GbrTarget, Inventory.CountNow(pl.GbrTarget) + 99)], pl.Parent.Territory,
            $"刺突漁（{CraftPlanner.ItemName(this.wanted)}）", GatherLimit, Route.Fish)
        {
            StopWhen = () => this.Held >= this.count,
            Spearfish = true,
        };
        this.step = SpearStep.Gather;
        this.NextPhase("GBR と AutoHook で刺突漁をしています");
        return TaskResult.Running;
    }

    /// <summary>
    /// GBR を動かす前に、ファゾム（必須）とトゥルー・オブ・オーシャン（付かなくても進める）を付ける。
    /// 使える状態なら使う。乗り物に乗っていれば降りる（乗ったままでは使えない。空中なら着地してから降りる。潜っている間は待つ）。
    /// </summary>
    private TaskResult SetupBuffs(TaskContext ctx)
    {
        var fathom = GameMemory.HasStatus(FathomStatus);
        if (!fathom)
        {
            switch (this.TryBuff(ctx, FathomAction, ref this.lastFathom, allowDismount: true, this.PhaseElapsed, FathomSetupLimit))
            {
                case SpearBuffs.Verdict.GiveUp:
                    return this.Fail($"ファゾムを付けられませんでした（行動の状態 {GameUi.ActionStatus(FathomAction)}）。ファゾムが無いと刺突漁の漁場が見えず、GBR が水中で止まります。"
                                     + "ファゾムを使うか、ほかのジョブから漁師に着替え直して（特性「オートファゾム」で付きます）から再開してください");
                default:
                    this.Status = "ファゾムを付けています";
                    return TaskResult.Running;
            }
        }

        if (!this.truthGaveUp && !GameMemory.HasStatus(TruthStatus))
        {
            switch (this.TryBuff(ctx, TruthAction, ref this.lastTruth, allowDismount: true, this.PhaseElapsed, FathomSetupLimit + TruthSetupLimit))
            {
                case SpearBuffs.Verdict.GiveUp:
                    this.truthGaveUp = true;
                    ctx.Log.Warn("刺突漁", $"トゥルー・オブ・オーシャンを付けられませんでした（行動の状態 {GameUi.ActionStatus(TruthAction)}）。魚影がナビマップに出ませんが、GBR は物の一覧でも魚影を探すので進めます");
                    break;
                default:
                    this.Status = "トゥルー・オブ・オーシャンを付けています";
                    return TaskResult.Running;
            }
        }

        ctx.Log.Write("刺突漁", $"ファゾム {(GameMemory.HasStatus(FathomStatus) ? "あり" : "なし")}・トゥルー・オブ・オーシャン {(GameMemory.HasStatus(TruthStatus) ? "あり" : "なし")}。AutoHook と GBR の用意に進みます");
        this.step = SpearStep.Prepare;
        return TaskResult.Running;
    }

    /// <summary>その状態を付けるための1歩（使う・降りる・待つ）。使ってから2秒・降りてから1秒はあける。</summary>
    private SpearBuffs.Verdict TryBuff(TaskContext ctx, uint action, ref DateTime lastUse, bool allowDismount, TimeSpan missingFor, TimeSpan limit)
    {
        var verdict = SpearBuffs.Decide(false, GameUi.ActionStatus(action), GameUi.PlayerFree(), GameUi.Mounted,
            Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving],
            allowDismount, missingFor, limit);
        switch (verdict)
        {
            case SpearBuffs.Verdict.Use when DateTime.UtcNow - lastUse >= TimeSpan.FromSeconds(2):
                lastUse = DateTime.UtcNow;
                ctx.Log.Write("刺突漁", $"{ActionName(action)}が付いていないので使います");
                GameUi.UseAction(action);
                break;
            case SpearBuffs.Verdict.Dismount when DateTime.UtcNow - this.lastDismount >= TimeSpan.FromSeconds(1):
                this.lastDismount = DateTime.UtcNow;
                ctx.Log.Write("刺突漁", $"{ActionName(action)}を使うため、乗り物から降ります");
                GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23：TalkToNpcTask と同じ）
                break;
        }

        return verdict;
    }

    private static string ActionName(uint action)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(action, out var a) ? a.Name.ExtractText() : $"行動 {action}";

    private TaskResult RunGather(TaskContext ctx, int held)
    {
        // GBR を動かしている間にファゾムが外れたら付け直す。乗り物の上では降ろさない（GBR の移動と取り合う）。1分付けられなければ止める
        if (GameMemory.HasStatus(FathomStatus))
        {
            this.fathomMissingSince = null;
        }
        else if (Me.Territory == this.plan!.Parent.Territory)
        {
            this.fathomMissingSince ??= DateTime.UtcNow;
            if (this.TryBuff(ctx, FathomAction, ref this.lastFathom, allowDismount: false, DateTime.UtcNow - this.fathomMissingSince.Value, FathomLostLimit) == SpearBuffs.Verdict.GiveUp)
                return this.Fail($"刺突漁の途中でファゾムが外れ、{FathomLostLimit.TotalMinutes:0}分付け直せませんでした（行動の状態 {GameUi.ActionStatus(FathomAction)}）。"
                                 + "漁場が見えないと GBR が止まるので止めます。ファゾムを使ってから再開してください");
        }

        var r = this.gather!.Step(ctx);
        this.Status = $"{CraftPlanner.ItemName(this.wanted)} {held}/{this.count}・{CraftPlanner.ItemName(this.plan!.Predator)} {Inventory.CountNow(this.plan.Predator)}　{this.gather.Status}";
        if (DateTime.UtcNow >= this.nextProgressLog)
        {
            this.nextProgressLog = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            ctx.Log.Debug("刺突漁", $"途中：{this.Status}・GBR「{ctx.GatherBuddy.StatusText()}」・エリア {Me.Territory}・位置 {Me.Position:0.0}"
                                   + $"・ファゾム {GameMemory.HasStatus(FathomStatus)}・トゥルー・オブ・オーシャン {GameMemory.HasStatus(TruthStatus)}・乗り物 {GameUi.Mounted}");
        }

        if (r == TaskResult.Running)
            return TaskResult.Running;

        this.gather.Cleanup(ctx);
        var gatherFailed = r == TaskResult.Failed ? this.gather.FailReason : null;
        this.gather = null;
        if (this.Held >= this.count)
            return TaskResult.Done;
        return this.Fail($"GBR の刺突漁が、{CraftPlanner.ItemName(this.wanted)} がそろう前に終わりました（{this.Held}/{this.count}"
                         + $"{(gatherFailed != null ? $"・{gatherFailed}" : string.Empty)}）");
    }

    /// <summary>刺突の画面の魚を記録し、魚影で欲しい魚の値を見たら、プリセットを合わせ直す。</summary>
    private void ObserveFish(TaskContext ctx)
    {
        if (this.plan is not { } pl || GameMemory.SpearfishingFish() is not { } fishNow)
            return;

        var target = GameMemory.TargetBaseId;
        var baseId = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.GatheringPoint>().TryGetRow(target, out var gp) ? gp.GatheringPointBase.RowId : 0;
        var where = baseId == pl.Shadow.GatheringPointBase ? "魚影" : baseId == pl.Parent.GatheringPointBase ? "親の漁場" : $"ほかの漁場（採集点 {target}）";
        var key = $"{where}|{string.Join(",", fishNow.Select(f => $"{f.Size}/{f.Speed}/{f.Inverse}"))}";
        if (key != this.lastFishKey)
        {
            this.lastFishKey = key;
            ctx.Log.Debug("刺突漁", $"刺突の画面（{where}）：{(fishNow.Count == 0 ? "魚なし" : string.Join("／", fishNow.Select(f => $"{AutoHookData.Describe(f.Size, f.Speed)}{(f.Inverse ? "・逆向き" : string.Empty)}")))}");
        }

        if (baseId != pl.Shadow.GatheringPointBase)
            return;
        if (SpearfishPlanner.Calibrate(fishNow.Select(f => (f.Size, f.Speed)), pl.ShadowRegulars, pl.Stats) is not { } seen || this.triedStats.Contains(seen))
            return;
        this.triedStats.Add(seen);

        var fish = AutoHookData.FishListPath(ctx.AutoHook.LoadedVersion) is { } path ? AutoHookData.LoadFishList(path) : [];
        var proxy = SpearfishPlanner.ProxyFor(seen, this.wanted, fish, pl.Shadow.Items.Concat(pl.Parent.Items).ToHashSet());
        ctx.Log.Warn("刺突漁", $"魚影で、いつもの魚と違う {AutoHookData.Describe(seen.Size, seen.Speed)} の魚を見ました。{CraftPlanner.ItemName(this.wanted)} とみて、"
                              + (proxy is { } px ? $"AutoHook のプリセットを {CraftPlanner.ItemName(px)}（同じ大きさ・速さ）に合わせ直します" : "合わせ直したいのですが、同じ大きさ・速さの魚が AutoHook のデータにありません"));
        if (proxy == null)
            return;
        this.plan = pl with { Stats = seen, StatsFrom = "魚影で見た値", Proxy = proxy };
        this.ImportGigPreset(ctx);
    }

    public override void Cleanup(TaskContext ctx)
    {
        this.equip?.Cleanup(ctx);
        this.equip = null;

        // GBR を止めて、リストと設定（潜水・刺突漁のために変えた設定と Use existing AutoHook presets を含む）を戻す
        if (this.gather != null)
            this.gather.Cleanup(ctx);
        else if (this.gbrTouched && !ctx.Gbr.RestoreIfIdle(ctx.GatherBuddy.IsAutoGatherEnabled(), ctx.Gbr.VendorIsBusy()))
            ctx.Log.Warn("刺突漁", "GBR が止まったことを確かめられないので、GBR の設定は後で戻します");
        this.gather = null;

        if (!this.autoHookTouched)
            return;

        // 置いた竿のプリセットを消し、前に選ばれていた竿のプリセットを選び直す
        foreach (var name in this.addedRodPresets)
        {
            if (ctx.AutoHook.SetPreset(name) && ctx.AutoHook.DeleteSelectedPreset())
                ctx.Log.Debug("刺突漁", $"AutoHook の竿のプリセット「{name}」を消しました");
            else
                ctx.Log.Warn("刺突漁", $"AutoHook の竿のプリセット「{name}」を消せませんでした。要らなければ AutoHook の画面で消してください");
        }

        this.addedRodPresets.Clear();
        if (this.previousRodPreset != null)
            ctx.AutoHook.SetPreset(this.previousRodPreset);

        if (this.previousAutoGig is { } gig)
            ctx.AutoHook.SetAutoGigState(gig);

        var msg = $"AutoHook の刺突漁のプリセット「{this.presetName}」は残しました（AutoHook に消す手段〔IPC〕が無いため）。要らなければ AutoHook の刺突漁の画面で消してください";
        ctx.Log.Write("刺突漁", msg);
        Svc.Chat.Print($"[AutoJobQuest] {msg}");
        this.autoHookTouched = false;
    }
}

/// <summary>
/// 刺突漁の状態（ファゾム・トゥルー・オブ・オーシャン）を付けるときの判断（ゲームを起動せずに試せるように分けた）。
/// </summary>
public static class SpearBuffs
{
    public enum Verdict
    {
        /// <summary>付いている。</summary>
        Ok,

        /// <summary>使う（行動が使える状態）。</summary>
        Use,

        /// <summary>乗り物から降りる（乗っていて行動が使えない。空中なら降りる操作で着地してから降りる）。</summary>
        Dismount,

        /// <summary>待つ（動けない・水中の乗り物の上など）。</summary>
        Wait,

        /// <summary>上限まで付けられなかった。</summary>
        GiveUp,
    }

    /// <param name="has">状態が付いているか。</param>
    /// <param name="actionStatus">行動の状態（ActionManager.GetActionStatus。0＝使える）。</param>
    /// <param name="playerFree">自由に動ける状態か。</param>
    /// <param name="mounted">乗り物に乗っているか。</param>
    /// <param name="airborne">水中か（潜っている間は降ろさない）。</param>
    /// <param name="allowDismount">降りてよいか（GBR が動かしている間は降ろさない）。</param>
    /// <param name="missingFor">付いていない時間。</param>
    /// <param name="limit">上限。</param>
    public static Verdict Decide(bool has, uint actionStatus, bool playerFree, bool mounted, bool airborne, bool allowDismount, TimeSpan missingFor, TimeSpan limit)
    {
        if (has)
            return Verdict.Ok;
        if (missingFor >= limit)
            return Verdict.GiveUp;
        if (actionStatus == 0 && playerFree)
            return Verdict.Use;
        if (allowDismount && mounted && !airborne && playerFree)
            return Verdict.Dismount;
        return Verdict.Wait;
    }
}

