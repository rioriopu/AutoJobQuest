using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// 特殊通貨の番号（SpecialShop の CostType=3 の ItemCost・CollectablesShopRewardScrip.Currency）をアイテムに直す。
///
/// この対応表はゲームのシートには無く、クライアントが実行時に持っている（CurrencyManager.SpecialItemBucket）。
/// ただし**そのキャラクターがまだ触れていないスクリップは載っていないことがある**（
/// 実測）。紫貨を一度も持ったことがないキャラでは、納品の報酬も秘伝書の値段も読めなくなる。
/// そこで、クライアント → 控え（設定 <see cref="Configuration.SpecialCurrencyFallback"/>）の順に引く。
/// 控えを使ったときは記録に残す。
/// </summary>
public static unsafe class SpecialCurrency
{
    private static readonly HashSet<byte> LoggedFallback = [];

    /// <summary>控え（設定から読み込む）。</summary>
    public static Dictionary<int, uint> Fallback { get; set; } = [];

    /// <summary>番号 → アイテム。引けなければ 0。</summary>
    public static uint ItemId(byte specialId)
    {
        var cm = CurrencyManager.Instance();
        if (cm != null)
        {
            var id = cm->GetItemIdBySpecialId(specialId);
            if (id != 0)
                return id;
        }

        if (Fallback.TryGetValue(specialId, out var fb) && fb != 0
            && Svc.Data.GetExcelSheet<Item>().TryGetRow(fb, out var row) && !row.Name.IsEmpty)
        {
            if (LoggedFallback.Add(specialId))
                Core.DebugLog.Current?.Line("通貨", $"特殊通貨 {specialId} はクライアントの表に無いので、控えの {row.Name.ExtractText()}（{fb}）を使います");
            return fb;
        }

        return 0;
    }
}
