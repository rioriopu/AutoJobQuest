using System;
using System.Collections.Generic;

namespace AutoJobQuest.Core;

/// <summary>
/// 動作の記録。画面に出すためのもの。
///
/// ファイルには書かない。記録したいのは「いま何が起きているか」で、
/// 後から追いたいときは Dalamud のログ（/xllog）に同じ内容が流れている。
/// </summary>
public sealed class RunLog
{
    /// <summary>画面に残す行数。</summary>
    private const int MaxInMemory = 400;

    private readonly List<string> lines = [];
    private readonly object gate = new();

    /// <summary>1行記録する。</summary>
    public void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{category}] {message}";

        lock (this.gate)
        {
            this.lines.Add(line);

            if (this.lines.Count > MaxInMemory)
                this.lines.RemoveRange(0, this.lines.Count - MaxInMemory);
        }

        Svc.Log.Information($"[{category}] {message}");
    }

    /// <summary>警告として記録する。画面では同じ行に並ぶが、/xllog では Warning になる。</summary>
    public void Warn(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{category}] ⚠ {message}";

        lock (this.gate)
        {
            this.lines.Add(line);

            if (this.lines.Count > MaxInMemory)
                this.lines.RemoveRange(0, this.lines.Count - MaxInMemory);
        }

        Svc.Log.Warning($"[{category}] {message}");
    }

    /// <summary>画面表示用に直近の行を返す。</summary>
    public List<string> Snapshot()
    {
        lock (this.gate)
            return [.. this.lines];
    }

    public void Clear()
    {
        lock (this.gate)
            this.lines.Clear();
    }
}
