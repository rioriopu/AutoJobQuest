using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>NPC の置き場所（Level シートから）。</summary>
public sealed record NpcSpot(uint NpcId, uint Territory, Vector3 Position);

/// <summary>秘伝書1冊の交換条件。</summary>
/// <param name="BookItemId">秘伝書のアイテム。</param>
/// <param name="TomeId">SecretRecipeBook の行（習得判定に使う）。</param>
/// <param name="ShopId">交換する SpecialShop。</param>
/// <param name="Price">値段（特殊通貨の個数）。</param>
/// <param name="SpecialCurrencyId">払う特殊通貨の番号（CostType=3 の ItemCost）。</param>
public sealed record BookOffer(uint BookItemId, uint TomeId, uint ShopId, uint Price, byte SpecialCurrencyId);

/// <summary>
/// 秘伝書に要るゲームデータをまとめて引く。
///
///  ・秘伝書 → 交換する SpecialShop（アイテム交換画面 InclusionShop の中でだけ開く店）と値段・通貨
///  ・収集品（既定は「収集用のシーダーロングボウ」）→ 納品の報酬（通貨・量）と受け付ける収集価値
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
                this.Offers[got] = new BookOffer(got, tome.RowId, shop.RowId, cost.CurrencyCost, (byte)cost.ItemCost.RowId);

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
                this.ScripNpcs.Add(new NpcSpot(id, lv.Territory.RowId, new Vector3(lv.X, lv.Y, lv.Z)));
        }

        if (this.CollectableNpcs.Count == 0)
            this.Notes.Add("収集品納品窓口の場所がゲームデータから引けませんでした");
        if (this.ScripNpcs.Count == 0)
            this.Notes.Add("スクリップ取引窓口の場所がゲームデータから引けませんでした");
    }

    /// <summary>
    /// 収集品納品窓口とスクリップ取引窓口が同じエリアにあり、そのエリアに解放済みのエーテライトがある組を選ぶ
    /// （イディルシャイア・モードゥナはどちらも Level シートだけで座標が引ける）。
    /// </summary>
    public (NpcSpot Collect, NpcSpot Scrip)? ChooseTown()
    {
        var unlocked = Svc.Aetherytes.Select(a => a.AetheryteId).ToHashSet();
        var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();
        foreach (var c in this.CollectableNpcs)
        {
            var s = this.ScripNpcs.FirstOrDefault(x => x.Territory == c.Territory);
            if (s == null)
                continue;
            if (aetherytes.Any(a => a.IsAetheryte && a.Territory.RowId == c.Territory && unlocked.Contains(a.RowId)))
                return (c, s);
        }

        return null;
    }
}
