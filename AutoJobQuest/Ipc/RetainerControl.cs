namespace AutoJobQuest.Ipc;

/// <summary>呼び鈴を使う間だけAutoRetainerの新規開始を抑制する。動作中の仕事は中断しない。</summary>
public sealed class RetainerControl : IpcGate
{
    public override string InternalName => "AutoRetainer";
    public bool Take(Configuration config)
    {
        if (config.RetainerSuppressionPendingRestore)
        {
            this.Release(config);
            if (config.RetainerSuppressionPendingRestore) return false;
        }
        if (!this.IsLoaded) return true;
        if (!this.TryInvoke("IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out var busy) || busy) return false;
        if (!this.Read(out var suppressed)) return false;
        if (suppressed) return true;
        config.RetainerSuppressionPendingRestore = true;
        config.Save();
        return this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true))
            && this.Read(out suppressed) && suppressed;
    }
    private bool Read(out bool value) => this.TryInvoke("GetSuppressed", () => this.Func<bool>("AutoRetainer.GetSuppressed").InvokeFunc(), out value);
    public bool Keep() => !this.IsLoaded || (this.Read(out var suppressed) && suppressed
        && this.TryInvoke("IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out var busy) && !busy);
    public void Release(Configuration config)
    {
        if (!config.RetainerSuppressionPendingRestore) return;
        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false))
            && this.Read(out var suppressed) && !suppressed)
        {
            config.RetainerSuppressionPendingRestore = false;
            config.Save();
        }
    }
}
