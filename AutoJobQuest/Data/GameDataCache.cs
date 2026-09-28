using System;
using System.Threading.Tasks;

namespace AutoJobQuest.Data;

/// <summary>
/// ゲームデータから作る表（納品要件・入手元・レシピ索引）をまとめて持つ。
///
/// 【起動時に作らない】全クエストの本文シートと全アイテムを舐めるので重い
/// （起動時の重い走査は Dalamud プラグインのよくある罠）。
/// 「計画」か「開始」を押したときに別スレッドで作り、以後は使い回す。
/// </summary>
public sealed class GameDataCache
{
    /// <summary>対象にするジョブクエの上限レベル（Lv60 まで）。</summary>
    public const int MaxQuestLevel = 60;

    private Task? building;

    public QuestCatalog? Quests { get; private set; }

    public SourceIndex? Sources { get; private set; }

    public CraftPlanner? Planner { get; private set; }

    /// <summary>製作装備の基準値（ClassJob → 作業精度・加工精度）。</summary>
    public System.Collections.Generic.Dictionary<uint, (int Craftsmanship, int Control)>? GearBaselines { get; private set; }

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
        this.building = Task.Run(() =>
        {
            try
            {
                var planner = new CraftPlanner();
                var quests = QuestCatalog.Build(MaxQuestLevel);
                var sources = SourceIndex.Build();
                this.GearBaselines = GearCheck.ComputeBaselines();
                this.Planner = planner;
                this.Quests = quests;
                this.Sources = sources;
            }
            catch (Exception ex)
            {
                this.BuildError = $"{ex.GetType().Name}: {ex.Message}";
                Svc.Log.Error(ex, "[AutoJobQuest] ゲームデータの読み込みに失敗");
            }
        });
    }
}
