using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoJobQuest.Ipc;

/// <summary>
/// RotationSolverReborn（RSR）への窓口。戦闘の攻撃を任せる。
///
/// 【名前の前置詞】IPC の前置詞は "RotationSolverReborn"。プラグインの InternalName は "RotationSolver" で
/// 一致しない（RSR IPCProvider.cs:19）。導入判定は InternalName、呼び出しは前置詞で行う。
///
/// 【型】ChangeOperatingMode の引数は enum StateCommandType : byte。byte で渡しても Dalamud が JSON 変換する
/// （Questionable も自前の enum で同じように呼んでいる：Questionable の External\RotationSolverRebornIpc.cs。
/// Dalamud の型の変換は、ソースと手元の再現で確かめた）。
///   Off=0, Auto=1, TargetOnly=2, Manual=3, AutoDuty=4, Henched=5, PvP=6
///
/// 【Henched（5）を使う理由】IsManual=true になり、敵への行動は「いまのハードターゲット」だけが対象になる
/// （ActionTargetInfo.cs:143）。さらに IsHenched のときは「こちらを狙っていない敵」も攻撃してよい扱いになる
/// （ObjectHelper.cs:382）。＝こちらがターゲットした指定のモンスターだけを殴る。戦闘後の自動 OFF も効かない。
///
/// 【優先リスト】メモリ上だけのリストで保存されない（RSR の再読み込みで消える）。Add は重複を確かめずに足し、
/// Remove は1件だけ消す。→ こちらで足した ID を覚えておき、足した回数だけ消す（<see cref="ClearOwnPriorities"/>）。
/// 動作停止後、または次のモンスターを殴った時、過去に指定したモンスターの設定は消す。
/// なお Henched では優先リストは狙う相手に効かない（RSR の ObjectHelper.cs：「攻撃してよいか」の判定で、IsHenched なら
/// どのみち真になる。狙う相手はハードターゲットだけ）。害は無いので、上のとおり足して消す。
///
/// 【範囲攻撃】Henched でも範囲攻撃は指定外の敵を巻き込み、自分中心の範囲攻撃はハードターゲットが無くても近くの敵に撃つ
/// （RSR の ActionTargetInfo.cs。確実に止まるのは設定 AoEType=Off だけ）。対象モンスター以外は
/// 攻撃しないため、Henched にしている間だけ AoEType を Off にし、使い終わったら元の値へ戻す（<see cref="RsrAoe"/>）。
/// 変更は IPC の OtherCommand(Settings, "AoEType Off")（RSR の RSCommands_OtherCommand.cs の DoSettingCommand。メモリ上の値だけを変え、
/// 保存は RSR の設定画面を閉じたときと終了時）。今の値を読む IPC は無いので、モードと同じく内部を読む（RsrStateReader.ReadAoeType）。
///
/// 【戻したかを確かめる】戻す命令が通っただけでは控えを消さない。実行していない間に実際の値を読み、
/// 使う前の値になっていれば控えを消す（<see cref="RestoreLeftover"/>）。戻す命令が失敗したときも、控えを残して10秒おきにやり直す。
/// </summary>
public sealed class RotationSolverIpc : IpcGate, Automation.IRotationControl
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

    private readonly Func<bool?> readTargetOverride;
    private bool? lastTarget;
    private DateTime lastTargetRead = DateTime.MinValue;

    public RotationSolverIpc(Configuration? store = null, Func<bool?>? readTargetOverride = null)
    {
        this.store = store;
        this.readTargetOverride = readTargetOverride ?? RsrStateReader.ReadTargetFreelyOverride;
    }

    /// <summary>動作モードを切り替える。</summary>
    public bool ChangeOperatingMode(byte mode)
        => this.TraceThen($"ChangeOperatingMode({mode})") && this.TryAction("ChangeOperatingMode",
            () => this.Func<byte, object>(Prefix + "ChangeOperatingMode").InvokeAction(mode));

    /// <summary>
    /// 範囲攻撃の設定を変える（RSR の OtherCommand(OtherCommandType.Settings=0, "AoEType 値")。
    /// OtherCommandType は byte の列挙で、Settings が先頭＝0：RSR の Basic/Data/RSCommandType.cs で確認）。
    /// </summary>
    public bool SetAoeType(byte value)
        => this.TraceThen($"OtherCommand(Settings, AoEType {RsrStateReader.AoeName(value)})") && this.TryAction("OtherCommand",
            () => this.Func<byte, string, object>(Prefix + "OtherCommand").InvokeAction(0, $"AoEType {RsrStateReader.AoeName(value)}"));

    /// <summary>RSR の真偽の設定を変える（OtherCommand(Settings=0, "名前 値")。メモリ上だけ変わる）。</summary>
    public bool SetBoolSetting(string name, bool value)
        => this.TraceThen($"OtherCommand(Settings, {name} {(value ? "true" : "false")})") && this.TryAction("OtherCommand",
            () => this.Func<byte, string, object>(Prefix + "OtherCommand").InvokeAction(0, $"{name} {(value ? "true" : "false")}"));

    /// <summary>
    /// Henched の間だけ false にする RSR の真偽の設定：
    ///  ・TargetFreely：狙い（ハードターゲット）が空になった瞬間に、RSR が48m 以内の一番近い敵を自分で狙う。候補に FATE や
    ///    「攻撃してよいか」の絞り込みが無いので、指定外の敵を殴り始めうる（対象モンスター以外は攻撃しない仕様に反する）。
    ///  ・IgnoreNonFateInFate：FATE の円の中では FATE 以外の敵を、FATE の外では FATE の敵を殴らない。狙った敵が FATE の中にいる・
    ///    FATE の敵に攻撃されたときに RSR が殴らず、45秒むだにして諦める（棒立ちにならず反撃する仕様に反する）。
    /// </summary>
    public static readonly string[] HenchedFalseSettings = ["TargetFreely", "IgnoreNonFateInFate"];

    // 真偽の設定：こちらが変えたもの（名前 → 使う前の値）・入れ直した回数・最後に確かめた時刻
    private readonly Dictionary<string, bool> boolTaken = [];
    private readonly Dictionary<string, int> boolResends = [];
    private readonly HashSet<string> boolWarnings = [];
    private string? safetyProblem;

    public void BeginRun()
    {
        this.boolWarnings.Clear();
        this.boolProblem = null;
    }

    // 真偽の設定を false にできなかったときの理由（問題なければ null）
    private string? boolProblem;

    /// <summary>
    /// 指定外の敵を攻撃しうる RSR の設定の問題（範囲攻撃を Off にできない・勝手に狙う設定を false にできない）。問題なければ null。
    /// 指定のモンスターを倒しに行く戦闘では、これが出たら止める（指定モンスター以外は攻撃しないため）。
    /// </summary>
    public string? TargetingProblem => this.AoeProblem ?? this.boolProblem;

    /// <summary>利用者が明示的に現在値を保持する。自動復元の控えだけを破棄し、RSRの設定は変更しない。</summary>
    public void KeepCurrentBoolSettings()
    {
        this.boolTaken.Clear();
        this.boolResends.Clear();
        this.store?.RsrBoolOriginals.Clear();
        this.store?.Save();
    }

    private DateTime lastBoolCheck = DateTime.MinValue;

    /// <summary>Henched の間、真偽の設定を false にしておく（3秒に1回だけ確かめる）。</summary>
    private void EnsureBoolsOff()
    {
        if (DateTime.UtcNow - this.lastBoolCheck < HenchedTracker.ResendInterval)
            return;
        this.lastBoolCheck = DateTime.UtcNow;

        foreach (var name in HenchedFalseSettings)
        {
            var current = RsrStateReader.ReadBool(name);
            var pendingOriginal = this.store != null && this.store.RsrBoolOriginals.TryGetValue(name, out var po) ? po : (bool?)null;
            var d = RsrBoolSetting.Take(current, false, this.boolTaken.ContainsKey(name), this.boolResends.GetValueOrDefault(name), pendingOriginal);
            switch (d.Action)
            {
                case RsrAoe.TakeAction.Adopt:
                    this.boolTaken[name] = d.Original ?? true;
                    Core.DebugLog.Current?.Line("IPC", $"前回こちらが false にした RSR の {name} が残っています。使い終わったら {d.Original} に戻します");
                    break;
                case RsrAoe.TakeAction.Warn:
                    if (this.boolWarnings.Add(name))
                    {
                        var msg = current == null
                            ? $"RSR の設定 {name} を読めません（{RsrStateReader.LastError}）。指定外の敵を狙う・FATE の敵に反撃しない可能性があります"
                            : $"RSR の設定 {name} を false にしても戻ります（利用者か RSR が変えた可能性）";
                        Core.DebugLog.Current?.Line("IPC", $"⚠ {msg}");
                        Svc.Chat.Print($"[AutoJobQuest] {msg}");
                        this.boolProblem = msg;
                    }

                    break;
                case RsrAoe.TakeAction.SendOff:
                    if (!this.boolTaken.ContainsKey(name))
                    {
                        this.boolTaken[name] = d.Original ?? true;
                        this.SaveBool(name, d.Original ?? true);
                        Core.DebugLog.Current?.Line("IPC", $"RSR の {name} を戦闘の間だけ false にします（使う前は {d.Original}。使い終わったら戻します）");
                    }
                    else
                    {
                        this.boolResends[name] = this.boolResends.GetValueOrDefault(name) + 1;
                    }

                    this.SetBoolSetting(name, false);
                    break;
            }
        }
    }

    /// <summary>真偽の設定を使う前の値へ戻す（こちらが変えたものだけ。利用者が変えていたら戻さない）。</summary>
    private void ReleaseBools()
    {
        foreach (var (name, original) in this.boolTaken.ToList())
        {
            var current = RsrStateReader.ReadBool(name);
            if (RsrBoolSetting.Restore(current, false, original) is not { } back)
            {
                Core.DebugLog.Current?.Line("IPC", $"RSR の {name} はもう {current} になっている（利用者が変えた）ので、戻しません");
                this.SaveBool(name, null);
                continue;
            }

            // 送れても、控えは戻ったと確かめるまで残す（RestoreLeftover が確かめる）
            if (this.SetBoolSetting(name, back))
                Core.DebugLog.Current?.Line("IPC", $"RSR の {name} を {back} に戻しました（戻ったかは後で確かめます）");
        }

        this.boolTaken.Clear();
        this.boolResends.Clear();
        this.lastBoolCheck = DateTime.MinValue;
        this.boolProblem = null;
    }

    private void SaveBool(string name, bool? original)
    {
        if (this.store == null)
            return;
        if (original is { } o)
        {
            if (this.store.RsrBoolOriginals.TryGetValue(name, out var cur) && cur == o)
                return;
            this.store.RsrBoolOriginals[name] = o;
        }
        else if (!this.store.RsrBoolOriginals.Remove(name))
        {
            return;
        }

        this.store.Save();
    }

    // 範囲攻撃をこちらが Off にしているか・使う前の値・入れ直した回数・最後に確かめた時刻
    private bool aoeTakenByMe;
    private byte? aoeOriginal;
    private int aoeResends;
    private bool aoeWarned;
    private DateTime lastAoeCheck = DateTime.MinValue;

    /// <summary>範囲攻撃を Off にできなかったときの理由（記録・画面用。問題なければ null）。</summary>
    public string? AoeProblem { get; private set; }

    /// <summary>
    /// Henched の間、範囲攻撃を Off にしておく（3秒に1回だけ確かめる。内部を読むのが重いため）。
    /// 読めない・入れても Off にならないときは、止めずに記録と警告だけ出す（範囲攻撃で指定外の敵を巻き込む可能性が残る）。
    /// </summary>
    private void EnsureAoeOff()
    {
        if (DateTime.UtcNow - this.lastAoeCheck < HenchedTracker.ResendInterval)
            return;
        this.lastAoeCheck = DateTime.UtcNow;

        var current = RsrStateReader.ReadAoeType();
        var pending = this.store?.RsrAoePending == true;
        var d = RsrAoe.Take(current, this.aoeTakenByMe, this.aoeResends, pending, this.store?.RsrAoeOriginal);
        switch (d.Action)
        {
            case RsrAoe.TakeAction.Nothing:
                return;

            case RsrAoe.TakeAction.Adopt:
                // 前回こちらが Off にしたまま残っている（読み込み直しなど）。控えの値を使う前の値として引き継ぐ
                this.aoeTakenByMe = true;
                this.aoeOriginal = d.Original;
                Core.DebugLog.Current?.Line("IPC", $"前回こちらが Off にした範囲攻撃が残っています。使い終わったら {RsrStateReader.AoeName(d.Original ?? RsrAoe.Full)} に戻します");
                return;

            case RsrAoe.TakeAction.Warn:
                this.AoeProblem = current == null
                    ? $"RSR の範囲攻撃の設定を読めません（{RsrStateReader.LastError}）。範囲攻撃で指定外の敵を巻き込む可能性があります"
                    : $"RSR の範囲攻撃を Off にしても {RsrStateReader.AoeName(current.Value)} に戻ります（利用者か RSR が変えた可能性）。範囲攻撃で指定外の敵を巻き込む可能性があります";
                if (!this.aoeWarned)
                {
                    this.aoeWarned = true;
                    Core.DebugLog.Current?.Line("IPC", $"⚠ {this.AoeProblem}");
                    Svc.Chat.Print($"[AutoJobQuest] {this.AoeProblem}");
                }

                return;

            case RsrAoe.TakeAction.SendOff:
                if (!this.aoeTakenByMe)
                {
                    this.aoeOriginal = d.Original;
                    this.aoeTakenByMe = true;
                    this.SaveAoe(true, d.Original);
                    Core.DebugLog.Current?.Line("IPC", $"RSR の範囲攻撃を戦闘の間だけ Off にします（使う前は {RsrStateReader.AoeName(d.Original ?? RsrAoe.Full)}。使い終わったら戻します）");
                }
                else
                {
                    this.aoeResends++;
                }

                if (!this.SetAoeType(RsrAoe.Off))
                    Core.DebugLog.Current?.Line("IPC", "RSR の範囲攻撃を Off にする命令を送れませんでした（次に確かめるときにやり直します）");
                return;
        }
    }

    /// <summary>範囲攻撃を使う前の値へ戻す（こちらが Off にしていたときだけ。利用者が変えていたら戻さない）。</summary>
    private void ReleaseAoe()
    {
        if (!this.aoeTakenByMe)
            return;

        var current = RsrStateReader.ReadAoeType();
        var original = this.aoeOriginal;
        this.aoeTakenByMe = false;
        this.aoeOriginal = null;
        this.aoeResends = 0;
        this.aoeWarned = false;
        this.AoeProblem = null;
        this.lastAoeCheck = DateTime.MinValue;

        if (RsrAoe.Restore(current, original) is not { } back)
        {
            Core.DebugLog.Current?.Line("IPC", $"RSR の範囲攻撃はもう {RsrStateReader.AoeName(current!.Value)} になっている（利用者が変えた）ので、戻しません");
            this.SaveAoe(false, null);
            return;
        }

        // 送れても、控えは戻ったと確かめるまで残す（RestoreLeftover が確かめる）
        if (this.SetAoeType(back))
            Core.DebugLog.Current?.Line("IPC", $"RSR の範囲攻撃を {RsrStateReader.AoeName(back)} に戻しました（戻ったかは後で確かめます）");
        else
            Core.DebugLog.Current?.Line("IPC", $"⚠ RSR の範囲攻撃を {RsrStateReader.AoeName(back)} に戻す命令を送れませんでした（控えを残し、実行していない間にやり直します）");
    }

    private void SaveAoe(bool pending, byte? original)
    {
        if (this.store == null || (this.store.RsrAoePending == pending && this.store.RsrAoeOriginal == original))
            return;
        this.store.RsrAoePending = pending;
        this.store.RsrAoeOriginal = original;
        this.store.Save();
    }

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
    /// （BeastHelper の作者は「自前の enum で呼んだら黙って効かなかった」と記録している。Dalamud が型の違う引数を JSON で変換して
    /// byte が通ることは、ソースと手元の再現で確かめた（BeastHelper の失敗の原因は、失敗した版が
    /// 履歴に無く分からなかった）。残る原因は PvP のエリア・RSR の自動 OFF。送った直後にモードを読んで確かめ（EnsureHenched）、
    /// それでも黙って送り続けないよう、ここでも数える）。
    ///
    /// 【true だけを成功とみなす】以前は「読めない（null）」も成功側に数えていたので、
    /// 状態の読み出しだけが壊れていると、攻撃しないまま近づき続け、3回で止める仕組みも働かなかった。
    /// なお AutorotationActive は「State か IsManual」なので、true でも Henched になったことまでは証明しない
    /// （IPC にモードを読む口が無いため。RSR の IPCProvider.cs:274・DataCenter.cs:246）。
    /// </summary>
    public bool HenchedUnresponsive => this.safetyProblem != null || this.tracker.Unresponsive;

    /// <summary><see cref="HenchedUnresponsive"/> のときの理由（記録と停止の文言用）。</summary>
    public string HenchedProblem => this.safetyProblem ?? this.tracker.Problem;

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
        // 外部ターゲット指定は内部をリフレクションで読むので重い。入れる前（最初の1回）は必ず読み、入れた後は3秒に1回にする（以前は毎フレーム読んだ）
        if (!this.tracker.HenchedByMe || DateTime.UtcNow - this.lastTargetRead >= HenchedTracker.ResendInterval)
        {
            this.lastTarget = this.readTargetOverride();
            this.lastTargetRead = DateTime.UtcNow;
        }

        var externalTarget = this.lastTarget;
        this.safetyProblem = externalTarget == false ? null : externalTarget == true
            ? "RSR の外部ターゲット指定が有効です。指定外を狙う可能性があるため戦闘を止めます"
            : "RSR の外部ターゲット指定の状態を読めないため戦闘を止めます";
        if (this.safetyProblem != null)
            return false;
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
        {
            if (this.tracker.HenchedByMe)
            {
                this.EnsureAoeOff();
                this.EnsureBoolsOff();
            }

            return true; // 応答あり、または送った直後の反映待ち
        }

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

        // 送った直後に内部のモードを読んで確かめる（RSR の IPC は同じ呼び出しの中で状態を変える）。
        // Henched になっていなければ（PvP のエリア・自動 OFF の条件など）、3秒×3回を待たずに「応答なし」として扱う
        if (RsrStateReader.ReadMode() is { } after && after != ModeHenched)
            this.tracker.Reject($"RSR に Henched を送った直後も、モードが {RsrStateReader.ModeName(after)} のままです（PvP のエリア・RSR の自動 OFF の条件〔カットシーン・エリア移動・ジョブ変更・戦闘不能〕の可能性）");

        this.EnsureAoeOff();
        this.EnsureBoolsOff();
        return true;
    }

    /// <summary>
    /// こちらが Henched にしていたときだけ、使う前のモードに戻す（使う前が Off か、読めなかったなら Off）。
    /// 利用者が使っていた RSR を勝手に止めない・勝手にモードを変えたままにしないため。範囲攻撃の設定も戻す。
    /// 戻す命令が通っても、控え（設定ファイル）は戻ったと確かめるまで残す（確かめるのは <see cref="RestoreLeftover"/>）。
    /// 命令が失敗しても「こちらが使っている」印は外す（同じ読み込みの中でも、実行していない間に控えからやり直せるように。
    /// 以前は印が残ったため、RestoreLeftover が「使っている最中」とみなして何もしなかった）。
    /// </summary>
    public void ReleaseHenched()
    {
        if (!this.tracker.HenchedByMe)
        {
            this.ReleaseAoe();
            this.ReleaseBools();
            return;
        }

        // 戻すのは、いまも Henched のときだけ（こちらが使っている間に利用者が別のモードにしたなら、そのままにする）。
        // いまのモードが読めなければ、今までどおり使う前のモードに戻す
        var current = RsrStateReader.ReadMode();
        var original = this.originalMode;
        this.tracker.Released();
        this.originalMode = null;
        this.modeMismatch = false;

        if (RsrRestore.Decide(current, original) is not { } back)
        {
            Core.DebugLog.Current?.Line("IPC", $"RSR はもう {RsrStateReader.ModeName(current!.Value)} になっている（利用者か RSR が切り替えた）ので、モードは戻しません");
            this.SaveStore(false, null);
            this.ReleaseAoe();
            this.ReleaseBools();
            return;
        }

        if (this.ChangeOperatingMode(back))
            Core.DebugLog.Current?.Line("IPC", $"RSR を {RsrStateReader.ModeName(back)} に戻しました{(original == null ? "（使う前のモードが読めなかったため Off）" : string.Empty)}。戻ったかは後で確かめます");
        else
            Core.DebugLog.Current?.Line("IPC", $"⚠ RSR を {RsrStateReader.ModeName(back)} に戻す命令を送れませんでした（控えを残し、実行していない間に10秒おきにやり直します）");

        this.ReleaseAoe();
        this.ReleaseBools();
    }

    /// <summary>
    /// 戻したかを確かめ、戻っていなければ戻す（実行していない間に、プラグインの側から10秒おきに呼ぶ）。
    ///  ・モード：いまが使う前のモードなら控えを消す（戻ったと確かめた）。いまも Henched なら戻す命令を送り直す。
    ///    それ以外のモード（利用者か RSR が切り替えた）なら、上書きせずに控えを消す。
    ///  ・範囲攻撃：いまが使う前の値なら控えを消す。いまも Off なら戻す命令を送り直す。それ以外なら控えを消す。
    /// 利用者に知らせることがあれば、その説明を返す（戻ったと確かめただけのときは記録にだけ残して null）。
    /// </summary>
    public string? RestoreLeftover()
    {
        if (this.store == null || this.tracker.HenchedByMe || this.aoeTakenByMe)
            return null;

        var notes = new List<string>();
        if (this.store.RsrHenchedPending)
        {
            var current = RsrStateReader.ReadMode();
            var original = this.store.RsrOriginalMode;
            switch (RsrRestore.Verify(current, original))
            {
                case RsrRestore.VerifyResult.Unknown:
                    break; // 読めるようになってから決める（RSR の読み込み直後など）
                case RsrRestore.VerifyResult.Restored:
                    this.SaveStore(false, null);
                    Core.DebugLog.Current?.Line("IPC", $"RSR が使う前のモード（{RsrStateReader.ModeName(current!.Value)}）に戻ったことを確かめました");
                    break;
                case RsrRestore.VerifyResult.UserChanged:
                    this.SaveStore(false, null);
                    notes.Add($"前回こちらが入れた RSR の Henched は、もう {RsrStateReader.ModeName(current!.Value)} に変わっていたので、控えを消しました（モードは変えていません）");
                    break;
                case RsrRestore.VerifyResult.Resend:
                {
                    var back = original ?? ModeOff;
                    if (this.ChangeOperatingMode(back))
                        notes.Add($"前回戻せなかった RSR のモードを {RsrStateReader.ModeName(back)} に戻しました（戻ったかは次に確かめます）");
                    break;
                }
            }
        }

        if (this.store.RsrAoePending)
        {
            var current = RsrStateReader.ReadAoeType();
            var original = this.store.RsrAoeOriginal;
            switch (RsrAoe.Verify(current, original))
            {
                case RsrRestore.VerifyResult.Unknown:
                    break;
                case RsrRestore.VerifyResult.Restored:
                    this.SaveAoe(false, null);
                    Core.DebugLog.Current?.Line("IPC", $"RSR の範囲攻撃が使う前の値（{RsrStateReader.AoeName(current!.Value)}）に戻ったことを確かめました");
                    break;
                case RsrRestore.VerifyResult.UserChanged:
                    this.SaveAoe(false, null);
                    notes.Add($"前回こちらが Off にした RSR の範囲攻撃は、もう {RsrStateReader.AoeName(current!.Value)} に変わっていたので、控えを消しました");
                    break;
                case RsrRestore.VerifyResult.Resend:
                {
                    var back = original ?? RsrAoe.Full;
                    if (this.SetAoeType(back))
                        notes.Add($"前回戻せなかった RSR の範囲攻撃を {RsrStateReader.AoeName(back)} に戻しました（戻ったかは次に確かめます）");
                    break;
                }
            }
        }

        // 真偽の設定（TargetFreely・IgnoreNonFateInFate）
        foreach (var (name, original) in this.store.RsrBoolOriginals.ToList())
        {
            if (this.boolTaken.ContainsKey(name))
                continue;
            var current = RsrStateReader.ReadBool(name);
            switch (RsrBoolSetting.Verify(current, false, original))
            {
                case RsrRestore.VerifyResult.Unknown:
                    break;
                case RsrRestore.VerifyResult.Restored:
                    this.SaveBool(name, null);
                    Core.DebugLog.Current?.Line("IPC", $"RSR の {name} が使う前の値（{original}）に戻ったことを確かめました");
                    break;
                case RsrRestore.VerifyResult.UserChanged:
                    this.SaveBool(name, null);
                    break;
                case RsrRestore.VerifyResult.Resend:
                    if (this.SetBoolSetting(name, original))
                        notes.Add($"前回戻せなかった RSR の {name} を {original} に戻しました（戻ったかは次に確かめます）");
                    break;
            }
        }

        return notes.Count > 0 ? string.Join(" / ", notes) : null;
    }

    /// <summary>戻したかを確かめる控えが残っているか（画面・事前点検用）。</summary>
    public bool RestorePending => this.store?.RsrHenchedPending == true || this.store?.RsrAoePending == true || this.store?.RsrBoolOriginals.Count > 0;

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

    /// <summary>戻したかを確かめた結果。</summary>
    public enum VerifyResult
    {
        /// <summary>いまの値が読めない（決めずに次に回す）。</summary>
        Unknown,

        /// <summary>使う前の値に戻っている（控えを消す）。</summary>
        Restored,

        /// <summary>まだこちらの値のまま（戻す命令を送り直す）。</summary>
        Resend,

        /// <summary>別の値になっている＝利用者か相手が変えた（上書きせずに控えを消す）。</summary>
        UserChanged,
    }

    /// <summary>
    /// 戻したかを確かめる（命令が通っただけでは戻ったことにしない）。
    /// 使う前が Henched だった（利用者が Henched で使っていた）ときは、Henched のままが「戻った」（必ず Off にはしない）。
    /// </summary>
    /// <param name="current">いまのモード（読めなければ null）。</param>
    /// <param name="original">使う前のモード（読めなかったなら null＝Off に戻したはず）。</param>
    public static VerifyResult Verify(byte? current, byte? original)
    {
        if (current is not { } c)
            return VerifyResult.Unknown;
        if (c == (original ?? RotationSolverIpc.ModeOff))
            return VerifyResult.Restored;
        return c == RotationSolverIpc.ModeHenched ? VerifyResult.Resend : VerifyResult.UserChanged;
    }
}

/// <summary>
/// RSR の範囲攻撃（AoEType）を戦闘の間だけ Off にする決まり（IPC を呼ばない部分。ゲームを起動せずに試せるように分けた）。
/// 値は RSR の AoEType：Off=0, Cleave=1, Full=2（RSR の Configuration/ConfigTypes.cs で確認）。
/// </summary>
public static class RsrAoe
{
    public const byte Off = 0;
    public const byte Full = 2;

    /// <summary>何回入れ直しても Off にならなければ、入れ直しをやめて警告する。</summary>
    public const int ResendLimit = 3;

    public enum TakeAction
    {
        /// <summary>何もしない（もう Off・利用者が自分で Off にしている等）。</summary>
        Nothing,

        /// <summary>Off にする命令を送る（初めて・または入れ直し）。</summary>
        SendOff,

        /// <summary>前回こちらが Off にしたまま残っている。控えの値を使う前の値として引き継ぐ。</summary>
        Adopt,

        /// <summary>読めない・入れ直しても Off にならない。止めずに警告する。</summary>
        Warn,
    }

    /// <summary>Off にするかの判断。</summary>
    /// <param name="current">いまの値（読めなければ null）。</param>
    /// <param name="takenByMe">こちらが Off にしているか。</param>
    /// <param name="resends">入れ直した回数。</param>
    /// <param name="hasPending">前回こちらが Off にしたままの控えがあるか。</param>
    /// <param name="pendingOriginal">控えの、使う前の値。</param>
    public static (TakeAction Action, byte? Original) Take(byte? current, bool takenByMe, int resends, bool hasPending, byte? pendingOriginal)
    {
        if (current is not { } c)
            return (TakeAction.Warn, null);

        if (c == Off)
        {
            // こちらが Off にしている／利用者が自分で Off にしている＝そのまま。前回の控えが残っていれば引き継ぐ
            if (!takenByMe && hasPending)
                return (TakeAction.Adopt, pendingOriginal);
            return (TakeAction.Nothing, null);
        }

        // Off ではない。初めてなら今の値を使う前の値として覚えて Off にする（前回の控えがあっても、Off でないなら
        // 利用者が戻したということなので、今の値が使う前の値）
        if (!takenByMe)
            return (TakeAction.SendOff, c);

        // こちらが Off にしたのに Off でない：入れ直す（上限まで）
        return resends < ResendLimit ? (TakeAction.SendOff, null) : (TakeAction.Warn, null);
    }

    /// <summary>
    /// 戻す値。戻さないなら null。いまの値が読めて Off でない（利用者が変えた）なら戻さない。
    /// いまも Off か読めないなら、使う前の値（分からなければ Full＝RSR の既定値）へ戻す。
    /// </summary>
    public static byte? Restore(byte? current, byte? original)
        => current is { } c && c != Off ? null : original ?? Full;

    /// <summary>戻したかを確かめる（使う前の値なら戻った。まだ Off なら送り直す。それ以外は利用者が変えた）。</summary>
    public static RsrRestore.VerifyResult Verify(byte? current, byte? original)
    {
        if (current is not { } c)
            return RsrRestore.VerifyResult.Unknown;
        if (c == (original ?? Full))
            return RsrRestore.VerifyResult.Restored;
        return c == Off ? RsrRestore.VerifyResult.Resend : RsrRestore.VerifyResult.UserChanged;
    }
}

/// <summary>
/// RSR に Henched を頼むときの応答の数え方（IPC を呼ばない部分だけ。ゲームを起動せずに試せるように分けた）。
///  ・こちらが入れていて、動作中（true）と読めたら「応答あり」。数を 0 に戻す。
///  ・送ってから3秒は反映待ち（送り直さない）。
///  ・それ以外は送り直す。こちらが入れた後の false を Unanswered、読めない（null）を Unreadable として数える。どちらか3回で「応答なし」。
/// </summary>
/// <summary>
/// RSR の真偽の設定を、Henched の間だけ決めた値にするかの判断（範囲攻撃の <see cref="RsrAoe"/> と同じ考え方。IPC を呼ばない部分）。
/// </summary>
public static class RsrBoolSetting
{
    /// <summary>決めた値にするかの判断。</summary>
    /// <param name="current">いまの値（読めなければ null）。</param>
    /// <param name="desired">Henched の間にしたい値。</param>
    /// <param name="takenByMe">こちらが変えているか。</param>
    /// <param name="resends">入れ直した回数（警告した後は -1）。</param>
    /// <param name="pendingOriginal">前回こちらが変えたままの控えの、使う前の値（無ければ null）。</param>
    public static (RsrAoe.TakeAction Action, bool? Original) Take(bool? current, bool desired, bool takenByMe, int resends, bool? pendingOriginal)
    {
        if (current is not { } c)
            return (resends == -1 ? RsrAoe.TakeAction.Nothing : RsrAoe.TakeAction.Warn, null);
        if (c == desired)
            return !takenByMe && pendingOriginal is { } po ? (RsrAoe.TakeAction.Adopt, po) : (RsrAoe.TakeAction.Nothing, null);
        if (!takenByMe)
            return (RsrAoe.TakeAction.SendOff, c);
        if (resends == -1)
            return (RsrAoe.TakeAction.Nothing, null);
        return resends < RsrAoe.ResendLimit ? (RsrAoe.TakeAction.SendOff, null) : (RsrAoe.TakeAction.Warn, null);
    }

    /// <summary>戻す値（戻さないなら null）。いまの値が読めて決めた値でない（利用者が変えた）なら戻さない。</summary>
    public static bool? Restore(bool? current, bool desired, bool original)
        => current is { } c && c != desired ? null : original;

    /// <summary>戻したかを確かめる（使う前の値なら戻った。まだ決めた値なら送り直す）。</summary>
    public static RsrRestore.VerifyResult Verify(bool? current, bool desired, bool original)
    {
        if (current is not { } c)
            return RsrRestore.VerifyResult.Unknown;
        if (c == original)
            return RsrRestore.VerifyResult.Restored;
        return c == desired ? RsrRestore.VerifyResult.Resend : RsrRestore.VerifyResult.UserChanged;
    }
}

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

    /// <summary>送った直後にモードを読んで、効いていないと分かったときの理由（無ければ null）。</summary>
    public string? Rejected { get; private set; }

    public bool Unresponsive => this.Unanswered >= 3 || this.Unreadable >= 3 || this.Rejected != null;

    /// <summary>送った直後にモードを読んで、効いていないと分かった。</summary>
    public void Reject(string reason) => this.Rejected = reason;

    public string Problem => this.Rejected ?? (this.Unreadable >= 3
        ? $"RSR の動作状態（AutorotationActive）を {this.Unreadable} 回続けて読めませんでした（IPC が変わった可能性）"
        : $"RSR に Henched への切り替えを {this.Unanswered} 回送っても動作中になりません（IPC が効いていない可能性）");

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
        this.Rejected = null;
        this.lastSend = DateTime.MinValue;
    }
}
