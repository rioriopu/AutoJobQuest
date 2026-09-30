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
    // 応答が失われても取得済みの可能性がある。操作許可とは分けて、依頼者指定の解除を残す。
    private bool releasePending;
    private bool requestAllowed;

    // 受注の窓を TextAdvance に受けさせない（クエストの完了の後片付けの間だけ：AllowQuestAccept）。解除・喪失で戻す
    private bool questAcceptOff;
    private bool appliedAcceptOff;

    /// <summary>こちらが外部制御を取っているか（ジョブクエを進める間・手動の報告の間）。</summary>
    public bool OwnsControl => this.ownControl;

    /// <summary>
    /// 外部制御を失った理由（取り消された・ほかの依頼者が持っている・応答しない）。失っていなければ null。
    /// 取り消された（こちらが持っていたはずなのに、誰も持っていない）ときは、取り直さない（利用者が TextAdvance の画面で
    /// 「Cancel external control」を押しても、以前は1フレーム後に取り直していた）。解除（ReleaseControl）で消える。
    /// </summary>
    public string? LossReason { get; private set; }

    // 取り消されたので、この実行の間は取り直さない
    private bool lostExternally;

    /// <summary>外部制御が取り消された（こちらの解除なしに誰も持っていない）か。実行を止める理由にする。</summary>
    public bool CancelledExternally => this.lostExternally;

    // 直前の照合の時刻（1秒に1回だけ問い合わせる。以前は毎フレーム問い合わせて、記録を1行ずつ書いていた）
    private System.DateTime lastEnsure = System.DateTime.MinValue;

    // 応答が読めなくなった時刻（読めない間は5秒まで待つ。以前は1回の失敗で実行全体が止まった）
    private System.DateTime? unreadableSince;

    /// <summary>応答が読めないまま待つ上限。</summary>
    public static readonly System.TimeSpan UnreadableLimit = System.TimeSpan.FromSeconds(5);

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

        // 持っていたのに TextAdvance が読み込まれていない＝読み直し・無効化の途中。どちらも依頼者の記録は消えるので、取り消しとして止める
        // （以前は印を下ろすだけで進み続け、読み直しの後に Questionable が取った設定のまま納品窓まで進んだ）
        if (!this.IsLoaded)
        {
            this.Lost("TextAdvance が読み直されました（または無効にされました）。取り直さずに止めます");
            return false;
        }

        if (this.DetectCancel())
            return false;

        var result = this.ApplyResult(this.requestAllowed);
        if (result == ApplyOutcome.Owned)
        {
            this.unreadableSince = null;
            return true;
        }

        if (result == ApplyOutcome.HeldByOther)
        {
            // こちらが持っていたのに、ほかの依頼者が持っている
            this.unreadableSince = null;
            this.TakenOver();
            return false;
        }

        // 読めない：入力の直前の照合（EnsureTurnInControl）と同じく、5秒までは持っているとみなす。続けば読み直しの途中とみなして止める
        this.unreadableSince ??= System.DateTime.UtcNow;
        if (System.DateTime.UtcNow - this.unreadableSince.Value < UnreadableLimit)
            return true;
        this.unreadableSince = null;
        this.Lost($"TextAdvance が {UnreadableLimit.TotalSeconds:0} 秒応答しません（読み直し・無効化の途中の可能性）。取り直さずに止めます");
        return false;
    }

    /// <summary>持っていた外部制御を、こちらの解除なしに失った。取り消しとして扱い、この実行の間は取り直さない。</summary>
    private void Lost(string reason)
    {
        this.ownControl = false;
        this.requestAllowed = false;
        this.questAcceptOff = false;
        this.appliedAcceptOff = false;
        this.lostExternally = true;
        this.LossReason = reason;
        Core.DebugLog.Current?.Line("IPC", reason);
    }

    /// <summary>
    /// こちらが持っていた外部制御を、ほかの依頼者が持っている。TextAdvance は「誰も持っていない」か「依頼者が同じ」ときしか受け付けないので、
    /// これはこちらの解除なしに外され（TextAdvance の画面での取り消し・読み直し）、ほかのプラグイン（動いている Questionable は毎フレーム取りに行く）が
    /// 取り直した形（以前は印を下ろすだけで進み続け、取り消しの検知がほとんど働かなかった）。取り消しとして扱い、取り直さずに止める。
    /// </summary>
    private void TakenOver()
    {
        this.ownControl = false;
        this.requestAllowed = false;
        this.questAcceptOff = false;
        this.appliedAcceptOff = false;
        this.lostExternally = true;
        this.LossReason = "TextAdvance の外部制御が外され、ほかのプラグイン（Questionable など）が取り直しました。取り直さずに止めます";
        Core.DebugLog.Current?.Line("IPC", this.LossReason);
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
    /// 受注の窓（JournalAccept）を TextAdvance に受けさせる・受けさせないを切り替える（こちらが外部制御を持っているときだけ）。
    /// TextAdvance は、受注を任されていると、どのクエストの受注の窓でも「受ける」を押す（TextAdvance の ExecQuestAccept）。
    /// クエストの完了の直後は、NPC が続けて別のクエストを差し出すことがあるので、後片付けの間だけ切る（不具合の例：
    /// 漁師 Lv15 の完了の直後にシシプが差し出したサイドクエスト「夢をも釣る船」を、手放した直後の TextAdvance が受けた）。
    /// 解除（ReleaseControl）で元に戻る。
    /// </summary>
    public bool AllowQuestAccept(bool allow)
    {
        if (!this.ownControl)
            return false;
        this.questAcceptOff = !allow;
        if (this.appliedAcceptOff == this.questAcceptOff)
            return true;
        return this.Apply(this.requestAllowed);
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

    /// <summary>
    /// 重要な入力の直前に操作権を照合する。空いていれば取得し、他者の所有権は奪わない。
    /// 持っている間は1秒に1回だけ問い合わせる。応答が読めないときは、持っていたなら5秒まで持っているとみなす。
    /// 取り消されたとき（こちらが持っていたはずなのに誰も持っていない）は取り直さずに false。理由は <see cref="LossReason"/>。
    /// </summary>
    public bool EnsureTurnInControl()
    {
        if (this.lostExternally)
            return false;
        var now = System.DateTime.UtcNow;
        if (this.ownControl && !this.requestAllowed && this.unreadableSince == null && now - this.lastEnsure < System.TimeSpan.FromSeconds(1))
            return true;
        this.lastEnsure = now;

        if (this.ownControl && this.DetectCancel())
            return false;

        var result = this.ApplyResult(false);
        if (result == ApplyOutcome.Owned)
        {
            this.unreadableSince = null;
            return true;
        }

        if (result == ApplyOutcome.Unreadable)
        {
            this.unreadableSince ??= now;
            if (now - this.unreadableSince.Value < UnreadableLimit)
                return this.ownControl;
            this.LossReason = $"TextAdvance が {UnreadableLimit.TotalSeconds:0} 秒応答しません（読み込み中の可能性）";
        }
        else if (this.ownControl)
        {
            // こちらが持っていたのに、ほかの依頼者が持っている＝取り消しの後に他者が取り直した
            this.unreadableSince = null;
            this.TakenOver();
            return false;
        }
        else
        {
            this.LossReason = "TextAdvance の外部制御をほかのプラグインが持っています";
        }

        this.unreadableSince = null;
        this.ownControl = false;
        this.requestAllowed = false;
        return false;
    }

    /// <summary>
    /// 入力（選択肢・納品窓）の直前に使う：間引かずに、いま照合する（入力の直前は毎回照合し、他者を奪わない）。
    /// 毎フレームの見張りには <see cref="EnsureTurnInControl"/>（1秒に1回）を使う。
    /// </summary>
    public bool VerifyTurnInControlNow()
    {
        this.lastEnsure = System.DateTime.MinValue;
        return this.EnsureTurnInControl();
    }

    /// <summary>
    /// こちらが持っていたはずの外部制御を、誰も持っていないか（利用者の取り消し、または TextAdvance の読み直し）。
    /// そうなら持っている印を下ろし、この実行の間は取り直さない。読めなければ判断しない（false）。
    /// </summary>
    private bool DetectCancel()
    {
        if (this.IsInExternalControl() != false)
            return false;
        this.ownControl = false;
        this.requestAllowed = false;
        this.releasePending = false;
        this.lostExternally = true;
        this.LossReason = "TextAdvance の外部制御が外されました（TextAdvance の画面での取り消し、または TextAdvance の読み直し）。取り直さずに止めます";
        Core.DebugLog.Current?.Line("IPC", this.LossReason);
        return true;
    }

    private enum ApplyOutcome
    {
        Owned,
        HeldByOther,
        Unreadable,
    }

    private bool Apply(bool allowRequest) => this.ApplyResult(allowRequest) == ApplyOutcome.Owned;

    private ApplyOutcome ApplyResult(bool allowRequest)
    {
        if (this.lostExternally)
            return ApplyOutcome.HeldByOther;

        var cfg = new ExternalTerritoryConfig
        {
            EnableQuestAccept = !this.questAcceptOff,
            EnableQuestComplete = true,
            EnableRewardPick = true,
            EnableRequestHandin = allowRequest,
            EnableCutsceneEsc = true,
            EnableCutsceneSkipConfirm = true,
            EnableTalkSkip = true,
            EnableRequestFill = allowRequest,
            EnableAutoInteract = false,
        };

        // 記録は、取る・設定を変えるときだけ書く（以前は毎フレーム1行ずつ書き、止まったときの「直前の記録」が埋まった）
        if (!this.ownControl || this.requestAllowed != allowRequest || this.appliedAcceptOff != this.questAcceptOff)
            this.Trace($"EnableExternalControl（納品窓の入力={(allowRequest ? "TextAdvance に任せる" : "こちらで行う")}"
                       + $"{(this.questAcceptOff ? "・受注の窓は受けない" : string.Empty)}）");
        this.releasePending = true;
        var received = this.TryInvoke("EnableExternalControl",
                     () => this.Func<string, ExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl")
                         .InvokeFunc(Plugin.InternalNameConst, cfg), out var accepted);
        if (received)
            this.releasePending = accepted;
        if (received && accepted)
        {
            this.ownControl = true;
            this.requestAllowed = allowRequest;
            this.appliedAcceptOff = this.questAcceptOff;
            this.LossReason = null;
            return ApplyOutcome.Owned;
        }

        return received ? ApplyOutcome.HeldByOther : ApplyOutcome.Unreadable;
    }

    /// <summary>こちらが取った外部制御を解除する。</summary>
    public void ReleaseControl()
    {
        // 取り消しの印は、実行の終わり（後始末）で消す（次の実行ではまた取れる）
        this.lostExternally = false;
        this.unreadableSince = null;
        this.questAcceptOff = false; // 受注の切り替えは解除のたびに戻す（次に取るときは受注も任せる）
        if (!this.ownControl && !this.releasePending)
            return;

        this.Trace("DisableExternalControl");

        // 解除できたときだけ「手放した」とする（失敗したら次の呼び出しでやり直す）
        if (this.TryInvoke("DisableExternalControl",
                () => this.Func<string, bool>("TextAdvance.DisableExternalControl").InvokeFunc(Plugin.InternalNameConst), out var released)
            && released)
        {
            this.ownControl = false;
            this.releasePending = false;
            this.requestAllowed = false;
            this.questAcceptOff = false;
            this.appliedAcceptOff = false;
        }
    }

    public bool? IsInExternalControl()
        => this.TryInvoke("IsInExternalControl",
            () => this.Func<bool>("TextAdvance.IsInExternalControl").InvokeFunc(), out var v) ? v : null;
}
