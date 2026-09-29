using System;
using System.Linq;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using AutoJobQuest.Planning;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 納品物にマテリアを1個付ける（自動装着）。
///
/// 手順は Automaton の GettingTooAttached.cs から「外す」部分を除いて切り出したもの:
///   開く（GeneralAction 12）→ 分類「所持品」→ アイテムを選ぶ → マテリアを選ぶ → 確認ダイアログの「装着する」
///   → 装着中の旗が下りる → 「対象のマテリア数 +1 AND カバンのマテリア −1」で確認 → 自分で開いたときだけ閉じる。
///
/// 補足:
///  ・着替えはしない（パッチ 3.2 から別のクラスのままで装着できる。公式パッチノート）。
///  ・「種類不問」のときは設定の品（既定は剛柔のマテリア）を付ける。
///  ・成功率が 100% 未満なら押さずに「戻る」を押して中止する（通常の装着の1穴目は 100%）。
///  ・YesAlready など他のプラグインが先に確認を押しても、結果の確認で成功とみなす。
///  ・AgentMateriaAttach の Materia プロパティは誤りがあるので MateriaSorted を使う。一覧は ItemCount / MateriaCount まで回す。
/// </summary>
public sealed unsafe class MeldTask : AutoTask
{
    private enum MeldStep
    {
        Prepare, Open, WaitOpen, SelectCategory, WaitCategory, SelectItem, WaitItemLoaded,
        SelectMateria, WaitDialog, CheckDialog, WaitMeldEnd, Verify, Close,
    }

    private const uint GeneralActionMateriaMelding = 12; // GeneralAction 12 = マテリア装着（ゲームデータで確認）
    private const string DialogAddonName = "MateriaAttachDialog";
    private const uint MeldButtonNodeId = 35;   // ECommons / YesAlready / SimpleTweaks の3つで一致
    private const uint ReturnButtonNodeId = 36;  // ECommons

    // 一覧の配列の長さ（FFXIVClientStructs の MateriaAttachData：ItemsSorted・MateriaSorted とも 140）。
    // 画面の件数がこれを超えて読まないようにする
    private const int SortedCapacity = 140;

    // 確認画面の成功率の位置（FFXIVClientStructs の MateriaAttachDialogAtkValues：41 番目）
    private const int SuccessRateIndex = 41;

    private readonly MateriaNeed need;
    private MeldStep step = MeldStep.Prepare;

    private InventoryType targetType;
    private int targetSlot;
    private uint materiaItemId;
    private uint targetLevelItem;
    private bool openedByUs;

    // 装着の画面を開けない理由（最後に見たもの）と、降りる操作を送った時刻（送りすぎない抑え。進む判断は騎乗の状態で行う）
    private string openBlocked = "まだ試していない";
    private DateTime lastDismount = DateTime.MinValue;

    // 装着の一般アクションが「使えない」と答え始めた時刻と、開く操作を最後に試した時刻
    private DateTime statusBlockedSince = DateTime.MinValue;
    private DateTime lastOpenTry = DateTime.MinValue;
    private byte materiaCountBefore;
    private long materiaStockBefore;
    private uint targetItemIdBefore;
    private DateTime lastHide = DateTime.MinValue;

    public MeldTask(MateriaNeed need)
    {
        this.need = need;
    }

    public override string Name => $"マテリア装着: {CraftPlanner.ItemName(this.need.TargetItemId)}";

    private static AgentMateriaAttach* Agent => AgentMateriaAttach.Instance();

    protected override TaskResult OnStart(TaskContext ctx)
    {
        // 装着の確認を YesAlready が先に押すと、成功率の確かめ（100% 未満なら押さない）を通らない。こちらの作業の間は止めてもらう
        ctx.YesAlready.Suppress();
        return TaskResult.Running;
    }

    private InventoryItem* LiveTarget => InventoryManager.Instance()->GetInventorySlot(this.targetType, this.targetSlot);

    protected override TaskResult Tick(TaskContext ctx)
    {
        switch (this.step)
        {
            case MeldStep.Prepare:
                return this.Prepare(ctx);

            case MeldStep.Open:
            {
                var target = this.LiveTarget;
                if (target == null || target->ItemId == 0)
                    return this.Fail("対象のアイテムが見つかりません");

                this.targetItemIdBefore = target->ItemId;
                this.materiaCountBefore = target->GetMateriaCount();
                this.materiaStockBefore = CountInBags(this.materiaItemId);
                if (this.materiaStockBefore <= 0)
                    return this.Fail($"カバンに {CraftPlanner.ItemName(this.materiaItemId)} がありません");

                // 装着の画面が、こちらが開く前から開いている＝利用者か他のプラグインが使っている。横取りしない
                // （以前はそのまま使っていたが、「利用者や他プラグインが開いた画面は押さない」に反する）
                if (Agent->IsAgentActive())
                    return this.Fail("マテリア装着の画面が開いています（こちらが開いたものではないので使いません）。閉じてからやり直してください");

                if (this.TimedOut(TimeSpan.FromSeconds(60)))
                    return this.Fail($"マテリア装着の画面を60秒たっても開けませんでした（最後の理由：{this.openBlocked}）");

                // 騎乗中なら降りる（騎乗中は装着の画面を開けない見込み。PlayerFree は騎乗を見ない）
                if (GameUi.Mounted)
                {
                    this.openBlocked = "騎乗中";
                    this.Status = "装着の前に降ります";
                    if (DateTime.UtcNow - this.lastDismount >= TimeSpan.FromSeconds(2))
                    {
                        this.lastDismount = DateTime.UtcNow;
                        GameUi.UseGeneralAction(23); // 降りる（GeneralAction 23）
                    }

                    return TaskResult.Running;
                }

                if (!GameUi.PlayerFree())
                {
                    this.openBlocked = "動ける状態でない";
                    this.Status = "動ける状態になるのを待っています";
                    return TaskResult.Running;
                }

                // ゲームが装着の操作を「いま使える」と答えるまで待つ（GetActionStatus が 0。断られるのを先に避ける）
                // 画面を開く種類の一般アクションで GetActionStatus を使った例が参照したソースに無く、使えるときに 0 を返すかは実機未確認。
                // 動ける・降りている状態で3秒たっても 0 にならなければ、1秒おきに開く操作を試す（以前の作りと同じ）
                var status = GameUi.GeneralActionStatus(GeneralActionMateriaMelding);
                if (status != 0)
                {
                    if (this.statusBlockedSince == DateTime.MinValue)
                        this.statusBlockedSince = DateTime.UtcNow;
                    this.openBlocked = $"ゲームが今は使えないと答えた（{status}）";
                    if (DateTime.UtcNow - this.statusBlockedSince < TimeSpan.FromSeconds(3))
                    {
                        this.Status = "マテリア装着が使えるようになるのを待っています";
                        return TaskResult.Running;
                    }
                }
                else
                {
                    this.statusBlockedSince = DateTime.MinValue;
                }

                // 開く操作は1秒おきまで（断られ続けても毎フレーム撃たない）
                if (DateTime.UtcNow - this.lastOpenTry < TimeSpan.FromSeconds(1))
                    return TaskResult.Running;
                this.lastOpenTry = DateTime.UtcNow;

                // 開く操作が一時的に断られても、その場で失敗にしない（以前は1回の拒否で止めていた）。上の60秒まで状態を見て開き直す
                if (!GameUi.UseGeneralAction(GeneralActionMateriaMelding))
                {
                    this.openBlocked = "開く操作が断られた";
                    this.Status = "マテリア装着の画面を開き直します";
                    return TaskResult.Running;
                }

                this.openedByUs = true;
                this.Next(MeldStep.WaitOpen);
                return TaskResult.Running;
            }

            case MeldStep.WaitOpen:
                if (Agent->IsAgentActive())
                    this.Next(MeldStep.SelectCategory);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("マテリア装着の画面が開きません");
                return TaskResult.Running;

            case MeldStep.SelectCategory:
                if (Agent->UpdateState != 0)
                {
                    if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("装着画面の読み込みが終わりません");
                    return TaskResult.Running;
                }

                if (Agent->Category != AgentMateriaAttach.FilterCategory.Inventory)
                    SendAgentEvent(0, 0, (int)AgentMateriaAttach.FilterCategory.Inventory);
                this.Next(MeldStep.WaitCategory);
                return TaskResult.Running;

            case MeldStep.WaitCategory:
                if (Agent->UpdateState == 0 && Agent->Category == AgentMateriaAttach.FilterCategory.Inventory)
                    this.Next(MeldStep.SelectItem);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("分類が「所持品」に切り替わりません");
                return TaskResult.Running;

            case MeldStep.SelectItem:
            {
                var live = this.LiveTarget;
                var data = Agent->Data;
                // 画面の中身がまだ無い（読み込み中）。null のまま読むとゲームごと落ちるので待つ
                if (data == null)
                    return this.TimedOut(TimeSpan.FromSeconds(10)) ? this.Fail("装着画面の中身を読めません") : TaskResult.Running;
                for (var i = 0; i < Math.Min((int)Agent->ItemCount, SortedCapacity); i++)
                {
                    var entry = data->ItemsSorted[i].Value;
                    if (entry == null)
                        continue;
                    if (entry->Item == live)
                    {
                        SendAgentEvent(0, 1, i, 1, 0);
                        this.Next(MeldStep.WaitItemLoaded);
                        return TaskResult.Running;
                    }
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("対象のアイテムが装着画面の一覧に出ません（装着に要るクラフターのレベルが足りない・穴が埋まっている等）");
                return TaskResult.Running;
            }

            case MeldStep.WaitItemLoaded:
                if (Agent->UpdateState == 0)
                    this.Next(MeldStep.SelectMateria);
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("アイテムを選んだあとの読み込みが終わりません");
                return TaskResult.Running;

            case MeldStep.SelectMateria:
            {
                var data = Agent->Data;
                if (data == null)
                    return this.TimedOut(TimeSpan.FromSeconds(10)) ? this.Fail("装着画面の中身を読めません") : TaskResult.Running;
                for (var i = 0; i < Math.Min((int)Agent->MateriaCount, SortedCapacity); i++)
                {
                    var entry = data->MateriaSorted[i].Value;
                    if (entry == null || entry->Item == null)
                        continue;
                    if (entry->Item->ItemId != this.materiaItemId)
                        continue;
                    if (entry->ItemLevel > this.targetLevelItem)
                        return this.Fail($"このマテリアはアイテムレベルが足りず付けられません（{entry->ItemLevel} > {this.targetLevelItem}）");

                    SendAgentEvent(0, 2, i, 1, 0);
                    this.Next(MeldStep.WaitDialog);
                    return TaskResult.Running;
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("指定のマテリアが右の一覧に出ません（等級・アイテムレベルの制限の可能性）");
                return TaskResult.Running;
            }

            case MeldStep.WaitDialog:
                if (Svc.Condition[ConditionFlag.MeldingMateria] && IsDialogReady(out _))
                    this.Next(MeldStep.CheckDialog);
                else if (this.MateriaCountIncreased())
                    this.Next(MeldStep.Verify); // 他のプラグインが先に押した
                else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail("装着の確認画面が出ません");
                return TaskResult.Running;

            case MeldStep.CheckDialog:
            {
                if (!IsDialogReady(out var dialog))
                {
                    if (!Svc.Condition[ConditionFlag.MeldingMateria])
                        this.Next(MeldStep.Verify);
                    else if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("確認画面が消えました");
                    return TaskResult.Running;
                }

                // 成功率を読めなければ押さない（値の数が足りない・型が違う＝配置が変わった可能性）
                // （型は実機で確かめていないので、以前と同じく整数なら Int・UInt のどちらでも読む）
                var atk = (AtkUnitBase*)dialog;
                var rateValue = atk->AtkValues != null && atk->AtkValuesCount > SuccessRateIndex ? dialog->TypedAtkValues->SuccessRate : default;
                if (rateValue.Type is not (AtkValueType.Int or AtkValueType.UInt))
                {
                    GameUi.ClickButton(atk, ReturnButtonNodeId);
                    return this.Fail($"確認画面の成功率を読めないため中止しました（値の数 {atk->AtkValuesCount}・型 {rateValue.Type}）");
                }

                var rate = rateValue.Type == AtkValueType.UInt ? (int)Math.Min(rateValue.UInt, int.MaxValue) : rateValue.Int;
                if (rate < 100)
                {
                    GameUi.ClickButton((AtkUnitBase*)dialog, ReturnButtonNodeId);
                    return this.Fail($"成功率が {rate}% のため中止しました（100% でないとマテリアを失う恐れがあります）");
                }

                var meld = dialog->GetComponentButtonById(MeldButtonNodeId);
                if (meld == null || !meld->IsEnabled)
                {
                    if (this.TimedOut(TimeSpan.FromSeconds(10)))
                        return this.Fail("「装着する」が押せる状態になりません");
                    return TaskResult.Running;
                }

                PressMeld(dialog);
                this.Next(MeldStep.WaitMeldEnd);
                return TaskResult.Running;
            }

            case MeldStep.WaitMeldEnd:
                if (!Svc.Condition[ConditionFlag.MeldingMateria])
                    this.Next(MeldStep.Verify);
                else if (this.TimedOut(TimeSpan.FromSeconds(15)))
                    return this.Fail("装着が終わりません");
                return TaskResult.Running;

            case MeldStep.Verify:
            {
                var increased = this.MateriaCountIncreased();
                var decreased = CountInBags(this.materiaItemId) == this.materiaStockBefore - 1;
                if (increased && decreased)
                {
                    ctx.Log.Write("装着", $"{CraftPlanner.ItemName(this.need.TargetItemId)} に {CraftPlanner.ItemName(this.materiaItemId)} を付けました");
                    this.Next(MeldStep.Close);
                    return TaskResult.Running;
                }

                if (this.TimedOut(TimeSpan.FromSeconds(10)))
                    return this.Fail($"装着できたか確かめられません（付いた={increased} / 減った={decreased}）");
                return TaskResult.Running;
            }

            case MeldStep.Close:
                // 閉じたことを確かめてから終える（続けて次の品に付けるとき、閉じきる前の画面を
                // 「こちらが開いたものではない画面」と取り違えて止まらないように）
                if (this.openedByUs && Agent->IsAgentActive())
                {
                    if (DateTime.UtcNow - this.lastHide >= TimeSpan.FromSeconds(1))
                    {
                        this.lastHide = DateTime.UtcNow;
                        Agent->Hide();
                    }

                    return this.TimedOut(TimeSpan.FromSeconds(10))
                        ? this.Fail("マテリア装着の画面が閉じません")
                        : TaskResult.Running;
                }

                this.openedByUs = false;
                return TaskResult.Done;
        }

        return TaskResult.Running;
    }

    private TaskResult Prepare(TaskContext ctx)
    {
        if (Inventory.HasMelded(this.need.TargetItemId, this.need.TargetHq, this.need.MateriaItemId))
            return TaskResult.Done;

        // 解放は実行の最初の「機能の解放」の段で行う。ここで未解放なら、そこで解放できなかったということ
        if (!Unlocks.IsUnlocked(Unlocks.Meld))
            return this.Fail($"マテリア装着が未解放です（クエスト「{Unlocks.QuestName(Unlocks.UnlockQuest(Unlocks.Meld))}」で解放されます）");

        var items = Svc.Data.GetExcelSheet<Item>();
        if (!items.TryGetRow(this.need.TargetItemId, out var target))
            return this.Fail("アイテムのデータが読めません");
        this.targetLevelItem = target.LevelItem.RowId;

        // 使うマテリア：指定品か、種類不問なら設定の品（AnyMateriaItemId＝剛柔のマテリア）
        if (this.need.MateriaItemId is { } mid)
        {
            this.materiaItemId = mid;
        }
        else
        {
            var any = MateriaCatalog.ResolveAny(ctx.Config.AnyMateriaItemId, this.need.TargetItemId, out var problem);
            if (any == null)
                return this.Fail(problem ?? "任意のマテリアに使う品を決められません");
            this.materiaItemId = any.Value;
        }

        // 付ける対象のスロット（カバン内・HQ 指定なら HQ・まだ穴が空いているもの）
        if (!this.FindTargetSlot(target.MateriaSlotCount))
            return this.Fail($"{CraftPlanner.ItemName(this.need.TargetItemId)}{(this.need.TargetHq ? "（HQ）" : string.Empty)} がカバンにありません（アーマリーチェストにある場合はカバンに移してください）");

        // 着替えはしない：パッチ 3.2 から「装着できる能力があれば、別のクラスのままでも装着できる」
        // （公式パッチノート 3.2。レベルの条件は従来どおり）。対象品の修理職はどれもそのクエストの職と同じで、
        // クエストを受けられる時点でレベルの条件を満たす（解析ツール repairjob で確認）。
        this.Next(MeldStep.Open);
        return TaskResult.Running;
    }

    private bool FindTargetSlot(byte slotCount)
    {
        if (FindInBags(this.need.TargetItemId, this.need.TargetHq, slotCount, out var type, out var slot))
        {
            this.targetType = type;
            this.targetSlot = slot;
            return true;
        }

        return false;
    }

    /// <summary>付ける対象の品がカバンにあるか（HQ 指定なら HQ）。穴の数は問わない（事前点検用）。</summary>
    public static bool InBags(uint itemId, bool hq)
        => FindInBags(itemId, hq, null, out _, out _);

    /// <summary>
    /// カバンの中で対象の品を探す。<paramref name="slotCount"/> を渡したら（付ける対象を選ぶとき）、**マテリアがまだ1つも付いていない**
    /// 品だけ（穴が無い品には付けられない。禁断は扱わない）。以前は「穴がまだ空いている」だけを見ていたので、利用者のマテリア付きの品に
    /// 足して付けうった（こちらが作った品にはマテリアが付いていない）。
    /// </summary>
    private static bool FindInBags(uint itemId, bool hq, byte? slotCount, out InventoryType type, out int slot)
    {
        type = default;
        slot = -1;
        var im = InventoryManager.Instance();
        if (im == null)
            return false;
        foreach (var t in Inventory.Bags)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId != itemId)
                    continue;
                if (hq && (s->Flags & InventoryItem.ItemFlags.HighQuality) == 0)
                    continue;
                if (slotCount is { } n && (n == 0 || s->GetMateriaCount() != 0))
                    continue;

                type = t;
                slot = i;
                return true;
            }
        }

        return false;
    }

    private void Next(MeldStep s)
    {
        this.step = s;
        this.NextPhase(s.ToString());
    }

    public override void Cleanup(TaskContext ctx)
    {
        if (this.openedByUs && Agent != null && Agent->IsAgentActive())
            Agent->Hide();
        this.openedByUs = false;
        ctx.YesAlready.Release();
    }

    private bool MateriaCountIncreased()
    {
        var live = this.LiveTarget;
        return live != null && live->ItemId == this.targetItemIdBefore && live->GetMateriaCount() == this.materiaCountBefore + 1;
    }

    /// <summary>GettingTooAttached.cs:146-154 と同じ。値はすべて AtkValueType.Int。</summary>
    private static void SendAgentEvent(ulong eventKind, params int[] values)
    {
        Core.DebugLog.Current?.Line("操作", $"装着画面へイベント送信: 種類{eventKind}（{string.Join(", ", values)}） 分類={Agent->Category} 品数={Agent->ItemCount} マテリア数={Agent->MateriaCount}");
        var ret = new AtkValue();
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i].Type = AtkValueType.Int;
            atkValues[i].Int = values[i];
        }

        Agent->ReceiveEvent(&ret, atkValues, (uint)values.Length, eventKind);
    }

    /// <summary>clib AddonMateriaAttachDialog.cs と同じ（ButtonClick / eventParam 0）。</summary>
    private static void PressMeld(AddonMateriaAttachDialog* dialog)
    {
        var addon = (AtkUnitBase*)dialog;
        Core.DebugLog.Current?.Line("操作", "装着の確認画面で「装着する」を押します（ButtonClick / 0）");
        var evt = new AtkEvent { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt, &data);
    }

    private static bool IsDialogReady(out AddonMateriaAttachDialog* dialog)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(DialogAddonName);
        dialog = (AddonMateriaAttachDialog*)addon;
        return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded();
    }

    private static long CountInBags(uint itemId)
    {
        long total = 0;
        var im = InventoryManager.Instance();
        foreach (var type in Inventory.Bags)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null)
                continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    total += slot->Quantity;
            }
        }

        return total;
    }
}
