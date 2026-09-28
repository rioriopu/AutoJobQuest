using System;
using System.Collections.Generic;
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

    /// <summary>画面をゲーム起動時に開くか。</summary>
    public bool OpenOnStartup { get; set; }

    // ---- 他プラグインを一時的に変えた控え（落ちたときに次回起動で戻すため） ----
    // GBR の Save() で一時状態がファイルに残るので、戻す手がかりをこちらにも書いておく。

    /// <summary>こちらが一時的に無効にした GBR の自動採集リストの名前。</summary>
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
