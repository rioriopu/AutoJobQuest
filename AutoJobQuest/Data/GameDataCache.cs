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

    public GameDataCache(Func<uint> collectableItemId)
    {
        this.collectableItemId = collectableItemId;
    }

    public QuestCatalog? Quests { get; private set; }

    public SourceIndex? Sources { get; private set; }

    public CraftPlanner? Planner { get; private set; }

    /// <summary>製作装備の基準値（ClassJob → 作業精度・加工精度）。</summary>
    public Dictionary<uint, (int Craftsmanship, int Control)>? GearBaselines { get; private set; }

    /// <summary>8職のジョブクエ（Lv60 まで）全部を作るのに要る秘伝書（完了済みかどうかに関わらず）。</summary>
    public List<BookNeed>? AllBooks { get; private set; }

    /// <summary>全ジョブクエの素材のうち、精選で得られる品（霊砂など）。</summary>
    public List<uint>? ReducibleMaterials { get; private set; }

    /// <summary>上の秘伝書すべてについての交換店・収集品・窓口の情報。</summary>
    public BookData? Books { get; private set; }

    /// <summary>作っている最中の例外（画面に出す）。</summary>
    public string? BuildError { get; private set; }

    public bool IsReady => this.Quests != null && this.Sources != null && this.Planner != null;

    public bool IsBuilding => this.building is { IsCompleted: false };

    /// <summary>まだ作っていなければ作り始める。</summary>
    public void EnsureBuilding()
    {
        if (this.IsReady || this.IsBuilding)
            return;

        this.BuildError = null;
        var collectable = this.collectableItemId();
        this.building = Task.Run(() =>
        {
            try
            {
                var started = DateTime.UtcNow;
                var planner = new CraftPlanner();
                var quests = QuestCatalog.Build(MaxQuestLevel);
                var sources = SourceIndex.Build();
                this.GearBaselines = GearCheck.ComputeBaselines();
                this.Planner = planner;
                this.Quests = quests;
                this.Sources = sources;

                // 全ジョブクエの製作で要る秘伝書（所持数を 0 とみなして全部作る前提で数える）
                var all = planner.Build(quests.Quests.SelectMany(q => q.Items), new EmptyInventory(), _ => false);
                var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SecretRecipeBook>();
                this.AllBooks = all.Crafts
                    .Where(c => c.SecretRecipeBookId != 0)
                    .GroupBy(c => c.SecretRecipeBookId)
                    .Select(g => new BookNeed(g.Key, sheet.TryGetRow(g.Key, out var r) ? r.Item.RowId : 0, g.Select(c => c.ItemId).Distinct().ToList()))
                    .OrderBy(b => b.TomeId)
                    .ToList();
                this.Books = BookData.Build(this.AllBooks.Select(b => b.BookItemId).Where(x => x != 0), collectable);

                // 全ジョブクエの素材のうち、精選で得られる品（霊砂など。キャラクタータブの表示用）
                this.ReducibleMaterials = all.RawTotal.Keys.Where(k => sources.Get(k).CanReduce).OrderBy(k => k).ToList();

                var log = Core.DebugLog.Current;
                log?.Line("データ", $"ゲームデータを読みました（{(DateTime.UtcNow - started).TotalSeconds:0.0}秒）：ジョブクエ {quests.Quests.Count} 本、秘伝書 {this.AllBooks.Count} 冊");
                foreach (var n in quests.Notes.Concat(sources.Notes).Concat(this.Books.Notes))
                    log?.Line("データ", "⚠ " + n);
            }
            catch (Exception ex)
            {
                this.BuildError = $"{ex.GetType().Name}: {ex.Message}";
                Svc.Log.Error(ex, "[AutoJobQuest] ゲームデータの読み込みに失敗");
                Core.DebugLog.Current?.Exception("データ", "ゲームデータの読み込みに失敗", ex);
            }
        });
    }

    /// <summary>何も持っていないとみなす所持数（全部作る場合の計算用）。</summary>
    private sealed class EmptyInventory : IInventoryView
    {
        public int CountNq(uint itemId) => 0;

        public int CountHq(uint itemId) => 0;
    }
}
