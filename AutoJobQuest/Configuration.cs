using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace AutoJobQuest;

/// <summary>
/// 設定。
///
/// 【Dalamud の設定ファイルは1プラグイン1ファイル】
/// キャラクターごとには分かれない（別のプラグインで4キャラ共有の事故が起きている）。
/// ここに置くのは「どのジョブを回すか」のような利用者の好みだけにして、
/// キャラクターの進み具合（どのクエストが済んだか等）は保存せず、毎回ゲームから読む。
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>ジョブクエを回すか（木工→調理の順）。</summary>
    public bool[] SelectedCrafters { get; set; } = new bool[8];

    /// <summary>
    /// マーケットボードの購入で、1回の合計金額がこれを超えたら確認窓を出す（ギル）。
    /// 既定は 500,000。
    /// </summary>
    public int ConfirmPurchaseAboveGil { get; set; } = 500_000;

    /// <summary>
    /// 納品物のHQが要るのにNQしかできなかったとき、何回まで作り直すか。
    /// 無限に作り直すと素材を食い潰すので上限を持つ。
    /// </summary>
    public int MaxRetryRounds { get; set; } = 3;

    /// <summary>
    /// 紫貨を稼ぐために作って納品する収集品（収集用のシーダーロングボウ＝30970）。
    /// 戦闘でしか取れない素材が無く、Lv50 の木工レシピで作れるため。別の品にしたい場合はここを変える。
    /// </summary>
    public uint ScripCollectableItemId { get; set; } = 30970;

    /// <summary>
    /// 納品物に「任意のマテリア」を付けるクエスト（各クラフター Lv20 の7本）で、買って付けるマテリア
    /// （剛柔のマテリア＝5679。能力値「不屈」・アイテムLv15 をゲームデータで確認）。
    /// 実行時に「マテリアであること」「納品物のアイテムLv以下であること」を確かめ、合わなければ始める前に止める。
    /// </summary>
    public uint AnyMateriaItemId { get; set; } = 5679;

    /// <summary>
    /// 記録（ログ）を残すフォルダ。既定は開発用のフォルダの ログ（作れなければプラグインの設定フォルダの ログ）。
    /// </summary>
    public string LogDirectory { get; set; } = Core.DebugLog.DefaultDirectory;

    /// <summary>実行していないときも、ショップ・マーケット等の画面を記録するか（手動操作の値を取りたいとき用）。</summary>
    public bool AlwaysRecordAddons { get; set; }

    /// <summary>画面をゲーム起動時に開くか。</summary>
    public bool OpenOnStartup { get; set; }

    // ---- 他プラグインを一時的に変えた控え（落ちたときに次回起動で戻すため） ----
    // GBR の Save() で一時状態がファイルに残るので、戻す手がかりをこちらにも書いておく。

    /// <summary>
    /// こちらが一時的に無効にした GBR の自動採集リスト（名前とフォルダ）。
    /// GBR は同じ名前のリストを許すので、名前だけでは戻し先を取り違える。
    /// </summary>
    public List<GbrListRef> GbrDisabledListRefs { get; set; } = [];

    /// <summary>旧形式（名前だけ）の控え。読み込み時に新形式へ移す。</summary>
    public List<string> GbrDisabledLists { get; set; } = [];

    /// <summary>こちらが一時的に書き換えた GBR の自動採集設定（名前 → 元の値）。</summary>
    public Dictionary<string, bool> GbrConfigOriginals { get; set; } = [];

    /// <summary>こちらの GBR リスト「AutoJobQuest」を有効にしたままか。</summary>
    public bool GbrOwnListActive { get; set; }

    [NonSerialized]
    private Dalamud.Plugin.IDalamudPluginInterface? pluginInterface;

    public void Initialize(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        this.pluginInterface = pi;

        // 旧形式の控え（名前だけ）を新形式へ移す。フォルダは分からないので空（＝一番上）として扱う
        foreach (var name in this.GbrDisabledLists)
            if (!this.GbrDisabledListRefs.Any(r => r.Name == name))
                this.GbrDisabledListRefs.Add(new GbrListRef(name, string.Empty));
        this.GbrDisabledLists.Clear();

        // 古い設定ファイルで配列の長さが違うと、チェックボックスの描画で範囲外になる
        if (this.SelectedCrafters is not { Length: 8 })
        {
            var fixedArr = new bool[8];
            if (this.SelectedCrafters != null)
                Array.Copy(this.SelectedCrafters, fixedArr, Math.Min(8, this.SelectedCrafters.Length));
            this.SelectedCrafters = fixedArr;
        }
    }

    public void Save()
        => this.pluginInterface?.SavePluginConfig(this);
}

/// <summary>GBR の自動採集リストを指す控え（名前とフォルダの組）。</summary>
[Serializable]
public sealed record GbrListRef(string Name, string FolderPath);
