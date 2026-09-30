using System;
using System.Collections.Generic;
using System.Linq;
using AutoJobQuest.Planning;

namespace AutoJobQuest.Automation;

/// <summary>
/// GBR にその作業をさせるのに要る設定（GBR が潜水・刺突漁ができる設定になっていなければ、
/// 自動で設定を変える）。違っていれば、その作業の間だけこちらで合わせ、終わったら戻す
/// （<see cref="Ipc.GbrOperations.OverrideBool"/> が元の値を控えに残し、<see cref="Ipc.GbrOperations.RestoreIfIdle"/> で戻す。
/// 途中でゲームが落ちても、次に起動したときに控えから戻す）。
///
/// どの設定が要るかは GBR 7.5.6.1 のソースで確かめた。
///
/// 釣果送信の同意（FishDataCollection）は、釣った魚のデータを GBR の外部サーバーへ送る同意。刺突漁に続き、竿の釣りでも
/// 釣りの間だけ ON にする（止まってしまうよりは ON にする。以前は変えずに、始める前に止めていた）。
/// ON にしたときは、外部へ送られることをチャットでも知らせる。
/// ゲームに触らない（読み書きは外から渡す）ので、ゲームを起動せずに試せる。
/// </summary>
public static class GbrRequiredSettings
{
    /// <summary>GBR の設定1つ。</summary>
    /// <param name="Name">GBR の AutoGatherConfig のプロパティ名。</param>
    /// <param name="Value">要る値。</param>
    /// <param name="Label">GBR の設定画面の表示（利用者が探せるように）。</param>
    /// <param name="Why">違うとどうなるか（根拠は GBR のソース）。</param>
    public sealed record Setting(string Name, bool Value, string Label, string Why);

    /// <summary>変えた設定と、変える前の値（読めなければ null）。</summary>
    public sealed record Change(Setting Setting, bool? Before);

    /// <summary>釣果送信の同意（外部へ送る同意なので、変えたらチャットでも知らせる）。</summary>
    public const string FishDataCollection = "FishDataCollection";

    /// <summary>GBR で集めるときは常に（採掘・園芸・釣り・刺突漁）。</summary>
    public static readonly IReadOnlyList<Setting> Always =
    [
        // AutoGather.cs:1068-1072（状態表示「Waiting for Gathering Point... (No Nav Mode)」のまま）
        new("UseNavigation", true, "Use vnavmesh Navigation", "OFF だと GBR はテレポートも含めて一切動かない"),

        // AutoGather.cs:745-746（GBR の画面にも「DISABLING THIS IS UNSUPPORTED」とある）
        new("DoGathering", true, "Enable Gathering Window Interaction", "OFF だと GBR は移動だけで採集しない"),
    ];

    /// <summary>釣り（竿・刺突漁）のとき。</summary>
    public static readonly IReadOnlyList<Setting> Fishing =
    [
        // AutoGather.AutoHook.cs:39-40（刺突漁は AutoGig を ON にしない＝突かない）。画面には無く、設定ファイルだけにある
        new("UseAutoHook", true, "UseAutoHook", "OFF だと GBR が AutoHook を動かさないので、釣らない・突かない。GBR の画面に無く、設定ファイルだけにある項目"),

        // AutoGather.cs:917-926（魚が目標にあるだけで、チャットにエラーを出して止まる）
        new(FishDataCollection, true, "Opt-in to fishing data collection",
            "OFF だと GBR は魚が目標にあるだけで止まる。ON の間は、釣った魚のデータが GBR の外部サーバーへ送られる"),
    ];

    /// <summary>竿の釣りのとき（刺突漁には効かない設定）。</summary>
    public static readonly IReadOnlyList<Setting> RodFishing =
    [
        // AutoGather.AutoHook.cs:53, 84-108（ON だと AutoHook のグローバルプリセットで釣り、魚のエサを万能ルアーに差し替えない：
        // エサは全て万能ルアーにする。刺突漁では見ない＝!IsSpearFish）
        new("UseAutoHookGlobalPreset", false, "Use AutoHook Global Preset", "ON だと GBR が AutoHook のグローバルプリセットで釣り、エサを万能ルアーに差し替えない"),
    ];

    /// <summary>刺突漁（潜水）のとき。</summary>
    public static readonly IReadOnlyList<Setting> Spearfishing =
    [
        // AutoGather.Var.cs:130-133（飛ばない → 水面を泳ぐだけで潜れない。沈没川船の漁場は水中）
        new("ForceWalking", false, "Force Walking", "ON だと GBR が飛ばないので潜れない（漁場は水中）"),
    ];

    /// <summary>その作業で要る設定。竿の釣りは釣りの設定と竿の釣りの設定、刺突漁は釣りの設定と刺突漁の設定を含む。</summary>
    /// <param name="route">集め方（GBR の自動採集を使うもの）。</param>
    /// <param name="spearfish">刺突漁か。</param>
    public static List<Setting> For(Route route, bool spearfish)
    {
        var list = Always.ToList();
        if (route == Route.Fish || spearfish)
            list.AddRange(Fishing);
        if (route == Route.Fish && !spearfish)
            list.AddRange(RodFishing);
        if (spearfish)
            list.AddRange(Spearfishing);
        return list;
    }

    /// <summary>
    /// 設定を合わせる。今の値が要る値と同じものは触らない。書けなかったらそこで止め、<paramref name="failed"/> に入れる
    /// （それまでに変えたものは戻り値に入る。戻すのは呼び出し側の後始末）。
    /// </summary>
    /// <param name="settings">要る設定。</param>
    /// <param name="read">今の値を読む（読めなければ null。その場合も書いてみる）。</param>
    /// <param name="write">書く（書けて、読み直して要る値になっていれば true）。</param>
    /// <param name="failed">書けなかった設定（全部書けたら null）。</param>
    /// <returns>変えた設定。</returns>
    public static List<Change> Apply(IEnumerable<Setting> settings, Func<string, bool?> read, Func<string, bool, bool> write, out Setting? failed)
    {
        failed = null;
        var changed = new List<Change>();
        foreach (var s in settings)
        {
            var before = read(s.Name);
            if (before == s.Value)
                continue;

            if (!write(s.Name, s.Value))
            {
                failed = s;
                return changed;
            }

            changed.Add(new Change(s, before));
        }

        return changed;
    }

    /// <summary>釣果送信の同意を ON にしたときに、チャットへ添える文（変えていなければ空）。</summary>
    public static string ConsentNote(IEnumerable<Change> changed)
        => changed.Any(c => c.Setting.Name == FishDataCollection)
            ? "（釣果送信の同意を ON にした間は、釣った魚のデータが GBR の外部サーバーへ送られます）"
            : string.Empty;

    /// <summary>ON / OFF / 読めない。</summary>
    public static string OnOff(bool? value) => value switch { true => "ON", false => "OFF", _ => "読めない" };

    /// <summary>記録用：「Force Walking」ON → OFF（理由）。</summary>
    public static string Describe(Change c) => $"「{c.Setting.Label}」{OnOff(c.Before)} → {OnOff(c.Setting.Value)}（{c.Setting.Why}）";

    /// <summary>書けなかったときの理由。</summary>
    public static string FailText(Setting s, string? error)
        => $"GBR の「{s.Label}」を {OnOff(s.Value)} にできませんでした（{s.Why}）{(string.IsNullOrEmpty(error) ? string.Empty : $"：{error}")}";

    /// <summary>今の値を並べる（記録用）。</summary>
    public static string Snapshot(IEnumerable<Setting> settings, Func<string, bool?> read)
        => string.Join("・", settings.Select(s => $"{s.Name} {OnOff(read(s.Name))}"));
}
