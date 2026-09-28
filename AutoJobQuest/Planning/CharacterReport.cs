using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Automation;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoJobQuest.Planning;

/// <summary>画面に出す1行。</summary>
public sealed record ReportLine(Severity Severity, string Text);

/// <summary>見出しと行のまとまり。</summary>
public sealed record ReportSection(string Title, List<ReportLine> Lines);

/// <summary>
/// キャラクターの状態をゲームから読み取り、プラグインが何を把握しているかを一覧にする
/// （収集品納品の解放などをキャラクター情報から読み取り、画面で確認できるように）。
///
/// すべて実行時にゲームから読む（設定ファイルには保存しない。キャラクターが変わっても正しく出るように）。
/// フレームワークのスレッドから呼ぶこと。
/// </summary>
public static class CharacterReport
{
    public static List<ReportSection> Build(TaskContext ctx)
    {
        var sections = new List<ReportSection>();
        void Add(string title, Action<List<ReportLine>> body)
        {
            var lines = new List<ReportLine>();
            try
            {
                body(lines);
            }
            catch (Exception ex)
            {
                lines.Add(new ReportLine(Severity.Error, $"読めませんでした: {ex.GetType().Name}: {ex.Message}"));
            }

            sections.Add(new ReportSection(title, lines));
        }

        var data = ctx.Data;

        Add("キャラクター", lines =>
        {
            var me = Svc.Objects.LocalPlayer;
            if (me == null)
            {
                lines.Add(new ReportLine(Severity.Error, "ログインしていません"));
                return;
            }

            lines.Add(new ReportLine(Severity.Ok, $"{me.Name.TextValue} @ {me.HomeWorld.ValueNullable?.Name.ExtractText()}（いまのジョブ：{Jobs.Name(Jobs.CurrentClassJob)} Lv{Jobs.Level(Jobs.CurrentClassJob)}）"));
            lines.Add(new ReportLine(Severity.Ok, $"ギル {Inventory.Gil():N0}／カバンの空き {Inventory.FreeBagSlots()} 枠／いる場所 {TeleportTask.TerritoryName(Me.Territory)}"));
        });

        Add("レベル（前提：クラフター・ギャザラーは全職 Lv60 以上）", lines =>
        {
            foreach (var job in Jobs.Crafters.Concat(Jobs.Gatherers))
            {
                var lv = Jobs.Level(job);
                lines.Add(new ReportLine(lv >= 60 ? Severity.Ok : Severity.Warn, $"{Jobs.Name(job)} Lv{lv}"));
            }

            var combat = CombatJobPicker.Pick();
            lines.Add(combat == null
                ? new ReportLine(Severity.Warn, "戦闘に使えるジョブ（ギアセットのある戦闘ジョブ）がありません")
                : new ReportLine(Severity.Ok, $"戦闘に使うジョブ：{Jobs.Name(combat.Value.ClassJob)} Lv{combat.Value.Level}（ギアセット {combat.Value.Gearset + 1}）"));
        });

        Add("製作装備（基準：ショップで買える Lv60 ノーマル品の合計）", lines =>
        {
            if (data.GearBaselines == null)
            {
                lines.Add(new ReportLine(Severity.Warn, "基準値はゲームデータの読み込み後に出ます"));
                return;
            }

            foreach (var job in Jobs.Crafters)
            {
                var gear = GearCheck.ReadGearset(job);
                var (bCr, bCo) = data.GearBaselines.GetValueOrDefault(job);
                if (gear.GearsetIndex < 0)
                {
                    lines.Add(new ReportLine(Severity.Error, $"{Jobs.Name(job)}：ギアセットがありません"));
                    continue;
                }

                var ok = gear.Craftsmanship >= bCr && gear.Control >= bCo;
                lines.Add(new ReportLine(ok ? Severity.Ok : Severity.Warn,
                    $"{Jobs.Name(job)}（ギアセット {gear.GearsetIndex + 1}）作業精度 {gear.Craftsmanship}/{bCr}　加工精度 {gear.Control}/{bCo}"));
            }

            foreach (var job in Jobs.Gatherers)
            {
                var idx = GearCheck.FindGearset(job);
                lines.Add(new ReportLine(idx >= 0 ? Severity.Ok : Severity.Warn, idx >= 0 ? $"{Jobs.Name(job)}：ギアセット {idx + 1}" : $"{Jobs.Name(job)}：ギアセットがありません"));
            }
        });

        Add("解放状況（ゲームから読み取り）", lines =>
        {
            var books = data.Books;

            // 収集品の納品：窓口の解放クエスト（ゲームデータ：CollectablesShop.Quest。「職人の新たなお仕事」）
            if (books != null && books.RequiredQuest != 0)
            {
                var done = QuestManager.IsQuestComplete(books.RequiredQuest);
                lines.Add(new ReportLine(done ? Severity.Ok : Severity.Warn,
                    $"収集品の納品：クエスト「{QuestName(books.RequiredQuest)}」（{books.RequiredQuest}）{(done ? "完了済み＝納品できます" : "未完了＝秘伝書が要るとき Questionable で進めます")}"));
            }
            else
            {
                lines.Add(new ReportLine(Severity.Warn, "収集品の納品：ゲームデータの読み込み後に出ます"));
            }

            // マテリア装着・精選：解放済みか（一般アクションの解放条件）と、未解放なら解放クエストを自動で進められるか
            lines.Add(UnlockLine(Unlocks.Meld, "付けるマテリアが残っていれば、開始時に Questionable で解放します"));
            lines.Add(UnlockLine(Unlocks.Reduction, "霊砂を精選で集める計画なら、開始時に Questionable で解放します"));

            unsafe
            {

                // 宿屋：Lifestream と同じ判定（Lifestream/Utils.cs IsInnUnlocked の3クエスト）
                var ui = UIState.Instance();
                var inn = ui != null && new uint[] { 65665, 66005, 65856 }.Any(q => ui->IsUnlockLinkUnlockedOrQuestCompleted(q));
                lines.Add(new ReportLine(inn ? Severity.Ok : Severity.Error, inn ? "宿屋：使えます（Lifestream と同じ判定）" : "宿屋：未解放（グリダニアの宿屋で製作できません）"));
            }

            lines.Add(new ReportLine(GoToInnTask.InGridaniaInn() ? Severity.Ok : Severity.Ok, GoToInnTask.InGridaniaInn() ? "いまグリダニアの宿屋にいます" : "いまはグリダニアの宿屋の外です（製作の前に移動します）"));
        });

        Add("霊砂（採集→精選で集める）", lines =>
        {
            if (data.Sources == null || data.ReducibleMaterials == null)
            {
                lines.Add(new ReportLine(Severity.Warn, "ゲームデータの読み込み後に出ます"));
                return;
            }

            // ジョブクエの素材のうち精選で得られる品と、その元になる収集品（精選の対応表：LuminaSupplemental ItemSupplement.csv）
            foreach (var id in data.ReducibleMaterials)
            {
                var name = CraftPlanner.ItemName(id);
                var all = data.Sources.Get(id).ReducedFrom;
                var usable = ReduceTask.UsableSources(data.Sources, id);
                var need = all.Count == 0 ? "（精選の元が見つかりません）" : string.Join("、", all.Select(s =>
                {
                    var g = data.Sources.Get(s).Gather;
                    var lv = g.Count == 0 ? 0 : g.Min(x => x.GatheringLevel);
                    var job = g.Count > 0 && g[0].Mining ? "採掘" : "園芸";
                    return $"{CraftPlanner.ItemName(s)}（{job}Lv{lv}{(usable.Contains(s) ? "・採れます" : "・レベル不足")}）";
                }));
                lines.Add(new ReportLine(usable.Count > 0 ? Severity.Ok : Severity.Warn, $"{name}：手持ち {Inventory.CountNow(id)} 個 ← {need}"));
            }
        });

        Add("秘伝書（8職のジョブクエ Lv60 までで要るもの）", lines =>
        {
            if (data.AllBooks == null)
            {
                lines.Add(new ReportLine(Severity.Warn, "ゲームデータの読み込み後に出ます"));
                return;
            }

            foreach (var b in data.AllBooks)
            {
                var learned = ExchangeBooksTask.IsLearned(b.TomeId);
                var owned = Inventory.CountNow(b.BookItemId);
                var items = string.Join("・", b.Items.Select(CraftPlanner.ItemName));
                lines.Add(new ReportLine(learned ? Severity.Ok : Severity.Warn,
                    $"{CraftPlanner.ItemName(b.BookItemId)}：{(learned ? "読了" : owned > 0 ? "未読（所持しているので読むだけ）" : "未読・未所持（紫貨で交換します）")}　…{items} に必要"));
            }
        });

        Add("紫貨と収集品", lines =>
        {
            var books = data.Books;
            if (books == null)
            {
                lines.Add(new ReportLine(Severity.Warn, "ゲームデータの読み込み後に出ます"));
                return;
            }

            var scrips = Inventory.CountSpecialCurrency(books.RewardSpecialCurrencyId, out var scripItem);
            var cap = DeliverCollectablesTask.ScripCap(books.RewardSpecialCurrencyId);
            lines.Add(new ReportLine(Severity.Ok, $"{CraftPlanner.ItemName(scripItem)}：{scrips}/{cap}"));
            var held = Inventory.CountCollectables(books.CollectableItemId, books.MinCollectability);
            lines.Add(new ReportLine(Severity.Ok,
                $"納品に使う収集品：{CraftPlanner.ItemName(books.CollectableItemId)}（収集価値 {books.MinCollectability} 以上で 1個 {books.RewardLow}〜{books.RewardHigh}）　手持ち {held} 個"));

            var town = books.ChooseTown();
            lines.Add(town == null
                ? new ReportLine(Severity.Warn, "収集品納品窓口とスクリップ取引窓口のある街に、解放済みのエーテライトがありません")
                : new ReportLine(Severity.Ok, $"納品・交換に使う街：{TeleportTask.TerritoryName(town.Value.Collect.Territory)}"));
        });

        Add("ジョブクエの進み具合（Lv60 まで）", lines =>
        {
            if (data.Quests == null)
            {
                lines.Add(new ReportLine(Severity.Warn, "ゲームデータの読み込み後に出ます"));
                return;
            }

            foreach (var job in Jobs.Crafters)
            {
                var qs = data.Quests.Quests.Where(q => q.ClassJobId == job).ToList();
                var done = qs.Count(q => QuestManager.IsQuestComplete(q.RowId));
                var next = qs.FirstOrDefault(q => !QuestManager.IsQuestComplete(q.RowId));
                lines.Add(new ReportLine(next == null ? Severity.Ok : Severity.Ok,
                    $"{Jobs.Name(job)}：{done}/{qs.Count} 本完了{(next == null ? string.Empty : $"　次は {next}")}"));
            }
        });

        Add("マテリア装着", lines =>
        {
            // 「任意のマテリア」のクエスト（各クラフター Lv20）には剛柔のマテリアを買って付ける
            var any = ctx.Config.AnyMateriaItemId;
            var isMateria = MateriaCatalog.IsMateria(any);
            var ilv = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(any, out var row) ? row.LevelItem.RowId : 0;
            lines.Add(new ReportLine(isMateria ? Severity.Ok : Severity.Error,
                isMateria
                    ? $"任意のマテリアに使う品：{CraftPlanner.ItemName(any)}（Item {any}・アイテムLv{ilv}）　手持ち {Inventory.CountNow(any)} 個"
                    : $"任意のマテリアに使う品（Item {any}）がマテリアとして見つかりません"));

            if (data.Quests == null)
                return;
            foreach (var q in data.Quests.Quests.Where(q => q.Materia != null && !QuestManager.IsQuestComplete(q.RowId)))
            {
                var m = q.Materia!;
                var hq = q.Items.FirstOrDefault(x => x.ItemId == m.TargetItemId)?.Hq ?? false;
                var melded = Inventory.HasMelded(m.TargetItemId, hq, m.MateriaItemId);
                var what = m.MateriaItemId is { } mid ? CraftPlanner.ItemName(mid) : $"任意 → {CraftPlanner.ItemName(any)}";
                lines.Add(new ReportLine(Severity.Ok, $"{Jobs.Name(q.ClassJobId)} Lv{q.Level}：{CraftPlanner.ItemName(m.TargetItemId)}{(hq ? "(HQ)" : string.Empty)} に {what}{(melded ? "（装着済みの品を持っています）" : string.Empty)}"));
            }
        });

        Add("行き先", lines =>
        {
            var mbTown = MarketBoardLocator.ChooseTown();
            lines.Add(mbTown == null
                ? new ReportLine(Severity.Warn, "マーケットボードのある街に解放済みのエーテライトがありません")
                : new ReportLine(Severity.Ok, $"マーケットで買うときの街：{TeleportTask.TerritoryName(mbTown.Value)}（マーケットボード {MarketBoardLocator.BoardsIn(mbTown.Value).Count} 台）"));
            var limit = ctx.Config.ConfirmPurchaseAboveGil;
            lines.Add(new ReportLine(Severity.Ok, limit > 0
                ? $"マーケットの購入：1回の購入額が {limit:N0} ギルを超えるときは、買う前に確認窓を出します（「いいえ」で自動動作を止めます）"
                : "マーケットの購入：確認窓を出さない設定です"));
        });

        Add("他のプラグインと設定", lines =>
        {
            var installed = Svc.PluginInterface.InstalledPlugins.ToList();
            foreach (var (internalName, display, why) in Preflight.RequiredPlugins)
            {
                var p = installed.FirstOrDefault(x => x.InternalName == internalName);
                lines.Add(p is { IsLoaded: true }
                    ? new ReportLine(Severity.Ok, $"{display} {p.Version}（{why}）")
                    : new ReportLine(Severity.Error, $"{display}：読み込まれていません（{why}）"));
            }

            var allagan = installed.Any(x => x.IsLoaded && x.InternalName is "InventoryTools" or "AllaganItemSearch");
            lines.Add(new ReportLine(allagan ? Severity.Ok : Severity.Warn, allagan ? "Allagan Tools：あり（GBR の NPC 購入に必要）" : "Allagan Tools：なし（GBR の NPC 購入が使えません）"));

            string Flag(string name) => ctx.Gbr.ReadAutoGatherBool(name) switch { true => "ON", false => "OFF", _ => "読めない" };
            var fish = ctx.Gbr.ReadAutoGatherBool("FishDataCollection");
            lines.Add(new ReportLine(fish == true ? Severity.Ok : Severity.Warn,
                $"GBR：釣果送信の同意 {Flag("FishDataCollection")}（釣りは GBR に一任。OFF だと GBR は釣りをしないので、釣りの素材があるときは始める前に止めます）／UseAutoHook {Flag("UseAutoHook")}／vnavmesh 移動 {Flag("UseNavigation")}／採集窓の操作 {Flag("DoGathering")}"));
            lines.Add(new ReportLine(Severity.Ok, $"GBR：終わったら帰宅 {Flag("GoHomeWhenDone")}／待機中に帰宅 {Flag("GoHomeWhenIdle")}（こちらが GBR を使う間だけ OFF にします）"));
            var quick = Preflight.ReadArtisanBool("QuickSynthMode");
            lines.Add(new ReportLine(quick == true ? Severity.Warn : Severity.Ok, $"Artisan：簡易製作 {(quick == true ? "ON（HQ 指定の品が NQ になります）" : quick == false ? "OFF" : "読めない")}"));
        });

        Add("記録（ログ）", lines =>
        {
            var dir = DebugLog.Current?.Directory ?? "（記録を始めていません）";
            lines.Add(new ReportLine(Severity.Ok, $"置き場所：{dir}"));
        });

        return sections;
    }

    /// <summary>機能の解放の1行：解放済みか、未解放なら解放クエストと、自動で進められるか（進められない理由）。</summary>
    private static ReportLine UnlockLine(uint generalAction, string whenLocked)
    {
        var name = Unlocks.Name(generalAction);
        var quest = Unlocks.UnlockQuest(generalAction);
        var questName = quest == 0 ? "（解放クエストが見つかりません）" : $"「{Unlocks.QuestName(quest)}」";
        if (Unlocks.IsUnlocked(generalAction))
            return new ReportLine(Severity.Ok, $"{name}：解放済み（クエスト{questName}）");

        if (quest == 0)
            return new ReportLine(Severity.Error, $"{name}：未解放。解放するクエストがゲームデータから見つかりません");

        var chain = Unlocks.ChainToRun(quest, out var blocked);
        return blocked != null
            ? new ReportLine(Severity.Warn, $"{name}：未解放。クエスト{questName}は自動では進められません（{blocked}）")
            : new ReportLine(Severity.Warn, $"{name}：未解放。{whenLocked}（{string.Join(" → ", chain.Select(Unlocks.QuestName))}）");
    }

    private static string QuestName(uint rowId)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().TryGetRow(rowId, out var q) ? q.Name.ExtractText() : rowId.ToString();
}
