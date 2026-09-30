namespace AutoJobQuest.Ipc;

/// <summary>
/// AutoHook への窓口（導入版 6.0.2.1〜6.0.2.3。IPC は AutoHook/Services/AutoHookIPC.cs、EzIPC で前置詞 "AutoHook"）。
///
/// 釣りそのものは GBR が AutoHook と連携して行う（GBR の自動採集が釣り場へ移動し、AutoHook の
/// プリセットを作って投げる）。刺突漁の魚影の魚（漁師 Lv68 の大方士）だけは、こちらが刺突漁のプリセットを取り込む（SpearfishTask）。
/// 設定を書き換える IPC は毎回 AutoHook 側で保存されるので、むやみに呼ばない。
/// </summary>
public sealed class AutoHookIpc : IpcGate
{
    public override string InternalName => "AutoHook";

    public bool? GetPluginState()
        => this.TryInvoke("GetPluginState", () => this.Func<bool>("AutoHook.GetPluginState").InvokeFunc(), out var v) ? v : null;

    /// <summary>
    /// AutoHook の有効／無効（AutoHook の AutoHookIPC.SetPluginState。Questionable の釣りの手順も同じ IPC で有効にし、終わると元に戻す）。
    /// 有効のまま釣り場にいると、AutoHook は自分で竿を投げる（自動で釣りを始める設定のとき）。
    /// </summary>
    public bool SetPluginState(bool state)
        => this.TraceThen($"SetPluginState({state})") && this.TryAction("SetPluginState", () => this.Func<bool, object>("AutoHook.SetPluginState").InvokeAction(state));

    /// <summary>
    /// プリセットを取り込んで選ぶ（竿は "AH…_"、刺突漁は "AHSF…_"。刺突漁は取り込むと選ばれ、刺突の自動が ON になる：導入版 6.0.2.3 の AutoHookIPC.cs）。
    /// 刺突漁のプリセットを消す IPC は無いので、取り込んだ刺突漁のプリセットは AutoHook に残る（GBR も同じ）。
    /// </summary>
    public bool ImportAndSelectPreset(string preset)
        => this.TraceThen($"ImportAndSelectPreset（{preset[..preset.IndexOf('_')]}）")
           && this.TryAction("ImportAndSelectPreset", () => this.Func<string, object>("AutoHook.ImportAndSelectPreset").InvokeAction(preset));

    /// <summary>竿のプリセットを名前で選ぶ（無ければ何も選ばれていない状態になる）。</summary>
    public bool SetPreset(string name)
        => this.TraceThen($"SetPreset(\"{name}\")") && this.TryAction("SetPreset", () => this.Func<string, object>("AutoHook.SetPreset").InvokeAction(name));

    /// <summary>選んでいる竿のプリセットを消す（<see cref="SetPreset"/> で自分のものを選んでから呼ぶ）。</summary>
    public bool DeleteSelectedPreset()
        => this.TraceThen("DeleteSelectedPreset") && this.TryAction("DeleteSelectedPreset", () => this.Func<object>("AutoHook.DeleteSelectedPreset").InvokeAction());

    /// <summary>刺突の自動（AutoGig）の ON/OFF。</summary>
    public bool SetAutoGigState(bool state)
        => this.TraceThen($"SetAutoGigState({state})") && this.TryAction("SetAutoGigState", () => this.Func<bool, object>("AutoHook.SetAutoGigState").InvokeAction(state));
}
