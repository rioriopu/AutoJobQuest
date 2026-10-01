using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using AutoJobQuest.Planning;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// グリダニアの宿屋へ入る（素材がそろったらグリダニアの宿屋で製作する）。
///
/// Lifestream の EnqueueLocalInnShortcut(2)。2 はグリダニア（LifestreamIpc の説明を参照）。
/// 着いたかは「いまのエリアが宿屋の個室（TerritoryIntendedUse=2）で、グリダニアと同じ地域」で確かめる
/// （177/178/179 の宿屋はどれも TerritoryIntendedUse=2。ゲームデータで実測）。
/// Lifestream は忙しいと何もせずに戻る（IPC は void）ので、呼んだあと IsBusy が立ったかで受け付けを確かめる。
/// </summary>
public sealed class GoToInnTask : AutoTask
{
    /// <summary>宿屋の個室を表す TerritoryIntendedUse の値（177/178/179 で実測）。</summary>
    private const uint InnIntendedUse = 2;

    /// <summary>グリダニア：新市街（Lifestream の宿屋番号 2 の街）。宿屋が同じ地域かの判定に使う。</summary>
    private const uint NewGridania = 132;

    private bool requested;
    private bool sawBusy;
    private int attempts;

    // Lifestream の IsBusy が読めない（null）のを最初に見た時刻。読めないのを「終わった」と扱わない
    private DateTime? unknownSince;

    /// <summary>他のプラグインの状態が読めないまま待つ上限。</summary>
    public static readonly TimeSpan UnknownLimit = TimeSpan.FromSeconds(30);

    public override string Name => "グリダニアの宿屋へ";

    public static bool InGridaniaInn()
    {
        var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
        if (!sheet.TryGetRow(Me.Territory, out var here) || !sheet.TryGetRow(NewGridania, out var city))
            return false;
        return here.TerritoryIntendedUse.RowId == InnIntendedUse && here.PlaceNameZone.RowId == city.PlaceNameZone.RowId;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (InGridaniaInn() && GameUi.PlayerFree())
            return TaskResult.Done;

        // 上限は実際に作業できた時間で測る（反撃・会話の窓の処理と、他者の画面を待った時間は数えない）
        if (this.WorkElapsed > TimeSpan.FromMinutes(4))
            return this.Fail("4分以内にグリダニアの宿屋へ入れませんでした");
        if (this.WallElapsed > TimeSpan.FromMinutes(12) + WindowWaitLimit)
            return this.Fail($"グリダニアの宿屋へ向かう途中で中断が続き、{this.WallElapsed.TotalMinutes:0}分たっても入れませんでした");

        if (this.WaitForWindows(out var windowFail))
            return windowFail != null ? this.Fail(windowFail) : TaskResult.Running;

        var busy = ctx.Lifestream.IsBusy();
        if (this.requested)
        {
            // 読めないのは「終わった」ではない。頼み直すと二重に頼むので、読めるようになるまで待つ（上限を過ぎたら止める）
            if (busy == null && !GameUi.BetweenAreas)
            {
                this.unknownSince ??= DateTime.UtcNow;
                this.Status = "Lifestream の状態を読めません（待っています）";
                return DateTime.UtcNow - this.unknownSince.Value > UnknownLimit
                    ? this.Fail($"Lifestream の状態（IsBusy）を {UnknownLimit.TotalSeconds:0} 秒読めません。宿屋へ向かっているか分からないので止めます")
                    : TaskResult.Running;
            }

            this.unknownSince = null;
            if (busy == true || GameUi.BetweenAreas)
            {
                this.sawBusy = true;
                this.Status = "Lifestream が宿屋へ向かっています";
                return TaskResult.Running;
            }

            // Lifestream の作業が終わったのに宿屋でない（宿屋が未解放なら Lifestream が自分で中断する：Lifestream の EnqueueGoToInn）→ 頼み直す
            if (this.sawBusy)
                ctx.Log.Write("宿屋", "Lifestream の作業が終わりましたが、宿屋に入っていません。頼み直します");
            this.requested = false;
        }

        if (!GameUi.PlayerFree() || GameUi.InCombat)
        {
            this.Status = "動ける状態になるのを待っています";
            return TaskResult.Running;
        }

        // Lifestream が別の作業中（他のプラグインや利用者が頼んだもの）なら、頼んでも「忙しい」で断られるので、終わるのを待つ
        if (busy == true)
        {
            this.Status = "Lifestream が別の作業中です（終わるのを待っています）";
            return TaskResult.Running;
        }

        if (this.attempts++ >= 3)
            return this.Fail("Lifestream で宿屋へ入れませんでした（3 回頼みました。宿屋が未解放の可能性）");

        if (!ctx.Lifestream.EnqueueLocalInn(LifestreamIpc.GridaniaInnIndex))
            return this.Fail("Lifestream に宿屋への移動を頼めませんでした");

        // 受け付けたかは頼んだ直後の IsBusy で分かる（Lifestream の受け付けは同じフレームで終わる：InnRequest。以前は 5 秒待っていた）
        this.requested = true;
        this.sawBusy = false;
        switch (InnRequest.AfterRequest(ctx.Lifestream.IsBusy()))
        {
            case InnRequest.Verdict.Accepted:
                this.sawBusy = true;
                break;
            case InnRequest.Verdict.Refused:
                return this.Fail("Lifestream が宿屋への移動を受け付けませんでした（Lifestream が別の作業中か、キャラクターが読み込み中。直前に確かめたときは空いていました）");
        }

        this.NextPhase("Lifestream に宿屋への移動を頼みました");
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // こちらが頼んだ宿屋への移動がまだ動いていれば止める（止めたのに宿屋へ向かい続けないように）
        if (this.requested && !InGridaniaInn() && ctx.Lifestream.IsBusy() == true)
            ctx.Lifestream.Abort();
    }
}

/// <summary>
/// 計画どおりに Artisan で作る（1レシピずつ CraftItem）。
///
///  ・作る前に秘伝書を確かめる（Artisan の CraftItem は確かめずに進まなくなる）。
///  ・作る前後で所持数を比べ、「材料が減った AND 完成品が増えた」で成功とみなす。
///  ・HQ 指定の品は HQ の数を数える。NQ しかできなければ記録し、次の周回で作り直す。
///  ・職の持ち替えは Artisan が自分で行う（ギアセットが必要）。
/// </summary>
public sealed class CraftOneTask : AutoTask
{
    private readonly PlannedCraft craft;
    private int beforeAll;
    private int beforeHq;
    private int expected;
    private bool requested;
    private bool sawBusy;
    private bool collectable;
    private Recipe recipe;

    // 今の頼みを出した時点の完成品の数・頼んだ回数・頼み直した回数（連続製作が途中で止まったら残りを頼み直す：CraftResume）
    private int attemptBase;
    private int attemptHq;
    private int requestCrafts;
    private int resumed;

    // こちらが Artisan の食事・薬を一時的に「使わない」にしたか（後始末で戻す）
    private bool consumablesDisabled;

    // HQ 指定の品の「試し作り」中か（まず1回作って HQ になるのを確かめてから残りを作る。
    // HQ にならなければ残りは作らず、材料を残したまま HQ の失敗として数える）
    private bool trial;

    // Artisan の IsBusy が読めない（null）のを最初に見た時刻。読めないのを「終わった」と扱わない
    private DateTime? unknownSince;

    // 作る前の材料の所持数（「材料が減った」を確かめるため）
    private readonly Dictionary<uint, int> ingredientsBefore = [];

    // 製作の失敗：失敗に見え始めた時刻（1秒続いたら本物とみる）・連続製作を止めてもらったか・製作が止まった時刻
    private DateTime? failureSeenAt;
    private bool failureStopRequested;
    private DateTime? idleSince;

    // 装備品の素材の選択（GearIngredients）：選ぶ欄（欄の番号・品）・済んだか・最後に操作した時刻・試した回数・レシピを開いた時刻
    private List<(int Slot, uint Item)>? gearSlots;
    private bool gearDone;
    private DateTime gearActAt = DateTime.MinValue;
    private int gearTries;
    private DateTime gearNoteAt = DateTime.MinValue;

    // 簡易製作：簡易製作で作るか・職を合わせる下請け（装備品の素材を選ぶ前にも使う）・最後に操作した時刻・レシピを開いた時刻・試した回数・
    // 最後に数が増えた時刻と数
    private bool quick;
    private AutoTask? jobSub;
    private DateTime quickActAt = DateTime.MinValue;
    private DateTime noteOpenedAt = DateTime.MinValue;
    private int quickTries;
    private DateTime quickProgressAt = DateTime.MinValue;
    private int quickProgressMade = -1;

    /// <summary>製作に失敗したときに、自分だけに見えるチャットへ出す文。</summary>
    public const string FailureChat = "製作に失敗したので、装備品や食事、スキル回し等を見直して下さい";

    public CraftOneTask(PlannedCraft craft)
    {
        this.craft = craft;
    }

    public override string Name => $"製作: {CraftPlanner.ItemName(this.craft.ItemId)} ×{this.craft.Crafts}回";

    /// <summary>作れた数（完成品の増えた数）。</summary>
    public int Made { get; private set; }

    /// <summary>HQ でできた数（HQ 指定の品の HQ 失敗を品目ごとに数えるため）。</summary>
    public int MadeHq { get; private set; }

    /// <summary>作るはずだった数。</summary>
    public int Expected => this.expected;

    /// <summary>
    /// HQ でできてほしい数（HQ 指定の品だけ）。作る前の手持ちの HQ で足りない分（HQ の納品の数 − 手持ちの HQ）で、
    /// 作る数より多くはしない。品質を問わない納品の分や材料の分まで HQ を求めない（HQ の失敗の数え方）。
    /// </summary>
    public int HqNeeded { get; private set; }

    /// <summary>作った品の計画。</summary>
    public PlannedCraft Craft => this.craft;

    /// <summary>終わりまで進んだか（HQ の数を数えてよいか）。</summary>
    public bool Finished { get; private set; }

    protected override TaskResult OnStart(TaskContext ctx)
    {
        if (this.craft.SecretRecipeBookId != 0 && !PlanBuilder.IsBookUnlocked(this.craft.SecretRecipeBookId))
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} の秘伝書が未読です");

        // 収集品（シーダーロングボウなど）は通常の数え方では 0 になるので、カバンの枠を直接数える
        this.collectable = Svc.Data.GetExcelSheet<Item>().TryGetRow(this.craft.ItemId, out var itemRow) && itemRow.AlwaysCollectable;

        var inv = Inventory.Snapshot();
        this.beforeAll = this.CountMade(inv);
        this.beforeHq = inv.CountHq(this.craft.ItemId);
        this.expected = this.craft.Crafts * this.craft.Yield;
        this.HqNeeded = !this.craft.WantHq ? 0
            : this.craft.HqTarget > 0 ? Math.Min(this.expected, Math.Max(0, this.craft.HqTarget - this.beforeHq))
            : this.expected;
        this.attemptBase = this.beforeAll;
        this.attemptHq = this.beforeHq;
        this.requestCrafts = this.craft.Crafts;
        this.trial = this.craft.WantHq && this.craft.Crafts > 1 && this.HqNeeded > 0 && !this.collectable;
        if (this.trial)
        {
            this.requestCrafts = 1;
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}（HQ 指定）は、まず1回作って HQ になるのを確かめてから残りの {this.craft.Crafts - 1} 回を作ります");
        }

        // 材料が足りるかを先に確かめる（足りないまま頼むと Artisan は材料切れで止まるだけ）
        this.recipe = Svc.Data.GetExcelSheet<Recipe>().GetRow(this.craft.RecipeId);

        // NQ でよい品は簡易製作で作る。HQ が要る完成品とその中間素材・収集品は通常の製作（Artisan）。
        // 簡易製作はゲームの決まりで、簡易製作のできるレシピを一度でも作ったことがあるときだけ（Artisan の判定と同じ）
        this.quick = !this.craft.WantHq && !this.craft.HqChain && !this.collectable && this.recipe.CanQuickSynth
                     && !this.recipe.Ingredient.Any(x => x.RowId != 0 && Svc.Data.GetExcelSheet<Item>().TryGetRow(x.RowId, out var gearItem) && gearItem.EquipSlotCategory.RowId != 0)
                     && GameMemory.IsRecipeComplete(this.craft.RecipeId);
        if (this.quick)
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} は NQ でよいので、簡易製作で {this.craft.Crafts} 回作ります");
        foreach (var (ing, amount) in CraftPlanner.Ingredients(this.recipe))
        {
            var have = inv.CountAll(ing);
            this.ingredientsBefore[ing] = have;
            if (have < amount * this.craft.Crafts)
                ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(ing)} が {have}/{amount * this.craft.Crafts} しかありません（作れる分だけ作ります）");
        }

        return TaskResult.Running;
    }

    protected override TaskResult Tick(TaskContext ctx)
    {
        if (this.quick)
            return this.TickQuick(ctx);

        var busy = ctx.Artisan.IsBusy();

        if (!this.requested)
        {
            if (busy != false)
            {
                // 読めない（null）ときも処理中とみなして待つ
                this.Status = "Artisan が空くのを待っています";
                if (this.PhaseElapsed > TimeSpan.FromMinutes(1))
                    return this.Fail("Artisan が他の処理をしていて空きません");
                return TaskResult.Running;
            }

            // Artisan の既定の食事・薬を、こちらが頼む製作では使わない（設定 UseArtisanConsumables が false のとき）。
            // 送る前に控えを保存する（読み込みの解除をまたいでも戻せるように）
            if (!ctx.Config.UseArtisanConsumables && !this.consumablesDisabled)
            {
                // 前の実行でこちらが入れて戻しきれなかった指定（控えにあり、値もこちらの値）なら、自分の指定としてそのまま使う
                var ownLeftover = ctx.Config.ArtisanTempConsumableRecipes.Contains(this.craft.RecipeId) && ctx.Artisan.IsOwnTemp(this.craft.RecipeId);
                if (!ownLeftover && !ctx.Artisan.CanDisableConsumables(this.craft.RecipeId))
                    return this.Fail("Artisan の食事・薬に既存の一時指定があるか、読み取れません。他の処理の指定を上書きせず止めました。Artisan を確認してください");
                if (!ctx.Config.ArtisanTempConsumableRecipes.Contains(this.craft.RecipeId))
                {
                    ctx.Config.ArtisanTempConsumableRecipes.Add(this.craft.RecipeId);
                    ctx.Config.Save();
                }

                if (!ownLeftover && !ctx.Artisan.DisableConsumablesTemporarily(this.craft.RecipeId))
                    return this.Fail($"Artisan に「この製作では食事・薬を使わない」を頼めませんでした（高価な消耗品を使わないよう、製作を始めません）: {string.Join(" / ", ctx.Artisan.LastErrors.Values)}");
                this.consumablesDisabled = true;
            }

            // 装備品の素材（製作手帳で「未選択」になる欄）は、Artisan に頼む前にこちらで選ぶ（不具合の例：ハードレザーグリモアDX で、
            // Artisan 4.0.5.212 が欄のボタンを押す部品で「Unable to cast object of type 'ReceiveEvent' to type 'ReceiveEventDelegate'」となり、
            // 選ぶ小窓が開かず製作を始められなかった）
            if (this.SelectGearIngredients(ctx) is { } gear)
                return gear;

            if (!ctx.Artisan.CraftItem((ushort)this.craft.RecipeId, this.requestCrafts))
                return this.Fail($"Artisan に製作を頼めませんでした: {string.Join(" / ", ctx.Artisan.LastErrors.Values)}");

            this.requested = true;
            this.NextPhase("Artisan が製作中");
            return TaskResult.Running;
        }

        // 読めないのは「終わった」ではない（以前は読めないと製作が終わった扱いにして、途中の数で判断していた）。
        // 読めるようになるまで待ち、上限を過ぎたら製作を止めて理由を出す
        if (busy == null)
        {
            this.unknownSince ??= DateTime.UtcNow;
            this.Status = "Artisan の状態を読めません（待っています）";
            if (DateTime.UtcNow - this.unknownSince.Value > GoToInnTask.UnknownLimit)
            {
                ctx.Artisan.SetEndurance(false);
                return this.Fail($"Artisan の状態（IsBusy）を {GoToInnTask.UnknownLimit.TotalSeconds:0} 秒読めません。製作が続いているか分からないので止めます");
            }

            return TaskResult.Running;
        }

        this.unknownSince = null;
        if (busy == true)
        {
            this.sawBusy = true;
            this.idleSince = null;
            var busyInv = Inventory.Snapshot();
            var made = this.CountMade(busyInv) - this.beforeAll;
            this.Status = $"Artisan が製作中（{Math.Max(0, made)}/{this.expected}個）";

            // 連続製作の途中で失敗したら（作っている最中の1回を除いても、始めた回数ができた回数より多い状態が1秒続いたら）、
            // 連続製作を止めてもらう。今の1回が終わってから、下で止める
            if (!this.failureStopRequested && this.FailedSoFar(busyInv, inProgress: true) > 0)
            {
                this.failureSeenAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - this.failureSeenAt.Value >= TimeSpan.FromSeconds(1))
                {
                    this.failureStopRequested = true;
                    ctx.Artisan.SetEndurance(false);
                    ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} の製作に失敗しました。連続製作を止めてもらい、今の1回が終わったら止めます");
                }
            }
            else if (!this.failureStopRequested)
            {
                this.failureSeenAt = null;
            }

            var limit = TimeSpan.FromSeconds(60 + 90 * this.requestCrafts);
            if (this.PhaseElapsed > limit)
            {
                ctx.Artisan.SetEndurance(false);
                return this.Fail($"{limit.TotalMinutes:0}分たっても製作が終わりません");
            }

            return TaskResult.Running;
        }

        // 頼んだ直後は Endurance がまだ OFF（レシピ選択の後で ON になる）。IsBusy が立つか、品が増えるまで待つ。
        // 60 秒は「動き出さなかった」と判断する上限（15 秒では開始の遅い環境で別の理由の失敗になる）
        if (!this.sawBusy && this.CountMade(Inventory.Snapshot()) <= this.attemptBase && this.PhaseElapsed < TimeSpan.FromSeconds(60))
            return TaskResult.Running;

        var inv = Inventory.Snapshot();
        var nowCount = this.CountMade(inv);

        // 製作に失敗していたら、その場で止めて知らせる（失敗すると材料を失うので、続けない）。
        // 品が増える反映がわずかに遅れても取り違えないよう、止まってから1秒は見直す
        this.idleSince ??= DateTime.UtcNow;
        var failed = this.FailedSoFar(inv, inProgress: false);
        if (failed > 0)
        {
            if (DateTime.UtcNow - this.idleSince.Value < TimeSpan.FromSeconds(1))
                return TaskResult.Running;
            ctx.Artisan.SetEndurance(false);
            Svc.Chat.Print($"[AutoJobQuest] {FailureChat}（{CraftPlanner.ItemName(this.craft.ItemId)}）");
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} の製作に失敗しました（{failed} 回。材料を失っています）。"
                             + "装備品や食事、スキル回し（Artisan のソルバー・マクロ）等を見直してから、もう一度開始してください（続きから進みます）");
        }

        // HQ 指定の試し作り（1回）の結果：HQ になっていれば残りを作る（下の頼み直しで続ける）。NQ だったら残りは作らない
        var stopAfterTrial = false;
        if (this.trial)
        {
            this.trial = false;
            var hqGain = inv.CountHq(this.craft.ItemId) - this.beforeHq;
            if (nowCount - this.attemptBase > 0 && hqGain <= 0)
            {
                stopAfterTrial = true;
                ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}（HQ 指定）の試し作りが NQ でした。材料を無駄にしないよう、残りの {this.craft.Crafts - 1} 回は作らずに HQ の失敗として数えます");
            }
        }

        // 連続製作が予定の途中で止まった（Artisan の「NQ ができたら止める」「失敗したら止める」等。Crafting List ではなく
        // 連続製作で頼んでいるので、この設定が効く）→ この頼みで進んでいれば、残りを同じ作業の中で頼み直す（CraftResume）
        // HQ が増えていなければ止めて材料を残す：この頼みで1個以上できたのに HQ が1つも増えず、予定の途中で止まったら、
        // 残りは頼まずに材料を残し、試し作りの NQ と同じく「HQ の失敗」として数える（以前は実行全体を止めていたので、
        // 試し作りの NQ〔失敗を数えて次の周回で作り直す〕と食い違い、1個もできない中断〔製作の失敗・Artisan が始めない〕まで
        // 「材料を残して停止」という誤った理由で止まり、製作の構えも解かれなかった）。1個もできないときは、下の頼み直しと診断に任せる
        if (!stopAfterTrial && this.craft.WantHq && this.HqNeeded > 0 && !this.collectable
            && nowCount - this.beforeAll < this.expected && nowCount - this.attemptBase > 0
            && inv.CountHq(this.craft.ItemId) <= this.attemptHq
            && inv.CountHq(this.craft.ItemId) - this.beforeHq < this.HqNeeded)
        {
            stopAfterTrial = true;
            ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}（HQ 指定）は、この頼みで HQ が1つも増えないまま連続製作が止まりました。"
                                 + "材料を残すため残りは作らず、HQ の失敗として数えます（装備・Artisan の設定を見直す目安）");
        }

        var resume = stopAfterTrial ? 0 : CraftResume.Decide(nowCount - this.beforeAll, this.expected, nowCount - this.attemptBase, this.resumed,
            this.craft.Crafts, CraftCut.Craftable(CraftPlanner.Ingredients(this.recipe), inv), this.craft.Yield);
        if (resume > 0)
        {
            this.resumed++;
            this.attemptBase = nowCount;
            this.attemptHq = inv.CountHq(this.craft.ItemId);
            this.requestCrafts = resume;
            this.requested = false;
            this.sawBusy = false;

            // 装備品の素材は1回ごとに使うので、頼み直す前に欄を見直す（選ばれていれば、そのまま頼む）
            this.gearDone = false;
            this.gearTries = 0;
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} の連続製作が {nowCount - this.beforeAll}/{this.expected} 個で止まったので、"
                                 + $"残り {resume} 回を頼み直します（Artisan の「NQ ができたら止める」等の設定で止まることがある）");
            this.NextPhase("残りの製作を Artisan に頼み直します");
            return TaskResult.Running;
        }

        this.Made = nowCount - this.beforeAll;
        var madeHq = inv.CountHq(this.craft.ItemId) - this.beforeHq;
        this.MadeHq = madeHq;

        // 減った AND 増えた（完成品の数だけでは、手持ちの移動などを製作と取り違える）
        var used = this.ingredientsBefore.Where(kv => inv.CountAll(kv.Key) < kv.Value)
            .Select(kv => $"{CraftPlanner.ItemName(kv.Key)} {kv.Value}→{inv.CountAll(kv.Key)}").ToList();

        if (this.Made <= 0)
        {
            return this.Fail(used.Count > 0
                ? $"{CraftPlanner.ItemName(this.craft.ItemId)} が1つも増えませんでしたが、材料は減っています（製作の失敗の可能性：{string.Join("、", used)}）"
                : $"{CraftPlanner.ItemName(this.craft.ItemId)} が1つも増えませんでした（材料不足か、Artisan が止まった可能性）");
        }

        if (used.Count == 0 && this.ingredientsBefore.Count > 0)
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} は {this.Made} 個増えましたが、材料が減っていません（製作ではない増え方。想定外なので止めます）");

        if (this.Made < this.expected)
            ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} は {this.Made}/{this.expected} 個でした");

        if (this.craft.WantHq)
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}: {this.Made}個（うち HQ {madeHq}個）");
        else
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)}: {this.Made}個");

        this.Finished = true;
        return TaskResult.Done;
    }

    /// <summary>この作業で失敗した回数（材料の減りと品の増えから数える：<see cref="CraftFailure"/>）。</summary>
    private int FailedSoFar(Inventory inv, bool inProgress)
    {
        var attempts = CraftFailure.Attempts(CraftPlanner.Ingredients(this.recipe).Select(x => (x.Item, x.Amount)),
            item => this.ingredientsBefore.GetValueOrDefault(item) - inv.CountAll(item));
        return CraftFailure.Failed(attempts, this.CountMade(inv) - this.beforeAll, this.craft.Yield, inProgress);
    }

    private int CountMade(Inventory inv)
        => this.collectable ? Inventory.CountCollectables(this.craft.ItemId, 0) : inv.CountAll(this.craft.ItemId);

    /// <summary>
    /// 装備品の素材を選ぶ。選び終えた（または選ぶ欄が無い）なら null、途中なら Running、選べなければ Failed。
    /// 手順は Artisan の SetIngredients と同じ：製作手帳にこのレシピを出し、未選択の欄の「選ぶ」ボタンを押し、開いた候補の小窓（ContextIconMenu）に
    /// （0, 0, 0, 品の番号, 型の無い値）を送る。ボタンはこちらの押し方（ボタン自身のイベントを送る：確認窓の「はい」と同じ）で押す。
    /// 欄が埋まったか（未選択の印が消えたか）で確かめる。6回試して選べなければ、手で選んでもらう。
    /// </summary>
    private unsafe TaskResult? SelectGearIngredients(TaskContext ctx)
    {
        if (this.gearDone)
            return null;

        // 素材の欄の並び（製作手帳の欄 i は、レシピの素材の i 番目：Artisan の SetIngredients と同じ元の並び。クリスタルは 7・8 番目で欄に無い）と、装備品の素材
        this.gearSlots ??= this.recipe.Ingredient.Take(6)
            .Select((x, n) => (Slot: n, Item: x.RowId))
            .Where(x => x.Item != 0 && !Inventory.IsCrystalItem(x.Item)
                        && Svc.Data.GetExcelSheet<Item>().TryGetRow(x.Item, out var it) && it.EquipSlotCategory.RowId != 0)
            .ToList();
        if (this.gearSlots.Count == 0)
        {
            this.gearDone = true;
            return null;
        }

        // 職を先に合わせる（職が違うと Artisan が頼まれてから着替え、そのとき製作の構えを解いて製作手帳が閉じるので、選んだ素材が外れる）
        if (this.AlignJob(ctx, out var jobFailed) is { } aligning)
            return aligning;
        if (jobFailed != null)
            return this.Fail($"装備品の素材を選ぶ前に、職を合わせられませんでした（{jobFailed}）");

        var now = DateTime.UtcNow;
        if (this.gearTries > 6)
            return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} の素材の装備品（{string.Join("、", this.gearSlots.Select(g => CraftPlanner.ItemName(g.Item)))}）を自動で選べませんでした。"
                             + "製作手帳でこのレシピを開き、「未選択」の欄でその品を選んでから「ジョブクエ開始」を押してください（続きから進みます）");

        // 製作手帳にこのレシピを出す
        if (!GameUi.IsReady("RecipeNote", out var note) || QuickSelectedRecipeId() != this.craft.RecipeId)
        {
            if (now - this.gearNoteAt >= TimeSpan.FromSeconds(2))
            {
                this.gearNoteAt = now;
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance()->OpenRecipeByRecipeId(this.craft.RecipeId);
            }

            this.Status = "装備品の素材を選ぶため、製作手帳を開いています";
            return TaskResult.Running;
        }

        var open = this.gearSlots.FirstOrDefault(g => GameUi.RecipeSlotUnselected(note, g.Slot));
        if (open.Item == 0)
        {
            this.gearDone = true;
            ctx.Log.Write("製作", $"装備品の素材を選びました：{string.Join("、", this.gearSlots.Select(g => CraftPlanner.ItemName(g.Item)))}（Artisan に製作を頼みます）");
            return null;
        }

        if (now - this.gearActAt < TimeSpan.FromSeconds(1))
            return TaskResult.Running;
        this.gearActAt = now;

        // 候補の小窓が開いていれば品を選ぶ。開いていなければ、欄の「選ぶ」ボタンを押す
        if (GameUi.IsReady("ContextIconMenu", out var menu))
        {
            GameUi.Fire(menu, true, 0, 0, 0, open.Item, null!);
            ctx.Log.Debug("製作", $"素材の欄 {open.Slot + 1} の候補から {CraftPlanner.ItemName(open.Item)} を選びました");
        }
        else
        {
            this.gearTries++;
            GameUi.ClickRecipeSlotSelect(note, open.Slot);
        }

        this.Status = $"装備品の素材（{CraftPlanner.ItemName(open.Item)}）を選んでいます";
        return TaskResult.Running;
    }

    /// <summary>製作手帳で選ばれているレシピ（読めなければ 0）。</summary>
    private static unsafe uint QuickSelectedRecipeId()
    {
        var rn = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (rn == null || rn->RecipeList == null)
            return 0;
        var data = rn->RecipeList;
        return data->Recipes != null && data->SelectedIndex < data->RecipeCount ? data->Recipes[data->SelectedIndex].RecipeId : 0u;
    }

    /// <summary>
    /// 簡易製作で作る（このプラグインが自分で行う。Artisan には品目ごとに簡易製作を頼む手段が無い：IPC に無く、全体の設定だけ）。
    /// 手順は Artisan の Operations.QuickSynthItem と同じ：レシピノートにこのレシピを出し、選ばれているのがこのレシピだと確かめてから
    /// 「簡易製作」（コールバック 9）を押し、回数の窓（SynthesisSimpleDialog）に回数を入れて始める。進み具合の窓（SynthesisSimple）は、
    /// 作り終えたら閉じる（コールバック -1）。30秒数が増えなければ、または始められなければ、残りを通常の製作（Artisan）に切り替える。
    /// </summary>
    private unsafe TaskResult TickQuick(TaskContext ctx)
    {
        var now = DateTime.UtcNow;
        var inv = Inventory.Snapshot();
        var made = this.CountMade(inv) - this.beforeAll;

        if (made >= this.expected)
        {
            if (GameUi.IsReady("SynthesisSimple", out var done))
                GameUi.Fire(done, true, -1);
            this.Made = made;
            this.MadeHq = Math.Max(0, inv.CountHq(this.craft.ItemId) - this.beforeHq);
            this.Finished = true;
            ctx.Log.Write("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} を簡易製作で作りました（{made}/{this.expected} 個）");
            return TaskResult.Done;
        }

        // 簡易製作でも失敗すると材料を失う。通常の製作と同じく、失敗に見えるのが1秒続いたら、残りを中止して（進み具合の窓に -1：
        // Artisan の CloseQuickSynthWindow と同じ）止まり、チャットに知らせる
        if (this.FailedSoFar(inv, inProgress: GameUi.IsVisible("SynthesisSimple")) > 0)
        {
            this.failureSeenAt ??= now;
            if (now - this.failureSeenAt.Value >= TimeSpan.FromSeconds(1))
            {
                if (GameUi.IsReady("SynthesisSimple", out var failWin))
                    GameUi.Fire(failWin, true, -1);
                Svc.Chat.Print($"[AutoJobQuest] {FailureChat}（{CraftPlanner.ItemName(this.craft.ItemId)}）");
                return this.Fail($"{CraftPlanner.ItemName(this.craft.ItemId)} の簡易製作に失敗しました（材料を失っています）。"
                                 + "装備品や食事等を見直してから、もう一度開始してください（続きから進みます）");
            }
        }
        else
        {
            this.failureSeenAt = null;
        }

        if (this.AlignJob(ctx, out var jobFailed) is { } aligning)
            return aligning;
        if (jobFailed != null)
            return this.QuickFallback(ctx, $"職を合わせられませんでした（{jobFailed}）");

        // 進み具合の窓が出ている間は待つ。30秒数が増えなければ、通常の製作に切り替える
        if (GameUi.IsVisible("SynthesisSimple"))
        {
            if (made != this.quickProgressMade)
            {
                this.quickProgressMade = made;
                this.quickProgressAt = now;
            }

            if (now - this.quickProgressAt > TimeSpan.FromSeconds(30))
            {
                if (GameUi.IsReady("SynthesisSimple", out var stuck))
                    GameUi.Fire(stuck, true, -1);
                return this.QuickFallback(ctx, "30秒たっても数が増えません");
            }

            this.Status = $"簡易製作中（{Math.Max(0, made)}/{this.expected} 個）";
            return TaskResult.Running;
        }

        // 回数の窓が出ていれば、残りの回数（99回まで）を入れて始める
        if (GameUi.IsReady("SynthesisSimpleDialog", out var dialog))
        {
            if (now - this.quickActAt >= TimeSpan.FromSeconds(1))
            {
                this.quickActAt = now;
                var remaining = Math.Min(99, (this.expected - Math.Max(0, made) + this.craft.Yield - 1) / this.craft.Yield);
                GameUi.Fire(dialog, true, remaining, true, true);
                this.quickProgressMade = made;
                this.quickProgressAt = now;
                ctx.Log.Debug("製作", $"簡易製作の回数 {remaining} 回を入れて始めました");
            }

            this.Status = "簡易製作を始めています";
            return TaskResult.Running;
        }

        if (this.quickTries > 8)
            return this.QuickFallback(ctx, "レシピノートから簡易製作を始められませんでした");

        // レシピノートにこのレシピが選ばれていれば「簡易製作」を押す。でなければ、このレシピを開く
        if (GameUi.IsReady("RecipeNote", out var note) && QuickSelectedRecipeId() == this.craft.RecipeId && now - this.noteOpenedAt > TimeSpan.FromSeconds(0.5))
        {
            if (now - this.quickActAt >= TimeSpan.FromSeconds(1.5))
            {
                this.quickActAt = now;
                this.quickTries++;
                GameUi.Fire(note, true, 9);
            }
        }
        else if (now - this.quickActAt >= TimeSpan.FromSeconds(2))
        {
            this.quickActAt = now;
            this.noteOpenedAt = now;
            this.quickTries++;
            FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance()->OpenRecipeByRecipeId(this.craft.RecipeId);
        }

        this.Status = "レシピノートで簡易製作を始めています";
        return TaskResult.Running;
    }

    /// <summary>
    /// 職をこのレシピの職に合わせる（製作の構えのままでは着替えられないので、先に解く）。合っていれば null、途中なら Running。
    /// 合わせられなければ <paramref name="failed"/> に理由を入れて null を返す。
    /// </summary>
    private TaskResult? AlignJob(TaskContext ctx, out string? failed)
    {
        failed = null;
        if (this.jobSub != null)
        {
            var r = this.jobSub.Step(ctx);
            this.Status = this.jobSub.Status;
            if (r == TaskResult.Running)
                return TaskResult.Running;
            this.jobSub.Cleanup(ctx);
            failed = r == TaskResult.Failed ? this.jobSub.FailReason ?? "理由は不明" : null;
            this.jobSub = null;
            return failed != null ? null : TaskResult.Running;
        }

        if (Jobs.CurrentClassJob != this.craft.ClassJobId)
        {
            var c = Svc.Condition;
            this.jobSub = c[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft] || c[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting]
                ? new ExitCraftStanceTask()
                : new EquipJobTask(this.craft.ClassJobId);
            return TaskResult.Running;
        }

        return null;
    }

    /// <summary>簡易製作をやめ、残りを通常の製作（Artisan）で作る。</summary>
    private TaskResult QuickFallback(TaskContext ctx, string why)
    {
        this.quick = false;
        var made = Math.Max(0, this.CountMade(Inventory.Snapshot()) - this.beforeAll);
        var left = (this.expected - made + this.craft.Yield - 1) / this.craft.Yield;
        this.requestCrafts = Math.Max(1, left);
        this.attemptBase = this.beforeAll + made;
        ctx.Log.Warn("製作", $"{CraftPlanner.ItemName(this.craft.ItemId)} の簡易製作をやめ、残りの {this.requestCrafts} 回を通常の製作（Artisan）で作ります（{why}）");
        return TaskResult.Running;
    }

    public override void Cleanup(TaskContext ctx)
    {
        // 職を合わせる下請け（簡易製作・装備品の素材の選択）が途中なら、後始末する
        this.jobSub?.Cleanup(ctx);
        this.jobSub = null;

        // 一時的に「使わない」にした食事・薬を戻す（戻せなければ控えに残し、止まっている間に戻す：Services）。
        // Artisan がまだ製作中なら、止め切るまで戻さない（戻した直後の製作で食べないように、控えに残して後で戻す）
        if (this.consumablesDisabled && ctx.Artisan.IsBusy() == false && ctx.Artisan.RestoreConsumables(this.craft.RecipeId))
        {
            ctx.Config.ArtisanTempConsumableRecipes.Remove(this.craft.RecipeId);
            ctx.Config.Save();
        }

        // こちらが頼んだ製作がまだ動いていれば止める（Endurance を OFF）
        if (!this.requested || ctx.Artisan.IsBusy() == false)
            return;

        if (ctx.Artisan.IsEndurance() == true)
            ctx.Artisan.SetEndurance(false);

        // Artisan の CraftItem は「レシピ選択 → Endurance を ON」を内部の順番待ちに積むので、
        // 止めた直後に遅れて Endurance が ON になり、製作が進むことがある。
        // Artisan が空く（IsBusy が false）まで見張り、その間に ON になったら OFF にする。
        ctx.AfterStop.Add(ArtisanStopWatch(ctx.Artisan, TimeSpan.FromSeconds(30)));
    }

    /// <summary>見張りの名前（読み込みの解除をまたいで引き継ぐときの目印）。</summary>
    public const string ArtisanWatchName = "Artisan の製作を止め切る";

    /// <summary>
    /// 製作の構え（製作手帳を開いた状態）のままか。Artisan の Crafting.State の IdleBetween と同じ見方
    /// （ConditionFlag.Crafting と PreparingToCraft が両方立っている）。
    /// 以前は「ExecutingCraftingAction が立っていない」も条件にしていたが、Artisan のソースに「簡易製作の後は ExecutingCraftingAction が
    /// 立ったまま残る」とあり（Artisan の Crafting.cs）、簡易製作の後に構えを解けず上限で止まりえた。
    /// </summary>
    public static bool InCraftStanceIdle()
    {
        var c = Svc.Condition;
        return c[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft]
               && c[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting];
    }

    /// <summary>
    /// 止めた後の見張り：Artisan が空くまで、遅れて Endurance が ON になったら OFF にする。
    /// 読み込みの解除（更新・無効化）の後は見張れないので、次に読み込んだとき同じ見張りを置き直す（Services）。
    /// </summary>
    public static (string Name, DateTime Until, Func<bool> Step) ArtisanStopWatch(ArtisanIpc artisan, TimeSpan duration)
        => (ArtisanWatchName, DateTime.UtcNow + duration, () =>
        {
            if (artisan.IsEndurance() == true)
                artisan.SetEndurance(false);
            return artisan.IsBusy() == false;
        });
}

/// <summary>
/// 製作の列の最後で、製作の構えを解く（以前は Artisan の「連続製作が終わったら構えを解く」
/// ＝ExitCraftStanceEndurance が OFF だと、構えのまま次の段〔マテリア装着・クエストの移動〕へ進んで待たされていた。
/// 事前点検で注意を出すだけだった）。
///
/// やり方は Artisan 自身の PreCrafting.TaskExitCraft と同じ：製作中でない（IdleBetween）ときに、製作手帳（RecipeNote）へ
/// 閉じる合図（Callback -1）を送る。製作手帳はこちらが CraftItem で頼んだ製作で開いたもの。Artisan が動いている間は触らない。
/// 進む判断は状態（構えが解けたか）。閉じる合図は、前の合図で状態が変わらないまま次のフレーム以降も窓が見えているときだけ
/// 送り直す（3回まで）。
/// </summary>
public sealed unsafe class ExitCraftStanceTask : AutoTask
{
    private int closes;
    private DateTime? sentAt;

    public override string Name => "製作の構えを解く";

    protected override TaskResult Tick(TaskContext ctx)
    {
        var c = Svc.Condition;
        var inStance = c[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft]
                       || c[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting];
        if (!inStance)
            return TaskResult.Done;

        if (this.WorkElapsed > TimeSpan.FromMinutes(1))
            return this.Fail("製作の構えを1分たっても解けません（製作手帳を閉じられない）");

        // Artisan が動いている・製作の操作中は待つ（Artisan の TaskExitCraft と同じく、製作中は触らない）
        if (ctx.Artisan.IsBusy() != false || !CraftOneTask.InCraftStanceIdle())
        {
            this.Status = "製作が終わるのを待っています";
            return TaskResult.Running;
        }

        var note = GameUi.Addon("RecipeNote");
        if (note == null)
        {
            this.Status = "製作手帳が閉じるのを待っています";
            return TaskResult.Running;
        }

        // 送った合図が効くまで（窓が消える・構えが解ける）待つ。同じフレームには効かないので、送った後は少なくとも次の
        // フレームまで待ち、2秒たっても窓が残っていれば送り直す（送り直しの間隔の2秒は、進む判断ではなく合図の再送の間隔）
        if (this.sentAt is { } at && DateTime.UtcNow - at < TimeSpan.FromSeconds(2))
            return TaskResult.Running;

        if (this.closes++ >= 3)
            return this.Fail("製作手帳に閉じる合図を3回送っても、製作の構えが解けません");

        GameUi.Fire(note, true, -1);
        this.sentAt = DateTime.UtcNow;
        ctx.Log.Write("製作", "製作手帳を閉じて、製作の構えを解きます");
        this.Status = "製作の構えを解いています";
        return TaskResult.Running;
    }
}
