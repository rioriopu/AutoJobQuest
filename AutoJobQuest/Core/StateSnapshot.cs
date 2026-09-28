using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using AutoJobQuest.Automation;
using AutoJobQuest.Data;
using AutoJobQuest.Ipc;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AutoJobQuest.Core;

/// <summary>
/// その瞬間のゲームと各プラグインの状態を文字にする（不具合を調べるための写し）。
/// 項目ごとに try/catch で囲み、1つが読めなくても残りは書く。フレームワークのスレッドから呼ぶこと。
/// </summary>
public static class StateSnapshot
{
    public static string Capture(TaskContext ctx, Runner? runner)
    {
        var sb = new StringBuilder();
        void Section(string title, Action body)
        {
            sb.AppendLine($"■ {title}");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  （読めませんでした: {ex.GetType().Name}: {ex.Message}）");
            }
        }

        Section("キャラクター", () =>
        {
            var me = Svc.Objects.LocalPlayer;
            sb.AppendLine($"  名前: {me?.Name.TextValue ?? "（未ログイン）"}@{me?.HomeWorld.ValueNullable?.Name.ExtractText()}");
            sb.AppendLine($"  エリア: {Me.Territory} {TeleportTask.TerritoryName(Me.Territory)}");
            var p = Me.Position;
            sb.AppendLine($"  座標: ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})");
            sb.AppendLine($"  ジョブ: {Jobs.Name(Jobs.CurrentClassJob)} Lv{Jobs.Level(Jobs.CurrentClassJob)}  HP {me?.CurrentHp}/{me?.MaxHp}");
            sb.AppendLine($"  ギル: {Inventory.Gil():N0}  カバンの空き: {Inventory.FreeBagSlots()}");
        });

        Section("状態フラグ（立っているもの）", () =>
        {
            var on = Enum.GetValues<ConditionFlag>().Distinct().Where(f => Svc.Condition[f]).Select(f => f.ToString());
            sb.AppendLine("  " + string.Join(", ", on));
        });

        Section("ターゲット", () =>
        {
            var t = Svc.Targets.Target;
            if (t == null)
            {
                sb.AppendLine("  なし");
                return;
            }

            sb.AppendLine($"  {t.Name.TextValue} 種類={t.ObjectKind} BaseId={t.BaseId} 距離={Vector3.Distance(t.Position, Me.Position):0.0}m");
        });

        Section("開いている画面", () => sb.AppendLine("  " + string.Join(", ", VisibleAddons())));

        Section("実行中の作業", () =>
        {
            if (runner?.Root is { } root)
                sb.AppendLine($"  {root.Name}: {root.Status}（経過 {root.Elapsed:hh\\:mm\\:ss}）");
            else
                sb.AppendLine($"  止まっています。前回: {runner?.LastResult}");
            sb.AppendLine($"  この実行でマーケットに払った額: {MarketBoardTask.SpentThisRun:N0} ギル");
        });

        Section("他のプラグイン", () =>
        {
            sb.AppendLine($"  GBR: 自動採集={ctx.GatherBuddy.IsAutoGatherEnabled()} 待機={ctx.GatherBuddy.IsWaiting()} 状態=「{ctx.GatherBuddy.StatusText()}」 購入中={ctx.Gbr.VendorIsBusy()} 購入状態=「{ctx.Gbr.VendorStatusText()}」");
            sb.AppendLine($"  Artisan: 処理中={ctx.Artisan.IsBusy()} Endurance={ctx.Artisan.IsEndurance()} リスト実行中={ctx.Artisan.IsListRunning()} 停止要求={ctx.Artisan.GetStopRequest()}");
            var step = ctx.Questionable.GetCurrentStepData();
            sb.AppendLine($"  Questionable: 実行中={ctx.Questionable.IsRunning()} クエスト={ctx.Questionable.GetCurrentQuestId()} 手順={(step == null ? "なし" : $"{step.Sequence}-{step.Step} {step.InteractionType} 位置={step.Position} エリア={step.TerritoryId}")}");
            sb.AppendLine($"  RSR: 自動ローテ={ctx.Rotation.IsActive()} こちらの優先指定={ctx.Rotation.HasOwnPriorities}");
            sb.AppendLine($"  Lifestream: 処理中={ctx.Lifestream.IsBusy()}");
            sb.AppendLine($"  vnavmesh: 使える={ctx.Navmesh.IsReady()} 移動中={ctx.Navmesh.IsMoving()} 構築={ctx.Navmesh.BuildProgress()}");
            sb.AppendLine($"  TextAdvance: 外部制御中={ctx.TextAdvance.IsInExternalControl()}");
            sb.AppendLine($"  AutoHook: 有効={ctx.AutoHook.GetPluginState()}");
        });

        Section("直近の IPC の失敗", () =>
        {
            IpcGate[] gates = [ctx.Artisan, ctx.Questionable, ctx.Lifestream, ctx.Navmesh, ctx.GatherBuddy, ctx.Rotation, ctx.AutoHook, ctx.TextAdvance, ctx.YesAlready];
            var any = false;
            foreach (var g in gates)
            {
                foreach (var (label, detail) in g.LastErrors)
                {
                    sb.AppendLine($"  {g.DisplayName}.{label}: {detail}");
                    any = true;
                }
            }

            if (ctx.Gbr.LastError != null)
            {
                sb.AppendLine($"  GBR（リフレクション）: {ctx.Gbr.LastError}");
                any = true;
            }

            if (!any)
                sb.AppendLine("  なし");
        });

        return sb.ToString();
    }

    /// <summary>短い1行の状態（定期的に記録へ書く用）。</summary>
    public static string Compact(Runner runner)
    {
        try
        {
            var p = Me.Position;
            var flags = new[] { ConditionFlag.InCombat, ConditionFlag.Mounted, ConditionFlag.BetweenAreas, ConditionFlag.OccupiedInEvent, ConditionFlag.Crafting, ConditionFlag.Gathering, ConditionFlag.Fishing, ConditionFlag.Casting }
                .Where(f => Svc.Condition[f]).Select(f => f.ToString());
            return $"エリア {Me.Territory} ({p.X:0},{p.Y:0},{p.Z:0}) ジョブ {Jobs.CurrentClassJob} 状態[{string.Join(",", flags)}] 画面[{string.Join(",", VisibleAddons().Take(8))}] 作業「{runner.Root?.Status}」";
        }
        catch (Exception ex)
        {
            return $"（状態を読めませんでした: {ex.Message}）";
        }
    }

    /// <summary>表示中のアドオンの名前。</summary>
    public static unsafe List<string> VisibleAddons()
    {
        var list = new List<string>();
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null)
            return list;

        var units = mgr->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < units.Count; i++)
        {
            var u = units.Entries[i].Value;
            if (u == null || !u->IsVisible)
                continue;
            var name = u->NameString;
            if (!string.IsNullOrEmpty(name) && !HiddenNoise.Contains(name))
                list.Add(name);
        }

        return list;
    }

    // 常に出ている画面（記録の邪魔になるので省く）
    private static readonly HashSet<string> HiddenNoise =
    [
        "_ParameterWidget", "_Money", "_MainCommand", "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09", "_ActionBarEx", "_ActionCross", "_ActionDoubleCrossL",
        "_ActionDoubleCrossR", "_NaviMap", "_DTR", "ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3",
        "_StatusCustom0", "_StatusCustom1", "_StatusCustom2", "_StatusCustom3", "_Exp", "_TargetInfo", "_TargetInfoMainTarget",
        "_TargetInfoBuffDebuff", "_TargetInfoCastBar", "_FocusTargetInfo", "_PartyList", "_AllianceList1", "_AllianceList2",
        "_EnemyList", "_ToDoList", "_Image", "_Image3", "_TextError", "_TextClassChange", "_AreaText", "_WideText", "_PoisonText",
        "_BagWidget", "_CastBar", "_ScreenText", "_Notification", "_NotificationCircleBook", "_NotificationParty", "_LimitBreak",
        "NamePlate", "_FlyText", "_MiniTalk", "_Status", "_ContentGauge", "_CharaCard", "_ActionContents", "_Hud",
    ];
}
