namespace AutoJobQuest.Ipc;

/// <summary>
/// Lifestream への窓口。テレポートと宿屋への移動に使う。
///
/// IPC 名は Lifestream の実ソースで確認済み（Lifestream/IPC/IPCProvider.cs。EzIPC で前置詞 "Lifestream"）:
///   bool Teleport(uint destination, byte subIndex)
///   bool IsBusy()
///   void Abort()
///   void EnqueueLocalInnShortcut(int? innIndex)   … 同じワールドの宿屋へ。宿屋の番号は下を参照
///
/// 【宿屋の番号】Lifestream の TaskPropertyShortcut.InnData（エリア ID で並ぶ SortedDictionary）の何番目か。
///   129 リムサ(0) / 130 ウルダハ(1) / 132 グリダニア(2) / 418 イシュガルド(3) / …
/// よってグリダニアは 2。到着したかは、こちらでエリアが宿屋の部屋になったかで確かめる。
///
/// 【IsBusy の性質】Teleport は Lifestream 内部の待機なしの経路を直接呼ぶため、呼んだあと IsBusy は
/// true にならない。詠唱の完了・エリア遷移はこちらで待つ。
/// 宿屋のショートカットは TaskManager に積まれるので IsBusy が true になる。
/// </summary>
public sealed class LifestreamIpc : IpcGate
{
    public override string InternalName => "Lifestream";

    /// <summary>グリダニアの宿屋の番号（上の説明を参照）。</summary>
    public const int GridaniaInnIndex = 2;

    /// <summary>指定のエーテライトへテレポートする。false なら未アクセスかテレポートできない状態。</summary>
    public bool TryTeleport(uint aetheryteId, byte subIndex, out bool accepted)
    {
        this.Trace($"Teleport(エーテライト {aetheryteId}, {subIndex})");
        return this.TryInvoke("Teleport",
            () => this.Func<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(aetheryteId, subIndex),
            out accepted);
    }

    /// <summary>Lifestream が何か処理中か。読めなければ null。</summary>
    public bool? IsBusy()
        => this.TryInvoke("IsBusy", () => this.Func<bool>("Lifestream.IsBusy").InvokeFunc(), out var busy)
            ? busy
            : null;

    /// <summary>同じワールドの宿屋へ向かう処理を積む。</summary>
    public bool EnqueueLocalInn(int innIndex)
        => this.TraceThen($"EnqueueLocalInnShortcut({innIndex})") && this.TryAction("EnqueueLocalInnShortcut",
            () => this.Func<int?, object>("Lifestream.EnqueueLocalInnShortcut").InvokeAction(innIndex));

    /// <summary>Lifestream の処理を中断する。自分が頼んだ処理のときだけ呼ぶこと。</summary>
    public bool Abort()
        => this.TraceThen("Abort()") && this.TryAction("Abort", () => this.Func<object>("Lifestream.Abort").InvokeAction());
}
