using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// 自分の操作が開かせた画面を記録する。
///
/// 名前で画面を引くだけでは、誰が開いたのか分からない。「名前で見つかったから閉じる・押す」をやると、
/// 他のプラグインの自動操作や、利用者が手で開いた画面を壊す。
///
///  ・<see cref="IsClaiming"/> が true の間に開いた（PostSetup）画面のアドレスと時刻だけを記録する。
///  ・PreFinalize は非表示になっただけでは来ないので、古いアドレスが別の画面に使い回される恐れがある。
///    そのため一定時間で失効させ、判定では「アドレス一致」かつ「いまその名前で引いた画面が操作できる」を両方求める。
///  ・確認ダイアログを押してよいかは <see cref="TryGetOwnedSince"/>（撃った時刻より後に開いたもの）で絞る。
///    自分の操作は移動や会話を含めて何分も続くので、その間に利用者が出した確認まで自分のものになるのを防ぐ。
/// </summary>
public sealed unsafe class AddonOwnership : IDisposable
{
    private static readonly string[] Tracked =
    [
        "InclusionShop", "ShopExchangeItemDialog", "ShopExchangeCurrency", "ShopExchangeCurrencyDialog", "CollectablesShop",
        "SelectYesno", "SelectString", "SelectIconString", "Talk",
        "PurifyResult", // 精選の結果（自分の精選で出たものだけ閉じる）
        "RetainerList", "InventoryRetainer", "InventoryRetainerLarge", "ContextMenu", "InputNumeric",
        "Request",      // クエストの納品窓（自分が始めたクエストの間に開いたものだけ入れる）
    ];

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    // アドレス → （開いた時刻〔変えない〕, 期限の延長に使う時刻）。
    // 開いた時刻を延長のたびに書き換えると、撃つ前から開いていた窓が「撃った後に開いた窓」として通り、
    // 納品窓では同じ窓を別の窓と取り違えるので、2つを分けて持つ
    private readonly Dictionary<nint, (DateTime OpenedAt, DateTime Touched)> owned = [];
    private bool registered;

    /// <summary>画面の開閉の知らせを受け取れているか（false なら、自分が開いた画面を見分けられない。事前点検で止める）。</summary>
    public bool Registered => this.registered;
    private bool claiming;

    public AddonOwnership()
    {
        try
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Tracked, this.OnPostSetup);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Tracked, this.OnPreFinalize);
            this.registered = true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoJobQuest] 画面の持ち主を記録する仕組みを登録できませんでした");
        }
    }

    /// <summary>自分の操作が進行中か。true の間に開いた画面だけを「自分のもの」として記録する。</summary>
    public bool IsClaiming
    {
        get => this.claiming;
        set
        {
            if (this.claiming != value)
                Core.DebugLog.Current?.Line("画面", value ? "ここから開いた画面を自分のものとして記録します" : "画面の記録を止めます");
            this.claiming = value;
        }
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!this.claiming)
            return;
        var now = DateTime.UtcNow;
        this.owned[args.Addon.Address] = (now, now);
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args) => this.owned.Remove(args.Addon.Address);

    /// <summary>その名前の画面が開いていて、自分が開いたものなら true。自分のものでなければ触らない。</summary>
    public bool TryGetOwned(string name, out AtkUnitBase* addon)
    {
        addon = null;
        if (!GameUi.IsReady(name, out var candidate))
            return false;

        var address = (nint)candidate;
        if (!this.owned.TryGetValue(address, out var rec))
            return false;

        if (DateTime.UtcNow - rec.Touched > Lifetime)
        {
            if (this.claiming)
            {
                // 自分の操作が続いていて同じ画面が開いたままなら延長する（長い操作の途中で持ち主を失わない）。
                // 開いた時刻は変えない
                this.owned[address] = (rec.OpenedAt, DateTime.UtcNow);
            }
            else
            {
                this.owned.Remove(address);
                return false;
            }
        }

        addon = candidate;
        return true;
    }

    /// <summary>
    /// その名前の画面が <paramref name="sinceUtc"/> より後に開いた自分のものなら true。
    /// 押してよいのは「いま撃った操作の結果として出たもの」だけ、というときに使う（記録は延長しない）。
    /// </summary>
    public bool TryGetOwnedSince(string name, DateTime sinceUtc, out AtkUnitBase* addon)
        => this.TryGetOwnedSince(name, sinceUtc, out addon, out _);

    /// <summary>上と同じ。開いた時刻（PostSetup の時刻）も返す（同じアドレスで開き直した窓を別の窓として扱うため）。</summary>
    public bool TryGetOwnedSince(string name, DateTime sinceUtc, out AtkUnitBase* addon, out DateTime openedAt)
    {
        addon = null;
        openedAt = DateTime.MinValue;
        if (!GameUi.IsReady(name, out var candidate))
            return false;
        if (!this.owned.TryGetValue((nint)candidate, out var rec) || rec.OpenedAt < sinceUtc)
            return false;

        addon = candidate;
        openedAt = rec.OpenedAt;
        return true;
    }

    /// <summary>記録を捨てて、記録も止める（作業の終わりに呼ぶ）。</summary>
    public void Clear()
    {
        this.owned.Clear();
        this.IsClaiming = false;
    }

    public void Dispose()
    {
        if (!this.registered)
            return;

        try
        {
            Svc.AddonLifecycle.UnregisterListener(this.OnPostSetup);
            Svc.AddonLifecycle.UnregisterListener(this.OnPreFinalize);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoJobQuest] 画面の持ち主の記録を解除できませんでした");
        }

        this.registered = false;
        this.owned.Clear();
    }
}
