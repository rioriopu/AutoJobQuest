using System;
using System.Collections.Generic;

namespace AutoJobQuest.Automation;

/// <summary>
/// リテイナーの段で、窓が自分の操作で出たものかを見分ける（ゲームを起動せずに試せるように分けた）。
///
/// 見分け方：
///  ・操作の直前に <see cref="MarkAct"/> を呼び、その時刻と、その時点で出ていた窓を控える。
///  ・操作の後に「持ち主の記録が控えた時刻以降」か「控えた時点では出ていなかった窓がいま操作できる」なら、自分の窓とみる。
///  ・控えを取り直すのは、実際に操作する直前だけ。操作の後に取り直すと、操作で出た窓を「前から出ていた」とみてしまう
///    （以前は持ち物を閉じた後にも取り直していたので、出直したリテイナーのメニューを認めずに止まるおそれがあった）。
///  ・持ち物の窓は、受け渡しを選んだときに一度だけ開き、品を動かしても作り直されない。
///    だから同じリテイナーの間は、最初に認めた窓（同じアドレス）が出ている限り認め続ける（
///    以前は引き出しを確かめた後に控えを取り直したので、2回目からは自分の持ち物の窓を認めず、30秒で必ず止まっていた）。
/// </summary>
public sealed class RetainerWindows
{
    /// <summary>操作の前後で出たかを見る窓。</summary>
    public static readonly string[] Watched =
        ["RetainerList", "SelectString", "InventoryRetainer", "InventoryRetainerLarge", "ContextMenu", "InputNumeric", "Talk"];

    /// <summary>リテイナーの持ち物の窓（広さの設定で名前が変わる）。</summary>
    public static readonly string[] InventoryNames = ["InventoryRetainer", "InventoryRetainerLarge"];

    private readonly Func<string, bool> visible;
    private readonly Func<string, DateTime, nint> ownedSince;
    private readonly Func<string, nint> ready;
    private readonly Func<DateTime> clock;
    private readonly HashSet<string> openAtAct = [];
    private nint inventory;
    private ulong inventoryFor;

    /// <param name="visible">その名前の窓が出ているか。</param>
    /// <param name="ownedSince">その時刻以降に自分の窓として開いた記録があれば、その窓（無ければ 0）。</param>
    /// <param name="ready">その名前の窓が操作できる状態なら、その窓（無ければ 0）。</param>
    /// <param name="clock">今の時刻（試験で差し替える）。</param>
    public RetainerWindows(Func<string, bool> visible, Func<string, DateTime, nint> ownedSince, Func<string, nint> ready, Func<DateTime>? clock = null)
    {
        this.visible = visible;
        this.ownedSince = ownedSince;
        this.ready = ready;
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>直前の自分の操作の時刻。</summary>
    public DateTime ActedAt { get; private set; } = DateTime.MinValue;

    /// <summary>直前の自分の操作の時点で、その窓が出ていたか。</summary>
    public bool WasOpenAtAct(string name) => this.openAtAct.Contains(name);

    /// <summary>操作の直前に呼ぶ。時刻と、その時点で出ていた窓を控える。</summary>
    public void MarkAct()
    {
        this.ActedAt = this.clock();
        this.openAtAct.Clear();
        foreach (var name in Watched)
            if (this.visible(name))
                this.openAtAct.Add(name);
    }

    /// <summary>直前の自分の操作の後に出た窓（持ち主の記録か、操作の前は出ていなかった窓）。無ければ 0。</summary>
    public nint Fresh(string name)
    {
        var owned = this.ownedSince(name, this.ActedAt);
        if (owned != 0)
            return owned;
        return this.openAtAct.Contains(name) ? 0 : this.ready(name);
    }

    /// <summary>
    /// そのリテイナーの持ち物の窓が、自分の操作で開いていて操作できるか。
    /// 一度認めた窓は、同じリテイナーの間は同じアドレスで出ている限り認める（別の窓に変わったら認めない）。
    /// </summary>
    public bool InventoryOpen(ulong retainer)
    {
        if (this.inventory != 0 && this.inventoryFor == retainer)
        {
            foreach (var name in InventoryNames)
                if (this.ready(name) == this.inventory)
                    return true;
            return false;
        }

        foreach (var name in InventoryNames)
        {
            var addon = this.Fresh(name);
            if (addon == 0)
                continue;
            this.inventory = addon;
            this.inventoryFor = retainer;
            return true;
        }

        return false;
    }

    /// <summary>持ち物の窓を閉じた・リテイナーを替えたときに呼ぶ（次は改めて自分の窓かを見る）。</summary>
    public void ForgetInventory()
    {
        this.inventory = 0;
        this.inventoryFor = 0;
    }
}
