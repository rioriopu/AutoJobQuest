using System;
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
///
/// 【控えの自動更新】控えは設定の初期値で、スクリップの階層が増えると番号が入れ替わる。
/// そこで、クライアントの表（SpecialItemBucket）を定期的に列挙し、控えと違えば記録してクライアントの値で控えを書き換える
/// （クライアントの値は、いま動いているゲームそのものの値。控えは、表に載っていないキャラのための予備）。
/// 同じ番号に2つの品が載っているときは、決められないので書き換えない（記録だけ）。
/// 番号1は、控えでは「詩学 28」、ClientStructs の注記では「白のクラフタースクリップ 25199」と食い違う。
/// どちらもこのプラグインでは使わない番号（秘伝書・納品で使うのは番号2＝紫貨 33913。どちらの資料でも一致）。実行時の値で決まる。
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

    /// <summary>
    /// 控えをクライアントの表で更新する。フレームワークのスレッドから呼ぶ。
    /// 表は列挙で読む（StdMap のインデクサは、無い鍵を挿入してしまう）。
    /// 戻り値：控えを書き換えたか（呼び出し側が設定を保存する）。
    /// </summary>
    public static bool RefreshFallback(Dictionary<int, uint> fallback)
    {
        var cm = CurrencyManager.Instance();
        if (cm == null)
            return false;

        var client = new List<(uint ItemId, int SpecialId)>();
        foreach (var pair in cm->SpecialItemBucket)
            client.Add((pair.Item1, pair.Item2.SpecialId));

        var sheet = Svc.Data.GetExcelSheet<Item>();
        var (updated, conflicts) = MergeClient(fallback, client, id => sheet.TryGetRow(id, out var row) && !row.Name.IsEmpty);
        foreach (var line in conflicts)
            Core.DebugLog.Current?.Line("通貨", line);
        foreach (var line in updated)
        {
            Core.DebugLog.Current?.Line("通貨", line);
            Svc.Log.Information($"[AutoJobQuest] {line}");
        }

        return updated.Count > 0;
    }

    /// <summary>
    /// クライアントの表（品 → 番号）を控え（番号 → 品）へ重ねる（試せるように分けた判断）。
    /// 番号 0・品 0・名前の無い品は飛ばす。同じ番号に2つの品があれば書き換えない。控えと同じなら何もしない。
    /// </summary>
    public static (List<string> Updated, List<string> Conflicts) MergeClient(Dictionary<int, uint> fallback, IEnumerable<(uint ItemId, int SpecialId)> client, Func<uint, bool> itemExists)
    {
        var updated = new List<string>();
        var conflicts = new List<string>();
        var seen = new Dictionary<int, uint>();
        var ambiguous = new HashSet<int>();
        foreach (var (item, sid) in client)
        {
            if (sid == 0 || item == 0 || !itemExists(item))
                continue;
            if (seen.TryGetValue(sid, out var first) && first != item)
            {
                if (ambiguous.Add(sid))
                    conflicts.Add($"特殊通貨 {sid} に、クライアントの表で2つの品（{first}・{item}）が載っています。決められないので控えは変えません");
                continue;
            }

            seen[sid] = item;
        }

        foreach (var (sid, item) in seen)
        {
            if (ambiguous.Contains(sid) || (fallback.TryGetValue(sid, out var old) && old == item))
                continue;
            updated.Add(fallback.TryGetValue(sid, out var before)
                ? $"特殊通貨 {sid} の控えを {before} から {item} に書き換えました（クライアントの表と違っていた）"
                : $"特殊通貨 {sid} の控え（{item}）をクライアントの表から足しました");
            fallback[sid] = item;
        }

        return (updated, conflicts);
    }
}
