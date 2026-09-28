using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AutoJobQuest.Ipc;

/// <summary>GBR の自動採集リストに入れる1品目。</summary>
/// <param name="ItemId">アイテム ID。</param>
/// <param name="TargetOwned">「持っていたい総数」（GBR は所持数 &lt; 目標の間だけ採る）。</param>
/// <param name="PreferredTerritory">優先して採るエリア（null なら GBR に任せる）。</param>
public sealed record GbrGatherEntry(uint ItemId, uint TargetOwned, uint? PreferredTerritory);

/// <summary>
/// GBR をリフレクションで操作する（採集リスト・設定の一時変更・NPC 購入）。
///
/// 手本は GBR 自身の「一時リスト」の作法（GBR CraftingGatherBridge.cs:229-297, 423-436）:
///   他の有効リストを無効にして覚える → 自分のリストを作って品目を入れる → UsesRetainerInventory=false
///   → Enabled=true → Save() → SetActiveItems(false) → IPC で ON。
///   戻すときは逆順。
///
/// 【控えを残す】GBR の Save() で一時状態がファイルに残る。途中でゲームが落ちると利用者のリストが
/// 無効のままになるので、無効にしたリスト名と書き換えた設定の元の値をこちらの設定に保存してから変える。
/// 次回起動時に <see cref="RestoreLeftovers"/> で戻す。
/// </summary>
public sealed class GbrOperations
{
    public const string OwnListName = "AutoJobQuest";

    private readonly GbrReflection reflection;
    private readonly Configuration config;

    // 今回の実行で無効にしたリスト（参照と、無効にした時点の名前＋フォルダ）。GBR が読み直されたら名前で探し直す。
    private readonly List<(object List, GbrListRef Ref)> disabledByMe = [];

    public GbrOperations(GbrReflection reflection, Configuration config)
    {
        this.reflection = reflection;
        this.config = config;
    }

    public string? LastError { get; private set; }

    private void SetError(string message)
    {
        this.LastError = message;
        Core.DebugLog.Current?.Line("GBR", "失敗: " + message);
    }

    private static void Note(string message) => Core.DebugLog.Current?.Line("GBR", message);

    // ------------------------------------------------------------------
    // 事前点検（読むだけ）

    /// <summary>GBR の設定を読む。読めなければ null。</summary>
    public bool? ReadAutoGatherBool(string name)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            return h.GetAutoGatherBool(name);
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の設定 {name} を読めません: {ex.Message}");
            return null;
        }
    }

    /// <summary>収集品を自動で納品しに行く設定か（true だと採った収集品を勝手に納品しに行く）。</summary>
    public bool? ReadAutoTurnInCollectables()
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var cc = h.CollectableConfig;
            return (bool)cc.GetType().GetProperty("AutoTurnInCollectables", GbrHandle.PubInst)!.GetValue(cc)!;
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の収集品設定を読めません: {ex.Message}");
            return null;
        }
    }

    /// <summary>GBR がその品目を扱えるか（採集品か魚で、場所が1件以上）。扱えるならエリアの一覧を返す。</summary>
    public List<uint>? GatherableTerritories(uint itemId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var g = h.FindGatherable(itemId);
            if (g == null)
                return [];
            return h.Locations(g).Select(GbrHandle.TerritoryOf).Distinct().ToList();
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の品目データを読めません（{itemId}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>その品目が GBR で魚として扱われるか。</summary>
    public bool? IsFish(uint itemId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var g = h.FindGatherable(itemId);
            return g != null && GbrHandle.IsFish(g);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 設定の一時変更

    /// <summary>
    /// 自動採集の設定を一時的に書き換える。元の値は控えに残す。
    /// 例: GoHomeWhenDone / GoHomeWhenIdle を false（終わったときに勝手に帰宅＝テレポしないように）。
    /// </summary>
    public bool OverrideBool(string name, bool value)
    {
        var h = this.reflection.Get();
        if (h == null)
        {
            this.SetError("GBR に届きません");
            return false;
        }

        try
        {
            var current = h.GetAutoGatherBool(name);
            if (current == value)
                return true;

            // 先に控えを保存してから書き換える（落ちても戻せるように）
            if (!this.config.GbrConfigOriginals.ContainsKey(name))
            {
                this.config.GbrConfigOriginals[name] = current;
                this.config.Save();
            }

            Note($"設定 {name} を一時的に {current} → {value}");
            h.SetAutoGatherBool(name, value);
            h.SaveConfig();
            return h.GetAutoGatherBool(name) == value;
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の設定 {name} を変えられません: {ex.Message}");
            return false;
        }
    }

    /// <summary>元に戻すべき一時変更が残っているか。</summary>
    public bool HasLeftovers
        => this.config.GbrOwnListActive || this.config.GbrDisabledListRefs.Count > 0 || this.config.GbrConfigOriginals.Count > 0;

    /// <summary>
    /// GBR の自動採集が止まっているときだけ、リストと設定を元に戻す。
    ///
    /// 【止まっていることを確かめる理由】GBR が採集中にリストを戻すと、GBR はそのまま利用者のリストを採り始め、
    /// 帰宅設定も戻るので終わったときにテレポする（こちらを読み直しただけで GBR の動きが変わる）。
    /// 止まっていない・読めないときは戻さずに控えを残し、あとで（止まっている間に定期的に）戻す。
    /// 購入の状態も「止まっている（false）」と読めたときだけ戻す（以前は読めない＝null を
    /// 止まっている側に数えていたので、購入が続いているか分からないまま設定を戻しえた）。
    /// </summary>
    /// <param name="gbrEnabled">GBR の自動採集の状態（IPC で読んだ値。読めなければ null）。</param>
    /// <param name="vendorBusy">GBR の NPC 購入が動いているか（読めなければ null）。</param>
    /// <returns>戻し終わったら true。</returns>
    public bool RestoreIfIdle(bool? gbrEnabled, bool? vendorBusy)
    {
        if (!this.HasLeftovers)
            return true;

        if (!CanRestore(gbrEnabled, vendorBusy))
        {
            Note($"GBR が動いている（または状態が読めない）ので、リストと設定はまだ戻しません（自動採集={gbrEnabled?.ToString() ?? "不明"}、購入中={vendorBusy?.ToString() ?? "不明"}）");
            return false;
        }

        var lists = this.RestoreGatherLists();
        var cfg = this.RestoreConfig();
        return lists && cfg;
    }

    /// <summary>戻してよいか：自動採集も NPC 購入も「止まっている（false）」と読めたときだけ（読めない null は戻さない）。</summary>
    public static bool CanRestore(bool? gbrEnabled, bool? vendorBusy) => gbrEnabled == false && vendorBusy == false;

    /// <summary>
    /// 設定を戻す本体（GBR への読み書きは外から渡す。ゲームを起動せずに試せるように分けた）。
    /// 今の値が「こちらが入れた値」（元の値の反対）なら元に戻し、そうでなければ利用者が変えたとみなして触らない。
    /// 最後に保存し、**保存が通ってから**控えから消す。例外なら控えは全部残す。
    /// </summary>
    public static bool RestoreConfigCore(
        Dictionary<string, bool> originals, Func<string, bool> get, Action<string, bool> set, Action save, Action<string> note, out string? error)
    {
        error = null;
        var done = new List<string>();
        try
        {
            foreach (var (name, original) in originals.ToList())
            {
                if (get(name) == !original)
                {
                    set(name, original);
                    note($"設定 {name} を元の {original} に戻します");
                }
                else
                {
                    note($"設定 {name} は途中で変わっていたので戻しません（今の値を尊重）");
                }

                done.Add(name);
            }

            save();
        }
        catch (Exception ex)
        {
            error = Unwrap(ex);
            return false;
        }

        foreach (var name in done)
            originals.Remove(name);
        note($"GBR の設定を保存しました（戻した・確かめた設定：{string.Join("、", done)}）");
        return true;
    }

    /// <summary>
    /// 無効にしたリストの戻し先を決める本体（リストの中身には触らない。ゲームを起動せずに試せるように分けた）。
    /// 控え1件ずつ、この実行で無効にしたリストそのもの（生きている参照）があればそれ、無ければ名前＋フォルダで探して
    /// ちょうど1つのときだけ。決まらない控えは Unresolved に入れる。
    /// </summary>
    public static (List<T> Enable, List<GbrListRef> Restored, List<GbrListRef> Unresolved) PlanListRestore<T>(
        IEnumerable<GbrListRef> refs, IEnumerable<(T List, GbrListRef Ref)> mine, IReadOnlyList<T> alive, Func<T, GbrListRef> refOf)
        where T : class
    {
        var enable = new List<T>();
        var restored = new List<GbrListRef>();
        var unresolved = new List<GbrListRef>();
        var mineList = mine.ToList();
        foreach (var reference in refs)
        {
            // この実行で無効にしたリストそのもの（同じ控えに当たるものは全部。同じ名前のリストを複数無効にした場合）
            var targets = mineList
                .Where(d => d.Ref == reference && alive.Any(x => ReferenceEquals(x, d.List)))
                .Select(d => d.List)
                .ToList();

            if (targets.Count == 0)
            {
                // 参照が無い：名前＋フォルダで探す。1つに決まるときだけ戻す
                var matches = alive.Where(x => refOf(x) == reference).ToList();
                if (matches.Count != 1)
                {
                    unresolved.Add(reference);
                    continue;
                }

                targets = matches;
            }

            enable.AddRange(targets);
            restored.Add(reference);
        }

        return (enable, restored, unresolved);
    }

    /// <summary>
    /// 一時的に書き換えた設定を戻す。今の値が「こちらが入れた値」のままのときだけ戻す
    /// （途中で利用者が変えたら、そちらを尊重する）。GBR が止まっていることを呼び出し側で確かめること。
    /// </summary>
    private bool RestoreConfig()
    {
        if (this.config.GbrConfigOriginals.Count == 0)
            return true;

        var h = this.reflection.Get();
        if (h == null)
            return false; // 届くようになってから戻す

        // 控えは GBR の保存が通ってから消す（以前は1件ずつ控えを消してから最後に保存していたので、
        // 保存で例外が出ると、控えだけ消えてやり直しの対象から外れていた）
        if (!RestoreConfigCore(this.config.GbrConfigOriginals, h.GetAutoGatherBool, h.SetAutoGatherBool, h.SaveConfig, Note, out var error))
        {
            this.SetError($"GBR の設定を戻せませんでした（控えは残し、止まっている間にやり直します）: {error}");
            return false;
        }

        this.config.Save();
        return true;
    }

    // ------------------------------------------------------------------
    // 自動採集リスト

    /// <summary>
    /// 専用リスト「AutoJobQuest」を作り直し、他の有効リストを退避して、自分のリストだけを有効にする。
    /// ここではまだ自動採集を ON にしない（呼び出し側が IPC で ON にする）。
    /// </summary>
    public bool PrepareGatherList(IReadOnlyList<GbrGatherEntry> entries, out List<uint> unsupported)
    {
        unsupported = [];
        var h = this.reflection.Get();
        if (h == null)
        {
            this.SetError("GBR に届きません");
            return false;
        }

        try
        {
            var r = new ListReflection(h);
            var list = r.FindOwnList() ?? r.CreateOwnList();

            // 前回の品目を全部消す
            var items = (IList)r.PItems.GetValue(list)!;
            for (var i = items.Count - 1; i >= 0; i--)
                r.MListRemoveAt.Invoke(list, [i]);

            foreach (var e in entries)
            {
                var g = h.FindGatherable(e.ItemId);
                if (g == null)
                {
                    unsupported.Add(e.ItemId);
                    continue;
                }

                if (!(bool)r.MListAdd.Invoke(list, [g, e.TargetOwned])!)
                    r.MListSetQty.Invoke(list, [g, e.TargetOwned]);

                if (e.PreferredTerritory is { } terr)
                {
                    var chosen = h.Locations(g).FirstOrDefault(l => GbrHandle.TerritoryOf(l) == terr);
                    if (chosen != null)
                        r.MChangePref.Invoke(r.Manager, [list, g, chosen]);
                }
            }

            // 手持ちだけで数えさせる（保存されない値なので毎回入れる）
            r.PUsesRetainer.SetValue(list, false);

            // 他の有効リストを退避（控えを保存してから）
            foreach (var l in r.AllLists())
            {
                if (ReferenceEquals(l, list))
                    continue;
                if (!(bool)r.PEnabled.GetValue(l)!)
                    continue;

                // 控えは名前とフォルダの組で残す（GBR は同じ名前のリストを許すため、名前だけでは戻し先を取り違える）
                var reference = new GbrListRef((string?)r.PName.GetValue(l) ?? string.Empty, (string?)r.PFolderPath.GetValue(l) ?? string.Empty);
                if (!this.config.GbrDisabledListRefs.Contains(reference))
                    this.config.GbrDisabledListRefs.Add(reference);
                this.config.Save();

                r.PEnabled.SetValue(l, false);
                this.disabledByMe.Add((l, reference));
            }

            Note($"自動採集リスト「{OwnListName}」を用意：{string.Join("、", entries.Select(e => $"{Data.CraftPlanner.ItemName(e.ItemId)} 目標{e.TargetOwned}{(e.PreferredTerritory is { } t ? $"（優先エリア {t}）" : string.Empty)}"))}"
                 + $"／一時的に無効にしたリスト：{(this.disabledByMe.Count == 0 ? "なし" : string.Join("、", this.disabledByMe.Select(d => d.Ref.Name)))}"
                 + (unsupported.Count > 0 ? $"／GBR で扱えない品目：{string.Join("、", unsupported.Select(Data.CraftPlanner.ItemName))}" : string.Empty));
            r.PEnabled.SetValue(list, true);
            this.config.GbrOwnListActive = true;
            this.config.Save();

            r.MSave.Invoke(r.Manager, null);
            r.MSetActive.Invoke(r.Manager, [false]);
            return true;
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の採集リストを用意できませんでした: {Unwrap(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 自分のリストを無効にし、退避したリストを戻す。GBR が止まっていることを呼び出し側で確かめること。
    ///
    /// 戻し方：控え1件ずつ、この実行で無効にしたリストそのもの（参照）が生きていればそれを戻す。参照が無いとき
    /// （GBR が読み直された・こちらが読み直された・落ちた）だけ、控えの「名前＋フォルダ」で探す。
    /// 候補が1つに決まらない控え（見つからない・同じ名前が複数）は戻さず、「戻せなかった控え」
    /// （<see cref="Configuration.GbrUnresolvedListRefs"/>）へ移して画面に出す（利用者が確かめて消すまで残す）。
    ///
    /// 以前は、参照が1つでも生きていると名前で探す経路に入らず、戻せなかった控えも
    /// まとめて消していた（記録には「後で戻す」と出るのに、実際にはやり直されなかった）。
    /// 控えの書き換えは GBR の保存が通ってからにする（保存で例外が出たら控えはそのまま＝やり直せる）。
    /// </summary>
    private bool RestoreGatherLists()
    {
        if (!this.config.GbrOwnListActive && this.config.GbrDisabledListRefs.Count == 0)
            return true;

        var h = this.reflection.Get();
        if (h == null)
            return false; // 届くようになってから戻す

        List<GbrListRef> restored;
        List<GbrListRef> unresolved;
        try
        {
            var r = new ListReflection(h);
            if (r.FindOwnList() is { } own)
            {
                // 品目も消しておく（利用者が GBR の画面で見たときに紛らわしくないように）
                var items = (IList)r.PItems.GetValue(own)!;
                for (var i = items.Count - 1; i >= 0; i--)
                    r.MListRemoveAt.Invoke(own, [i]);
                r.PEnabled.SetValue(own, false);
            }

            // 戻し先を決める（PlanListRestore。同じ控えに当たるリストは全部・参照が無ければ名前＋フォルダで1つに決まるときだけ）
            var alive = r.AllLists().ToList();
            var plan = PlanListRestore(
                this.config.GbrDisabledListRefs, this.disabledByMe, alive,
                x => new GbrListRef((string?)r.PName.GetValue(x) ?? string.Empty, (string?)r.PFolderPath.GetValue(x) ?? string.Empty));
            restored = plan.Restored;
            unresolved = plan.Unresolved;

            foreach (var t in plan.Enable)
            {
                if (!(bool)r.PEnabled.GetValue(t)!)
                    r.PEnabled.SetValue(t, true);
            }

            foreach (var u in unresolved)
            {
                var count = alive.Count(x => (string?)r.PName.GetValue(x) == u.Name && ((string?)r.PFolderPath.GetValue(x) ?? string.Empty) == u.FolderPath);
                Note($"⚠ リスト「{u.Name}」（フォルダ「{u.FolderPath}」）は{(count == 0 ? "見つからない" : $"同じ名前が {count} 個ある")}ので戻しません。GBR の画面で確かめてください（画面の「戻せなかった GBR のリスト」に残します）");
            }

            r.MSetActive.Invoke(r.Manager, [false]);
            r.MSave.Invoke(r.Manager, null);
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の採集リストを戻せませんでした（控えは残し、止まっている間にやり直します）: {Unwrap(ex)}");
            return false;
        }

        // 保存が通った。戻せたものは控えから消し、戻せなかったものは別の控えへ移す（自動ではやり直さない：
        // 見つからない・同じ名前が複数は、時間がたっても変わらないため。利用者が確かめて消す）
        foreach (var u in unresolved)
            if (!this.config.GbrUnresolvedListRefs.Contains(u))
                this.config.GbrUnresolvedListRefs.Add(u);
        this.disabledByMe.Clear();
        this.config.GbrDisabledListRefs.Clear();
        this.config.GbrOwnListActive = false;
        this.config.Save();

        Note($"自動採集リストを元に戻しました（戻したリスト：{(restored.Count == 0 ? "なし" : string.Join("、", restored.Select(x => x.Name)))}"
             + (unresolved.Count > 0 ? $"／戻せなかったリスト：{string.Join("、", unresolved.Select(u => u.Name))}" : string.Empty) + "）");
        return true;
    }

    // ------------------------------------------------------------------
    // NPC 購入（VendorBuyListManager）

    /// <summary>
    /// NPC 購入の専用リストを用意する。ギルの店で買えるものだけを入れる。
    /// 戻り値のリスト ID を <see cref="StartVendor"/> に渡す。
    /// </summary>
    public Guid? PrepareVendorList(IReadOnlyList<(uint ItemId, uint TargetOwned)> entries, out List<uint> notGil)
    {
        notGil = [];
        var h = this.reflection.Get();
        if (h == null)
        {
            this.SetError("GBR に届きません");
            return null;
        }

        try
        {
            var vblm = h.VendorBuyListManager;
            var vt = vblm.GetType();

            if ((bool)vt.GetProperty("IsBusy", GbrHandle.PubInst)!.GetValue(vblm)!)
            {
                this.SetError("GBR の購入が別に動いています");
                return null;
            }

            var def = ((IEnumerable)vt.GetProperty("Lists", GbrHandle.PubInst)!.GetValue(vblm)!).Cast<object>()
                .FirstOrDefault(d => string.Equals(d.GetType().GetProperty("Name")!.GetValue(d) as string, OwnListName, StringComparison.OrdinalIgnoreCase));
            def ??= vt.GetMethod("CreateList", GbrHandle.PubInst, null, [typeof(string), typeof(bool)], null)!
                .Invoke(vblm, [OwnListName, false]);
            var listId = (Guid)def!.GetType().GetProperty("Id")!.GetValue(def)!;

            var entryList = (IList)def.GetType().GetProperty("Entries")!.GetValue(def)!;
            var removeEntry = vt.GetMethod("RemoveEntry", GbrHandle.PubInst, null, [typeof(Guid)], null)!;

            // 前回の品目を消す
            foreach (var e in entryList.Cast<object>().ToList())
                removeEntry.Invoke(vblm, [(Guid)e.GetType().GetProperty("Id")!.GetValue(e)!]);

            var gilRoute = this.GilRouteItems(h, entries.Select(e => e.ItemId));
            var trySet = vt.GetMethod("TrySetTarget", GbrHandle.PubInst, null,
                [typeof(Guid), typeof(uint), typeof(uint), typeof(bool), typeof(bool), typeof(bool)], null)!;

            foreach (var (itemId, target) in entries)
            {
                if (gilRoute != null && !gilRoute.Contains(itemId))
                {
                    notGil.Add(itemId);
                    continue;
                }

                // openWindow:false（既定の true だと GBR の窓が開く）
                var ok = (bool)trySet.Invoke(vblm, [listId, itemId, target, false, false, false])!;
                if (!ok)
                    notGil.Add(itemId);
            }

            // 事後確認：ギルの店（ShopType==0）以外に落ちた品目は外す
            foreach (var e in entryList.Cast<object>().ToList())
            {
                var et = e.GetType();
                var shopType = Convert.ToInt32(et.GetProperty("ShopType")!.GetValue(e));
                if (shopType == 0)
                    continue;

                var itemId = (uint)et.GetProperty("ItemId")!.GetValue(e)!;
                notGil.Add(itemId);
                removeEntry.Invoke(vblm, [(Guid)et.GetProperty("Id")!.GetValue(e)!]);
            }

            var skipped = notGil.ToHashSet();
            Note($"NPC 購入リストを用意：{string.Join("、", entries.Where(e => !skipped.Contains(e.ItemId)).Select(e => $"{Data.CraftPlanner.ItemName(e.ItemId)} 目標{e.TargetOwned}"))}"
                 + (skipped.Count > 0 ? $"／ギルの店で自動購入できない：{string.Join("、", skipped.Select(Data.CraftPlanner.ItemName))}" : string.Empty));
            return listId;
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の購入リストを用意できませんでした: {Unwrap(ex)}");
            return null;
        }
    }

    /// <summary>購入を始める。戻り値は GBR の StartResult の名前（失敗時は null）。</summary>
    public string? StartVendor(Guid listId)
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var vblm = h.VendorBuyListManager;
            var start = vblm.GetType().GetMethod("Start", GbrHandle.PubInst)!;
            var result = start.Invoke(vblm, [listId, null])?.ToString();
            Note($"NPC 購入を開始 → {result}");
            return result;
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の購入を始められませんでした: {Unwrap(ex)}");
            return null;
        }
    }

    /// <summary>購入処理中か（店を閉じる待ち・中止待ちも含む）。読めなければ null。</summary>
    public bool? VendorIsBusy()
    {
        var h = this.reflection.Get();
        if (h == null)
            return null;
        try
        {
            var vblm = h.VendorBuyListManager;
            return (bool)vblm.GetType().GetProperty("IsBusy", GbrHandle.PubInst)!.GetValue(vblm)!;
        }
        catch
        {
            return null;
        }
    }

    public string VendorStatusText()
    {
        var h = this.reflection.Get();
        if (h == null)
            return string.Empty;
        try
        {
            var vblm = h.VendorBuyListManager;
            return vblm.GetType().GetProperty("StatusText", GbrHandle.PubInst)!.GetValue(vblm) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>購入を止める（こちらが始めた購入のときだけ）。</summary>
    public void StopVendor()
    {
        var h = this.reflection.Get();
        if (h == null)
            return;
        try
        {
            var vblm = h.VendorBuyListManager;
            Note("NPC 購入を止めます");
            vblm.GetType().GetMethod("Stop", GbrHandle.PubInst, null, Type.EmptyTypes, null)!.Invoke(vblm, null);
        }
        catch (Exception ex)
        {
            this.SetError($"GBR の購入を止められませんでした: {Unwrap(ex)}");
        }
    }

    /// <summary>
    /// 購入リスト「AutoJobQuest」の品目を消す（実行後に GBR の設定へ品目が残らないように）。
    /// GBR の購入が動いているときは何もしない。
    /// </summary>
    public void ClearVendorList()
    {
        var h = this.reflection.Get();
        if (h == null)
            return;
        try
        {
            var vblm = h.VendorBuyListManager;
            var vt = vblm.GetType();
            if ((bool)vt.GetProperty("IsBusy", GbrHandle.PubInst)!.GetValue(vblm)!)
                return;

            var def = ((IEnumerable)vt.GetProperty("Lists", GbrHandle.PubInst)!.GetValue(vblm)!).Cast<object>()
                .FirstOrDefault(d => string.Equals(d.GetType().GetProperty("Name")!.GetValue(d) as string, OwnListName, StringComparison.OrdinalIgnoreCase));
            if (def == null)
                return;

            var entryList = (IList)def.GetType().GetProperty("Entries")!.GetValue(def)!;
            var removeEntry = vt.GetMethod("RemoveEntry", GbrHandle.PubInst, null, [typeof(Guid)], null)!;
            var n = 0;
            foreach (var e in entryList.Cast<object>().ToList())
            {
                removeEntry.Invoke(vblm, [(Guid)e.GetType().GetProperty("Id")!.GetValue(e)!]);
                n++;
            }

            if (n > 0)
                Note($"NPC 購入リスト「{OwnListName}」の品目を消しました（{n} 件）");
        }
        catch (Exception ex)
        {
            this.SetError($"NPC 購入リストの品目を消せませんでした: {Unwrap(ex)}");
        }
    }

    /// <summary>
    /// 欲しい品目のうち「ギルの店で、自動購入に対応した NPC から買える」ものの集合を返す。
    /// 店データがまだ読み込み中なら null（判定せず、事後確認だけに頼る）。
    /// 全ギル店エントリーを1回だけ走査する（品目ごとに全エントリーを舐めると1フレームが長く止まるため）。
    /// </summary>
    private HashSet<uint>? GilRouteItems(GbrHandle h, IEnumerable<uint> wanted)
    {
        var resolver = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorShopResolver", throwOnError: false);
        var vpm = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorPurchaseManager", throwOnError: false);
        var excl = h.GbrAsm.GetType("GatherBuddy.Vulcan.Vendors.VendorDevExclusions", throwOnError: false);
        if (resolver == null || vpm == null || excl == null)
            return null;

        if (resolver.GetProperty("IsInitialized", GbrHandle.PubStatic)?.GetValue(null) is not true)
            return null;

        var isSupported = vpm.GetMethod("IsPurchaseSupported", GbrHandle.PubStatic);
        var isExcluded = excl.GetMethod("IsExcluded", GbrHandle.PubStatic);
        var entries = resolver.GetProperty("GilShopEntries", GbrHandle.PubStatic)?.GetValue(null) as IEnumerable;
        if (isSupported == null || isExcluded == null || entries == null)
            return null;

        var want = wanted.ToHashSet();
        var result = new HashSet<uint>();
        PropertyInfo? pItem = null, pNpcs = null;
        foreach (var e in entries)
        {
            pItem ??= e.GetType().GetProperty("ItemId");
            pNpcs ??= e.GetType().GetProperty("Npcs");
            var item = (uint)pItem!.GetValue(e)!;
            if (!want.Contains(item) || result.Contains(item))
                continue;

            foreach (var n in (IEnumerable)pNpcs!.GetValue(e)!)
            {
                if ((bool)isSupported.Invoke(null, [e, n])! && !(bool)isExcluded.Invoke(null, [n])!)
                {
                    result.Add(item);
                    break;
                }
            }
        }

        return result;
    }

    private static string Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
            ex = inner;
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>自動採集リストまわりのメンバー。</summary>
    private sealed class ListReflection
    {
        public readonly object Manager;
        public readonly Type ListType;
        public readonly MethodInfo MAddList;
        public readonly MethodInfo MSave;
        public readonly MethodInfo MSetActive;
        public readonly MethodInfo MChangePref;
        public readonly PropertyInfo PLists;
        public readonly MethodInfo MListAdd;
        public readonly MethodInfo MListSetQty;
        public readonly MethodInfo MListRemoveAt;
        public readonly PropertyInfo PItems;
        public readonly PropertyInfo PName;
        public readonly PropertyInfo PDescription;
        public readonly PropertyInfo PFolderPath;
        public readonly PropertyInfo PEnabled;
        public readonly PropertyInfo PUsesRetainer;

        public ListReflection(GbrHandle h)
        {
            this.Manager = h.ListsManager;
            var mgrType = this.Manager.GetType();
            this.ListType = h.GbrAsm.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList", throwOnError: true)!;
            var iGatherable = h.GameDataAsm.GetType("GatherBuddy.Interfaces.IGatherable", throwOnError: true)!;
            var iLocation = h.GameDataAsm.GetType("GatherBuddy.Interfaces.ILocation", throwOnError: true)!;

            this.MAddList = mgrType.GetMethod("AddList", GbrHandle.PubInst)!;
            this.MSave = M(mgrType, "Save");
            this.MSetActive = M(mgrType, "SetActiveItems", typeof(bool));
            this.MChangePref = M(mgrType, "ChangePreferredLocation", this.ListType, iGatherable, iLocation);
            this.PLists = mgrType.GetProperty("Lists", GbrHandle.PubInst)!;

            this.MListAdd = M(this.ListType, "Add", iGatherable, typeof(uint));
            this.MListSetQty = M(this.ListType, "SetQuantity", iGatherable, typeof(uint));
            this.MListRemoveAt = M(this.ListType, "RemoveAt", typeof(int));
            this.PItems = this.ListType.GetProperty("Items", GbrHandle.PubInst)!;
            this.PName = this.ListType.GetProperty("Name", GbrHandle.PubInst)!;
            this.PDescription = this.ListType.GetProperty("Description", GbrHandle.PubInst)!;
            this.PFolderPath = this.ListType.GetProperty("FolderPath", GbrHandle.PubInst)!;
            this.PEnabled = this.ListType.GetProperty("Enabled", GbrHandle.PubInst)!;
            this.PUsesRetainer = this.ListType.GetProperty("UsesRetainerInventory", GbrHandle.NonPubInst)!;
        }

        public IEnumerable<object> AllLists() => ((IEnumerable)this.PLists.GetValue(this.Manager)!).Cast<object>();

        public object? FindOwnList()
            => this.AllLists().FirstOrDefault(l => string.Equals((string?)this.PName.GetValue(l), OwnListName, StringComparison.Ordinal));

        public object CreateOwnList()
        {
            var list = Activator.CreateInstance(this.ListType)!;
            this.PName.SetValue(list, OwnListName);
            this.PDescription.SetValue(list, "AutoJobQuest が管理（手で編集しないでください）");
            this.PEnabled.SetValue(list, false);
            this.MAddList.Invoke(this.Manager, [list, null]);
            return list;
        }

        private static MethodInfo M(Type t, string name, params Type[] ps)
            => t.GetMethod(name, GbrHandle.PubInst, null, ps, null) ?? throw new MissingMethodException(t.FullName, name);
    }
}
