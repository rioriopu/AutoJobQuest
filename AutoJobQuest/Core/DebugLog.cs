using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoJobQuest.Core;

/// <summary>
/// 不具合を調べるための記録をファイルに残す（細かく説明しなくても分かるように）。
///
/// 置き場所（既定）：<c>DefaultDirectory</c>。作れなければプラグインの設定フォルダの <c>ログ\</c>。
///   全体_yyyyMMdd.log        … その日の記録すべて（プラグインを読み込んでいる間ずっと）
///   実行_yyyyMMdd_HHmmss.log … 「ジョブクエ開始」から止まるまでの記録（1回の実行で1つ）
///   失敗_yyyyMMdd_HHmmss.md  … 止まったときの状況（状態の写し＋直前の記録）
///   状態_yyyyMMdd_HHmmss.md  … 画面の「今の状態を書き出す」で作る写し
///
/// 書き込みは別スレッドでまとめて行う（ゲームのフレームを止めないため）。
/// 他のクラスからも書けるよう、読み込み中は <see cref="Current"/> で取れる。
/// </summary>
public sealed class DebugLog : IDisposable
{
    /// <summary>既定の置き場所。</summary>
    public const string DefaultDirectory = @"C:\ソース\AutoJobQuest\ログ";

    private readonly BlockingCollection<(string Path, string Text)> queue = new(new ConcurrentQueue<(string, string)>());
    private readonly Thread writer;
    private readonly object runGate = new();
    private readonly Queue<string> recent = new();
    private string? runFile;

    /// <summary>読み込み中のインスタンス（無ければ null）。</summary>
    public static DebugLog? Current { get; private set; }

    /// <summary>実際に書いているフォルダ。</summary>
    public string Directory { get; }

    /// <summary>いまの実行の記録ファイル（実行していなければ null）。</summary>
    public string? RunFile
    {
        get
        {
            lock (this.runGate)
                return this.runFile;
        }
    }

    /// <summary>最後に書いた失敗の報告ファイル。</summary>
    public string? LastFailureReport { get; private set; }

    public DebugLog(string? preferredDirectory)
    {
        this.Directory = ResolveDirectory(preferredDirectory);
        this.writer = new Thread(this.WriteLoop) { IsBackground = true, Name = "AutoJobQuest.DebugLog" };
        this.writer.Start();
        Current = this;
        this.Line("記録", $"記録を始めました（{this.Directory}）");
    }

    private static string ResolveDirectory(string? preferred)
    {
        foreach (var dir in new[] { preferred, DefaultDirectory, Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "ログ") })
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".書き込み確認");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // 次の候補へ
            }
        }

        return Svc.PluginInterface.ConfigDirectory.FullName;
    }

    private string DailyFile => Path.Combine(this.Directory, $"全体_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>1行書く（全体と、実行中なら実行の記録の両方へ）。</summary>
    public void Line(string category, string message)
    {
        // 頭に「[実行#操作]」を付ける（同じ操作の記録を追えるように。動いていなければ付けない）
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {RunIds.Tag}[{category}] {message}";
        lock (this.runGate)
        {
            this.recent.Enqueue(text);
            while (this.recent.Count > 400)
                this.recent.Dequeue();
        }

        this.Enqueue(text + Environment.NewLine);
    }

    /// <summary>複数行のまとまり（画面の中身・状態の写しなど）を書く。</summary>
    public void Block(string category, string title, string body)
    {
        var sb = new StringBuilder();
        sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] ── {title} ──").AppendLine();
        foreach (var line in body.Split('\n'))
            sb.Append("    ").Append(line.TrimEnd('\r')).AppendLine();
        this.Enqueue(sb.ToString());
    }

    /// <summary>例外を書く（スタックトレース込み）。</summary>
    public void Exception(string category, string message, Exception ex)
        => this.Block(category, message, ex.ToString());

    private void Enqueue(string text)
    {
        if (this.queue.IsAddingCompleted)
            return;
        this.queue.Add((this.DailyFile, text));
        string? run;
        lock (this.runGate)
            run = this.runFile;
        if (run != null)
            this.queue.Add((run, text));
    }

    /// <summary>実行の記録を始める。</summary>
    public string BeginRun(string name)
    {
        var path = Path.Combine(this.Directory, $"実行_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        lock (this.runGate)
            this.runFile = path;
        this.Line("実行", $"開始: {name}（この実行の記録: {path}）");
        return path;
    }

    /// <summary>実行の記録を閉じる。</summary>
    public void EndRun(string result)
    {
        this.Line("実行", $"終了: {result}");
        lock (this.runGate)
            this.runFile = null;
    }

    /// <summary>失敗したときの報告（状態の写し＋直前の記録）を書き出す。</summary>
    public string WriteFailureReport(string reason, string snapshot)
    {
        var path = Path.Combine(this.Directory, $"失敗_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        var sb = new StringBuilder();
        sb.AppendLine($"# 止まった理由（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）").AppendLine();
        sb.AppendLine(reason).AppendLine();
        sb.AppendLine("## 止まった時点の状態").AppendLine();
        sb.AppendLine("```").AppendLine(snapshot).AppendLine("```").AppendLine();
        sb.AppendLine("## 直前の記録（新しいものが下）").AppendLine();
        sb.AppendLine("```");
        lock (this.runGate)
            foreach (var l in this.recent)
                sb.AppendLine(l);
        sb.AppendLine("```");
        string? run;
        lock (this.runGate)
            run = this.runFile;
        if (run != null)
            sb.AppendLine().AppendLine($"この実行の全記録: {run}");

        this.queue.Add((path, sb.ToString()));
        this.LastFailureReport = path;
        this.Line("実行", $"失敗の報告を書き出しました: {path}");
        return path;
    }

    /// <summary>状態の写しをファイルに書き出す。</summary>
    public string WriteSnapshot(string snapshot)
    {
        var path = Path.Combine(this.Directory, $"状態_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        this.queue.Add((path, $"# 状態の写し（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）\n\n```\n{snapshot}\n```\n"));
        this.Line("記録", $"状態を書き出しました: {path}");
        return path;
    }

    private void WriteLoop()
    {
        var utf8 = new UTF8Encoding(false);
        var batch = new Dictionary<string, StringBuilder>();
        foreach (var (path, text) in this.queue.GetConsumingEnumerable())
        {
            batch.Clear();
            Add(batch, path, text);

            // 溜まっている分をまとめて書く
            while (this.queue.TryTake(out var more))
                Add(batch, more.Path, more.Text);

            foreach (var (p, sb) in batch)
            {
                try
                {
                    File.AppendAllText(p, sb.ToString(), utf8);
                }
                catch
                {
                    // 書けないときは捨てる（ゲームの動作を止めない）
                }
            }
        }

        static void Add(Dictionary<string, StringBuilder> b, string p, string t)
        {
            if (!b.TryGetValue(p, out var sb))
                b[p] = sb = new StringBuilder();
            sb.Append(t);
        }
    }

    public void Dispose()
    {
        this.Line("記録", "記録を終えます（プラグインの読み込み解除）");
        this.queue.CompleteAdding();
        this.writer.Join(TimeSpan.FromSeconds(2));
        if (Current == this)
            Current = null;
    }
}
