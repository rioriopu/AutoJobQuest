using System;

namespace AutoJobQuest.Ipc;

/// <summary>
/// 呼び鈴を使う間だけ、AutoRetainer の新規開始を一時停止する（AutoRetainer.SetSuppressed。動作中の仕事は中断しない）。
///
/// 【AutoRetainer の中身（導入版 4.6.2.7 を逆コンパイルして確認）】
///  ・抑制は真偽値1つ（Modules/IPC.cs の static Suppressed）。設定には保存しないので、AutoRetainer を読み直すと false に戻る。
///  ・PluginState.IsBusy は「自分の作業の列が動作中・軍票の納品中・Lifestream の移動中」のどれか（Helpers/Utils.cs）。
///    こちらが Lifestream で移動している間も true になるので、呼び鈴の前に着いてから見る。
///  ・呼び鈴を開くと、既定の設定（OpenBellBehavior＝Enable_AutoRetainer）で AutoRetainer 自身が有効になる。
///    抑制中は動かない（SchedulerMain.PluginEnabled が抑制を見る）。だから抑制を戻すのは、呼び鈴を閉じた後にする。
/// </summary>
public sealed class RetainerControl : IpcGate
{
    public override string InternalName => "AutoRetainer";

    /// <summary>一時停止を頼んだ結果。</summary>
    public enum TakeResult
    {
        /// <summary>一時停止できた（入っていなければ、止める相手がいないのでこれ）。</summary>
        Taken,

        /// <summary>AutoRetainer が動作中（待てば空くかもしれない）。</summary>
        Busy,

        /// <summary>状態を読めない・一時停止できない。</summary>
        Failed,
    }

    /// <summary>
    /// 控えの期限。これを過ぎた控えは戻しを送らず、控えだけ消して知らせる。
    /// こちらの控えは、呼び鈴を閉じれば数秒で戻して消える。10分を過ぎて残っているのは、呼び鈴を開いたまま読み込みが解除された・
    /// 戻しが失敗し続けた、のどちらか。その間にほかのプラグインが一時停止を立てているかもしれず、
    /// 抑制は真偽値1つなので、誰が立てたかは見分けられない。だから期限の後は外さない（以前は期限を見る前に戻しを送っていたので、
    /// 期限が効かず、ほかのプラグインの一時停止を外しえた）。
    /// </summary>
    public static readonly TimeSpan RestoreRetryLimit = TimeSpan.FromMinutes(10);

    /// <summary>直近の失敗の理由（画面・記録用）。</summary>
    public string LastProblem { get; private set; } = string.Empty;

    /// <summary>直近の <see cref="Take"/> で、期限を過ぎた控えを戻さずに消したときの知らせ（無ければ空）。</summary>
    public string StaleNotice { get; private set; } = string.Empty;

    /// <summary>控えが期限を過ぎているか。</summary>
    public static bool IsStale(Configuration config)
        => config.RetainerSuppressionPendingRestore && DateTime.UtcNow - config.RetainerSuppressionSetAt > RestoreRetryLimit;

    private DateTime lastKeep = DateTime.MinValue;
    private bool lastKeepResult = true;

    public TakeResult Take(Configuration config)
    {
        this.StaleNotice = string.Empty;

        // 期限を過ぎた控えは戻さずに消す（ほかのプラグインが後から立てた一時停止かもしれない）。
        // いまも一時停止が立っていれば、下の「ほかの誰かがすでに止めている」として、こちらは立てず戻さない
        if (IsStale(config))
        {
            this.Forget(config);
            this.StaleNotice = "前回こちらが立てた AutoRetainer の一時停止の控えが10分以上前のものなので、戻さずに消しました"
                               + "（ほかのプラグインが後から立てた一時停止を外さないため）";
        }

        // 前回の戻しの控えが残っていれば（期限の内）、先に戻す
        if (config.RetainerSuppressionPendingRestore)
        {
            this.Release(config);
            if (config.RetainerSuppressionPendingRestore)
            {
                this.LastProblem = "前回こちらが立てた一時停止を戻せていません";
                return TakeResult.Failed;
            }
        }

        if (!this.IsLoaded)
            return TakeResult.Taken;
        if (!this.TryInvoke("IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out var busy))
        {
            this.LastProblem = "AutoRetainer の動作中かを読めません";
            return TakeResult.Failed;
        }

        if (busy)
            return TakeResult.Busy;
        if (!this.Read(out var suppressed))
        {
            this.LastProblem = "AutoRetainer の一時停止の状態を読めません";
            return TakeResult.Failed;
        }

        // ほかの誰かがすでに止めている。こちらは立てていないので、戻さない
        if (suppressed)
            return TakeResult.Taken;

        config.RetainerSuppressionPendingRestore = true;
        config.RetainerSuppressionSetAt = DateTime.UtcNow;
        config.Save();
        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true))
            && this.Read(out suppressed) && suppressed)
            return TakeResult.Taken;
        this.LastProblem = "一時停止を頼んでも、止まったことを確かめられません";
        return TakeResult.Failed;
    }

    private bool Read(out bool value)
        => this.TryInvoke("GetSuppressed", () => this.Func<bool>("AutoRetainer.GetSuppressed").InvokeFunc(), out value);

    /// <summary>
    /// 呼び鈴を開いている間、一時停止が続いていて AutoRetainer が動いていないか（0.25 秒に1回だけ問い合わせる）。
    /// 入っていなければ true。
    /// </summary>
    public bool Keep()
    {
        if (!this.IsLoaded)
            return true;
        if (DateTime.UtcNow - this.lastKeep < TimeSpan.FromMilliseconds(250))
            return this.lastKeepResult;
        this.lastKeep = DateTime.UtcNow;
        this.lastKeepResult = this.Read(out var suppressed) && suppressed
            && this.TryInvoke("IsBusy", () => this.Func<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc(), out var busy) && !busy;
        return this.lastKeepResult;
    }

    /// <summary>
    /// こちらが立てた一時停止を戻す（控えがあるときだけ）。
    /// AutoRetainer が読み込まれていなければ、抑制は保存されないので戻す相手が無い。控えを消す。
    /// </summary>
    public void Release(Configuration config)
    {
        if (!config.RetainerSuppressionPendingRestore)
            return;
        if (!this.IsLoaded)
        {
            this.Forget(config);
            return;
        }

        if (this.TryAction("SetSuppressed", () => this.Func<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false))
            && this.Read(out var suppressed) && !suppressed)
            this.Forget(config);
    }

    /// <summary>
    /// 止まっている間の再試行（Services から10秒おき）。期限の内は戻す。期限を過ぎたら戻しを送らず、控えだけ消す。
    /// 期限を過ぎて、いまも一時停止が立っている（または読めない）なら true（呼び出し側がチャットで知らせる）。
    /// </summary>
    public bool RetryRelease(Configuration config)
    {
        if (!config.RetainerSuppressionPendingRestore)
            return false;
        if (!IsStale(config))
        {
            this.Release(config);
            return false;
        }

        this.Forget(config);
        if (!this.IsLoaded)
            return false;
        return !this.Read(out var suppressed) || suppressed;
    }

    private void Forget(Configuration config)
    {
        config.RetainerSuppressionPendingRestore = false;
        config.RetainerSuppressionSetAt = DateTime.MinValue;
        config.Save();
    }
}
