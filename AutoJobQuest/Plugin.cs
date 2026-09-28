using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using AutoJobQuest.Core;
using AutoJobQuest.Ui;

namespace AutoJobQuest;

/// <summary>
/// 製作系ジョブクエ（木工〜調理、Lv1〜60）を自動で進める。
///
/// 流れ:
///   事前点検 → 計画（納品要件・製作リスト・素材の入手元）
///   → 素材集め（マーケットボード・NPC購入・戦闘→採集・釣り）
///   → 秘伝書（未読なら収集品→紫貨→交換→使用）
///   → グリダニアの宿屋で製作 → マテリア装着
///   → クエストの進行（Questionable）
///
/// 他プラグインの本体（DLL）には手を加えない。公開されている IPC で連携し、IPC が無い GBR の
/// 自動採集リスト・NPC 購入リスト・帰宅設定だけはリフレクションで一時的に書き換える
/// （GBR 自身の一時リストと同じ作法。止めたら元に戻し、落ちても次回起動で戻す）。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    /// <summary>このプラグインの InternalName（他プラグインの停止要求・外部制御の要求者名に使う）。</summary>
    public const string InternalNameConst = "AutoJobQuest";

    private const string CommandName = "/ajq";

    private readonly WindowSystem windows = new("AutoJobQuest");

    private readonly Configuration config;
    private readonly RunLog log;
    private readonly Services services;
    private readonly MainWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        this.config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Initialize(Svc.PluginInterface);

        this.log = new RunLog();
        this.services = new Services(this.config, this.log);
        this.window = new MainWindow(this.config, this.log, this.services);

        this.windows.AddWindow(this.window);
        foreach (var w in this.services.ExtraWindows)
            this.windows.AddWindow(w);

        Svc.PluginInterface.UiBuilder.Draw += this.windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "ジョブクエ自動化の画面を開きます。/ajq stop で止めます。",
        });

        Svc.Framework.Update += this.OnUpdate;

        this.log.Write("Info", "読み込みました");

        if (this.config.OpenOnStartup)
            this.window.IsOpen = true;
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            this.services.Tick();
        }
        catch (Exception ex)
        {
            // 毎フレーム走るので、ここで止めて次のフレームで続ける
            Svc.Log.Error($"[AutoJobQuest] 処理中に例外が出ました: {ex}");
            this.services.OnUnhandled(ex);
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim())
        {
            case "":
                this.window.IsOpen = !this.window.IsOpen;
                break;

            case "stop":
                this.services.Runner.RequestStop("コマンド /ajq stop");
                break;

            default:
                Svc.Chat.Print($"使い方: {CommandName}（画面の開閉） / {CommandName} stop（止める）");
                break;
        }
    }

    private void OpenWindow()
        => this.window.IsOpen = true;

    public void Dispose()
    {
        Svc.Framework.Update -= this.OnUpdate;
        Svc.Commands.RemoveHandler(CommandName);

        Svc.PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;

        this.windows.RemoveAllWindows();

        // 【アンロード経路では相手のプラグインに触れない】
        // ここで他プラグインを止めたり設定を戻したりすると、こちらを再読み込みしただけで
        // 相手の動作が変わってしまう。こちらが動かしていた処理の後始末は、
        // 停止ボタン・停止コマンド・失敗時の経路で行う（Runner.RequestStop）。
        this.services.Dispose();
    }
}
