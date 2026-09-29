using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace AutoJobQuest.Ipc;

/// <summary>
/// RSR（RotationSolverReborn）の「今の動作モード」を、RSR の内部の状態から読む（
/// RSR をこちらで使ったあと、使う前のモードに戻す）。
///
/// 【なぜ内部を読むか】RSR の IPC には、モードを切り替える口（ChangeOperatingMode）はあるが、今のモードを読む口が無い
/// （AutorotationActive は「動作中か」だけ：RSR の IPCProvider.cs:274）。そのため以前は、使い終わったら Off に戻すしかなかった。
/// モードは RSR の内部の静的な状態 RotationSolver.Basic.DataCenter の6つの旗
/// （State・IsManual・IsTargetOnly・IsAutoDuty・IsHenched・IsPvPStateEnabled）で決まる
/// （RSR の RSCommands_StateSpecialCommand.cs の UpdateState で、モードごとに立てる旗を確認。導入版 7.5.6.11 の DLL にも名前がある）。
///
/// 読むだけで、書き換えはしない（切り替えは IPC で行う）。読めなければ null（呼び出し側は「元のモード不明」として扱う）。
/// 本体への届き方は GBR と同じ（Dalamud の PluginManager → LocalPlugin の instance。GbrReflection を参照）。
/// </summary>
public static class RsrStateReader
{
    public const string InternalName = "RotationSolver";

    /// <summary>直近の失敗（記録用）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>今のモード（RSR の StateCommandType の値：Off=0, Auto=1, TargetOnly=2, Manual=3, AutoDuty=4, Henched=5, PvP=6）。読めなければ null。</summary>
    public static byte? ReadMode()
    {
        try
        {
            var plugin = FindPluginInstance(InternalName);
            if (plugin == null)
            {
                LastError = "RSR が読み込まれていません";
                return null;
            }

            // DataCenter は RotationSolver.Basic.dll にある。RSR が読み込んだものを使う（自分の文脈で解決すると別物になる）
            var alc = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
            var basic = alc?.Assemblies.FirstOrDefault(a => a.GetName().Name == "RotationSolver.Basic");
            var dc = basic?.GetType("RotationSolver.Basic.DataCenter", throwOnError: false);
            if (dc == null)
            {
                LastError = "RSR の内部の状態（RotationSolver.Basic.DataCenter）が見つかりません（RSR の版が変わった可能性）";
                return null;
            }

            bool Flag(string name)
                => dc.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool b
                    ? b
                    : throw new MissingMemberException("RotationSolver.Basic.DataCenter", name);

            LastError = null;
            return ModeFromFlags(Flag("State"), Flag("IsManual"), Flag("IsTargetOnly"), Flag("IsAutoDuty"), Flag("IsHenched"), Flag("IsPvPStateEnabled"));
        }
        catch (Exception ex)
        {
            LastError = $"RSR の今のモードを読めません: {ex.GetType().Name}: {ex.Message}";
            Core.DebugLog.Current?.Line("IPC", LastError);
            return null;
        }
    }

    /// <summary>
    /// 今の範囲攻撃の設定（RSR の AoEType：Off=0, Cleave=1, Full=2）。読めなければ null。
    /// RSR の設定は RotationSolver.Basic.Service.Config（Service は internal、Config は public static）の AoEType
    /// （RSR の Basic/Service.cs・Configuration/Configs.cs・ConfigTypes.cs で確認）。読むだけで、書き換えは IPC で行う。
    /// </summary>
    public static byte? ReadAoeType()
    {
        try
        {
            var plugin = FindPluginInstance(InternalName);
            if (plugin == null)
            {
                LastError = "RSR が読み込まれていません";
                return null;
            }

            var alc = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
            var basic = alc?.Assemblies.FirstOrDefault(a => a.GetName().Name == "RotationSolver.Basic");
            var service = basic?.GetType("RotationSolver.Basic.Service", throwOnError: false);
            var config = service?.GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var value = config?.GetType().GetProperty("AoEType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config);
            if (value == null || !value.GetType().IsEnum)
            {
                LastError = "RSR の範囲攻撃の設定（Service.Config.AoEType）が見つかりません（RSR の版が変わった可能性）";
                return null;
            }

            return Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LastError = $"RSR の範囲攻撃の設定を読めません: {ex.GetType().Name}: {ex.Message}";
            Core.DebugLog.Current?.Line("IPC", LastError);
            return null;
        }
    }

    /// <summary>
    /// RSR の真偽の設定（ConditionBoolean の Value）を読む。読めなければ null。
    /// RSR の設定の [ConditionBool] の欄は、Service.Config に同じ名前の ConditionBoolean のプロパティとして出る
    /// （RSR の Configuration/Configs.cs・ConditionBoolean.cs。IPC の設定コマンド「Settings 名前 値」も同じプロパティを書き換える：
    /// RSCommands_OtherCommand.cs の UpdateSetting）。読むだけで、書き換えは IPC で行う。
    /// </summary>
    public static bool? ReadBool(string name)
    {
        try
        {
            var plugin = FindPluginInstance(InternalName);
            if (plugin == null)
            {
                LastError = "RSR が読み込まれていません";
                return null;
            }

            var alc = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
            var basic = alc?.Assemblies.FirstOrDefault(a => a.GetName().Name == "RotationSolver.Basic");
            var service = basic?.GetType("RotationSolver.Basic.Service", throwOnError: false);
            var config = service?.GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var cb = config?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(config);
            var value = cb?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(cb);
            if (value is bool b)
                return b;

            LastError = $"RSR の設定 {name}（Service.Config.{name}.Value）が見つかりません（RSR の版が変わった可能性）";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"RSR の設定 {name} を読めません: {ex.GetType().Name}: {ex.Message}";
            Core.DebugLog.Current?.Line("IPC", LastError);
            return null;
        }
    }

    /// <summary>導入版7.5.6.12のDataCenter.TargetFreelyOverride（静的Boolean）を読むだけ。</summary>
    public static bool? ReadTargetFreelyOverride()
    {
        try
        {
            var plugin = FindPluginInstance(InternalName);
            if (plugin == null)
                return null;
            var basic = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly)?.Assemblies
                .FirstOrDefault(a => a.GetName().Name == "RotationSolver.Basic");
            return basic?.GetType("RotationSolver.Basic.DataCenter")?
                .GetProperty("TargetFreelyOverride", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
        }
        catch { return null; }
    }

    /// <summary>範囲攻撃の設定の名前（RSR の AoEType の名前。IPC の設定コマンドにもこの名前で渡す）。</summary>
    public static string AoeName(byte value) => value switch
    {
        0 => "Off",
        1 => "Cleave",
        2 => "Full",
        _ => $"不明({value})",
    };

    /// <summary>
    /// 6つの旗からモードを決める（RSR の UpdateState の裏返し）。Henched は IsManual も立つので、Manual より先に見る。
    /// </summary>
    public static byte ModeFromFlags(bool state, bool manual, bool targetOnly, bool autoDuty, bool henched, bool pvp)
    {
        if (!state)
            return 0; // Off
        if (pvp)
            return 6; // PvP
        if (henched)
            return 5; // Henched
        if (autoDuty)
            return 4; // AutoDuty
        if (targetOnly)
            return 2; // TargetOnly
        if (manual)
            return 3; // Manual
        return 1; // Auto
    }

    /// <summary>モードの表示名（記録・画面用）。</summary>
    public static string ModeName(byte mode) => mode switch
    {
        0 => "Off",
        1 => "Auto",
        2 => "TargetOnly",
        3 => "Manual",
        4 => "AutoDuty",
        5 => "Henched",
        6 => "PvP",
        _ => $"不明({mode})",
    };

    /// <summary>読み込まれているプラグインの本体（見つからなければ null）。GbrReflection と同じ経路。Artisan の内部を読むとき（ArtisanHqEstimate）も使う。</summary>
    internal static object? FindPluginInstance(string internalName)
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
            if (et.GetProperty("InternalName")?.GetValue(entry) as string != internalName)
                continue;
            if (et.GetProperty("IsLoaded")?.GetValue(entry) is not true)
                continue;

            FieldInfo? field = null;
            for (var t = et; t != null && field == null; t = t.BaseType)
                field = t.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(entry) is { } plugin)
                return plugin;
        }

        return null;
    }
}
