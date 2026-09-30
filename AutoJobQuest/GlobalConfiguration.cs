using System;
using Dalamud.Configuration;

namespace AutoJobQuest;

/// <summary>
/// 全体で1つの設定（Dalamud の設定ファイル pluginConfigs\AutoJobQuest.json）。
/// ログインする前に使うものだけを置く（ゲーム起動時に画面を開くか・記録の置き場所）。
/// ほかの設定と、他のプラグインを一時的に変えた控えは、キャラクターごとのファイルに置く（<see cref="Configuration"/>。以前は
/// ゲームを2つ起動すると、1つのファイルを2つのゲームが取り合っていた）。
/// ここは設定ファイルを手で書き換えたときにしか変わらないので、2つのゲームで取り合わない。
/// </summary>
[Serializable]
public sealed class GlobalConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// 記録（ログ）を残すフォルダ。空なら自動：開発環境（開発用のフォルダがある）では、そのフォルダの ログ、
    /// それ以外ではプラグインの設定フォルダの ログ（配布したとき、ほかの人の PC に開発用のフォルダを作らない）。
    /// </summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>画面をゲーム起動時に開くか。</summary>
    public bool OpenOnStartup { get; set; }
}
