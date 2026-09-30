using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Interface;

namespace AutoJobQuest.Ipc;

/// <summary>必須プラグイン1つ。</summary>
/// <param name="Internal">InternalName（どれか1つでよいものは「|」で並べる）。</param>
/// <param name="Display">画面の名前。</param>
/// <param name="Why">用途。</param>
/// <param name="RepoUrl">入れるときのリポジトリ（null＝Dalamud の公式）。実機の導入元（manifest の InstalledFromUrl）で確かめた URL。</param>
public sealed record RequiredPlugin(string Internal, string Display, string Why, string? RepoUrl);

/// <summary>
/// 必須プラグインの導入状況を調べ、ワンクリックで入れる（「必須プラグイン」タブ。
/// Questionable の設定の「依存関係」タブ・AutoDuty の情報タブと同じ仕組み）。
///
/// Dalamud の公開の窓口（IDalamudPluginInterface）には、一覧を読む（InstalledPlugins）と画面を開く（OpenPluginInstallerTo）しか無い。
/// 入れるには、Questionable（Windows\ConfigComponents\PluginConfigComponent.cs:660-731）と同じく、Dalamud の内部の
/// PluginManager.InstallPluginAsync をリフレクションで呼ぶ。手順：
///  1) Dalamud の読み込み済みの一覧（PluginManager.AvailablePlugins）から、InternalName と API レベルが合い、リポジトリの URL が
///     一致する目録を選ぶ（読み込み済みの本物のリポジトリから選ぶので、入れた後に「リポジトリが無い（孤立）」にならない）。
///  2) 見つからず、リポジトリが未登録なら、登録（利用者がボタンを押したときだけ）→ 設定の保存 → リポジトリの読み直しを待つ → 1) をもう一度。
///  3) InstallPluginAsync を引数名で呼ぶ（Dalamud の版で引数の数が違う：api15-rollup は4つ・master と estell 版は3つ）。
///  4) どこかで失敗したら、Dalamud のプラグイン画面をその名前で開く（手で入れてもらう）。
/// Dalamud の内部は予告なく変わるので、壊れたら 4) に落ちる。
/// </summary>
public sealed class PluginInstaller
{
    public static readonly RequiredPlugin[] Required =
    [
        new("Artisan", "Artisan", "製作", "https://love.puni.sh/ment.json"),
        new("GatherBuddyReborn", "GatherBuddyReborn", "採集・釣り・NPC購入", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
        new("vnavmesh", "vnavmesh", "移動", "https://puni.sh/api/repository/veyn"),
        new("AutoHook", "AutoHook", "釣り", "https://love.puni.sh/ment.json"),
        new("Lifestream", "Lifestream", "テレポ・宿屋", "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json"),
        new("Questionable", "Questionable", "クエストの受注・報告", "https://puni.sh/api/plugins"),
        new("RotationSolver", "RotationSolverReborn", "戦闘", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
        new("TextAdvance", "TextAdvance", "会話送り・納品", "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json"),
        new("InventoryTools|AllaganItemSearch", "Allagan Tools", "GBR の NPC 購入の前提", null),
        new("BossModReborn", "BossModReborn（BMR）", "戦闘の補助", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
    ];

    /// <summary>導入済み（読み込み済み）か、そうでなければ何か。</summary>
    public enum State
    {
        /// <summary>読み込まれている。</summary>
        Loaded,

        /// <summary>入っているが読み込まれていない（無効・読み込みの失敗）。</summary>
        NotLoaded,

        /// <summary>入っていない。</summary>
        Missing,
    }

    // 入れている最中のもの（表示名 → いまの状態の文）と、終わったものの結果
    private readonly Dictionary<string, string> busy = [];
    private readonly Dictionary<string, string> results = [];
    private readonly object gate = new();

    /// <summary>公開の窓口で、導入の状態と版を読む。</summary>
    public static (State State, string? Version, string? From) Read(RequiredPlugin p)
    {
        var names = p.Internal.Split('|');
        var hits = Svc.PluginInterface.InstalledPlugins
            .Where(x => names.Any(n => string.Equals(x.InternalName, n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (hits.Count == 0)
            return (State.Missing, null, null);
        var best = hits.FirstOrDefault(x => x.IsLoaded) ?? hits[0];
        string? from = null;
        try
        {
            from = best.Manifest?.InstalledFromUrl;
        }
        catch
        {
            // 導入元は表示だけに使う
        }

        return (best.IsLoaded ? State.Loaded : State.NotLoaded, best.Version?.ToString(), from);
    }

    /// <summary>入れている最中なら、その状態の文。</summary>
    public string? Busy(RequiredPlugin p)
    {
        lock (this.gate)
            return this.busy.GetValueOrDefault(p.Display);
    }

    /// <summary>最後に入れようとした結果（無ければ null）。</summary>
    public string? Result(RequiredPlugin p)
    {
        lock (this.gate)
            return this.results.GetValueOrDefault(p.Display);
    }

    /// <summary>そのプラグインのリポジトリが、Dalamud に有効な形で登録されているか（公式なら true。読めなければ null）。</summary>
    public static bool? RepoRegistered(RequiredPlugin p)
    {
        if (p.RepoUrl == null)
            return true;
        try
        {
            var conf = DalamudService("Dalamud.Configuration.Internal.DalamudConfiguration");
            var list = (IEnumerable)conf.GetType().GetProperty("ThirdRepoList", Pub)!.GetValue(conf)!;
            return list.Cast<object>().Any(r => (string?)r.GetType().GetProperty("Url", Pub)!.GetValue(r) == p.RepoUrl
                                                && (bool)r.GetType().GetProperty("IsEnabled", Pub)!.GetValue(r)!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dalamud のプラグイン画面を、その名前で開く（入っていない・読み込まれていないときの手動の道）。</summary>
    public static void OpenInstaller(RequiredPlugin p, bool installed)
        => Svc.PluginInterface.OpenPluginInstallerTo(installed ? PluginInstallerOpenKind.InstalledPlugins : PluginInstallerOpenKind.AllPlugins, p.Display);

    /// <summary>
    /// 入れる（利用者がボタンを押したときだけ呼ぶ）。リポジトリが未登録なら登録してから入れる。終わるまで待たない（結果は <see cref="Result"/>）。
    /// </summary>
    public void Install(RequiredPlugin p)
    {
        lock (this.gate)
        {
            if (this.busy.ContainsKey(p.Display))
                return;
            this.busy[p.Display] = "入れています…";
            this.results.Remove(p.Display);
        }

        Task.Run(async () =>
        {
            string result;
            try
            {
                result = await this.InstallCore(p);
            }
            catch (Exception ex)
            {
                result = $"入れられませんでした（{Unwrap(ex)}）。Dalamud のプラグイン画面で入れてください";
                Svc.Framework.RunOnFrameworkThread(() => OpenInstaller(p, installed: false));
            }

            Core.DebugLog.Current?.Line("プラグイン", $"{p.Display}：{result}");
            lock (this.gate)
            {
                this.busy.Remove(p.Display);
                this.results[p.Display] = result;
            }
        });
    }

    private async Task<string> InstallCore(RequiredPlugin p)
    {
        var pm = DalamudService("Dalamud.Plugin.Internal.PluginManager");
        var pmType = pm.GetType();
        var api = (int)pmType.GetProperty("DalamudApiLevel", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        var manifest = FindManifest(pm, p, api);
        if (manifest == null && p.RepoUrl != null && RepoRegistered(p) != true)
        {
            // リポジトリを登録（有効化）して、読み直しを待つ（ECommons と違って待つ：待たないと一覧が空のまま探すことがある）
            this.SetBusy(p, "リポジトリを登録しています…");
            AddRepo(p.RepoUrl);
            var reload = pmType.GetMethod("SetPluginReposFromConfigAsync", Pub, null, [typeof(bool)], null)
                         ?? throw new MissingMethodException("PluginManager.SetPluginReposFromConfigAsync");
            await (Task)reload.Invoke(pm, [true])!;
            manifest = FindManifest(pm, p, api);
        }

        if (manifest == null)
        {
            Svc.Framework.RunOnFrameworkThread(() => OpenInstaller(p, installed: false));
            return $"リポジトリの一覧に見つかりませんでした（API {api}）。Dalamud のプラグイン画面を開きました";
        }

        this.SetBusy(p, "ダウンロードしています…");
        var mt = manifest.GetType();
        var testingOnly = (bool)(mt.GetProperty("IsTestingExclusive", Pub)?.GetValue(manifest) ?? false)
                          || (int)mt.GetProperty("DalamudApiLevel", Pub)!.GetValue(manifest)! != api;
        var install = pmType.GetMethods(Pub).Where(m => m.Name == "InstallPluginAsync").OrderBy(m => m.GetParameters().Length).FirstOrDefault()
                      ?? throw new MissingMethodException("PluginManager.InstallPluginAsync");
        var args = install.GetParameters().Select(par => par.Name switch
        {
            "repoManifest" => manifest,
            "useTesting" => testingOnly,
            "reason" => Enum.Parse(par.ParameterType, "Installer"),
            _ => par.HasDefaultValue ? par.DefaultValue : null,
        }).ToArray();
        var task = (Task)install.Invoke(pm, args)!;
        await task;
        var local = task.GetType().GetProperty("Result", Pub)?.GetValue(task);
        var loaded = local?.GetType().GetProperty("IsLoaded", Pub)?.GetValue(local) as bool?;
        return loaded == true ? "入れて読み込みました" : "入れましたが、まだ読み込まれていません（Dalamud のプラグイン画面で有効にしてください）";
    }

    private void SetBusy(RequiredPlugin p, string text)
    {
        lock (this.gate)
            this.busy[p.Display] = text;
    }

    /// <summary>読み込み済みの一覧から、InternalName・API レベル・リポジトリが合う目録を選ぶ（無ければ null）。</summary>
    private static object? FindManifest(object pm, RequiredPlugin p, int api)
    {
        var names = p.Internal.Split('|');
        var all = (IEnumerable)pm.GetType().GetProperty("AvailablePlugins", Pub)!.GetValue(pm)!;
        foreach (var m in all)
        {
            var t = m.GetType();
            var name = (string?)t.GetProperty("InternalName", Pub)?.GetValue(m);
            if (name == null || !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            var level = (int)t.GetProperty("DalamudApiLevel", Pub)!.GetValue(m)!;
            var testLevel = t.GetProperty("TestingDalamudApiLevel", Pub)?.GetValue(m) as int?;
            if (level != api && testLevel != api)
                continue;
            var repo = t.GetProperty("SourceRepo", Pub)?.GetValue(m);
            if (repo == null)
                continue;
            var third = (bool)repo.GetType().GetProperty("IsThirdParty", Pub)!.GetValue(repo)!;
            var url = (string?)repo.GetType().GetProperty("PluginMasterUrl", Pub)?.GetValue(repo);
            if (p.RepoUrl == null ? !third : url == p.RepoUrl)
                return m;
        }

        return null;
    }

    /// <summary>リポジトリを Dalamud の設定に登録する（あれば有効にする）。設定の保存は Dalamud に頼む。</summary>
    private static void AddRepo(string url)
    {
        var conf = DalamudService("Dalamud.Configuration.Internal.DalamudConfiguration");
        var ct = conf.GetType();
        var list = (IList)ct.GetProperty("ThirdRepoList", Pub)!.GetValue(conf)!;
        var existing = list.Cast<object>().FirstOrDefault(r => (string?)r.GetType().GetProperty("Url", Pub)!.GetValue(r) == url);
        if (existing != null)
        {
            existing.GetType().GetProperty("IsEnabled", Pub)!.SetValue(existing, true);
        }
        else
        {
            var st = ct.Assembly.GetType("Dalamud.Configuration.ThirdPartyRepoSettings", throwOnError: true)!;
            var repo = Activator.CreateInstance(st, nonPublic: true)!;
            st.GetProperty("Url", Pub)!.SetValue(repo, url);
            st.GetProperty("IsEnabled", Pub)!.SetValue(repo, true);
            list.Add(repo);
        }

        ct.GetMethod("QueueSave", Pub, null, Type.EmptyTypes, null)?.Invoke(conf, null);
        Core.DebugLog.Current?.Line("プラグイン", $"Dalamud にリポジトリを登録しました：{url}");
    }

    private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Dalamud の内部のサービス（Service&lt;T&gt;.Get()）。GbrReflection と同じ取り方。</summary>
    private static object DalamudService(string typeName)
    {
        var dalamud = Svc.PluginInterface.GetType().Assembly;
        var service = dalamud.GetType("Dalamud.Service`1", throwOnError: true)!;
        var type = dalamud.GetType(typeName, throwOnError: true)!;
        return service.MakeGenericType(type).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
    }

    private static string Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
            ex = inner;
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
