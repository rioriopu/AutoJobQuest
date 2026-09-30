using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace AutoJobQuest.Core;

/// <summary>
/// キャラクターごとの設定ファイルの読み書き。
///
/// 置き場所：プラグインの設定フォルダの characters\&lt;ContentId（16進16桁）&gt;.json。
/// 同じキャラクターが2つのゲームに同時にログインすることは無いので、2つのゲームが同じファイルを書くことは無い。
/// 書くときは一時ファイルに書いてから置き換える（書いている途中で落ちても、元のファイルが壊れない）。
/// 読めないファイルは名前を変えて残し、既定の値で始める（上書きで失わない）。
/// </summary>
public static class CharacterConfigStore
{
    /// <summary>
    /// 読み書きの決まり。リストや辞書は、既定の値に足さずに置き換える（足すと、既定の値とファイルの値が重なる）。
    /// </summary>
    private static readonly JsonSerializerSettings Settings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Formatting = Formatting.Indented,
    };

    /// <summary>そのキャラクターの設定ファイルの場所。</summary>
    public static string PathFor(string directory, ulong contentId)
        => Path.Combine(directory, $"{contentId:X16}.json");

    /// <summary>
    /// そのキャラクターの設定を読む。ファイルが無ければ既定の値。読めなければ、ファイルの名前を変えて残し、既定の値にする
    /// （<paramref name="note"/> にその知らせ）。
    /// </summary>
    public static Configuration Load(string directory, ulong contentId, out string? note)
    {
        note = null;
        var path = PathFor(directory, contentId);
        if (!File.Exists(path))
            return new Configuration();

        try
        {
            var loaded = new Configuration();
            JsonConvert.PopulateObject(File.ReadAllText(path, Encoding.UTF8), loaded, Settings);
            return loaded;
        }
        catch (Exception ex)
        {
            var kept = $"{path}.読めなかった_{DateTime.Now:yyyyMMdd_HHmmss}";
            try
            {
                File.Move(path, kept);
            }
            catch
            {
                kept = path;
            }

            note = $"設定ファイル {Path.GetFileName(path)} を読めなかったので、既定の値で始めます（{ex.GetType().Name}: {ex.Message}）。元のファイルは {Path.GetFileName(kept)} に残しました";
            return new Configuration();
        }
    }

    /// <summary>そのキャラクターの設定を書く。書けたら true。</summary>
    public static bool Save(Configuration config, string path, out string? problem)
    {
        problem = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".書き込み中";
            File.WriteAllText(temp, JsonConvert.SerializeObject(config, Settings), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 設定の値を、別の設定から写す（読み書きできる公開のプロパティすべて。ファイルに書かないもの〔JsonIgnore〕は写さない）。
    /// 画面や作業は同じ設定のオブジェクトを持ち続けるので、キャラクターを切り替えるときは中身だけを入れ替える。
    /// </summary>
    public static void CopyInto(Configuration target, Configuration source)
    {
        foreach (var p in typeof(Configuration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0 || p.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;
            p.SetValue(target, p.GetValue(source));
        }
    }
}
