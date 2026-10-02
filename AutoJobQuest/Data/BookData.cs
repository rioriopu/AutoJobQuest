using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>NPC の置き場所（Level シートから）。</summary>
/// <summary>窓口1つ（NPC・エリア・位置）。UnlockQuest は、その窓口の画面を開くのに要るクエスト（PreHandler.UnlockQuest。無ければ 0）。</summary>
public sealed record NpcSpot(uint NpcId, uint Territory, Vector3 Position, uint UnlockQuest = 0);

/// <summary>秘伝書1冊の交換条件。</summary>
/// <param name="BookItemId">秘伝書のアイテム。</param>
/// <param name="TomeId">SecretRecipeBook の行（習得判定に使う）。</param>
/// <param name="ShopId">交換する SpecialShop。</param>
/// <param name="Price">値段（特殊通貨の個数）。</param>
/// <param name="SpecialCurrencyId">払う特殊通貨の番号（CostType=3 の ItemCost）。</param>
/// <param name="RequiredQuests">
/// 交換に要るクエスト（店の SpecialShop.Quest と、品ごとの ItemStruct.Quest。0 は除く）。
/// ゲームデータの調査：紫貨の秘伝書の店は「一流の道具」（Q66959）が未完了だと使えない。以前は確かめていなかった。
/// </param>
public sealed record BookOffer(uint BookItemId, uint TomeId, uint ShopId, uint Price, byte SpecialCurrencyId, IReadOnlyList<uint>? RequiredQuests = null);

/// <summary>
/// 秘伝書に要るゲームデータをまとめて引く。
///
///  ・秘伝書 → 交換する SpecialShop（アイテム交換画面 InclusionShop の中でだけ開く店）と値段・通貨
///  ・収集品（既定は「収集用のシーダーロングボウ」。秘伝書の要る職に合わせて同じ段の品に替える：ScripCollectable）→ 納品の報酬（通貨・量）と受け付ける収集価値
///  ・収集品納品窓口とスクリップ取引窓口の NPC と、Level シートにある座標
///
/// ENpcBase を全部見るので重い。別スレッドで作る。
/// </summary>
public sealed class BookData
{
    // ENpcData の値の上位 16bit（イベントハンドラの種別）
    private const uint HandlerCustomTalk = 0x000B;
    private const uint HandlerPreHandler = 0x0036;
    private const uint HandlerInclusionShop = 0x003A;
    private const uint HandlerCollectablesShop = 0x003B;

    /// <summary>Level シートで NPC の置き場所を表す Type。</summary>
    private const byte LevelTypeNpc = 8;

    public Dictionary<uint, BookOffer> Offers { get; } = [];

    /// <summary>納品する収集品。</summary>
    public uint CollectableItemId { get; private set; }

    /// <summary>その収集品を作るレシピ。</summary>
    public uint CollectableRecipeId { get; private set; }

    public uint CollectablesShopId { get; private set; }

    /// <summary>納品窓口の店の名前（例「収集品納品」）。話しかけて選択肢が出たときの手がかり。</summary>
    public string CollectablesShopName { get; private set; } = string.Empty;

    /// <summary>納品画面のタブ（ClassJob）。</summary>
    public uint CollectableTabClassJob { get; private set; }

    public byte RewardSpecialCurrencyId { get; private set; }

    public int RewardLow { get; private set; }

    public int RewardHigh { get; private set; }

    public int MinCollectability { get; private set; }

    /// <summary>納品に必要なクエスト（CollectablesShopItem.RequiredQuest、無ければ CollectablesShop.Quest）。0 なら無し。</summary>
    public uint RequiredQuest { get; private set; }

    /// <summary>交換画面の系統（InclusionShopCategory の行）と、その中の種別の位置（subrow）。</summary>
    public Dictionary<uint, (uint Category, int Subrow)> ShopPaths { get; } = [];

    /// <summary>秘伝書の店を含む系統（InclusionShopCategory の行）。店を開いた NPC によって系統の行が違う（50/56/60/71）。</summary>
    public HashSet<uint> BookCategories { get; } = [];

    /// <summary>InclusionShopSeries の行 → 秘伝書の店の位置（subrow）。</summary>
    public Dictionary<uint, int> BookSeriesSubrow { get; } = [];

    public List<NpcSpot> CollectableNpcs { get; } = [];

    public List<NpcSpot> ScripNpcs { get; } = [];

    public List<string> Notes { get; } = [];

    public static BookData Build(IEnumerable<uint> bookItemIds, uint collectableItemId)
    {
        var d = new BookData { CollectableItemId = collectableItemId };
        d.ResolveBooks(bookItemIds.ToHashSet());
        d.ResolveCollectable();
        d.ResolveNpcs();
        return d;
    }

    /// <summary>
    /// 同じ窓口（CollectablesShop）の別の収集品に替えた写し。秘伝書の店・窓口の場所は写す（重い ENpcBase の走査をしない）。
    /// 収集品をジョブに合わせて選ぶ（<see cref="ScripCollectable"/>）ときに、読み込み済みの表から作る。窓口が違えば null。
    /// <see cref="Notes"/> は収集品の分だけを持つ（秘伝書の店・窓口の分は元の表の Notes を見る）。
    /// </summary>
    public BookData? ForCollectable(uint itemId)
    {
        if (itemId == this.CollectableItemId)
            return this;

        var d = new BookData { CollectableItemId = itemId };
        d.ResolveCollectable();
        if (d.CollectablesShopId == 0 || d.CollectablesShopId != this.CollectablesShopId)
            return null;

        foreach (var (k, v) in this.Offers)
            d.Offers[k] = v;
        foreach (var (k, v) in this.ShopPaths)
            d.ShopPaths[k] = v;
        d.BookCategories.UnionWith(this.BookCategories);
        foreach (var (k, v) in this.BookSeriesSubrow)
            d.BookSeriesSubrow[k] = v;
        d.CollectableNpcs.AddRange(this.CollectableNpcs);
        d.ScripNpcs.AddRange(this.ScripNpcs);
        return d;
    }

    private void ResolveBooks(HashSet<uint> books)
    {
        var tomes = Svc.Data.GetExcelSheet<SecretRecipeBook>();
        var series = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
        var categories = Svc.Data.GetExcelSheet<InclusionShopCategory>();

        // InclusionShop の中から開ける SpecialShop（系統, 位置）
        var reachable = new Dictionary<uint, (uint Series, int Subrow)>();
        foreach (var s in series)
        {
            for (var i = 0; i < s.Count; i++)
            {
                var shop = s[i].SpecialShop.RowId;
                if (shop != 0 && !reachable.ContainsKey(shop))
                    reachable[shop] = (s.RowId, i);
            }
        }

        foreach (var shop in Svc.Data.GetExcelSheet<SpecialShop>())
        {
            if (!reachable.TryGetValue(shop.RowId, out var path))
                continue;

            foreach (var entry in shop.Item)
            {
                var got = entry.ReceiveItems[0].Item.RowId;
                if (!books.Contains(got) || this.Offers.ContainsKey(got))
                    continue;

                var cost = entry.ItemCosts[0];
                if (cost.CostType != 3)
                    continue; // 特殊通貨で払う店だけ（紫貨）

                var tome = tomes.FirstOrDefault(t => t.Item.RowId == got);
                var required = new[] { shop.Quest.RowId, entry.Quest.RowId }.Where(q => q != 0).Distinct().ToList();
                this.Offers[got] = new BookOffer(got, tome.RowId, shop.RowId, cost.CurrencyCost, (byte)cost.ItemCost.RowId, required);

                var cat = categories.FirstOrDefault(c => c.InclusionShopSeries.RowId == path.Series);
                this.ShopPaths[shop.RowId] = (cat.RowId, path.Subrow);
            }
        }

        // 秘伝書の店を含む系列と系統をすべて集める（どの窓口で開いても対応できるように）
        var bookShops = this.Offers.Values.Select(o => o.ShopId).ToHashSet();
        foreach (var s in series)
        {
            for (var i = 0; i < s.Count; i++)
            {
                if (bookShops.Contains(s[i].SpecialShop.RowId) && !this.BookSeriesSubrow.ContainsKey(s.RowId))
                    this.BookSeriesSubrow[s.RowId] = i;
            }
        }

        foreach (var c in categories)
            if (this.BookSeriesSubrow.ContainsKey(c.InclusionShopSeries.RowId))
                this.BookCategories.Add(c.RowId);

        foreach (var b in books.Where(b => !this.Offers.ContainsKey(b)))
            this.Notes.Add($"秘伝書 {CraftPlanner.ItemName(b)} を交換できる店が見つかりません");
    }

    private void ResolveCollectable()
    {
        var shopItems = Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>();
        foreach (var row in shopItems)
        {
            for (var i = 0; i < row.Count; i++)
            {
                var it = row[i];
                if (it.Item.RowId != this.CollectableItemId)
                    continue;

                var reward = it.CollectablesShopRewardScrip.ValueNullable;
                var refine = it.CollectablesShopRefine.ValueNullable;
                if (reward == null || refine == null)
                    continue;

                this.RewardSpecialCurrencyId = (byte)reward.Value.Currency;
                this.RewardLow = reward.Value.LowReward;
                this.RewardHigh = reward.Value.HighReward;
                this.MinCollectability = refine.Value.LowCollectability;
                this.RequiredQuest = it.RequiredQuest.RowId;

                // どの窓口の、どのタブの品か（CollectablesShop.ShopItems[slot] の slot → ClassJob 8+slot）
                foreach (var shop in Svc.Data.GetExcelSheet<CollectablesShop>())
                {
                    for (var slot = 0; slot < shop.ShopItems.Count; slot++)
                    {
                        if (shop.ShopItems[slot].RowId != row.RowId)
                            continue;
                        this.CollectablesShopId = shop.RowId;
                        this.CollectablesShopName = shop.Name.ExtractText();
                        this.CollectableTabClassJob = (uint)(8 + slot);

                        // 品に必要クエストが無ければ、窓口の解放クエストを使う
                        // （ゲームデータ実測：3866626 の Quest = 67631「職人の新たなお仕事」）
                        if (this.RequiredQuest == 0)
                            this.RequiredQuest = shop.Quest.RowId;
                    }
                }

                break;
            }
        }

        var recipe = Svc.Data.GetExcelSheet<Recipe>().FirstOrDefault(r => r.ItemResult.RowId == this.CollectableItemId && r.Number != 0);
        this.CollectableRecipeId = recipe.RowId;

        if (this.RewardLow == 0 || this.CollectableRecipeId == 0)
            this.Notes.Add($"収集品 {CraftPlanner.ItemName(this.CollectableItemId)} の納品データかレシピが見つかりません");
    }

    private void ResolveNpcs()
    {
        var customTalk = Svc.Data.GetExcelSheet<CustomTalk>();
        var preHandler = Svc.Data.GetExcelSheet<PreHandler>();
        var inclusion = Svc.Data.GetExcelSheet<InclusionShop>();
        var categories = Svc.Data.GetExcelSheet<InclusionShopCategory>();

        var bookCategories = this.BookCategories;

        bool IsCollectablesShop(uint v) => v >> 16 == HandlerCollectablesShop && v == this.CollectablesShopId;

        bool IsBookInclusionShop(uint v)
        {
            if (v >> 16 != HandlerInclusionShop || !inclusion.TryGetRow(v, out var shop))
                return false;
            foreach (var c in shop.Category)
                if (bookCategories.Contains(c.RowId))
                    return true;
            return false;
        }

        var collectNpcs = new HashSet<uint>();
        var scripNpcs = new HashSet<uint>();

        // 窓口 → 画面を開くのに要るクエスト（PreHandler.UnlockQuest。イディルシャイア＝67634「職人、新たな世界へ」、
        // モードゥナ＝67631「職人の新たなお仕事」。未完了だと話しかけても画面が開かない）
        var unlockOf = new Dictionary<uint, uint>();
        foreach (var npc in Svc.Data.GetExcelSheet<ENpcBase>())
        {
            foreach (var d in npc.ENpcData)
            {
                var v = d.RowId;
                if (v == 0)
                    continue;

                switch (v >> 16)
                {
                    case HandlerCollectablesShop when IsCollectablesShop(v):
                        collectNpcs.Add(npc.RowId);
                        break;
                    case HandlerCustomTalk when customTalk.TryGetRow(v, out var ct) && IsCollectablesShop(ct.SpecialLinks.RowId):
                        collectNpcs.Add(npc.RowId);
                        break;
                    case HandlerPreHandler when preHandler.TryGetRow(v, out var ph) && IsBookInclusionShop(ph.Target.RowId):
                        scripNpcs.Add(npc.RowId);
                        if (ph.UnlockQuest.RowId != 0)
                            unlockOf[npc.RowId] = ph.UnlockQuest.RowId;
                        break;
                    case HandlerInclusionShop when IsBookInclusionShop(v):
                        scripNpcs.Add(npc.RowId);
                        break;
                }
            }
        }

        foreach (var lv in Svc.Data.GetExcelSheet<Level>())
        {
            if (lv.Type != LevelTypeNpc)
                continue;
            var id = lv.Object.RowId;
            if (collectNpcs.Contains(id))
                this.CollectableNpcs.Add(new NpcSpot(id, lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z)));
            if (scripNpcs.Contains(id))
                this.ScripNpcs.Add(new NpcSpot(id, lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z), unlockOf.GetValueOrDefault(id)));
        }

        if (this.CollectableNpcs.Count == 0)
            this.Notes.Add("収集品納品窓口の場所がゲームデータから引けませんでした");
        if (this.ScripNpcs.Count == 0)
            this.Notes.Add("スクリップ取引窓口の場所がゲームデータから引けませんでした");
    }

    /// <summary>
    /// 秘伝書の流れに欠かせないデータがそろっているかを確かめ、足りないものを返す（空なら問題なし）。
    /// フレームワークのスレッドから呼ぶ（特殊通貨の引き当てと、解放済みエーテライトを読むため）。
    ///
    /// 以前は Notes を警告として記録するだけで先へ進み、素材を集め終わった後で
    /// 「秘伝書が未読」という別の段の理由で止まっていた（原因の段と止まる段が離れて調べにくい）。
    /// </summary>
    /// <param name="requiredBookItems">交換が要る秘伝書（アイテム ID）。</param>
    /// <param name="currencyItem">特殊通貨の番号 → アイテム（省略時はゲームから：SpecialCurrency.ItemId。試すときに差し替える）。</param>
    /// <param name="townAvailable">両方の窓口のある街に行けるか（省略時はゲームから：ChooseTown。試すときに差し替える）。</param>
    /// <param name="questDone">クエストが完了しているか（省略時はゲームから。行ける街が無いときの理由の文言に使う。試すときに差し替える）。</param>
    public List<string> Validate(IEnumerable<uint> requiredBookItems, Func<byte, uint>? currencyItem = null, Func<bool>? townAvailable = null, Func<uint, bool>? questDone = null)
    {
        currencyItem ??= SpecialCurrency.ItemId;
        townAvailable ??= () => this.ChooseTown() != null;
        var problems = new List<string>();
        foreach (var b in requiredBookItems)
            if (!this.Offers.ContainsKey(b))
                problems.Add($"秘伝書「{CraftPlanner.ItemName(b)}」を交換できる店がゲームデータから見つかりません");

        if (this.RewardLow <= 0)
            problems.Add($"収集品「{CraftPlanner.ItemName(this.CollectableItemId)}」の納品の報酬（紫貨の量）がゲームデータから読めません");
        if (this.CollectableRecipeId == 0)
            problems.Add($"収集品「{CraftPlanner.ItemName(this.CollectableItemId)}」のレシピがゲームデータから見つかりません");
        if (this.CollectableTabClassJob == 0)
            problems.Add($"収集品「{CraftPlanner.ItemName(this.CollectableItemId)}」を受け付ける納品窓口がゲームデータから見つかりません");

        // 納品でもらう通貨と、秘伝書の値段の通貨が同じ品か（違えば、いくら納品しても交換できない）
        var rewardItem = currencyItem(this.RewardSpecialCurrencyId);
        if (rewardItem == 0)
            problems.Add($"納品の報酬の特殊通貨（番号 {this.RewardSpecialCurrencyId}）をアイテムに直せません");
        foreach (var o in this.Offers.Values)
        {
            var costItem = currencyItem(o.SpecialCurrencyId);
            if (costItem == 0)
                problems.Add($"「{CraftPlanner.ItemName(o.BookItemId)}」の値段の特殊通貨（番号 {o.SpecialCurrencyId}）をアイテムに直せません");
            else if (rewardItem != 0 && costItem != rewardItem)
                problems.Add($"「{CraftPlanner.ItemName(o.BookItemId)}」の値段の通貨（{CraftPlanner.ItemName(costItem)}）が、納品でもらう通貨（{CraftPlanner.ItemName(rewardItem)}）と違います");
            if (o.Price == 0)
                problems.Add($"「{CraftPlanner.ItemName(o.BookItemId)}」の値段がゲームデータから読めません");
        }

        if (this.CollectableNpcs.Count == 0 || this.ScripNpcs.Count == 0)
            problems.Add("収集品納品窓口かスクリップ取引窓口の場所がゲームデータから引けません");
        else if (!townAvailable())
            problems.Add(this.WhyNoTown(questDone));

        return problems;
    }

    /// <summary>
    /// 収集品納品窓口とスクリップ取引窓口が同じエリアにあり、そのエリアに解放済みのエーテライトがあり、
    /// スクリップ取引窓口の画面を開くクエスト（PreHandler.UnlockQuest）が完了している組を選ぶ
    /// （イディルシャイア・モードゥナはどちらも Level シートだけで座標が引ける。
    /// 以前はクエストを見ずに、並びが先のイディルシャイアを選び、67634 が未完了だと画面が開かずに止まった）。
    /// </summary>
    /// <param name="aetheryteUnlocked">エーテライトが解放済みか（省略時はゲームから。試すときに差し替える）。</param>
    /// <param name="questDone">クエストが完了しているか（省略時はゲームから。試すときに差し替える）。</param>
    public (NpcSpot Collect, NpcSpot Scrip)? ChooseTown(Func<uint, bool>? aetheryteUnlocked = null, Func<uint, bool>? questDone = null)
    {
        if (aetheryteUnlocked == null)
        {
            var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
            aetheryteUnlocked = unlocked.Contains;
        }

        questDone ??= q => FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q);
        var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();
        foreach (var c in this.CollectableNpcs)
        {
            var s = this.ScripNpcs.FirstOrDefault(x => x.Territory == c.Territory && (x.UnlockQuest == 0 || questDone(x.UnlockQuest)));
            if (s == null)
                continue;
            if (aetherytes.Any(a => a.IsAetheryte && a.Territory.RowId == c.Territory && aetheryteUnlocked(a.RowId)))
                return (c, s);
        }

        return null;
    }

    /// <summary>窓口のある町を選べない理由（事前点検・止めるときの文言。クエストが未完了の窓口を名前つきで出す）。</summary>
    public string WhyNoTown(Func<uint, bool>? questDone = null)
    {
        questDone ??= q => FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(q);
        var locked = this.ScripNpcs.Where(x => x.UnlockQuest != 0 && !questDone(x.UnlockQuest))
            .Select(x => $"{TerritoryLabel(x.Territory)} の窓口は「{Unlocks.QuestName(x.UnlockQuest)}」が未完了")
            .ToList();
        return locked.Count > 0
            ? $"スクリップ取引窓口を開けません（{string.Join("、", locked)}）。どちらかの町の窓口を開けるようにしてから始めてください"
            : "収集品納品窓口とスクリップ取引窓口のある街に、解放済みのエーテライトがありません";
    }

    private static string TerritoryLabel(uint territory)
        => Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t) ? t.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{territory}" : $"#{territory}";
}
