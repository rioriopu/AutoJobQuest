namespace AutoJobQuest.Ipc;

/// <summary>
/// AutoHook への窓口（導入版 6.0.2.1。IPC は AutoHook/IPC/AutoHookIPC.cs、EzIPC で前置詞 "AutoHook"）。
///
/// 釣りそのものは GBR が AutoHook と連携して行う（GBR の自動採集が釣り場へ移動し、AutoHook の
/// プリセットを作って投げる）。こちらからは状態の確認だけに使う。
/// 設定を書き換える IPC は毎回 AutoHook 側で保存されるので、むやみに呼ばない。
/// </summary>
public sealed class AutoHookIpc : IpcGate
{
    public override string InternalName => "AutoHook";

    public bool? GetPluginState()
        => this.TryInvoke("GetPluginState", () => this.Func<bool>("AutoHook.GetPluginState").InvokeFunc(), out var v) ? v : null;
}
