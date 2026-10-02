using System;
using Dalamud.Configuration;

namespace AutoJobQuest;

/// <summary>
/// 全体で1つの設定（Dalamud の設定ファイル pluginConfigs\AutoJobQuest.json）。
/// ログインする前に使うものだけを置く（ゲーム起動時に画面を開くか・記録の置き場所）。
/// ほかの設定と、他のプラグインを一時的に変えた控えは、キャラクターごとのファイルに置く（<see cref="Configuration"/>。以前は
/// ゲームを2つ起動すると、1つのファイルを2つのゲームが取り合っていた）。
/// ここが変わるのは、設定ファイルを手で書き換えたときと、デバッグタブで記録を切り替えたときだけなので、2つのゲームで取り合いにくい
/// （記録の切り替えは、切り替えたゲームにはすぐ効き、もう一方のゲームには次に読み込んだときから効く）。
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

    /// <summary>
    /// 詳しい記録（ログ）をファイルに残すか。既定は OFF（デバッグタブで切り替える）。
    /// OFF の間はファイルを書かず、記録のフォルダも作らない（<see cref="Core.DebugLog"/>）。
    /// </summary>
    public bool FileLogging { get; set; }

    /// <summary>画面をゲーム起動時に開くか。</summary>
    public bool OpenOnStartup { get; set; }
}
