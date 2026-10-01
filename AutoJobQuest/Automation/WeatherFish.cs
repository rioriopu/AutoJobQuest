using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Automation;

/// <summary>
/// 天気の読み取り（漁師 Lv40「晴れでは事をし損じる」の雨乞魚は雨・雪のときしか釣れないので、
/// グリダニアが雨天でなければマーケットボードで買う。雨天のときだけは釣る）。
/// </summary>
public static unsafe class GameWeather
{
    /// <summary>検証の仕組み用：設定すると、ゲームの代わりにこれを使う（エリア → 天気。読めなければ null）。</summary>
    public static Func<uint, uint?>? Test { get; set; }

    /// <summary>検証の仕組み用：設定すると、今いるエリアをこれで読む。</summary>
    public static Func<uint>? TestTerritory { get; set; }

    /// <summary>今いるエリア（TerritoryType の行）。</summary>
    public static uint CurrentTerritory => TestTerritory?.Invoke() ?? Svc.ClientState.TerritoryType;

    /// <summary>
    /// 今いるエリアの今の天気（ゲームデータ Weather の行）。そのエリアにいない・読めないときは null。
    /// GBR の EnhancedCurrentWeather と同じ読み方：エリア固有の天気があればそれ、無ければ WeatherManager.GetCurrentWeather。
    /// </summary>
    public static uint? CurrentIn(uint territory)
    {
        if (CurrentTerritory != territory)
            return null;
        if (Test is { } test)
            return test(territory);
        try
        {
            var wm = WeatherManager.Instance();
            if (wm == null)
                return null;
            var id = wm->HasIndividualWeather((ushort)territory) ? wm->GetIndividualWeather((ushort)territory) : wm->GetCurrentWeather();
            return id == 0 ? null : id;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("天気", $"天気を読めませんでした（{ex.GetType().Name}）");
            return null;
        }
    }

    /// <summary>天気の名前（ゲームデータ Weather。記録用）。</summary>
    public static string Name(uint weather)
        => Svc.Data.GetExcelSheet<Weather>().TryGetRow(weather, out var w) && w.Name.ExtractText() is { Length: > 0 } n ? n : $"天気#{weather}";
}

/// <summary>魚の釣れる条件（AutoHook の魚のデータ。導入版の fish_list.json を1回だけ読む）。</summary>
public static class FishConditions
{
    /// <summary>検証の仕組み用：設定すると、魚のデータの代わりにこれを使う（品 → 釣れる天気。データに無ければ null）。</summary>
    public static Func<uint, IReadOnlyList<uint>?>? TestWeathers { get; set; }

    private static Dictionary<uint, AutoHookFish>? cache;
    private static string? cachePath;

    /// <summary>検証の仕組み用：設定すると、魚のデータの代わりにこれを使う（品 → 釣れる時間帯。無ければ null）。</summary>
    public static Func<uint, (double Start, double Hours)?>? TestWindows { get; set; }

    /// <summary>その魚が釣れる天気（空ならいつでも）。データが無い・読めないときは null。</summary>
    public static IReadOnlyList<uint>? Weathers(uint itemId, Version? autoHookVersion)
    {
        if (TestWeathers is { } test)
            return test(itemId);
        return Find(itemId, autoHookVersion)?.Weathers;
    }

    /// <summary>
    /// その魚が釣れる時間帯（エオルゼア時間。時限性の採取物も待たずに買う）。いつでも釣れる・データが無いときは null。
    /// 検証の仕組みで天気だけを差し替えているときは、実物のデータを読まない（差し替えが無ければ null）。
    /// </summary>
    public static (double Start, double Hours)? Window(uint itemId, Version? autoHookVersion)
    {
        if (TestWindows is { } test)
            return test(itemId);
        if (TestWeathers != null)
            return null;
        return Find(itemId, autoHookVersion)?.Window;
    }

    private static AutoHookFish? Find(uint itemId, Version? autoHookVersion)
    {
        try
        {
            var path = AutoHookData.FishListPath(autoHookVersion);
            if (path == null)
                return null;
            if (cache == null || cachePath != path)
            {
                cache = AutoHookData.LoadFishList(path);
                cachePath = path;
            }

            return cache.TryGetValue(itemId, out var f) ? f : null;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Current?.Line("天気", $"AutoHook の魚のデータを読めませんでした（{ex.GetType().Name}: {ex.Message}）");
            return null;
        }
    }
}

/// <summary>エオルゼア時間（1 時間＝現実の 175 秒。ゲームのサーバーの時刻から出す）。</summary>
public static class EorzeaTime
{
    /// <summary>検証の仕組み用：設定すると、今のエオルゼア時間（時。小数で分も表す）をこれで読む。</summary>
    public static Func<double>? TestHour { get; set; }

    /// <summary>今のエオルゼア時間（0 以上 24 未満の時。17.5 は 17:30）。サーバーの時刻を読めなければ、PC の時計で出す。</summary>
    public static double Hour()
    {
        if (TestHour is { } test)
            return test();
        long unix;
        try
        {
            unix = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime();
        }
        catch
        {
            unix = 0;
        }

        if (unix <= 0)
            unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return unix % (175L * 24) / 175.0;
    }

    /// <summary>その時刻が時間帯に入っているか（日をまたぐ時間帯も扱う：18 時から 12 時間＝18:00〜06:00）。</summary>
    public static bool InWindow(double hour, (double Start, double Hours) window)
        => window.Hours >= 24 || (hour - window.Start + 24) % 24 < window.Hours;

    /// <summary>時刻の表示（17.5 → 「17:30」）。</summary>
    public static string Clock(double hour)
    {
        var minutes = (int)Math.Floor(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>時間帯の表示（「ET 18:00〜06:00」）。</summary>
    public static string Text((double Start, double Hours) window)
        => $"ET {Clock(window.Start)}〜{Clock((window.Start + window.Hours) % 24)}";
}

/// <summary>
/// Questionable の釣りの手順の魚が、天気の限られた魚で、いまの天気で釣れないなら、マーケットボードで買うかの判断
/// （雨天でなかった場合のみマーケットボードで買う）。
/// 時間帯の限られた魚（ET 18:00〜06:00 のフルムーンサーディンなど）も同じ扱い（時限性・天候の採取物は待たずに買う）。
/// 天気の条件は AutoHook の魚のデータ（Weathers）から読む（品や天気は埋め込まない）。天気が合っていれば Questionable が釣る。
/// 判断は、釣りの手順のエリアにいるときに行う（釣れるかは、実際にいる場所の天気で決まる）。釣っている途中で天気が外れたときも買う。
/// Questionable の釣りの手順は NQ の数で「そろった」を見る（GetInventoryItemCount の既定）ので、NQ で数え、NQ を買う。
/// </summary>
public static class WeatherFishBuy
{
    public enum Verdict
    {
        /// <summary>天気の限られた魚ではない（天気の条件が無い・データが無い）。</summary>
        NoWeatherFish,

        /// <summary>もうそろっている（NQ の数）。</summary>
        Enough,

        /// <summary>釣りの手順のエリアにまだいない。着いてから決める。</summary>
        NotInArea,

        /// <summary>天気を読めない。Questionable に任せる。</summary>
        WeatherUnknown,

        /// <summary>天気が合っている。Questionable が釣る。</summary>
        InWeather,

        /// <summary>マーケットで売買できない品。天気が変わるのを待って Questionable が釣る。</summary>
        CannotBuy,

        /// <summary>このクエストでは一度買いに行った（買いきれなかった）。天気が変わるのを待って Questionable が釣る。</summary>
        AlreadyTried,

        /// <summary>天気が合わない。マーケットボードで足りない分を買う。</summary>
        Buy,
    }

    /// <summary>今の手順（段・番号）から後で、同じ段にある最初の釣りの手順（採る品と数がそろっているもの）。無ければ null。</summary>
    public static QuestionableStep? FishAhead(IReadOnlyList<QuestionableStep> steps, int sequence, int stepIndex)
        => steps.Where(s => s.Sequence == sequence && s.Index >= stepIndex && s.Type == "Fish" && s.GatherItemId is > 0 && s.GatherCount is > 0)
                .OrderBy(s => s.Index)
                .FirstOrDefault();

    /// <summary>
    /// 買うかの判断。天気の条件（<paramref name="weathers"/>）と時間帯の条件（<paramref name="window"/>。
    /// 時限性の採取物も待たずに買う）のどちらかがある魚が対象。両方が合っていれば Questionable が釣り、どちらかが合わなければ買う。
    /// 時間帯はエリアに着いたときのエオルゼア時間（<paramref name="eorzeaHour"/>）で見る。
    /// </summary>
    public static Verdict Decide(IReadOnlyList<uint>? weathers, int nqOwned, int count, bool inArea, uint? weather, bool marketable, bool tried,
        (double Start, double Hours)? window = null, double? eorzeaHour = null)
    {
        var byWeather = weathers is { Count: > 0 };
        if (!byWeather && window == null)
            return Verdict.NoWeatherFish;
        if (nqOwned >= count)
            return Verdict.Enough;
        if (!inArea)
            return Verdict.NotInArea;
        if ((byWeather && weather == null) || (window != null && eorzeaHour == null))
            return Verdict.WeatherUnknown;
        var weatherOk = !byWeather || weathers!.Contains(weather!.Value);
        var timeOk = window is not { } w || EorzeaTime.InWindow(eorzeaHour!.Value, w);
        if (weatherOk && timeOk)
            return Verdict.InWeather;
        if (!marketable)
            return Verdict.CannotBuy;
        if (tried)
            return Verdict.AlreadyTried;
        return Verdict.Buy;
    }

    /// <summary>マーケットで売買できる品か（ゲームデータ Item：マーケットの分類があり、売買不可でない。SourceIndex と同じ見方）。</summary>
    public static bool Marketable(uint itemId)
        => Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var it) && it.ItemSearchCategory.RowId != 0 && !it.IsUntradable;
}
