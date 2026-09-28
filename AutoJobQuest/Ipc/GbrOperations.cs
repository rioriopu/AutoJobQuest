using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AutoJobQuest.Ipc;

/// <summary>GBR の自動採集リストに入れる1品目。</summary>
/// <param name="ItemId">アイテム ID。</param>
/// <param name="TargetOwned">「持っていたい総数」（GBR は所持数 &lt; 目標の間だけ採る）。</param>
/// <param name="PreferredTerritory">優先して採るエリア（null なら GBR に任せる）。</param>
public sealed record GbrGatherEntry(uint ItemId, uint TargetOwned, uint? PreferredTerritory);

/// <summary>
/// GBR をリフレクションで操作する（採集リスト・設定の一時変更・NPC 購入）。
///
/// 手本は GBR 自身の「一時リスト」の作法（GBR CraftingGatherBridge.cs:229-297, 423-436）:
///   他の有効リストを無効にして覚える → 自分のリストを作って品目を入れる → UsesRetainerInventory=false
///   → Enabled=true → Save() → SetActiveItems(false) → IPC で ON。
///   戻すときは逆順。
///
/// 【控えを残す】GBR の Save() で一時状態がファイルに残る。途中でゲームが落ちると利用者のリストが
/// 無効のままになるので、無効にしたリスト名と書き換えた設定の元の値をこちらの設定に保存してから変える。
/// 次回起動時に <see cref="RestoreLeftovers"/> で戻す。
/// </summary>
public sealed class GbrOperations
{
    public const string OwnListName = "AutoJobQuest";

    private readonly GbrReflection reflection;
    private readonly Configuration config;

    // 今回の実行で無効にしたリスト（参照）。GBR が読み直されたら名前で探し直す。
    private readonly List<object> disabledByMe = [];

    public GbrOperations(GbrReflection reflection, Configuration config)
    {
        this.reflection = reflection;
        this.config = config;
    }

    public string? LastError { get; private set; }

    // ------------------------------------------------------------------
    // 事前点検（読むだけ）

    /// <summary>GBR の設定を読む。読めなければ null。</summary>
    public bool? ReadAutoGatherBool(string name)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            return h.GetAutoGatherBool(name);
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の設定 {name} を読めません: {ex.Message}";
            return null;
        }
    }

    /// <summary>収集品を自動で納品しに行く設定か（true だと採った収集品を勝手に納品しに行く）。</summary>
    public bool? ReadAutoTurnInCollectables()
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var cc = h.CollectableConfig;
            return (bool)cc.GetType().GetProperty("AutoTurnInCollectables", GbrHandle.PubInst)!.GetValue(cc)!;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の収集品設定を読めません: {ex.Message}";
            return null;
        }
    }

    /// <summary>GBR がその品目を扱えるか（採集品か魚で、場所が1件以上）。扱えるならエリアの一覧を返す。</summary>
    public List<uint>? GatherableTerritories(uint itemId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var g = h.FindGatherable(itemId);
            if (g == null)
                return [];
            return h.Locations(g).Select(GbrHandle.TerritoryOf).Distinct().ToList();
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の品目データを読めません（{itemId}）: {ex.Message}";
            return null;
        }
    }

    /// <summary>その品目が GBR で魚として扱われるか。</summary>
    public bool? IsFish(uint itemId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var g = h.FindGatherable(itemId);
            return g != null && GbrHandle.IsFish(g);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 設定の一時変更

    /// <summary>
    /// 自動採集の設定を一時的に書き換える。元の値は控えに残す。
    /// 例: GoHomeWhenDone / GoHomeWhenIdle を false（終わったときに勝手に帰宅＝テレポしないように）。
    /// </summary>
    public bool OverrideBool(string name, bool value)
    {
        var h = this.reflection.Get();
        if (h == null)
        {
            this.LastError = "GBR に届きません";
            return false;
        }

        try
        {
            var current = h.GetAutoGatherBool(name);
            if (current == value)
                return true;

            // 先に控えを保存してから書き換える（落ちても戻せるように）
            if (!this.config.GbrConfigOriginals.ContainsKey(name))
            {
                this.config.GbrConfigOriginals[name] = current;
                this.config.Save();
            }

            h.SetAutoGatherBool(name, value);
            h.SaveConfig();
            return h.GetAutoGatherBool(name) == value;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の設定 {name} を変えられません: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 一時的に書き換えた設定を戻す。今の値が「こちらが入れた値」のままのときだけ戻す
    /// （途中で利用者が変えたら、そちらを尊重する）。
    /// </summary>
    public void RestoreConfig()
    {
        if (this.config.GbrConfigOriginals.Count == 0)
            return;

        var h = this.reflection.Get();
        if (h == null)
            return; // 次回起動時に戻す

        try
        {
            foreach (var (name, original) in this.config.GbrConfigOriginals.ToList())
            {
                if (h.GetAutoGatherBool(name) == !original)
                    h.SetAutoGatherBool(name, original);
                this.config.GbrConfigOriginals.Remove(name);
            }

            h.SaveConfig();
            this.config.Save();
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の設定を戻せませんでした（次回起動時に再試行します）: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // 自動採集リスト

    /// <summary>
    /// 専用リスト「AutoJobQuest」を作り直し、他の有効リストを退避して、自分のリストだけを有効にする。
    /// ここではまだ自動採集を ON にしない（呼び出し側が IPC で ON にする）。
    /// </summary>
    public bool PrepareGatherList(IReadOnlyList<GbrGatherEntry> entries, out List<uint> unsupported)
    {
        unsupported = [];
        var h = this.reflection.Get();
        if (h == null)
        {
            this.LastError = "GBR に届きません";
            return false;
        }

        try
        {
            var r = new ListReflection(h);
            var list = r.FindOwnList() ?? r.CreateOwnList();

            // 前回の品目を全部消す
            var items = (IList)r.PItems.GetValue(list)!;
            for (var i = items.Count - 1; i >= 0; i--)
                r.MListRemoveAt.Invoke(list, [i]);

            foreach (var e in entries)
            {
                var g = h.FindGatherable(e.ItemId);
                if (g == null)
                {
                    unsupported.Add(e.ItemId);
                    continue;
                }

                if (!(bool)r.MListAdd.Invoke(list, [g, e.TargetOwned])!)
                    r.MListSetQty.Invoke(list, [g, e.TargetOwned]);

                if (e.PreferredTerritory is { } terr)
                {
                    var chosen = h.Locations(g).FirstOrDefault(l => GbrHandle.TerritoryOf(l) == terr);
                    if (chosen != null)
                        r.MChangePref.Invoke(r.Manager, [list, g, chosen]);
                }
            }

            // 手持ちだけで数えさせる（保存されない値なので毎回入れる）
            r.PUsesRetainer.SetValue(list, false);

            // 他の有効リストを退避（控えを保存してから）
            foreach (var l in r.AllLists())
            {
                if (ReferenceEquals(l, list))
                    continue;
                if (!(bool)r.PEnabled.GetValue(l)!)
                    continue;

                var name = (string?)r.PName.GetValue(l) ?? string.Empty;
                if (!this.config.GbrDisabledLists.Contains(name))
                    this.config.GbrDisabledLists.Add(name);
                this.config.Save();

                r.PEnabled.SetValue(l, false);
                this.disabledByMe.Add(l);
            }

            r.PEnabled.SetValue(list, true);
            this.config.GbrOwnListActive = true;
            this.config.Save();

            r.MSave.Invoke(r.Manager, null);
            r.MSetActive.Invoke(r.Manager, [false]);
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の採集リストを用意できませんでした: {Unwrap(ex)}";
            return false;
        }
    }

    /// <summary>自分のリストを無効にし、退避したリストを戻す。</summary>
    public void RestoreGatherLists()
    {
        if (!this.config.GbrOwnListActive && this.config.GbrDisabledLists.Count == 0)
            return;

        var h = this.reflection.Get();
        if (h == null)
            return; // 次回起動時に戻す

        try
        {
            var r = new ListReflection(h);
            if (r.FindOwnList() is { } own)
            {
                // 品目も消しておく（利用者が GBR の画面で見たときに紛らわしくないように）
                var items = (IList)r.PItems.GetValue(own)!;
                for (var i = items.Count - 1; i >= 0; i--)
                    r.MListRemoveAt.Invoke(own, [i]);
                r.PEnabled.SetValue(own, false);
            }

            var alive = r.AllLists().ToList();
            foreach (var l in this.disabledByMe)
            {
                if (alive.Any(x => ReferenceEquals(x, l)) && !(bool)r.PEnabled.GetValue(l)!)
                    r.PEnabled.SetValue(l, true);
            }

            // GBR が読み直されて参照が無効になった場合や、前回落ちた場合は名前で探して戻す
            foreach (var name in this.config.GbrDisabledLists)
            {
                foreach (var l in alive.Where(x => (string?)r.PName.GetValue(x) == name))
                    if (!(bool)r.PEnabled.GetValue(l)!)
                        r.PEnabled.SetValue(l, true);
            }

            r.MSetActive.Invoke(r.Manager, [false]);
            r.MSave.Invoke(r.Manager, null);

            this.disabledByMe.Clear();
            this.config.GbrDisabledLists.Clear();
            this.config.GbrOwnListActive = false;
            this.config.Save();
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の採集リストを戻せませんでした（次回起動時に再試行します）: {Unwrap(ex)}";
        }
    }

    /// <summary>前回落ちたときに残った一時変更を戻す（起動後、GBR に届いたら1回）。</summary>
    public bool RestoreLeftovers()
    {
        if (!this.config.GbrOwnListActive && this.config.GbrDisabledLists.Count == 0 && this.config.GbrConfigOriginals.Count == 0)
            return true;

        if (this.reflection.Get() == null)
            return false;

        this.RestoreGatherLists();
        this.RestoreConfig();
        return true;
    }

    // ------------------------------------------------------------------
    // NPC 購入（VendorBuyListManager）

    /// <summary>
    /// NPC 購入の専用リストを用意する。ギルの店で買えるものだけを入れる。
    /// 戻り値のリスト ID を <see cref="StartVendor"/> に渡す。
    /// </summary>
    public Guid? PrepareVendorList(IReadOnlyList<(uint ItemId, uint TargetOwned)> entries, out List<uint> notGil)
    {
        notGil = [];
        var h = this.reflection.Get();
        if (h == null)
        {
            this.LastError = "GBR に届きません";
            return null;
        }

        try
        {
            var vblm = h.VendorBuyListManager;
            var vt = vblm.GetType();

            if ((bool)vt.GetProperty("IsBusy", GbrHandle.PubInst)!.GetValue(vblm)!)
            {
                this.LastError = "GBR の購入が別に動いています";
                return null;
            }

            var def = ((IEnumerable)vt.GetProperty("Lists", GbrHandle.PubInst)!.GetValue(vblm)!).Cast<object>()
                .FirstOrDefault(d => string.Equals(d.GetType().GetProperty("Name")!.GetValue(d) as string, OwnListName, StringComparison.OrdinalIgnoreCase));
            def ??= vt.GetMethod("CreateList", GbrHandle.PubInst, null, [typeof(string), typeof(bool)], null)!
                .Invoke(vblm, [OwnListName, false]);
            var listId = (Guid)def!.GetType().GetProperty("Id")!.GetValue(def)!;

            var entryList = (IList)def.GetType().GetProperty("Entries")!.GetValue(def)!;
            var removeEntry = vt.GetMethod("RemoveEntry", GbrHandle.PubInst, null, [typeof(Guid)], null)!;

            // 前回の品目を消す
            foreach (var e in entryList.Cast<object>().ToList())
                removeEntry.Invoke(vblm, [(Guid)e.GetType().GetProperty("Id")!.GetValue(e)!]);

            var hasGilRoute = this.GilRouteChecker(h);
            var trySet = vt.GetMethod("TrySetTarget", GbrHandle.PubInst, null,
                [typeof(Guid), typeof(uint), typeof(uint), typeof(bool), typeof(bool), typeof(bool)], null)!;

            foreach (var (itemId, target) in entries)
            {
                if (hasGilRoute != null && !hasGilRoute(itemId))
                {
                    notGil.Add(itemId);
                    continue;
                }

                // openWindow:false（既定の true だと GBR の窓が開く）
                var ok = (bool)trySet.Invoke(vblm, [listId, itemId, target, false, false, false])!;
                if (!ok)
                    notGil.Add(itemId);
            }

            // 事後確認：ギルの店（ShopType==0）以外に落ちた品目は外す
            foreach (var e in entryList.Cast<object>().ToList())
            {
                var et = e.GetType();
                var shopType = Convert.ToInt32(et.GetProperty("ShopType")!.GetValue(e));
                if (shopType == 0)
                    continue;

                var itemId = (uint)et.GetProperty("ItemId")!.GetValue(e)!;
                notGil.Add(itemId);
                removeEntry.Invoke(vblm, [(Guid)et.GetProperty("Id")!.GetValue(e)!]);
            }

            return listId;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の購入リストを用意できませんでした: {Unwrap(ex)}";
            return null;
        }
    }

    /// <summary>購入を始める。戻り値は GBR の StartResult の名前（失敗時は null）。</summary>
    public string? StartVendor(Guid listId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var vblm = h.VendorBuyListManager;
            var start = vblm.GetType().GetMethod("Start", GbrHandle.PubInst)!;
            return start.Invoke(vblm, [listId, null])?.ToString();
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の購入を始められませんでした: {Unwrap(ex)}";
            return null;
        }
    }

    /// <summary>購入処理中か（店を閉じる待ち・中止待ちも含む）。読めなければ null。</summary>
    public bool? VendorIsBusy()
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var vblm = h.VendorBuyListManager;
            return (bool)vblm.GetType().GetProperty("IsBusy", GbrHandle.PubInst)!.GetValue(vblm)!;
        }
        catch
        {
            return null;
        }
    }

    public string VendorStatusText()
    {
        var h = this.reflection.Get();
        if (h == null)
            return string.Empty;
        try
        {
            var vblm = h.VendorBuyListManager;
            return vblm.GetType().GetProperty("StatusText", GbrHandle.PubInst)!.GetValue(vblm) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>購入を止める（こちらが始めた購入のときだけ）。</summary>
    public void StopVendor()
    {
        var h = this.reflection.Get();
        if (h == null)
            return;
        try
        {
            var vblm = h.VendorBuyListManager;
            vblm.GetType().GetMethod("Stop", GbrHandle.PubInst, null, Type.EmptyTypes, null)!.Invoke(vblm, null);
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の購入を止められませんでした: {Unwrap(ex)}";
        }
    }

    /// <summary>
    /// 「ギルの店で、自動購入に対応した NPC から買えるか」を判定する関数を作る。
    /// 店データがまだ読み込み中なら null（判定せず、事後確認だけに頼る）。
    /// </summary>
    private Func<uint, bool>? GilRouteChecker(GbrHandle h)
    {
        var resolver = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorShopResolver", throwOnError: false);
        var vpm = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorPurchaseManager", throwOnError: false);
        var excl = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorDevExclusions", throwOnError: false);
        if (resolver == null || vpm == null || excl == null)
            return null;

        if (resolver.GetProperty("IsInitialized", GbrHandle.PubStatic)?.GetValue(null) is not true)
            return null;

        var isSupported = vpm.GetMethod("IsPurchaseSupported", GbrHandle.PubStatic);
        var isExcluded = excl.GetMethod("IsExcluded", GbrHandle.PubStatic);
        var entries = resolver.GetProperty("GilShopEntries", GbrHandle.PubStatic)?.GetValue(null) as IEnumerable;
        if (isSupported == null || isExcluded == null || entries == null)
            return null;

        var list = entries.Cast<object>().ToList();
        return itemId => list.Any(e =>
            (uint)e.GetType().GetProperty("ItemId")!.GetValue(e)! == itemId
            && ((IEnumerable)e.GetType().GetProperty("Npcs")!.GetValue(e)!).Cast<object>()
                .Any(n => (bool)isSupported.Invoke(null, [e, n])! && !(bool)isExcluded.Invoke(null, [n])!));
    }

    private static string Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
            ex = inner;
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>自動採集リストまわりのメンバー。</summary>
    private sealed class ListReflection
    {
        public readonly object Manager;
        public readonly Type ListType;
        public readonly MethodInfo MAddList;
        public readonly MethodInfo MSave;
        public readonly MethodInfo MSetActive;
        public readonly MethodInfo MChangePref;
        public readonly PropertyInfo PLists;
        public readonly MethodInfo MListAdd;
        public readonly MethodInfo MListSetQty;
        public readonly MethodInfo MListRemoveAt;
        public readonly PropertyInfo PItems;
        public readonly PropertyInfo PName;
        public readonly PropertyInfo PDescription;
        public readonly PropertyInfo PEnabled;
        public readonly PropertyInfo PUsesRetainer;

        public ListReflection(GbrHandle h)
        {
            this.Manager = h.ListsManager;
            var mgrType = this.Manager.GetType();
            this.ListType = h.GbrAsm.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList", throwOnError: true)!;
            var iGatherable = h.GameDataAsm.GetType("GatherBuddy.Interfaces.IGatherable", throwOnError: true)!;
            var iLocation = h.GameDataAsm.GetType("GatherBuddy.Interfaces.ILocation", throwOnError: true)!;

            this.MAddList = mgrType.GetMethod("AddList", GbrHandle.PubInst)!;
            this.MSave = M(mgrType, "Save");
            this.MSetActive = M(mgrType, "SetActiveItems", typeof(bool));
            this.MChangePref = M(mgrType, "ChangePreferredLocation", this.ListType, iGatherable, iLocation);
            this.PLists = mgrType.GetProperty("Lists", GbrHandle.PubInst)!;

            this.MListAdd = M(this.ListType, "Add", iGatherable, typeof(uint));
            this.MListSetQty = M(this.ListType, "SetQuantity", iGatherable, typeof(uint));
            this.MListRemoveAt = M(this.ListType, "RemoveAt", typeof(int));
            this.PItems = this.ListType.GetProperty("Items", GbrHandle.PubInst)!;
            this.PName = this.ListType.GetProperty("Name", GbrHandle.PubInst)!;
            this.PDescription = this.ListType.GetProperty("Description", GbrHandle.PubInst)!;
            this.PEnabled = this.ListType.GetProperty("Enabled", GbrHandle.PubInst)!;
            this.PUsesRetainer = this.ListType.GetProperty("UsesRetainerInventory", GbrHandle.NonPubInst)!;
        }

        public IEnumerable<object> AllLists() => ((IEnumerable)this.PLists.GetValue(this.Manager)!).Cast<object>();

        public object? FindOwnList()
            => this.AllLists().FirstOrDefault(l => string.Equals((string?)this.PName.GetValue(l), OwnListName, StringComparison.Ordinal));

        public object CreateOwnList()
        {
            var list = Activator.CreateInstance(this.ListType)!;
            this.PName.SetValue(list, OwnListName);
            this.PDescription.SetValue(list, "AutoJobQuest が管理（手で編集しないでください）");
            this.PEnabled.SetValue(list, false);
            this.MAddList.Invoke(this.Manager, [list, null]);
            return list;
        }

        private static MethodInfo M(Type t, string name, params Type[] ps)
            => t.GetMethod(name, GbrHandle.PubInst, null, ps, null) ?? throw new MissingMethodException(t.FullName, name);
    }
}
