using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace AutoJobQuest.Ipc;

/// <summary>
/// HQ 指定の品が HQ になる見込み。
///
/// 【なぜ要るか】机上計算では、Lv50 以下の HQ 指定品は CP 180 でも見込み 99.5% 以上だが、Lv53 以上は CP 180 だと 22〜89% に落ちる。
/// 今は HQ にならなかった回数を数えて上限で止めるだけなので、CP が足りないと材料を使ってから止まる。始める前に言えるようにする。
///
/// 【やり方】Artisan 自身の計算を借りる（RSR の内部を読むのと同じ経路。IPC には無い）：
///  ・能力値：CharacterStats.GetBaseStatsForClassHeuristic（その職のギアセット。食事・薬は入れない＝こちらは既定で使わせない）
///  ・レシピの状態：Crafting.BuildCraftStateForRecipe
///  ・ソルバー：CraftingProcessor.GetSolverForRecipe（利用者のレシピ別設定を反映。Raphael 指定で解がまだ無ければ Artisan 自身が標準のソルバーに戻す）
///  ・通し：Simulator.CreateInitial → Solver.Solve → Simulator.Execute を完成まで。状態の変化と行動の成否は乱数で振る
///    （Artisan の画面のシミュレーターと同じく、状態の確率は CraftState.NormalCraftConditionProbabilities。CraftState の既定は空＝ずっと「通常」なので入れる）
///  ・完成時の HQ 率（Calculations.GetHQChance）の平均を見込みとする。完成しなかった回は 0。
/// 【運なしの計算は使わない】Artisan の SolverUtils.SimulateSolverExecution（行動は必ず成功・状態はずっと通常）は、運ありと大きく食い違う
/// （机上計算：CP 180・Lv53 で運なし 100%／運あり 56%、CP 300・Lv60 で運なし 26%／運あり 92%）。
/// 【レベル】計画には、いまのレベルより上のレシピ（後のクエストの品）も入る。いまのレベルで計算すると必ず低く出るので、
/// レシピの職レベルまで上がったとして計算する（装備はいまのギアセットのまま）。
/// </summary>
public static class ArtisanHqEstimate
{
    /// <summary>この見込みを下回ったら注意を出す。</summary>
    public const double WarnBelow = 90;

    /// <summary>1レシピを通す回数の上限（100回ずつ。見込みが注意の境目から十分離れていれば早めに打ち切る）。</summary>
    public const int MaxRuns = 400;

    /// <summary>1レシピの見込み。Percent が null なら計算できなかった（Note に理由）。</summary>
    public sealed record Result(uint RecipeId, uint ItemId, double? Percent, int Runs, string Stats, string Solver, string? Note);

    // 同じ条件（レシピ・能力値・ソルバー）の結果は使い回す（点検のたびに計算し直さない）
    private static readonly Dictionary<string, Result> Cache = [];

    /// <summary>
    /// HQ の見込みを計算する（フレームワークのスレッドから呼ぶ。能力値はゲームの記憶から読む）。
    /// 通すのはレシピごとに並行（Artisan の計算はゲームに触らない）。Artisan の内部が読めなければ error に理由を入れて空を返す。
    /// </summary>
    public static List<Result> Estimate(IEnumerable<(uint RecipeId, uint ItemId, uint ClassJob)> recipes, out string? error)
    {
        error = null;
        var api = Api.Load(out error);
        if (api == null)
            return [];

        var prepared = new List<(string Key, uint RecipeId, uint ItemId, Func<Result> Run)>();
        var results = new List<Result>();
        var recipeSheet = Svc.Data.GetExcelSheet<Recipe>();
        foreach (var (recipeId, itemId, job) in recipes.DistinctBy(r => r.RecipeId))
        {
            try
            {
                if (!recipeSheet.TryGetRow(recipeId, out var recipe))
                    continue;
                var craft = api.BuildCraft(recipe, job);
                var (solver, solverName) = api.CreateSolver(recipeId, craft);
                var stats = api.StatsText(craft);
                if (solver == null)
                {
                    results.Add(new Result(recipeId, itemId, null, 0, stats, solverName, "Artisan のソルバーが選べません"));
                    continue;
                }

                var key = $"{recipeId}|{stats}|{solverName}";
                lock (Cache)
                {
                    if (Cache.TryGetValue(key, out var cached))
                    {
                        results.Add(cached);
                        continue;
                    }
                }

                prepared.Add((key, recipeId, itemId, () => api.Run(recipeId, itemId, craft, solver, stats, solverName)));
            }
            catch (Exception e)
            {
                results.Add(new Result(recipeId, itemId, null, 0, string.Empty, string.Empty, $"準備に失敗（{Unwrap(e).Message}）"));
            }
        }

        var computed = new Result[prepared.Count];
        Parallel.For(0, prepared.Count, i =>
        {
            try
            {
                computed[i] = prepared[i].Run();
            }
            catch (Exception e)
            {
                computed[i] = new Result(prepared[i].RecipeId, prepared[i].ItemId, null, 0, string.Empty, string.Empty, $"計算に失敗（{Unwrap(e).Message}）");
            }
        });

        for (var i = 0; i < prepared.Count; i++)
        {
            if (computed[i].Percent != null)
            {
                lock (Cache)
                    Cache[prepared[i].Key] = computed[i];
            }

            results.Add(computed[i]);
        }

        return results;
    }

    /// <summary>見込みの判断（試せるように分けた部分）：100回ずつ回して、境目から10%以上離れたら打ち切る。</summary>
    public static bool Enough(int runs, double mean)
        => runs >= MaxRuns || (runs >= 100 && Math.Abs(mean - WarnBelow) >= 10);

    /// <summary>
    /// Artisan の DLL に、使う型・関数・欄がその形で実在するか（検証の仕組みから、導入版の DLL を読み込んで確かめる。無ければ null、違えば理由）。
    /// </summary>
    public static string? CheckBinding(Assembly artisan)
    {
        try
        {
            Api.Bind(artisan);
            return null;
        }
        catch (Exception e)
        {
            return Unwrap(e).Message;
        }
    }

    /// <summary>
    /// 検証の仕組み用：能力値を指定して、Artisan の計算で見込みを出す（ゲームの記憶は読まない。レシピ別設定は使わず Artisan の既定のソルバー）。
    /// Artisan の起動時の準備（ソルバーの登録・設定）は呼び出し側で済ませておく。
    /// </summary>
    public static Result SimulateWithStats(Assembly artisan, Recipe recipe, uint classJob, int craftsmanship, int control, int cp, int level)
    {
        var api = Api.Bind(artisan);
        var craft = api.BuildCraft(recipe, classJob, (craftsmanship, control, cp, level));
        var (solver, name) = api.CreateSolver(recipe.RowId, craft);
        return solver == null
            ? new Result(recipe.RowId, recipe.ItemResult.RowId, null, 0, api.StatsText(craft), name, "Artisan のソルバーが選べません")
            : api.Run(recipe.RowId, recipe.ItemResult.RowId, craft, solver, api.StatsText(craft), name);
    }

    private static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    /// <summary>Artisan の内部の型・関数（導入版 4.0.5.212 の逆コンパイルと Artisan のソースで確認した名前）。</summary>
    private sealed class Api
    {
        private static Api? loaded;
        private static Assembly? loadedFrom;

        private MethodInfo statsForJob = null!;
        private Type jobType = null!;
        private FieldInfo statsLevel = null!;
        private FieldInfo statsCraftsmanship = null!;
        private FieldInfo statsControl = null!;
        private FieldInfo statsCp = null!;
        private MethodInfo buildCraft = null!;
        private FieldInfo conditionProbabilities = null!;
        private MethodInfo normalProbabilities = null!;
        private FieldInfo statLevel = null!;
        private FieldInfo statCraftsmanship = null!;
        private FieldInfo statControl = null!;
        private FieldInfo statCp = null!;
        private FieldInfo craftProgress = null!;
        private FieldInfo qualityMax = null!;
        private MethodInfo getSolver = null!;
        private PropertyInfo descName = null!;
        private MethodInfo createSolver = null!;
        private object? recipeConfigs;
        private MethodInfo clone = null!;
        private MethodInfo solve = null!;
        private PropertyInfo recAction = null!;
        private object skillNone = null!;
        private MethodInfo createInitial = null!;
        private MethodInfo status = null!;
        private object inProgress = null!;
        private MethodInfo execute = null!;
        private object cantUse = null!;
        private FieldInfo stepProgress = null!;
        private FieldInfo stepQuality = null!;
        private MethodInfo hqChance = null!;

        public static Api? Load(out string? error)
        {
            error = null;
            try
            {
                var plugin = RsrStateReader.FindPluginInstance("Artisan");
                if (plugin == null)
                {
                    error = "Artisan が読み込まれていません";
                    return null;
                }

                var asm = plugin.GetType().Assembly;
                if (loaded == null || loadedFrom != asm)
                {
                    loaded = Bind(asm);
                    loadedFrom = asm;
                }

                loaded.recipeConfigs = ReadRecipeConfigs(plugin);
                return loaded;
            }
            catch (Exception e)
            {
                error = $"Artisan の内部を読めません（{Unwrap(e).Message}。Artisan の版が変わった可能性）";
                return null;
            }
        }

        /// <summary>DLL から型・関数・欄を結び付ける（無い・形が違えば、何が違うかを書いた例外）。</summary>
        public static Api Bind(Assembly asm)
        {
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type T(string name) => asm.GetType(name, throwOnError: false) ?? throw new MissingMemberException($"型 {name} がありません");
            MethodInfo M(Type t, string name, BindingFlags f, params Type[] args)
            {
                var m = t.GetMethods(f).Where(x => x.Name == name).ToList();
                if (m.Count != 1)
                    throw new MissingMemberException($"{t.Name}.{name} が {m.Count} 個あります（1個のはず）");
                var ps = m[0].GetParameters();
                if (ps.Length != args.Length)
                    throw new MissingMemberException($"{t.Name}.{name} の引数が {ps.Length} 個です（{args.Length} 個のはず）");
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != typeof(object) && ps[i].ParameterType != args[i])
                        throw new MissingMemberException($"{t.Name}.{name} の {i + 1} 番目の引数が {ps[i].ParameterType.Name} です（{args[i].Name} のはず）");
                }

                return m[0];
            }

            FieldInfo F(Type t, string name, Type type)
            {
                var f = t.GetField(name, I) ?? throw new MissingMemberException($"{t.Name}.{name} がありません");
                if (f.FieldType != type)
                    throw new MissingMemberException($"{t.Name}.{name} の型が {f.FieldType.Name} です（{type.Name} のはず）");
                return f;
            }

            var any = typeof(object);
            var api = new Api();
            var statsType = T("Artisan.GameInterop.CharacterStats");
            var craftType = T("Artisan.CraftingLogic.CraftState");
            var stepType = T("Artisan.CraftingLogic.StepState");
            api.statsForJob = M(statsType, "GetBaseStatsForClassHeuristic", S, any);
            api.jobType = api.statsForJob.GetParameters()[0].ParameterType;
            if (!api.jobType.IsEnum || api.statsForJob.ReturnType != statsType)
                throw new MissingMemberException("CharacterStats.GetBaseStatsForClassHeuristic の形が違います（職の enum を受け取り CharacterStats を返すはず）");
            api.statsLevel = F(statsType, "Level", typeof(int));
            api.statsCraftsmanship = F(statsType, "Craftsmanship", typeof(int));
            api.statsControl = F(statsType, "Control", typeof(int));
            api.statsCp = F(statsType, "CP", typeof(int));

            api.buildCraft = M(T("Artisan.GameInterop.Crafting"), "BuildCraftStateForRecipe", S, statsType, api.jobType, typeof(Recipe));
            if (api.buildCraft.ReturnType != craftType)
                throw new MissingMemberException("Crafting.BuildCraftStateForRecipe が CraftState を返しません");

            api.conditionProbabilities = F(craftType, "CraftConditionProbabilities", typeof(float[]));
            api.normalProbabilities = M(craftType, "NormalCraftConditionProbabilities", S, typeof(int));
            api.statLevel = F(craftType, "StatLevel", typeof(int));
            api.statCraftsmanship = F(craftType, "StatCraftsmanship", typeof(int));
            api.statControl = F(craftType, "StatControl", typeof(int));
            api.statCp = F(craftType, "StatCP", typeof(int));
            api.craftProgress = F(craftType, "CraftProgress", typeof(int));
            api.qualityMax = F(craftType, "CraftQualityMax", typeof(int));

            var recipeConfigType = T("Artisan.CraftingLogic.RecipeConfig");
            api.getSolver = M(T("Artisan.CraftingLogic.CraftingProcessor"), "GetSolverForRecipe", S, recipeConfigType, craftType);
            var descType = api.getSolver.ReturnType;
            api.descName = descType.GetProperty("Name", I) ?? throw new MissingMemberException("ISolverDefinition.Desc.Name がありません");
            var solverType = T("Artisan.CraftingLogic.Solver");
            api.createSolver = M(descType, "CreateSolver", I, craftType);
            if (api.createSolver.ReturnType != solverType)
                throw new MissingMemberException("Desc.CreateSolver が Solver を返しません");

            api.clone = M(solverType, "Clone", I);
            api.solve = M(solverType, "Solve", I, craftType, stepType);
            api.recAction = api.solve.ReturnType.GetProperty("Action", I) ?? throw new MissingMemberException("Solver.Recommendation.Action がありません");
            api.skillNone = Enum.Parse(api.recAction.PropertyType, "None");

            var simType = T("Artisan.CraftingLogic.Simulator");
            api.createInitial = M(simType, "CreateInitial", S, craftType, typeof(int));
            api.status = M(simType, "Status", S, craftType, stepType);
            api.inProgress = Enum.Parse(api.status.ReturnType, "InProgress");
            api.execute = M(simType, "Execute", S, craftType, stepType, api.recAction.PropertyType, typeof(float), typeof(float));
            var item1 = api.execute.ReturnType.GetField("Item1") ?? throw new MissingMemberException("Simulator.Execute の戻り値の形が違います");
            if (api.execute.ReturnType.GetField("Item2")?.FieldType != stepType)
                throw new MissingMemberException("Simulator.Execute の戻り値に StepState がありません");
            api.cantUse = Enum.Parse(item1.FieldType, "CantUse");

            api.stepProgress = F(stepType, "Progress", typeof(int));
            api.stepQuality = F(stepType, "Quality", typeof(int));

            api.hqChance = M(T("Artisan.CraftingLogic.Calculations"), "GetHQChance", S, typeof(double));
            if (api.hqChance.ReturnType != typeof(int))
                throw new MissingMemberException("Calculations.GetHQChance が int を返しません");

            // 利用者のレシピ別設定（読むだけ）：Artisan.P.Config.RecipeConfigs
            var configType = T("Artisan.Configuration");
            if (T("Artisan.Artisan").GetField("Config", I)?.FieldType != configType)
                throw new MissingMemberException("Artisan.Config がありません");
            var dict = configType.GetField("RecipeConfigs", I)?.FieldType;
            if (dict == null || !typeof(IDictionary).IsAssignableFrom(dict) || dict.GetGenericArguments() is not [var k, var v] || k != typeof(uint) || v != recipeConfigType)
                throw new MissingMemberException("Configuration.RecipeConfigs の形が違います（uint → RecipeConfig の辞書のはず）");

            return api;
        }

        // 利用者のレシピ別設定（P.Config.RecipeConfigs）。読めなければ null（そのときは Artisan の既定のソルバーになる）
        private static object? ReadRecipeConfigs(object plugin)
        {
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var config = plugin.GetType().GetField("Config", I)?.GetValue(plugin);
            return config?.GetType().GetField("RecipeConfigs", I)?.GetValue(config);
        }

        /// <summary>
        /// レシピの状態（能力値はその職のギアセット、レベルはレシピの職レベルまで上げる）。
        /// given を渡すと、能力値とレベルをその値にする（検証の仕組み用。ゲームの記憶を読まない）。
        /// </summary>
        public object BuildCraft(Recipe recipe, uint classJob, (int Craftsmanship, int Control, int Cp, int Level)? given = null)
        {
            var job = Enum.ToObject(this.jobType, classJob);
            object stats;
            if (given is { } g)
            {
                stats = Activator.CreateInstance(this.statsLevel.DeclaringType!)!;
                this.statsCraftsmanship.SetValue(stats, g.Craftsmanship);
                this.statsControl.SetValue(stats, g.Control);
                this.statsCp.SetValue(stats, g.Cp);
                this.statsLevel.SetValue(stats, g.Level);
            }
            else
            {
                // CharacterStats は構造体。箱に入った値の欄を書き換え、その箱のまま渡す
                stats = this.statsForJob.Invoke(null, [job])!;
                var need = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
                var now = Data.Jobs.Level(classJob);
                this.statsLevel.SetValue(stats, Math.Max(now, (int)need));
            }

            var craft = this.buildCraft.Invoke(null, [stats, job, recipe])!;
            var level = (int)this.statLevel.GetValue(craft)!;
            this.conditionProbabilities.SetValue(craft, this.normalProbabilities.Invoke(null, [level]));
            return craft;
        }

        public (object? Solver, string Name) CreateSolver(uint recipeId, object craft)
        {
            object? config = null;
            if (this.recipeConfigs is IDictionary d && d.Contains(recipeId))
                config = d[recipeId];
            var desc = this.getSolver.Invoke(null, [config, craft])!;
            var name = this.descName.GetValue(desc) as string ?? string.Empty;
            var solver = string.IsNullOrEmpty(name) ? null : this.createSolver.Invoke(desc, [craft]);
            return (solver, name);
        }

        public string StatsText(object craft)
            => $"作業精度 {this.statCraftsmanship.GetValue(craft)}・加工精度 {this.statControl.GetValue(craft)}・CP {this.statCp.GetValue(craft)}・Lv{this.statLevel.GetValue(craft)}";

        /// <summary>乱数で何度も通して、完成時の HQ 率の平均を出す（乱数の種はレシピで決める：点検のたびに数字が揺れないように）。</summary>
        public Result Run(uint recipeId, uint itemId, object craft, object solver, string stats, string solverName)
        {
            var rng = new Random(unchecked((int)(recipeId * 7919u + 17u)));
            var progressGoal = (int)this.craftProgress.GetValue(craft)!;
            var maxQuality = (int)this.qualityMax.GetValue(craft)!;
            double sum = 0;
            var runs = 0;
            while (true)
            {
                for (var i = 0; i < 100; i++, runs++)
                    sum += this.OneRun(craft, solver, rng, progressGoal, maxQuality);
                if (Enough(runs, sum / runs))
                    break;
            }

            return new Result(recipeId, itemId, sum / runs, runs, stats, solverName, null);
        }

        private double OneRun(object craft, object solver, Random rng, int progressGoal, int maxQuality)
        {
            var s = this.clone.Invoke(solver, null)!;
            var step = this.createInitial.Invoke(null, [craft, 0])!;
            for (var guard = 0; guard < 200 && this.inProgress.Equals(this.status.Invoke(null, [craft, step])); guard++)
            {
                var action = this.recAction.GetValue(this.solve.Invoke(s, [craft, step]))!;
                if (action.Equals(this.skillNone))
                    break;
                var tuple = this.execute.Invoke(null, [craft, step, action, (float)rng.NextDouble(), (float)rng.NextDouble()])!;
                var tt = tuple.GetType();
                if (tt.GetField("Item1")!.GetValue(tuple)!.Equals(this.cantUse))
                    break;
                step = tt.GetField("Item2")!.GetValue(tuple)!;
            }

            if ((int)this.stepProgress.GetValue(step)! < progressGoal || maxQuality <= 0)
                return 0;
            var quality = (int)this.stepQuality.GetValue(step)!;
            return (int)this.hqChance.Invoke(null, [quality * 100.0 / maxQuality])!;
        }
    }
}
