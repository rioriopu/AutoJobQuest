using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Data;

/// <summary>
/// どの製作職が、どのレシピを作れるか。
///  ・ジョブクエを進める職（選んだ職）は Lv70 以上（開始の条件）。
///  ・ほかの製作職はレベルの制限なし（未解放＝Lv0 でもよい）。中間素材のレシピのレベルに届く職がいれば、素材を集めて作る。
///    届く職がいなければ、その中間素材は作らずにマーケットボードで買う（素材も集めない）。
///  ・届く職でも、ギアセットが無ければ作れない（職を替えられない）。また、ギアセットの主道具・副道具・頭・胴・腕・脚・足が
///    Lv68 以上でなければ作らずに買う（製作の失敗で素材を失わないため：開始の条件と同じ線引き。設定で切れる：<see cref="RequireGear"/>）。
/// 状態は外から渡す（ゲームを起動せずに試せるように）。今のキャラクターの状態は <see cref="FromGame"/> で読む。
/// </summary>
public sealed class CraftAbility
{
    private readonly Func<uint, int> level;
    private readonly Func<uint, bool> hasGearset;
    private readonly Func<uint, bool> geared;

    /// <param name="level">職のレベル（未解放なら 0）。</param>
    /// <param name="hasGearset">職のギアセットがあるか。</param>
    /// <param name="geared">職のギアセットの装備が開始の条件（Lv68 以上）を満たすか。</param>
    public CraftAbility(Func<uint, int> level, Func<uint, bool> hasGearset, Func<uint, bool> geared)
    {
        this.level = level;
        this.hasGearset = hasGearset;
        this.geared = geared;
    }

    /// <summary>
    /// 選ばなかった製作職にも、装備の条件（Lv68 以上）を課すか（既定 true。設定から <see cref="Configuration.Normalize"/> が入れる）。
    /// 選んだ職は、この設定にかかわらず開始の条件で確かめる（<see cref="GearCheck.GearProblem"/>）。
    /// </summary>
    public static bool RequireGear { get; set; } = true;

    /// <summary>その職で、そのレベルのレシピを作れるか。</summary>
    public bool Can(uint classJobId, int recipeLevel)
        => this.WhyNot(classJobId, recipeLevel) == null;

    /// <summary>そのレシピを作れるか。</summary>
    public bool Can(Recipe r) => this.Can(Jobs.CraftTypeToClassJob(r.CraftType.RowId), RecipeLevel(r));

    /// <summary>作れない理由（作れるなら null）。</summary>
    public string? WhyNot(uint classJobId, int recipeLevel)
    {
        var lv = this.level(classJobId);
        if (lv <= 0)
            return "未解放";
        if (lv < recipeLevel)
            return $"Lv{lv}・レシピ Lv{recipeLevel}";
        if (!this.hasGearset(classJobId))
            return "ギアセットが無い";
        if (RequireGear && !this.geared(classJobId))
            return $"ギアセットの装備が Lv{GearCheck.RequiredEquipLevel} 未満";
        return null;
    }

    /// <summary>レシピのレベル（職のレベルで比べる値。読めなければ作れない扱いの 999）。</summary>
    public static int RecipeLevel(Recipe r) => r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 999;

    /// <summary>今のキャラクターの状態から作る（フレームワークのスレッドから呼ぶ。ギアセットとレベルを、その時点で写し取る）。</summary>
    public static CraftAbility FromGame()
    {
        var levels = Jobs.Crafters.ToDictionary(j => j, Jobs.Level);
        var gearsets = Jobs.Crafters.ToDictionary(j => j, j => GearCheck.FindGearset(j) >= 0);
        var geared = Jobs.Crafters.ToDictionary(j => j, j => GearCheck.LowGearsetSlots(j) is { Count: 0 });
        return new CraftAbility(levels.GetValueOrDefault, gearsets.GetValueOrDefault, geared.GetValueOrDefault);
    }

    /// <summary>作れない品の説明を1行にまとめる（計画の注意用）。</summary>
    public static string Summary(IReadOnlyDictionary<uint, string> notCraftable)
        => "作れる職がいない中間素材は、作らずにマーケットボードで買います（素材も集めません）："
           + string.Join("、", notCraftable.OrderBy(x => x.Key).Select(x => $"{CraftPlanner.ItemName(x.Key)}（{x.Value}）"));
}
