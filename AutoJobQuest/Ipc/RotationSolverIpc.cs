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

    // Henched にしたことと使う前のモードの控え（設定ファイル）。試験では無し
    private readonly Configuration? store;

    public RotationSolverIpc(Configuration? store = null)
    {
        this.store = store;
    }

    /// <summary>動作モードを切り替える。</summary>
    public bool ChangeOperatingMode(byte mode)
        => this.TraceThen($"ChangeOperatingMode({mode})") && this.TryAction("ChangeOperatingMode",
            () => this.Func<byte, object>(Prefix + "ChangeOperatingMode").InvokeAction(mode));

    // Henched の応答の数え方（IPC を呼ばない部分。HenchedTracker）
    private readonly HenchedTracker tracker = new();

    // こちらが Henched にする前の RSR のモード（RsrStateReader で読む。読めなければ null）。使い終わったらこれに戻す
    private byte? originalMode;

    // 動作中（AutorotationActive）のとき、本当に Henched かを内部のモードで確かめた時刻と、その結果。
    // 内部を読むのはリフレクションで重いので、送り直しの間隔（3秒）に1回だけにし、結果は次に確かめるまで使う
    // （毎フレーム「動作中」で数え直すと、Henched にならないまま送り続け、3回で止める仕組みが働かない）
    private DateTime lastModeCheck = DateTime.MinValue;
    private bool modeMismatch;

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
    public bool HenchedUnresponsive => this.tracker.Unresponsive;

    /// <summary><see cref="HenchedUnresponsive"/> のときの理由（記録と停止の文言用）。</summary>
    public string HenchedProblem => this.tracker.Problem;

    /// <summary>
    /// Henched にする（まだこちらが入れていなければ）。入れる前の RSR のモードを RSR の内部から読んで覚える
    /// （使い終わったら元のモードに戻す。読めなければ Off に戻す）。
    /// こちらが入れた後に RSR が自分で OFF になった（エリア移動・死亡・着替え）ときは入れ直す。
    /// 状態が読めないときも入れ直す（RSR の ChangeOperatingMode は切り替えではなく「そのモードにする」なので、
    /// 同じモードを送っても OFF にはならない：RSR の IPCProvider.cs:125 → RSCommands.UpdateState）。
    /// 送るのは3秒に1回まで（反映を待たずに毎フレーム送ると、RSR の切り替え表示がチャットにあふれる）。
    /// </summary>
    public bool EnsureHenched()
    {
        var active = this.IsActive();

        // 動作中でも Henched とは限らない（AutorotationActive は「State か IsManual」。利用者が Auto に切り替えた等）。
        // 内部のモードが読めれば、それで確かめる（読めなければ今までどおり動作中かで判断する）
        if (active == true && this.tracker.HenchedByMe && DateTime.UtcNow - this.lastModeCheck >= HenchedTracker.ResendInterval)
        {
            this.lastModeCheck = DateTime.UtcNow;
            var mode = RsrStateReader.ReadMode();
            this.modeMismatch = mode is { } m && m != ModeHenched;
            if (this.modeMismatch)
                Core.DebugLog.Current?.Line("IPC", $"RSR は動作中ですが、モードが {RsrStateReader.ModeName(mode!.Value)} です（Henched ではない）。Henched に入れ直します");
        }

        if (active == true && this.tracker.HenchedByMe && this.modeMismatch)
            active = false;

        var firstTake = !this.tracker.HenchedByMe;
        var action = this.tracker.Decide(active, DateTime.UtcNow);
        if (action != HenchedTracker.Action.Send)
            return true; // 応答あり、または送った直後の反映待ち

        if (firstTake)
        {
            // 前に Henched にしたまま戻せていない控えがあり、いまも Henched なら、それは前回こちらが入れたもの。
            // 使う前のモードは控えのほうを使う（いまの Henched を使う前のモードとして覚えると、戻しても Henched のまま残る）
            var current = RsrStateReader.ReadMode();
            var pending = this.store?.RsrHenchedPending == true;
            this.originalMode = RsrRestore.OriginalForNewTake(current, pending, this.store?.RsrOriginalMode);
            if (pending && this.originalMode != current)
                Core.DebugLog.Current?.Line("IPC", $"前回こちらが入れた Henched が残っています。使う前のモードは控えの {(this.originalMode is { } pm ? RsrStateReader.ModeName(pm) : "（読めなかった＝Off）")} とします");
            Core.DebugLog.Current?.Line("IPC", this.originalMode is { } m
                ? $"RSR の使う前のモードは {RsrStateReader.ModeName(m)}。使い終わったら {RsrStateReader.ModeName(m)} に戻します"
                : $"⚠ RSR の使う前のモードを読めません（{RsrStateReader.LastError}）。使い終わったら Off に戻します");
        }

        if (this.tracker.Unanswered >= 3 || this.tracker.Unreadable >= 3)
            Core.DebugLog.Current?.Line("IPC", $"⚠ {this.tracker.Problem}");

        if (!this.ChangeOperatingMode(ModeHenched))
            return false;
        this.tracker.Sent(DateTime.UtcNow);
        if (firstTake)
            this.SaveStore(true, this.originalMode);
        return true;
    }

    /// <summary>
    /// こちらが Henched にしていたときだけ、使う前のモードに戻す（使う前が Off か、読めなかったなら Off）。
    /// 利用者が使っていた RSR を勝手に止めない・勝手にモードを変えたままにしないため。
    /// </summary>
    public void ReleaseHenched()
    {
        if (!this.tracker.HenchedByMe)
            return;

        // 戻すのは、いまも Henched のときだけ（こちらが使っている間に利用者が別のモードにしたなら、そのままにする）。
        // いまのモードが読めなければ、今までどおり使う前のモードに戻す
        var current = RsrStateReader.ReadMode();
        if (RsrRestore.Decide(current, this.originalMode) is not { } back)
        {
            Core.DebugLog.Current?.Line("IPC", $"RSR はもう {RsrStateReader.ModeName(current!.Value)} になっている（利用者か RSR が切り替えた）ので、モードは戻しません");
            this.tracker.Released();
            this.originalMode = null;
            this.modeMismatch = false;
            this.SaveStore(false, null);
            return;
        }

        if (this.ChangeOperatingMode(back))
        {
            Core.DebugLog.Current?.Line("IPC", $"RSR を {RsrStateReader.ModeName(back)} に戻しました{(this.originalMode == null ? "（使う前のモードが読めなかったため Off）" : string.Empty)}");
            this.tracker.Released();
            this.originalMode = null;
            this.modeMismatch = false;
            this.SaveStore(false, null);
        }
    }

    /// <summary>
    /// 前に Henched にしたまま戻せていない控えがあれば戻す（実行していない間に、プラグインの側から呼ぶ）。
    /// いまも Henched なら控えのモードへ戻し、もう別のモード（利用者か RSR が切り替えた）なら控えを消すだけ。
    /// 戻したとき・控えを消したときは、その説明を返す。何もしなければ null。
    /// </summary>
    public string? RestoreLeftover()
    {
        if (this.store?.RsrHenchedPending != true || this.tracker.HenchedByMe)
            return null;

        var current = RsrStateReader.ReadMode();
        if (current == null)
            return null; // 読めるようになってから決める（RSR の読み込み直後など）

        if (RsrRestore.Decide(current, this.store.RsrOriginalMode) is not { } back)
        {
            this.SaveStore(false, null);
            return $"前回こちらが入れた RSR の Henched は、もう {RsrStateReader.ModeName(current.Value)} に変わっていたので、控えを消しました（モードは変えていません）";
        }

        if (!this.ChangeOperatingMode(back))
            return null; // 次の機会にやり直す

        this.SaveStore(false, null);
        return $"前回戻せなかった RSR のモードを {RsrStateReader.ModeName(back)} に戻しました";
    }

    private void SaveStore(bool pending, byte? original)
    {
        if (this.store == null || (this.store.RsrHenchedPending == pending && this.store.RsrOriginalMode == original))
            return;
        this.store.RsrHenchedPending = pending;
        this.store.RsrOriginalMode = original;
        this.store.Save();
    }

    /// <summary>今の RSR のモードの名前（事前点検・画面用）。読めなければ null。</summary>
    public string? CurrentModeName() => RsrStateReader.ReadMode() is { } m ? RsrStateReader.ModeName(m) : null;

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

    /// <summary>こちらが Henched にしたまま戻していないか。</summary>
    public bool HenchedByMe => this.tracker.HenchedByMe;
}

/// <summary>使い終わったときに RSR のモードを戻すかの決まり（IPC を呼ばない部分。ゲームを起動せずに試せるように分けた）。</summary>
public static class RsrRestore
{
    /// <summary>
    /// 戻すモード。戻さないなら null。
    ///  ・いまのモードが読めて Henched でない（利用者か RSR が切り替えた）→ 戻さない（利用者の選んだモードを上書きしない）
    ///  ・いまも Henched、または読めない → 使う前のモードに戻す（使う前が読めなかったなら Off）
    /// </summary>
    /// <param name="current">いまのモード（読めなければ null）。</param>
    /// <param name="original">使う前のモード（読めなかったなら null）。</param>
    public static byte? Decide(byte? current, byte? original)
        => current is { } c && c != RotationSolverIpc.ModeHenched ? null : original ?? RotationSolverIpc.ModeOff;

    /// <summary>
    /// これから Henched にするとき、「使う前のモード」として覚えるもの。
    /// 前に Henched にしたまま戻せていない控えがあり、いまも Henched（または読めない）なら、控えのモード
    /// （いまの Henched は前回こちらが入れたもので、利用者のモードではない）。それ以外はいまのモード。
    /// </summary>
    /// <param name="current">いまのモード（読めなければ null）。</param>
    /// <param name="hasPending">戻せていない控えがあるか。</param>
    /// <param name="pendingOriginal">控えの、使う前のモード。</param>
    public static byte? OriginalForNewTake(byte? current, bool hasPending, byte? pendingOriginal)
        => hasPending && current is null or RotationSolverIpc.ModeHenched ? pendingOriginal : current;
}

/// <summary>
/// RSR に Henched を頼むときの応答の数え方（IPC を呼ばない部分だけ。ゲームを起動せずに試せるように分けた）。
///  ・こちらが入れていて、動作中（true）と読めたら「応答あり」。数を 0 に戻す。
///  ・送ってから3秒は反映待ち（送り直さない）。
///  ・それ以外は送り直す。こちらが入れた後の false を Unanswered、読めない（null）を Unreadable として数える。どちらか3回で「応答なし」。
/// </summary>
public sealed class HenchedTracker
{
    public enum Action
    {
        /// <summary>応答あり（何もしない）。</summary>
        Ok,

        /// <summary>送った直後の反映待ち（何もしない）。</summary>
        Wait,

        /// <summary>Henched を送る。</summary>
        Send,
    }

    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(3);

    public bool HenchedByMe { get; private set; }

    public int Unanswered { get; private set; }

    public int Unreadable { get; private set; }

    private DateTime lastSend = DateTime.MinValue;

    public bool Unresponsive => this.Unanswered >= 3 || this.Unreadable >= 3;

    public string Problem => this.Unreadable >= 3
        ? $"RSR の動作状態（AutorotationActive）を {this.Unreadable} 回続けて読めませんでした（IPC が変わった可能性）"
        : $"RSR に Henched への切り替えを {this.Unanswered} 回送っても動作中になりません（IPC が効いていない可能性）";

    /// <param name="active">RSR の AutorotationActive（読めなければ null）。</param>
    /// <param name="now">いまの時刻。</param>
    public Action Decide(bool? active, DateTime now)
    {
        if (this.HenchedByMe && active == true)
        {
            this.Unanswered = 0;
            this.Unreadable = 0;
            return Action.Ok;
        }

        if (now - this.lastSend < ResendInterval)
            return Action.Wait;

        if (this.HenchedByMe && active == false)
            this.Unanswered++;
        if (this.HenchedByMe && active == null)
            this.Unreadable++;
        return Action.Send;
    }

    /// <summary>Henched を送った。</summary>
    public void Sent(DateTime now)
    {
        this.HenchedByMe = true;
        this.lastSend = now;
    }

    /// <summary>元のモードに戻した。</summary>
    public void Released()
    {
        this.HenchedByMe = false;
        this.Unanswered = 0;
        this.Unreadable = 0;
        this.lastSend = DateTime.MinValue;
    }
}
