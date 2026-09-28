using System.Numerics;

namespace AutoJobQuest.Ipc;

/// <summary>
/// Questionable への窓口。クエストの受注・進行・報告を任せる。
///
/// IPC 名と型は Questionable 15.756.3.22 の逆コンパイルで確認済み（Questionable.External/QuestionableIpc.cs）:
///   bool IsRunning()
///   string GetCurrentQuestId()
///   StepData GetCurrentStepData()
///   bool StartSingleQuest(string questId)   … questId は Quest の行 ID − 65536 を10進の文字列にしたもの
///   bool IsQuestComplete(string) / bool IsQuestAccepted(string) / bool IsReadyToAcceptQuest(string)
///   bool IsQuestLocked(string)
///   bool Stop(string label)
///
/// 【製作系クラスクエでの Questionable の動き（ソースで確認した事実）】
///  ・ほぼ全部のクエストに「Craft」の手順がある。そこで Questionable はまず
///    Artisan.StartListById(クエストの行 ID) を呼ぶ（Craft.cs DoCraft.Start）。Artisan には
///    同じ番号の既製リスト（PremadeLists）があるので、それが動き出す。既製リストは「持っていれば飛ばす」
///    設定が OFF なので、材料が手元にあれば追加で作ってしまう。材料が無い行は「Insufficient materials」で飛ばす。
///    → こちらで先に全部作り、材料を使い切った状態でクエストを進める。進める前に、既製リストが
///      手元の材料で作れてしまう品が無いかを確かめて記録する（RunQuestTask）。
///  ・木工 Lv20「一途な意志」だけ「WaitForManualProgress」（マテリア装着待ち）の手順がある。
///    この手順は Questionable の中では永久に終わらない（WaitNextStepOrSequence が常に StillRunning）。
///    飛ばす IPC は無いので、ここに来たらこちらで Questionable を止めて、報告だけ自前で行う。
///  ・受注の直後に 1 秒の待ち（WaitAtEnd: WaitQuestAccepted + WaitDelay(1s)）が入る。
/// </summary>
public sealed class QuestionableIpc : IpcGate
{
    public override string InternalName => "Questionable";

    /// <summary>GetCurrentStepData の戻り値。Questionable 側の StepData と同じ名前の項目を持つ。</summary>
    public sealed class StepData
    {
        public string QuestId { get; set; } = string.Empty;

        public byte Sequence { get; set; }

        public int Step { get; set; }

        public string InteractionType { get; set; } = string.Empty;

        public Vector3? Position { get; set; }

        public uint TerritoryId { get; set; }
    }

    /// <summary>Quest の行 ID から Questionable のクエスト番号（文字列）へ。</summary>
    public static string ToQuestId(uint questRowId) => (questRowId & 0xFFFF).ToString();

    public bool? IsRunning()
        => this.TryInvoke("IsRunning", () => this.Func<bool>("Questionable.IsRunning").InvokeFunc(), out var v) ? v : null;

    public string? GetCurrentQuestId()
        => this.TryInvoke("GetCurrentQuestId", () => this.Func<string>("Questionable.GetCurrentQuestId").InvokeFunc(), out var v) ? v : null;

    public StepData? GetCurrentStepData()
        => this.TryInvoke("GetCurrentStepData", () => this.Func<StepData>("Questionable.GetCurrentStepData").InvokeFunc(), out var v) ? v : null;

    public bool StartSingleQuest(uint questRowId)
        => this.TryInvoke("StartSingleQuest",
               () => this.Func<string, bool>("Questionable.StartSingleQuest").InvokeFunc(ToQuestId(questRowId)), out var ok)
           && ok;

    public bool? IsReadyToAcceptQuest(uint questRowId)
        => this.TryInvoke("IsReadyToAcceptQuest",
            () => this.Func<string, bool>("Questionable.IsReadyToAcceptQuest").InvokeFunc(ToQuestId(questRowId)), out var v) ? v : null;

    public bool? IsQuestLocked(uint questRowId)
        => this.TryInvoke("IsQuestLocked",
            () => this.Func<string, bool>("Questionable.IsQuestLocked").InvokeFunc(ToQuestId(questRowId)), out var v) ? v : null;

    /// <summary>Questionable を止める。自分が始めた進行のときだけ呼ぶこと。</summary>
    public bool Stop(string label)
        => this.TryInvoke("Stop", () => this.Func<string, bool>("Questionable.Stop").InvokeFunc(label), out _);
}
