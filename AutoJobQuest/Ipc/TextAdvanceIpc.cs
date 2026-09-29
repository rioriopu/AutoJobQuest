namespace AutoJobQuest.Ipc;

/// <summary>
/// TextAdvance への窓口（導入版 3.3.0.1 を正とする）。
///
/// Questionable は動いている間、自分で TextAdvance の外部制御を取る（納品の自動入力も含む）。ただし他者が外部制御を
/// 持っていれば取りに行かない（Questionable の TextAdvanceIpc）。
/// こちらは、ジョブクエを Questionable に進めてもらう間と、自前で報告する間（木工 Lv20 のマテリア装着待ち）に外部制御を取る
/// （納品窓に TextAdvance が一覧の先頭を入れ、こちらの RequestFiller と取り合わないように、納品窓の入力だけ切る）。
/// 使い終わったら必ず解除する（他者が制御中だと Questionable が取りに行けない）。
/// 外部制御の設定は、同じ依頼者なら上書きできる（TextAdvance の IPCProvider.cs：EnableExternalControl）。
///
/// 【導入版の納品】アイテムを選ぶ小窓の先頭を常に選ぶ。品質（NQ/HQ）の指定項目は導入版に無い。
/// </summary>
public sealed class TextAdvanceIpc : IpcGate
{
    public override string InternalName => "TextAdvance";

    /// <summary>TextAdvance の ExternalTerritoryConfig と同じ名前の項目（null は本体設定のまま）。</summary>
    public sealed class ExternalTerritoryConfig
    {
        public bool? EnableQuestAccept;
        public bool? EnableQuestComplete;
        public bool? EnableRewardPick;
        public bool? EnableRequestHandin;
        public bool? EnableCutsceneEsc;
        public bool? EnableCutsceneSkipConfirm;
        public bool? EnableTalkSkip;
        public bool? EnableRequestFill;
        public bool? EnableAutoInteract;
    }

    private bool ownControl;
    private bool requestAllowed;

    /// <summary>こちらが外部制御を取っているか（ジョブクエを進める間・手動の報告の間）。</summary>
    public bool OwnsControl => this.ownControl;

    /// <summary>いま納品窓の入力を TextAdvance に任せているか（こちらが扱わない納品窓の間だけ）。</summary>
    public bool RequestAllowed => this.ownControl && this.requestAllowed;

    /// <summary>
    /// 報告（会話送り・受注・完了・報酬選び）を任せる設定で外部制御を取る。
    /// 納品窓への入力と受け渡し（RequestFill / RequestHandin）は任せない：こちらの RequestFill.cs が
    /// 条件（HQ 等）に合う品を選んで入れて渡すため（同じ窓を2つが取り合わないように）。
    /// </summary>
    public bool TakeControlForTurnIn()
    {
        if (this.ownControl)
            return this.KeepControl();
        return this.Apply(false);
    }

    // 外部制御がまだこちらのものかを最後に確かめた時刻
    private System.DateTime lastCheck = System.DateTime.MinValue;

    /// <summary>
    /// 取ったはずの外部制御がまだこちらのものか確かめ、無くなっていれば取り直す（3秒に1回。以前は、
    /// TextAdvance が読み直されると依頼者の記録が消え、Questionable が取り直して納品窓の取り合いが戻るのに、
    /// こちらは「取っている」と思ったままだった）。TextAdvance が読み込まれていなければ、取っている印を下ろす。
    ///
    /// 【確かめ方】同じ設定で EnableExternalControl を呼び直す。TextAdvance は「誰も持っていない」か「依頼者が同じ」ときだけ受け付けて
    /// true を返す（TextAdvance の IPCProvider.cs）。true ならこちらのもの、false なら他者（Questionable 等）が持っている。
    /// 以前は IsInExternalControl（誰かが持っているか）だけを見ていたので、他者が取り直しても「こちらが持っている」と取り違えた。
    /// </summary>
    public bool KeepControl()
    {
        if (!this.ownControl)
            return false;
        if (System.DateTime.UtcNow - this.lastCheck < System.TimeSpan.FromSeconds(3))
            return true;
        this.lastCheck = System.DateTime.UtcNow;

        if (!this.IsLoaded)
        {
            this.ownControl = false;
            this.requestAllowed = false;
            return false;
        }

        if (this.Apply(this.requestAllowed))
            return true;

        // 他者が持っている（こちらの設定にならない）。印を下ろし、呼び出し側が記録に出す
        Core.DebugLog.Current?.Line("IPC", "TextAdvance の外部制御がほかの依頼者に移っていました（取り直せません）");
        this.ownControl = false;
        this.requestAllowed = false;
        return false;
    }

    /// <summary>
    /// 外部制御がこちらのものか、いま確かめる（間引かない）。こちらのものなら true、他者が持っていれば false、読めなければ null。
    /// こちらが持っていないときは、取りに行かずに「誰かが持っているか」だけを返す（誰も持っていなければ true＝取れる）。
    /// </summary>
    public bool? CanOwn()
    {
        if (this.ownControl)
        {
            this.lastCheck = System.DateTime.MinValue;
            return this.KeepControl();
        }

        return this.IsInExternalControl() is { } held ? !held : null;
    }

    /// <summary>
    /// 納品窓の入力と受け渡しを TextAdvance に任せる・任せないを切り替える（こちらが外部制御を持っているときだけ）。
    /// こちらの RequestFiller が扱わない納品窓（このクエストの納品物ではない品＝クエスト専用アイテム等を求める窓）の間だけ任せる。
    /// </summary>
    public bool AllowRequestFill(bool allow)
    {
        if (!this.ownControl)
            return false;
        if (this.requestAllowed == allow)
            return true;
        return this.Apply(allow);
    }

    private bool Apply(bool allowRequest)
    {
        var cfg = new ExternalTerritoryConfig
        {
            EnableQuestAccept = true,
            EnableQuestComplete = true,
            EnableRewardPick = true,
            EnableRequestHandin = allowRequest,
            EnableCutsceneEsc = true,
            EnableCutsceneSkipConfirm = true,
            EnableTalkSkip = true,
            EnableRequestFill = allowRequest,
            EnableAutoInteract = false,
        };

        this.Trace($"EnableExternalControl（納品窓の入力={(allowRequest ? "TextAdvance に任せる" : "こちらで行う")}）");
        var ok = this.TryInvoke("EnableExternalControl",
                     () => this.Func<string, ExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl")
                         .InvokeFunc(Plugin.InternalNameConst, cfg), out var accepted)
                 && accepted;
        if (ok)
        {
            this.ownControl = true;
            this.requestAllowed = allowRequest;
        }

        return ok;
    }

    /// <summary>こちらが取った外部制御を解除する。</summary>
    public void ReleaseControl()
    {
        if (!this.ownControl)
            return;

        this.Trace("DisableExternalControl");

        // 解除できたときだけ「手放した」とする（失敗したら次の呼び出しでやり直す）
        if (this.TryInvoke("DisableExternalControl",
                () => this.Func<string, bool>("TextAdvance.DisableExternalControl").InvokeFunc(Plugin.InternalNameConst), out var released)
            && released)
        {
            this.ownControl = false;
            this.requestAllowed = false;
        }
    }

    public bool? IsInExternalControl()
        => this.TryInvoke("IsInExternalControl",
            () => this.Func<bool>("TextAdvance.IsInExternalControl").InvokeFunc(), out var v) ? v : null;
}
