namespace AutoJobQuest.Automation;

/// <summary>
/// 釣りのエサは万能ルアー（低確率で消失する可能性があるため5個所持する。
/// 全て消失してしまった場合のみ、リムサの万能ルアー販売 NPC から5個購入する）。
///
/// GBR は竿の釣りのプリセットを作るとき、魚の決まったエサを持っていればそれを、無ければ万能ルアーを使う
/// （GBR AutoHookPresetBuilder.cs:292-303, 381-392, 456-467）。万能ルアーを持っているかは見ないので、0個だと投げられないまま
/// 上限（90分）まで待つ。そこで、竿の釣りの前に0個なら5個買い、釣りの途中で0個になったら GBR を止めて次の周回で買い直す。
///
/// 買う場所はゲームデータで確かめた：万能ルアーを置くギルショップは 263015「アイテムの購入」の1つだけで、それを持つ NPC は
/// 「よろず屋」の2人（ENpc 1005422・1032822）。リムサ・ロミンサはそのうち 1005422（下甲板層・漁師ギルドのそば (-397.6, 3.1, 81.0)。
/// Questionable の漁師クエスト「漁師の苦悩 2087」等もここで万能ルアーを買う）。
/// ゲームに触らない（数は外から渡す）ので、ゲームを起動せずに試せる。
/// </summary>
public static class VersatileLure
{
    /// <summary>万能ルアー（Item）。</summary>
    public const uint ItemId = 29717;

    /// <summary>リムサ・ロミンサ：下甲板層のよろず屋（ENpc）。</summary>
    public const uint LimsaVendorNpc = 1005422;

    /// <summary>万能ルアーを置くギルショップ（GilShop「アイテムの購入」）。</summary>
    public const uint GilShop = 263015;

    /// <summary>リムサ・ロミンサ：下甲板層（TerritoryType）。</summary>
    public const uint LimsaLowerDecks = 129;

    /// <summary>1回に買う数。</summary>
    public const int BuyCount = 5;

    /// <summary>買うか：全部なくなったときだけ（1〜4個なら買い足さない）。</summary>
    public static bool ShouldBuy(int held) => held <= 0;

    /// <summary>竿の釣りを始めてよいか・続けてよいか（1個以上あるか）。</summary>
    public static bool CanFish(int held) => held > 0;

    /// <summary>竿の釣りの前に買うもの（買わなくてよければ null）。</summary>
    public static VendorNeed? PurchaseBefore(int held)
        => ShouldBuy(held) ? new VendorNeed(ItemId, BuyCount, LimsaVendorNpc) : null;
}
