using System.Linq;

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

    /// <summary>
    /// Artisan の RecipeConfig の「使わない」の値（CraftingLogic\RecipeConfig.cs：Default=0・Disabled=1）。
    /// 一時指定（Temp…）に入れると、そのレシピでは食事・薬を使わない（FoodEnabled/PotionEnabled が false）。
    /// </summary>
    public const uint ConsumableDisabled = 1;

    /// <summary>
    /// そのレシピの食事・薬を一時的に「使わない」にする（IPC ChangeFood・ChangePotion の temporary=true。
    /// Artisan の一時指定は [NonSerialized] で保存されない。IPC.cs の ChangeFood・ChangePotion）。両方送れたら true。
    /// </summary>
    public bool DisableConsumablesTemporarily(uint recipeId)
    {
        this.Trace($"ChangeFood/ChangePotion(レシピ {recipeId}, 使わない, 一時)");
        var food = this.TryAction("ChangeFood",
            () => this.Func<uint, uint, bool, bool, object>("Artisan.ChangeFood").InvokeAction(recipeId, ConsumableDisabled, false, true));
        var potion = this.TryAction("ChangePotion",
            () => this.Func<uint, uint, bool, bool, object>("Artisan.ChangePotion").InvokeAction(recipeId, ConsumableDisabled, false, true));
        return food && potion;
    }

    /// <summary>一時的な食事・薬の指定を元に戻す（IPC SetTempFoodBackToNormal・SetTempPotionBackToNormal）。両方送れたら true。</summary>
    public bool RestoreConsumables(uint recipeId)
    {
        this.Trace($"SetTempFoodBackToNormal/SetTempPotionBackToNormal(レシピ {recipeId})");
        var food = this.TryAction("SetTempFoodBackToNormal",
            () => this.Func<uint, object>("Artisan.SetTempFoodBackToNormal").InvokeAction(recipeId));
        var potion = this.TryAction("SetTempPotionBackToNormal",
            () => this.Func<uint, object>("Artisan.SetTempPotionBackToNormal").InvokeAction(recipeId));
        return food && potion;
    }

    /// <summary>
    /// 控えに残っている一時指定をすべて戻す（戻せたものは控えから消す）。戻せたレシピの数を返す。
    /// Artisan が読み込まれていなければ何もしない（Artisan を読み込み直せば一時指定は消えるが、読み込み直したかは分からないので控えは残す）。
    /// </summary>
    public int RestoreLeftoverConsumables(Configuration config)
    {
        if (config.ArtisanTempConsumableRecipes.Count == 0 || !this.IsLoaded)
            return 0;
        var done = 0;
        foreach (var recipeId in config.ArtisanTempConsumableRecipes.ToList())
        {
            if (!this.RestoreConsumables(recipeId))
                continue;
            config.ArtisanTempConsumableRecipes.Remove(recipeId);
            done++;
        }

        if (done > 0)
            config.Save();
        return done;
    }

    /// <summary>Endurance を止める（こちらが CraftItem で始めた製作を止めるときだけ使う）。</summary>
    public bool SetEndurance(bool on)
        => this.TraceThen($"SetEnduranceStatus({on})") && this.TryAction("SetEnduranceStatus",
            () => this.Func<bool, object>("Artisan.SetEnduranceStatus").InvokeAction(on));
}
