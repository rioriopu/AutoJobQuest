using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace AutoJobQuest.Data;

/// <summary>実際に竿を投げた立ち位置と向き（GBR の釣りの記録の1件）。</summary>
public sealed record CastSpot(Vector3 Position, float Rotation, uint Catch);

/// <summary>
/// GBR の釣りの記録（GBR の利用者が実際に竿を投げた立ち位置と向き）を読む（例：漁師 Lv58 で、Questionable の釣りの位置から
/// 約1mずれた所へ動き、谷間へ向いたら投げられた）。ゲームは釣り場での立ち位置と向きで投げられるかを決める。GBR の自動採集はこの記録の
/// 位置へ動き、記録の向きに合わせてから投げる（AutoGather.cs・FishRecorder.Remote.cs）。
/// 置き場所は GBR の設定フォルダ（pluginConfigs\GatherbuddyReborn）の、配られた記録（GatherBuddy.CustomInfo.fish_records.json）と自分の記録（fish_records.dat）。
/// 中身は名前に反して JSON ではなく、先頭1バイトの版（2）＋ MessagePack の配列（1件＝18項目の配列。1＝釣れた魚・12〜14＝位置・15＝向き：FishRecord.cs の Key）。
/// 初めて要るときに裏で読み、読み終わるまでは null を返す（ゲームを止めないため）。
/// </summary>
public static class FishCastSpots
{
    private static List<CastSpot>? all;
    private static Task? loading;

    /// <summary>読めなかった理由（読めていれば null）。</summary>
    public static string? LoadError { get; private set; }

    /// <summary>記録の置き場所（配られた記録・自分の記録）。</summary>
    public static IReadOnlyList<string> RecordsPaths
    {
        get
        {
            var dir = Path.Combine(Svc.PluginInterface.ConfigDirectory.Parent!.FullName, "GatherbuddyReborn");
            return [Path.Combine(dir, "GatherBuddy.CustomInfo.fish_records.json"), Path.Combine(dir, "fish_records.dat")];
        }
    }

    /// <summary>位置つきの記録すべて。読み終わるまでは null（初めて呼ばれたときに裏で読み始める）。</summary>
    public static IReadOnlyList<CastSpot>? All
    {
        get
        {
            if (all == null && loading == null)
            {
                var paths = RecordsPaths;
                loading = Task.Run(() => all = paths.SelectMany(Load).ToList());
            }

            return all;
        }
    }

    /// <summary>記録のファイルを読む（位置と向きのそろった記録だけ：GBR の PositionDataValid と同じ）。読めなければ空（理由は LoadError）。</summary>
    public static List<CastSpot> Load(string path)
    {
        var list = new List<CastSpot>();
        try
        {
            if (!File.Exists(path))
                return list;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 2 || bytes[0] != 2)
            {
                LoadError = $"{Path.GetFileName(path)} の版が 2 ではありません（{(bytes.Length > 0 ? bytes[0] : -1)}）";
                return list;
            }

            var r = new MessagePackReader(bytes, 1);
            var count = r.ReadArrayHeader();
            for (var n = 0; n < count; n++)
            {
                var fields = r.ReadArrayHeader();
                uint fish = 0;
                float x = 0, y = 0, z = 0, rot = 0;
                for (var k = 0; k < fields; k++)
                {
                    switch (k)
                    {
                        case 1: fish = (uint)r.ReadInteger(); break;
                        case 12: x = r.ReadFloat(); break;
                        case 13: y = r.ReadFloat(); break;
                        case 14: z = r.ReadFloat(); break;
                        case 15: rot = r.ReadFloat(); break;
                        default: r.Skip(); break;
                    }
                }

                if (x != 0 && y != 0 && z != 0 && rot != 0)
                    list.Add(new CastSpot(new Vector3(x, y, z), rot, fish));
            }
        }
        catch (Exception ex)
        {
            LoadError = $"{Path.GetFileName(path)}：{ex.Message}";
        }

        return list;
    }

    /// <summary>
    /// 目標の位置から <paramref name="within"/> m（3次元）以内の記録の立ち位置を、目標に近い順に、互いに <paramref name="apart"/> m 以上離れたものだけ最大 <paramref name="max"/> か所。
    /// </summary>
    public static List<CastSpot> Near(IEnumerable<CastSpot> spots, Vector3 target, float within = 10f, int max = 5, float apart = 0.3f)
    {
        var picked = new List<CastSpot>();
        foreach (var s in spots.Where(s => Vector3.Distance(s.Position, target) <= within).OrderBy(s => Vector3.Distance(s.Position, target)))
        {
            if (picked.Any(p => Vector3.Distance(p.Position, s.Position) < apart))
                continue;
            picked.Add(s);
            if (picked.Count >= max)
                break;
        }

        return picked;
    }

    /// <summary>GBR の記録を読むのに要る分だけの MessagePack の読み手（配列・整数・小数・文字列・バイト列・無・真偽・連想配列・拡張）。</summary>
    private sealed class MessagePackReader(byte[] b, int pos)
    {
        private int pos = pos;

        private byte Next() => b[this.pos++];

        private ReadOnlySpan<byte> Take(int n)
        {
            var s = b.AsSpan(this.pos, n);
            this.pos += n;
            return s;
        }

        public int ReadArrayHeader()
        {
            var t = Next();
            if (t is >= 0x90 and <= 0x9f)
                return t & 0x0f;
            return t switch
            {
                0xdc => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
                0xdd => (int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
                _ => throw new InvalidDataException($"配列でない型 0x{t:x2}（位置 {this.pos - 1}）"),
            };
        }

        public long ReadInteger()
        {
            var t = Next();
            if (t <= 0x7f)
                return t;
            if (t >= 0xe0)
                return (sbyte)t;
            return t switch
            {
                0xcc => Next(),
                0xcd => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
                0xce => BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
                0xcf => (long)BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
                0xd0 => (sbyte)Next(),
                0xd1 => BinaryPrimitives.ReadInt16BigEndian(Take(2)),
                0xd2 => BinaryPrimitives.ReadInt32BigEndian(Take(4)),
                0xd3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
                _ => throw new InvalidDataException($"整数でない型 0x{t:x2}（位置 {this.pos - 1}）"),
            };
        }

        public float ReadFloat()
        {
            var t = b[this.pos];
            if (t == 0xca)
            {
                this.pos++;
                return BinaryPrimitives.ReadSingleBigEndian(Take(4));
            }

            if (t == 0xcb)
            {
                this.pos++;
                return (float)BinaryPrimitives.ReadDoubleBigEndian(Take(8));
            }

            return ReadInteger();
        }

        public void Skip()
        {
            var t = b[this.pos];
            if (t <= 0x7f || t >= 0xe0 || t is >= 0xcc and <= 0xd3)
            {
                ReadInteger();
                return;
            }

            this.pos++;
            if (t is >= 0x80 and <= 0x8f)
            {
                SkipMany((t & 0x0f) * 2);
                return;
            }

            if (t is >= 0x90 and <= 0x9f)
            {
                SkipMany(t & 0x0f);
                return;
            }

            if (t is >= 0xa0 and <= 0xbf)
            {
                this.pos += t & 0x1f;
                return;
            }

            switch (t)
            {
                case 0xc0: case 0xc2: case 0xc3: return;
                case 0xca: this.pos += 4; return;
                case 0xcb: this.pos += 8; return;
                case 0xc4: case 0xd9: Advance(Next()); return;
                case 0xc5: case 0xda: Advance(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
                case 0xc6: case 0xdb: Advance((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
                case 0xdc: SkipMany(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
                case 0xdd: SkipMany((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
                case 0xde: SkipMany(BinaryPrimitives.ReadUInt16BigEndian(Take(2)) * 2); return;
                case 0xdf: SkipMany((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)) * 2); return;
                case 0xd4: this.pos += 2; return;
                case 0xd5: this.pos += 3; return;
                case 0xd6: this.pos += 5; return;
                case 0xd7: this.pos += 9; return;
                case 0xd8: this.pos += 17; return;
                case 0xc7: Advance(Next() + 1); return;
                case 0xc8: Advance(BinaryPrimitives.ReadUInt16BigEndian(Take(2)) + 1); return;
                case 0xc9: Advance((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)) + 1); return;
                default: throw new InvalidDataException($"読めない型 0x{t:x2}（位置 {this.pos - 1}）");
            }
        }

        // 長さを読んでから進める（this.pos += Next() と書くと、長さを読む前の位置に足してしまう）
        private void Advance(int n) => this.pos += n;

        private void SkipMany(int n)
        {
            for (var i = 0; i < n; i++)
                Skip();
        }
    }
}
