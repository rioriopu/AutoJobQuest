using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AutoJobQuest.Core;

namespace AutoJobQuest.Ui;

/// <summary>
/// はい／いいえの確認窓。作業が質問を出している間だけ開く。
///
/// マーケットボードの購入額が 500,000 ギルを超えるときに警告を出し、
/// 「はい」で自動動作を続け、「いいえ」で自動動作を止める。
/// 「いいえ」を押したら、質問した作業の答えを待たずに実行係ごと止める。
/// </summary>
public sealed class ConfirmWindow : Window
{
    private readonly ConfirmService confirm;
    private readonly Runner runner;

    public ConfirmWindow(ConfirmService confirm, Runner runner)
        : base("AutoJobQuest の確認##AutoJobQuestConfirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.confirm = confirm;
        this.runner = runner;
        this.IsOpen = true;
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
    }

    /// <summary>
    /// 閉じられても毎回開き直す。× や Esc で閉じると質問が見えなくなり、作業が答えを永久に待つため
    /// である。表示するかどうかは <see cref="DrawConditions"/>（質問があるか）で決める。
    /// </summary>
    public override void PreOpenCheck() => this.IsOpen = true;

    public override bool DrawConditions() => this.confirm.Question != null;

    public override void PreDraw()
    {
        // 見落とさないよう画面の中央に出す
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var q = this.confirm.Question;
        if (q == null)
            return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), this.confirm.Title);
        ImGui.Separator();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.TextUnformatted(q);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("はい", new Vector2(120, 0)))
            this.confirm.Reply(true);

        ImGui.SameLine();
        if (ImGui.Button("いいえ", new Vector2(120, 0)))
        {
            this.confirm.Reply(false);
            this.runner.RequestStop("確認で「いいえ」が押されました");
        }
    }
}
