using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AutoJobQuest.Data;

/// <summary>秘伝書1冊と、それが要る納品物・中間素材。</summary>
public sealed record BookNeed(uint TomeId, uint BookItemId, List<uint> Items);

/// <summary>
/// ゲームデータから作る表（納品要件・入手元・レシピ索引・装備の基準・秘伝書）をまとめて持つ。
///
/// 【起動時に作らない】全クエストの本文シートと全アイテム・全 NPC を舐めるので重い
/// （起動時の重い走査は Dalamud プラグインのよくある罠）。
/// 「計画」「開始」「キャラクター情報」を開いたときに別スレッドで作り、以後は使い回す。
/// </summary>
public sealed class GameDataCache
{
    /// <summary>対象にするジョブクエの上限レベル（Lv60 まで）。</summary>
    public const int MaxQuestLevel = 60;

    private readonly Func<uint> collectableItemId;
    private Task? building;

    // 作り終えた一式。全部そろってから1回だけ入れ替える（途中まで作った表を「準備済み」として使わせないため。
    // 以前は前半の表を先に公開していたので、後半で例外が出ても IsReady が true のまま、
    // 秘伝書などの表が欠けたまま進み、作り直しもされなかった）
    private volatile Snapshot? ready;

    public GameDataCache(Func<uint> collectableItemId)
    {
        this.collectableItemId = collectableItemId;
    }

    public QuestCatalog? Quests => this.ready?.Quests;

    public SourceIndex? Sources => this.ready?.Sources;

    public CraftPlanner? Planner => this.ready?.Planner;

    /// <summary>製作装備の基準値（ClassJob → 作業精度・加工精度）。</summary>
    public Dictionary<uint, (int Craftsmanship, int Control)>? GearBaselines => this.ready?.GearBaselines;

    /// <summary>8職のジョブクエ（Lv60 まで）全部を作るのに要る秘伝書（完了済みかどうかに関わらず）。</summary>
    public List<BookNeed>? AllBooks => this.ready?.AllBooks;

    /// <summary>全ジョブクエの素材のうち、精選で得られる品（霊砂など）。</summary>
    public List<uint>? ReducibleMaterials => this.ready?.ReducibleMaterials;

    /// <summary>上の秘伝書すべてについての交換店・収集品・窓口の情報。</summary>
    public BookData? Books => this.ready?.Books;

    // 作っている最中の例外。別のスレッドで書くので volatile
    private volatile string? buildError;

    /// <summary>作っている最中の例外（画面に出す）。表を作り終えた後の例外（記録の書き出し等）は入れない。</summary>
    public string? BuildError => this.buildError;

    /// <summary>全部の表を作り終えたか（一部だけでは true にならない）。</summary>
    public bool IsReady => this.ready != null;

    public bool IsBuilding => this.building is { IsCompleted: false };

    /// <summary>まだ作っていなければ作り始める。前回失敗していれば作り直す。</summary>
    public void EnsureBuilding()
    {
        if (this.IsReady || this.IsBuilding)
            return;

        this.buildError = null;
        var collectable = this.collectableItemId();
        this.building = Task.Run(() =>
        {
            try
            {
                var started = DateTime.UtcNow;
                var planner = new CraftPlanner();
                var quests = QuestCatalog.Build(MaxQuestLevel);
                var sources = SourceIndex.Build();
                var baselines = GearCheck.ComputeBaselines();

                // 全ジョブクエの製作で要る秘伝書（所持数を 0 とみなして全部作る前提で数える）
                var all = planner.Build(quests.Quests.SelectMany(q => q.Items), new EmptyInventory(), _ => false);
                var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SecretRecipeBook>();
                var allBooks = all.Crafts
                    .Where(c => c.SecretRecipeBookId != 0)
                    .GroupBy(c => c.SecretRecipeBookId)
                    .Select(g => new BookNeed(g.Key, sheet.TryGetRow(g.Key, out var r) ? r.Item.RowId : 0, g.Select(c => c.ItemId).Distinct().ToList()))
                    .OrderBy(b => b.TomeId)
                    .ToList();
                var books = BookData.Build(allBooks.Select(b => b.BookItemId).Where(x => x != 0), collectable);

                // 全ジョブクエの素材のうち、精選で得られる品（霊砂など。キャラクタータブの表示用）
                var reducible = all.RawTotal.Keys.Where(k => sources.Get(k).CanReduce).OrderBy(k => k).ToList();

                // ここまで全部そろってから公開する
                this.ready = new Snapshot(quests, sources, planner, baselines, allBooks, reducible, books);

                var log = Core.DebugLog.Current;
                log?.Line("データ", $"ゲームデータを読みました（{(DateTime.UtcNow - started).TotalSeconds:0.0}秒）：ジョブクエ {quests.Quests.Count} 本、秘伝書 {allBooks.Count} 冊");
                foreach (var n in quests.Notes.Concat(sources.Notes).Concat(books.Notes))
                    log?.Line("データ", "⚠ " + n);
            }
            catch (Exception ex)
            {
                // 途中まで作った表は公開しない（次の EnsureBuilding で作り直す）。
                // 表を公開した後の例外（記録の書き出し等）なら、表はそろっているので失敗にしない
                if (this.ready == null)
                    this.buildError = $"{ex.GetType().Name}: {ex.Message}";
                Svc.Log.Error(ex, "[AutoJobQuest] ゲームデータの読み込みに失敗");
                Core.DebugLog.Current?.Exception("データ", "ゲームデータの読み込みに失敗", ex);
            }
        });
    }

    /// <summary>作り終えた表の一式（入れ替えは参照1つで行う）。</summary>
    private sealed record Snapshot(
        QuestCatalog Quests,
        SourceIndex Sources,
        CraftPlanner Planner,
        Dictionary<uint, (int Craftsmanship, int Control)> GearBaselines,
        List<BookNeed> AllBooks,
        List<uint> ReducibleMaterials,
        BookData Books);

    /// <summary>何も持っていないとみなす所持数（全部作る場合の計算用）。</summary>
    private sealed class EmptyInventory : IInventoryView
    {
        public int CountNq(uint itemId) => 0;

        public int CountHq(uint itemId) => 0;
    }
}
