using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoJobQuest.Ipc;

/// <summary>
/// Allagan Tools（InternalName InventoryTools）への窓口：リテイナーごとの所持数を、リテイナーを開かずに読む
/// （Allagan Tools でどこに何が何個あるか分かるので、要るリテイナーだけ開いて引き出す）。
///
/// IPC 名と型は、使っている側のソースで確かめた（Allagan Tools 本体のソースでは確かめていない）：
///  ・AllaganTools.IsInitialized() → bool（Artisan の RetainerInfo・GBR の IpcSubscribers）
///  ・AllaganTools.ItemCount(uint 品, ulong キャラクター〔リテイナー〕の番号, int 持ち物の種類) → uint（ECommons の AllaganToolsIPC）
///  ・AllaganTools.ItemCountHQ（同じ形。HQ だけの数）
///  ・AllaganTools.InventoryCountByTypes(uint[] 持ち物の種類, ulong? キャラクターの番号) → uint
/// ItemCount は品質を問わない数（Artisan は ItemCount を「全部」、ItemCountHQ を「HQ だけ」として使っている）。
/// リテイナーの番号は RetainerManager の RetainerId（Artisan と同じ）。
/// </summary>
public sealed class AllaganToolsIpc : IpcGate
{
    public override string InternalName => "InventoryTools";

    public override string DisplayName => "Allagan Tools";

    /// <summary>リテイナーの持ち物の7ページ（クリスタルは別：RetainerCrystals）。</summary>
    private static readonly int[] RetainerPages =
    [
        (int)InventoryType.RetainerPage1, (int)InventoryType.RetainerPage2, (int)InventoryType.RetainerPage3, (int)InventoryType.RetainerPage4,
        (int)InventoryType.RetainerPage5, (int)InventoryType.RetainerPage6, (int)InventoryType.RetainerPage7,
    ];

    /// <summary>
    /// 今のキャラクターが持つキャラクター（リテイナーなど）の番号（AllaganTools.GetCharactersOwnedByActive(false)：ECommons の AllaganToolsIPC で確認）。
    /// リテイナー以外（フリーカンパニーなど）が混じっても、リテイナーの持ち物の種類で数えるので 0 になる。読めなければ null。
    /// </summary>
    public HashSet<ulong>? OwnedByActive()
        => this.TryInvoke("GetCharactersOwnedByActive",
            () => this.Func<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive").InvokeFunc(false), out var set)
            ? set
            : null;

    /// <summary>読み込み済みで使えるか。</summary>
    public bool IsInitialized()
        => this.IsLoaded
           && this.TryInvoke("IsInitialized", () => this.Func<bool>("AllaganTools.IsInitialized").InvokeFunc(), out var ok)
           && ok;

    /// <summary>
    /// そのリテイナーの持ち物（7ページとクリスタル）について、Allagan Tools に記録があるか（入っている品の数が 1 以上か）。
    /// 一度も開いていないリテイナーは記録が無く 0 になる（空のリテイナーも 0：その場合は開いて読むだけなので害は無い）。読めなければ null。
    /// </summary>
    public bool? HasRecord(ulong retainerId)
    {
        var types = RetainerPages.Select(x => (uint)x).Append((uint)InventoryType.RetainerCrystals).ToArray();
        return this.TryInvoke("InventoryCountByTypes",
            () => this.Func<uint[], ulong?, uint>("AllaganTools.InventoryCountByTypes").InvokeFunc(types, retainerId), out var n)
            ? n > 0
            : null;
    }

    /// <summary>そのリテイナーにある数（全部・HQ）。クリスタルはクリスタルの欄だけ、ほかは7ページを見る。読めなければ null。</summary>
    public (int All, int Hq)? RetainerCount(uint itemId, ulong retainerId, bool crystal)
    {
        int[] types = crystal ? [(int)InventoryType.RetainerCrystals] : RetainerPages;
        long all = 0;
        long hq = 0;
        foreach (var type in types)
        {
            if (!this.TryInvoke("ItemCount", () => this.Func<uint, ulong, int, uint>("AllaganTools.ItemCount").InvokeFunc(itemId, retainerId, type), out var n))
                return null;
            all += n;
        }

        if (all > 0 && !crystal)
        {
            foreach (var type in types)
            {
                if (!this.TryInvoke("ItemCountHQ", () => this.Func<uint, ulong, int, uint>("AllaganTools.ItemCountHQ").InvokeFunc(itemId, retainerId, type), out var n))
                    return null;
                hq += n;
            }
        }

        return ((int)all, (int)System.Math.Min(hq, all));
    }
}
