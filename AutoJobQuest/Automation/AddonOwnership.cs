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
    ];

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Dictionary<nint, DateTime> owned = [];
    private bool registered;
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
        this.owned[args.Addon.Address] = DateTime.UtcNow;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args) => this.owned.Remove(args.Addon.Address);

    /// <summary>その名前の画面が開いていて、自分が開いたものなら true。自分のものでなければ触らない。</summary>
    public bool TryGetOwned(string name, out AtkUnitBase* addon)
    {
        addon = null;
        if (!GameUi.IsReady(name, out var candidate))
            return false;

        var address = (nint)candidate;
        if (!this.owned.TryGetValue(address, out var at))
            return false;

        if (DateTime.UtcNow - at > Lifetime)
        {
            if (this.claiming)
            {
                // 自分の操作が続いていて同じ画面が開いたままなら延長する（長い操作の途中で持ち主を失わない）
                this.owned[address] = DateTime.UtcNow;
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
    {
        addon = null;
        if (!GameUi.IsReady(name, out var candidate))
            return false;
        if (!this.owned.TryGetValue((nint)candidate, out var at) || at < sinceUtc)
            return false;

        addon = candidate;
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
