using System;
using Dalamud.Plugin.Ipc;

namespace AutoJobQuest.Ipc;

/// <summary>
/// GatherBuddyReborn（GBR）の IPC 窓口。自動採集の ON/OFF と状態を読み書きする。
///
/// IPC 名と型は GBR 7.5.6.1 の実ソース・実機 DLL で確認済み:
///   GatherBuddyReborn.Version                 () -> int（2 を期待）
///   GatherBuddyReborn.IsAutoGatherEnabled     () -> bool
///   GatherBuddyReborn.SetAutoGatherEnabled    (bool) -> void   … 購読は &lt;bool, object&gt; + InvokeAction
///   GatherBuddyReborn.IsAutoGatherWaiting     () -> bool
///   GatherBuddyReborn.GetAutoGatherStatusText () -> string     … 表示専用。分岐に使わない
///
/// 【罠】
///  ・完了と失敗は同じ OFF で区別できない。成否は所持数で判定する。
///  ・ON にしても知覚不足などで黙って ON にならないことがある。Set の後は読み直す。
///  ・同じ値を Set しても何も起きない（通知も来ない）。
///  ・GBR が読み直されても OFF の通知は来ない。
/// </summary>
public sealed class GatherBuddyIpc : IpcGate
{
    public override string InternalName => "GatherBuddyReborn";

    private const string Prefix = "GatherBuddyReborn.";

    public int? Version()
        => this.TryInvoke("Version", () => this.Func<int>(Prefix + "Version").InvokeFunc(), out var v) ? v : null;

    public bool? IsAutoGatherEnabled()
        => this.TryInvoke("IsAutoGatherEnabled", () => this.Func<bool>(Prefix + "IsAutoGatherEnabled").InvokeFunc(), out var v) ? v : null;

    public bool? IsWaiting()
        => this.TryInvoke("IsAutoGatherWaiting", () => this.Func<bool>(Prefix + "IsAutoGatherWaiting").InvokeFunc(), out var v) ? v : null;

    public string StatusText()
        => this.TryInvoke("GetAutoGatherStatusText", () => this.Func<string>(Prefix + "GetAutoGatherStatusText").InvokeFunc(), out var v)
            ? v ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 自動採集を切り替える。戻り値は「読み直して望みの状態になったか」。
    /// </summary>
    public bool SetAutoGatherEnabled(bool value)
    {
        if (this.IsAutoGatherEnabled() == value)
            return true;

        this.Trace($"SetAutoGatherEnabled({value})");
        this.TryAction("SetAutoGatherEnabled",
            () => this.Func<bool, object>(Prefix + "SetAutoGatherEnabled").InvokeAction(value));

        return this.IsAutoGatherEnabled() == value;
    }
}
