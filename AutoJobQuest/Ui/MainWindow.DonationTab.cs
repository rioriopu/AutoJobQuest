using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AutoJobQuest.Ui;

// 主画面の「ご支援」タブ。ほかのタブと関わりの無い描画なので、別のファイルに分けた。
public sealed partial class MainWindow
{
    private const string PatreonUrl = "https://www.patreon.com/c/SuppotToEstell";

    private void DrawDonationTab()
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.5f, 1f));
        ImGui.TextWrapped("AutoJobQuest をご利用いただき、誠にありがとうございます");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.TextWrapped(
            "皆さまの温かいご支援が、本プラグインの開発・メンテナンスを支える大きな力となっております。\n" +
            "頂いたサポートは新機能の開発、不具合修正、FFXIV のメジャーパッチへの追従に大切に使わせていただきます。\n" +
            "今後ともどうぞよろしくお願いいたします。");
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f, 1f, 0.85f, 1f));
        ImGui.TextWrapped("いつもご支援くださり、心より感謝申し上げます。");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Patreon で支援する##openpatreon", new Vector2(240, 36)))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = PatreonUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.log.Warn("ご支援", $"Patreon の URL を開けませんでした: {ex.Message}");
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("ブラウザで Patreon のページを開きます。");

        ImGui.SameLine();
        if (ImGui.Button("URL をコピー##copypatreon", new Vector2(160, 36)))
            ImGui.SetClipboardText(PatreonUrl);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Patreon の URL をクリップボードにコピーします。");

        ImGui.Spacing();
        ImGui.TextDisabled(PatreonUrl);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("※ Patreon サイトの利用は外部サービスとして行われます。AutoJobQuest は寄付処理には一切関与しません。");
    }
}
