using System;
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
        => this.TraceThen($"ChangeOperatingMode({mode})") && this.TryAction("ChangeOperatingMode",
            () => this.Func<byte, object>(Prefix + "ChangeOperatingMode").InvokeAction(mode));

    private bool henchedByMe;
    private DateTime lastHenchedSend = DateTime.MinValue;
    private int unanswered;
    private int unreadable;

    /// <summary>
    /// Henched を送っても RSR が動作中にならない（false）か、状態が読めない（null）ことが続いたか（それぞれ3回）。
    /// RSR の AutorotationActive は Henched で true になる（State と IsManual が立つ：RSR の RSCommands_StateSpecialCommand.cs・
    /// DataCenter.IsActivatedIPC）。これが続くのは IPC が効いていないということ
    /// （BeastHelper の作者は「自前の enum で呼んだら黙って効かなかった」と記録している。Dalamud は型が違う引数を
    /// JSON で変換するので byte は通るはずだが、実機では未確認のため、黙って送り続けずに気づけるようにする）。
    ///
    /// 【true だけを成功とみなす】以前は「読めない（null）」も成功側に数えていたので、
    /// 状態の読み出しだけが壊れていると、攻撃しないまま近づき続け、3回で止める仕組みも働かなかった。
    /// なお AutorotationActive は「State か IsManual」なので、true でも Henched になったことまでは証明しない
    /// （IPC にモードを読む口が無いため。RSR の IPCProvider.cs:274・DataCenter.cs:246）。
    /// </summary>
    public bool HenchedUnresponsive => this.unanswered >= 3 || this.unreadable >= 3;

    /// <summary><see cref="HenchedUnresponsive"/> のときの理由（記録と停止の文言用）。</summary>
    public string HenchedProblem => this.unreadable >= 3
        ? $"RSR の動作状態（AutorotationActive）を {this.unreadable} 回続けて読めませんでした（IPC が変わった可能性）"
        : $"RSR に Henched への切り替えを {this.unanswered} 回送っても動作中になりません（IPC が効いていない可能性）";

    /// <summary>
    /// Henched にする（まだこちらが入れていなければ）。入れる前に RSR が動いていたら、利用者が使っていたとみなして記録する
    /// （IPC ではモードの種類までは読めないので、戻すときは Off にしかできないため）。
    /// こちらが入れた後に RSR が自分で OFF になった（エリア移動・死亡・着替え）ときは入れ直す。
    /// 状態が読めないときも入れ直す（RSR の ChangeOperatingMode は切り替えではなく「そのモードにする」なので、
    /// 同じモードを送っても OFF にはならない：RSR の IPCProvider.cs:125 → RSCommands.UpdateState）。
    /// 送るのは3秒に1回まで（反映を待たずに毎フレーム送ると、RSR の切り替え表示がチャットにあふれる）。
    /// </summary>
    public bool EnsureHenched()
    {
        var active = this.IsActive();
        if (this.henchedByMe && active == true)
        {
            this.unanswered = 0;
            this.unreadable = 0;
            return true;
        }

        if (DateTime.UtcNow - this.lastHenchedSend < TimeSpan.FromSeconds(3))
            return true; // 送った直後は反映待ち

        if (!this.henchedByMe && active == true)
            Core.DebugLog.Current?.Line("IPC", "⚠ RSR はこちらが使う前から動いていました。終わったときは Off に戻ります（元のモードは IPC で読めないため）");

        if (this.henchedByMe && active == false && ++this.unanswered >= 3)
            Core.DebugLog.Current?.Line("IPC", $"⚠ RSR に Henched を {this.unanswered} 回送っても動作中になりません");

        if (this.henchedByMe && active == null && ++this.unreadable >= 3)
            Core.DebugLog.Current?.Line("IPC", $"⚠ RSR の動作状態を {this.unreadable} 回続けて読めません");

        if (!this.ChangeOperatingMode(ModeHenched))
            return false;
        this.henchedByMe = true;
        this.lastHenchedSend = DateTime.UtcNow;
        return true;
    }

    /// <summary>こちらが Henched にしていたときだけ Off に戻す（利用者が使っていた RSR を勝手に止めないため）。</summary>
    public void ReleaseHenched()
    {
        if (!this.henchedByMe)
            return;
        if (this.ChangeOperatingMode(ModeOff))
        {
            this.henchedByMe = false;
            this.unanswered = 0;
            this.unreadable = 0;
            this.lastHenchedSend = DateTime.MinValue;
        }
    }

    /// <summary>自動ローテーションが動いているか。読めなければ null。</summary>
    public bool? IsActive()
        => this.TryInvoke("AutorotationActive",
            () => this.Func<bool>(Prefix + "AutorotationActive").InvokeFunc(), out var v) ? v : null;

    /// <summary>優先して狙うモンスター（名前 ID）を足す。足した ID は覚えておく。</summary>
    public bool AddPriority(uint bnpcNameId)
    {
        this.Trace($"AddPriorityNameID({bnpcNameId})");
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
            this.Trace($"RemovePriorityNameID({id})");
            if (this.TryAction("RemovePriorityNameID",
                    () => this.Func<uint, object>(Prefix + "RemovePriorityNameID").InvokeAction(id)))
                this.ownPriorities.Remove(id);
        }
    }

    /// <summary>こちらが足した優先指定が残っているか。</summary>
    public bool HasOwnPriorities => this.ownPriorities.Count > 0;
}
