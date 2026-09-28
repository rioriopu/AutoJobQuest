using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;

namespace AutoJobQuest.Ipc;

/// <summary>
/// GBR の本体インスタンスへリフレクションで届く窓口。
///
/// 経路: Dalamud.Service&lt;PluginManager&gt;.Get().InstalledPlugins → LocalPlugin の private フィールド instance。
/// 開発版として読み込まれた場合（LocalDevPlugin）は基底型へ辿らないと見つからない。
///
/// 【GBR が読み直されたとき】型も参照も全部無効になり、OFF の通知も来ない。
/// ActivePluginsChanged では旗を立てるだけにし、使うたびに instance の参照が同じかを確かめる。
/// 型は必ず GBR が読み込んだアセンブリから取る（自分の文脈で解決すると別物になる）。
///
/// すべてフレームワークのスレッドから呼ぶこと（GBR のリストは排他なしの List/Dictionary）。
/// </summary>
public sealed class GbrReflection : IDisposable
{
    public const string InternalName = "GatherBuddyReborn";

    private GbrHandle? handle;
    private volatile bool rescanRequested = true;
    private DateTime nextScan = DateTime.MinValue;

    public GbrReflection()
    {
        Svc.PluginInterface.ActivePluginsChanged += this.OnActivePluginsChanged;
    }

    /// <summary>直近の失敗（画面表示用）。</summary>
    public string? LastError { get; private set; }

    public void Dispose()
        => Svc.PluginInterface.ActivePluginsChanged -= this.OnActivePluginsChanged;

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs e)
    {
        if (e.AffectedInternalNames.Contains(InternalName))
            this.rescanRequested = true;
    }

    /// <summary>GBR の本体を握る。未導入・未読み込みなら null。</summary>
    public GbrHandle? Get()
    {
        if (this.handle != null && !this.rescanRequested && this.handle.StillCurrent())
            return this.handle;

        this.handle = null;
        if (!this.rescanRequested && DateTime.UtcNow < this.nextScan)
            return null;

        this.rescanRequested = false;
        this.nextScan = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var dalamud = Svc.PluginInterface.GetType().Assembly;
            var service = dalamud.GetType("Dalamud.Service`1", throwOnError: true)!;
            var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true)!;
            var pm = service.MakeGenericType(pmType)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null)!;
            var installed = (IEnumerable)pmType
                .GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(pm)!;

            foreach (var entry in installed)
            {
                var et = entry.GetType();
                if (et.GetProperty("InternalName")?.GetValue(entry) as string != InternalName)
                    continue;
                if (et.GetProperty("IsLoaded")?.GetValue(entry) is not true)
                    continue;

                FieldInfo? field = null;
                for (var t = et; t != null && field == null; t = t.BaseType)
                    field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);

                var plugin = field?.GetValue(entry);
                if (field == null || plugin == null)
                    continue;

                this.handle = new GbrHandle(entry, field, plugin);
                this.LastError = null;
                return this.handle;
            }

            this.LastError = "GBR が読み込まれていません";
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の本体に届きませんでした: {ex.GetType().Name}: {ex.Message}";
            Svc.Log.Warning($"[AutoJobQuest] {this.LastError}");
        }

        return null;
    }
}

/// <summary>GBR インスタンス1個ぶんのキャッシュ。GBR が読み直されたら丸ごと捨てる。</summary>
public sealed class GbrHandle
{
    public const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;
    public const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
    public const BindingFlags NonPubInst = BindingFlags.NonPublic | BindingFlags.Instance;

    public readonly object Entry;
    public readonly FieldInfo InstanceField;
    public readonly object Plugin;
    public readonly Type PluginType;
    public readonly Assembly GbrAsm;
    public readonly Assembly GameDataAsm;

    public GbrHandle(object entry, FieldInfo instanceField, object plugin)
    {
        this.Entry = entry;
        this.InstanceField = instanceField;
        this.Plugin = plugin;
        this.PluginType = plugin.GetType();
        this.GbrAsm = this.PluginType.Assembly;
        this.GameDataAsm = this.PluginType.GetProperty("GameData", PubStatic)!.PropertyType.Assembly;
    }

    public bool StillCurrent() => ReferenceEquals(this.InstanceField.GetValue(this.Entry), this.Plugin);

    // ---- よく使う入口 ----

    /// <summary>GatherBuddy.Config.Configuration</summary>
    public object Config => this.PluginType.GetProperty("Config", PubStatic)!.GetValue(null)!;

    /// <summary>GatherBuddy.AutoGather.AutoGatherConfig</summary>
    public object AutoGatherConfig => this.Config.GetType().GetProperty("AutoGatherConfig", PubInst)!.GetValue(this.Config)!;

    /// <summary>GatherBuddy.Config.CollectableConfig</summary>
    public object CollectableConfig => this.Config.GetType().GetProperty("CollectableConfig", PubInst)!.GetValue(this.Config)!;

    /// <summary>GatherBuddy.GameData</summary>
    public object GameData => this.PluginType.GetProperty("GameData", PubStatic)!.GetValue(null)!;

    /// <summary>GatherBuddy.AutoGather.Lists.AutoGatherListsManager（internal フィールド）</summary>
    public object ListsManager => this.PluginType.GetField("AutoGatherListsManager", NonPubInst)!.GetValue(this.Plugin)!;

    /// <summary>GatherBuddy.Vulcan.Vendors.VendorBuyListManager</summary>
    public object VendorBuyListManager => this.PluginType.GetProperty("VendorBuyListManager", PubStatic)!.GetValue(null)!;

    public void SaveConfig()
        => this.Config.GetType().GetMethod("Save", PubInst, null, Type.EmptyTypes, null)!.Invoke(this.Config, null);

    public bool GetAutoGatherBool(string name)
        => (bool)this.AutoGatherConfig.GetType().GetProperty(name, PubInst)!.GetValue(this.AutoGatherConfig)!;

    public void SetAutoGatherBool(string name, bool value)
        => this.AutoGatherConfig.GetType().GetProperty(name, PubInst)!.SetValue(this.AutoGatherConfig, value);

    /// <summary>
    /// アイテム ID から GBR の IGatherable（採集品か魚）を引く。
    /// 【罠】キーは uint。int で引くと例外にならず null が返る（実測）。
    /// </summary>
    public object? FindGatherable(uint itemId)
    {
        var gd = this.GameData;
        var gatherables = (IDictionary)gd.GetType().GetProperty("Gatherables", PubInst)!.GetValue(gd)!;
        var fishes = (IDictionary)gd.GetType().GetProperty("Fishes", PubInst)!.GetValue(gd)!;
        object key = itemId;
        return (gatherables.Contains(key) ? gatherables[key] : null)
               ?? (fishes.Contains(key) ? fishes[key] : null);
    }

    /// <summary>採集品・魚の場所一覧（ILocation）。</summary>
    public List<object> Locations(object gatherable)
        => ((IEnumerable)gatherable.GetType().GetProperty("Locations", PubInst)!.GetValue(gatherable)!).Cast<object>().ToList();

    /// <summary>場所のエリア（TerritoryType の行）。</summary>
    public static uint TerritoryOf(object location)
    {
        var terr = location.GetType().GetProperty("Territory", PubInst)!.GetValue(location)!;
        return (uint)terr.GetType().GetProperty("Id", PubInst)!.GetValue(terr)!;
    }

    /// <summary>魚か（IGatherable.Type：1=Gatherable, 2=Fish）。</summary>
    public static bool IsFish(object gatherable)
        => gatherable.GetType().FullName == "GatherBuddy.Classes.Fish";
}
