namespace AutoJobQuest.Ipc;

/// <summary>
/// TextAdvance への窓口（導入版 3.3.0.1 を正とする）。
///
/// Questionable は動いている間、自分で TextAdvance の外部制御を取る（納品の自動入力も含む）。
/// こちらが外部制御を取るのは、Questionable が止まる手順（木工 Lv20 のマテリア装着待ち）を
/// 自前で報告するときだけ。使い終わったら必ず解除する（他者が制御中だと Questionable が取りに行けない）。
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

    /// <summary>
    /// 報告（会話送り・受注・完了・報酬選び）を任せる設定で外部制御を取る。
    /// 納品窓への入力と受け渡し（RequestFill / RequestHandin）は任せない：こちらの RequestFill.cs が
    /// 条件（HQ 等）に合う品を選んで入れて渡すため（同じ窓を2つが取り合わないように）。
    /// </summary>
    public bool TakeControlForTurnIn()
    {
        if (this.ownControl)
            return true;

        var cfg = new ExternalTerritoryConfig
        {
            EnableQuestAccept = true,
            EnableQuestComplete = true,
            EnableRewardPick = true,
            EnableRequestHandin = false,
            EnableCutsceneEsc = true,
            EnableCutsceneSkipConfirm = true,
            EnableTalkSkip = true,
            EnableRequestFill = false,
            EnableAutoInteract = false,
        };

        this.Trace("EnableExternalControl（報告用）");
        var ok = this.TryInvoke("EnableExternalControl",
                     () => this.Func<string, ExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl")
                         .InvokeFunc(Plugin.InternalNameConst, cfg), out var accepted)
                 && accepted;
        if (ok)
            this.ownControl = true;
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
            this.ownControl = false;
    }

    public bool? IsInExternalControl()
        => this.TryInvoke("IsInExternalControl",
            () => this.Func<bool>("TextAdvance.IsInExternalControl").InvokeFunc(), out var v) ? v : null;
}
