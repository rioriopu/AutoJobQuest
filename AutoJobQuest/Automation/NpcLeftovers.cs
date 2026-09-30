using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// NPC が出したまま残る窓（受注の窓・選択肢・会話）を見つけて閉じる（不具合の例：漁師 Lv30 の完了の直後に、
/// シシプの普段のメニュー「何を聞く？」が開いたまま残り、次のクエストを頼む前の「動ける状態を待つ」で止まり続けた。
/// Lv15 の完了の直後には、シシプが続けて差し出したサイドクエスト「夢をも釣る船」の受注の窓を TextAdvance が受けた）。
/// ゲームはクエストの完了の約0.05秒後に、同じ NPC の会話を続けることがある（次のクエストの差し出し・普段のメニュー）。
/// 閉じ方：受注の窓は「受注しない」（受けない）、選択肢は何も選ばずに閉じる（-1：GBR の VendorInteractionHelper・KnockOnIssuerTask と同じ）、
/// 会話は送る。何も選ばない・受けないので、閉じても失うものは無い。
/// </summary>
public static unsafe class NpcLeftovers
{
    public enum Kind
    {
        /// <summary>NPC の窓は開いていない。</summary>
        None,

        /// <summary>受注の窓（JournalAccept）。</summary>
        QuestOffer,

        /// <summary>選択肢（SelectString・SelectIconString）。</summary>
        Menu,

        /// <summary>会話の窓（Talk）。</summary>
        Talk,
    }

    /// <summary>開いている NPC の窓（受注の窓 → 選択肢 → 会話の順に見る）と、記録に書く中身。</summary>
    public static Kind Find(out AtkUnitBase* addon, out string detail)
    {
        detail = string.Empty;
        if (GameUi.IsReady("JournalAccept", out addon))
        {
            detail = GameUi.QuestOfferTitle(addon) is { Length: > 0 } title ? $"受注の窓「{title}」" : "受注の窓";
            return Kind.QuestOffer;
        }

        if (GameUi.MenuEntries(out addon) is { } entries)
        {
            detail = $"選択肢（{string.Join(" / ", entries)}）";
            return Kind.Menu;
        }

        if (GameUi.IsReady("Talk", out addon))
        {
            detail = "会話の窓";
            return Kind.Talk;
        }

        addon = null;
        return Kind.None;
    }

    /// <summary>その窓を閉じる（受注の窓は受けない・選択肢は選ばない・会話は送る）。</summary>
    public static void Close(Kind kind, AtkUnitBase* addon)
    {
        switch (kind)
        {
            case Kind.QuestOffer:
                GameUi.DeclineQuestOffer(addon);
                break;
            case Kind.Menu:
                GameUi.Fire(addon, true, -1);
                break;
            case Kind.Talk:
                GameUi.AdvanceTalk();
                break;
        }
    }
}
