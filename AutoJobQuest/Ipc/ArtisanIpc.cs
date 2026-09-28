namespace AutoJobQuest.Ipc;

/// <summary>
/// Artisan への窓口。
///
/// IPC 名と型は Artisan のソースで確認済み（Artisan/IPC/IPC.cs）:
///   void CraftItem(ushort recipeId, int amount)  … amount は「作る回数」（個数ではない）
///   bool IsBusy()        … Endurance 中・リスト実行中・内部タスク待ち・製作画面が待機以外 のどれか
///   bool GetEnduranceStatus() / bool IsListRunning() / bool IsListPaused()
///   bool GetStopRequest() / void SetStopRequest(bool)
///
/// 【CraftItem の性質】（IPC.cs CraftX）
///  ・レシピを選ぶ処理を積んでから Endurance を「回数指定・IPC 上書き」で ON にする。
///  ・レベルや秘伝書の確認はしない。秘伝書を読んでいないレシピを渡すと進まないので、こちらで先に確かめる。
///  ・簡易製作にするかどうかは Artisan の設定「Use Quick Synthesis where possible」に従う。
///    品目ごとに外から指定する IPC は無い。
///  ・ジョブのギアセットが無いと着替えで止まる。事前点検で確かめる。
///
/// 【SetStopRequest の性質】（Artisan.cs StopCrafting）
///  リスト実行中に true を渡すと「一時停止」になり、リストは実行中のまま残る（IsListRunning は true のまま）。
///  中止ではない。false で再開する。
/// </summary>
public sealed class ArtisanIpc : IpcGate
{
    public override string InternalName => "Artisan";

    public bool CraftItem(ushort recipeId, int crafts)
    {
        this.Trace($"CraftItem(レシピ {recipeId}, {crafts}回)");
        return this.TryAction("CraftItem",
            () => this.Func<ushort, int, object>("Artisan.CraftItem").InvokeAction(recipeId, crafts));
    }

    /// <summary>何か処理中か。読めなければ null（読めないときは「処理中」とみなして待つのが安全）。</summary>
    public bool? IsBusy()
        => this.TryInvoke("IsBusy", () => this.Func<bool>("Artisan.IsBusy").InvokeFunc(), out var v) ? v : null;

    public bool? IsEndurance()
        => this.TryInvoke("GetEnduranceStatus", () => this.Func<bool>("Artisan.GetEnduranceStatus").InvokeFunc(), out var v) ? v : null;

    public bool? IsListRunning()
        => this.TryInvoke("IsListRunning", () => this.Func<bool>("Artisan.IsListRunning").InvokeFunc(), out var v) ? v : null;

    public bool? GetStopRequest()
        => this.TryInvoke("GetStopRequest", () => this.Func<bool>("Artisan.GetStopRequest").InvokeFunc(), out var v) ? v : null;

    public bool SetStopRequest(bool stop)
        => this.TraceThen($"SetStopRequest({stop})") && this.TryAction("SetStopRequest",
            () => this.Func<bool, object>("Artisan.SetStopRequest").InvokeAction(stop));

    /// <summary>Endurance を止める（こちらが CraftItem で始めた製作を止めるときだけ使う）。</summary>
    public bool SetEndurance(bool on)
        => this.TraceThen($"SetEnduranceStatus({on})") && this.TryAction("SetEnduranceStatus",
            () => this.Func<bool, object>("Artisan.SetEnduranceStatus").InvokeAction(on));
}
