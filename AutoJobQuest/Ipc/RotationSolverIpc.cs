using System.Collections.Generic;

namespace AutoJobQuest.Ipc;

/// <summary>
/// RotationSolverReborn（RSR）への窓口。戦闘の攻撃を任せる。
///
/// 【名前の前置詞】IPC の前置詞は "RotationSolverReborn"。プラグインの InternalName は "RotationSolver" で
/// 一致しない（RSR IPCProvider.cs:19）。導入判定は InternalName、呼び出しは前置詞で行う。
///
/// 【型】ChangeOperatingMode の引数は enum StateCommandType : byte。byte で渡しても Dalamud が JSON 変換する
/// （Questionable が自前の enum で同じように呼んでいる）。
///   Off=0, Auto=1, TargetOnly=2, Manual=3, AutoDuty=4, Henched=5, PvP=6
///
/// 【Henched（5）を使う理由】IsManual=true になり、敵への行動は「いまのハードターゲット」だけが対象になる
/// （ActionTargetInfo.cs:143）。さらに IsHenched のときは「こちらを狙っていない敵」も攻撃してよい扱いになる
/// （ObjectHelper.cs:382）。＝こちらがターゲットした指定のモンスターだけを殴る。戦闘後の自動 OFF も効かない。
///
/// 【優先リスト】メモリ上だけのリストで保存されない（RSR の再読み込みで消える）。Add は重複を確かめずに足し、
/// Remove は1件だけ消す。→ こちらで足した ID を覚えておき、足した回数だけ消す（<see cref="ClearOwnPriorities"/>）。
/// 動作停止後、または次のモンスターを殴った時、過去に指定したモンスターの設定は消す。
/// </summary>
public sealed class RotationSolverIpc : IpcGate
{
    public override string InternalName => "RotationSolver";

    public override string DisplayName => "RotationSolverReborn";

    private const string Prefix = "RotationSolverReborn.";

    public const byte ModeOff = 0;
    public const byte ModeHenched = 5;

    // こちらが優先リストに足した名前 ID（足した回数ぶん並ぶ）
    private readonly List<uint> ownPriorities = [];

    /// <summary>動作モードを切り替える。</summary>
    public bool ChangeOperatingMode(byte mode)
        => this.TryAction("ChangeOperatingMode",
            () => this.Func<byte, object>(Prefix + "ChangeOperatingMode").InvokeAction(mode));

    /// <summary>自動ローテーションが動いているか。読めなければ null。</summary>
    public bool? IsActive()
        => this.TryInvoke("AutorotationActive",
            () => this.Func<bool>(Prefix + "AutorotationActive").InvokeFunc(), out var v) ? v : null;

    /// <summary>優先して狙うモンスター（名前 ID）を足す。足した ID は覚えておく。</summary>
    public bool AddPriority(uint bnpcNameId)
    {
        var ok = this.TryAction("AddPriorityNameID",
            () => this.Func<uint, object>(Prefix + "AddPriorityNameID").InvokeAction(bnpcNameId));
        if (ok)
            this.ownPriorities.Add(bnpcNameId);
        return ok;
    }

    /// <summary>こちらが足した優先指定を、足した回数ぶん全部消す。</summary>
    public void ClearOwnPriorities()
    {
        foreach (var id in this.ownPriorities.ToArray())
        {
            if (this.TryAction("RemovePriorityNameID",
                    () => this.Func<uint, object>(Prefix + "RemovePriorityNameID").InvokeAction(id)))
                this.ownPriorities.Remove(id);
        }
    }

    /// <summary>こちらが足した優先指定が残っているか。</summary>
    public bool HasOwnPriorities => this.ownPriorities.Count > 0;
}
