using System;
using System.Collections.Generic;

namespace AutoJobQuest.Ipc;

/// <summary>
/// YesAlready を一時的に止める（停止要求の共有データ "YesAlready.StopRequests" に自分の名前を入れる）。
///
/// 【使いどころ】こちらが窓に答える作業の間だけ止める：クエスト（納品窓。YesAlready は一覧の先頭を入れる）・マテリア装着・
/// 秘伝書の交換・秘伝書を読む。マーケットの購入は確認ダイアログを通らない関数経路なので止めない。
///
/// 【罠】YesAlready が再読み込みされると一覧が Clear される。止めている間は
/// 定期的に入っているかを確かめて入れ直す（<see cref="KeepSuppressed"/>）。
/// 終わったら必ず外す。Dispose では RelinquishData だけを行う（相手の状態には触れない）。
/// </summary>
public sealed class YesAlreadyIpc : IpcGate, IDisposable
{
    public override string InternalName => "YesAlready";

    private const string Tag = "YesAlready.StopRequests";

    private HashSet<string>? requests;
    private bool suppressing;

    private HashSet<string>? Requests
    {
        get
        {
            if (this.requests != null)
                return this.requests;

            try
            {
                this.requests = Svc.PluginInterface.GetOrCreateData(Tag, () => new HashSet<string>());
            }
            catch (Exception ex)
            {
                this.WarnThrottled($"停止要求の共有データを取れませんでした: {ex.Message}");
            }

            return this.requests;
        }
    }

    /// <summary>止める。</summary>
    public void Suppress()
    {
        this.suppressing = true;
        this.Requests?.Add(Plugin.InternalNameConst);
    }

    /// <summary>止めている間、毎フレーム呼ぶ（相手の再読み込みで消えた要求を入れ直す）。</summary>
    public void KeepSuppressed()
    {
        if (this.suppressing && this.Requests is { } r && !r.Contains(Plugin.InternalNameConst))
            r.Add(Plugin.InternalNameConst);
    }

    /// <summary>止めるのをやめる。</summary>
    public void Release()
    {
        this.suppressing = false;
        this.Requests?.Remove(Plugin.InternalNameConst);
    }

    public void Dispose()
    {
        if (this.requests == null)
            return;

        try
        {
            // 自分の要求だけは外しておく（残すと YesAlready が止まったままになる）
            this.requests.Remove(Plugin.InternalNameConst);
            Svc.PluginInterface.RelinquishData(Tag);
        }
        catch
        {
            // アンロード中なので握り潰す
        }

        this.requests = null;
    }
}
