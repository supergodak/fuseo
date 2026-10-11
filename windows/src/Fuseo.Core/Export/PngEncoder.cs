using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fuseo.Core.Export;

/// <summary>
/// 自前の PNG エンコーダ（8bit RGB・非インターレース）。書くチャンクは IHDR・IDAT・IEND **だけ**
/// ＝tEXt/iTXt/zTXt/eXIf/tIME/iCCP 等を構造的に持ち得ない（docs/windows-design.md §4.2）。
/// フィルタは行ごとに 5 種から絶対値和最小を選ぶ（一般的な経験則）。
/// </summary>
public static class PngEncoder
{
    private const int IdatChunk = 1 << 20;

    public static byte[] Encode(RgbImage img)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)img.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)img.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type RGB
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // non-interlaced
        WriteChunk(ms, "IHDR", ihdr);

        var z = Compress(Filter(img));
        for (var off = 0; off < z.Length; off += IdatChunk)
            WriteChunk(ms, "IDAT", z.AsSpan(off, Math.Min(IdatChunk, z.Length - off)));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] Filter(RgbImage img)
    {
        var rowLen = img.Width * 3;
        var outBuf = new byte[checked((rowLen + 1) * img.Height)];
        Parallel.For(0, img.Height, y =>
        {
            var cur = img.Rgb.AsSpan(y * rowLen, rowLen);
            var prev = y == 0 ? Span<byte>.Empty : img.Rgb.AsSpan((y - 1) * rowLen, rowLen);
            var cand = new byte[5][];
            long best = long.MaxValue;
            var bestType = 0;
            for (var t = 0; t < 5; t++)
            {
                cand[t] = new byte[rowLen];
                long sum = 0;
                for (var i = 0; i < rowLen; i++)
                {
                    int a = i >= 3 ? cur[i - 3] : 0;
                    int b = prev.IsEmpty ? 0 : prev[i];
                    int c = i >= 3 && !prev.IsEmpty ? prev[i - 3] : 0;
                    var pred = t switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                    var v = (byte)(cur[i] - pred);
                    cand[t][i] = v;
                    sum += v < 128 ? v : 256 - v;
                }
                if (sum < best) { best = sum; bestType = t; }
            }
            var o = y * (rowLen + 1);
            outBuf[o] = (byte)bestType;
            cand[bestType].CopyTo(outBuf, o + 1);
        });
        return outBuf;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }
}

/// <summary>PNG の CRC-32（ISO 3309・多項式 0xEDB88320）。BCL に無いので自前（System.IO.Hashing は追加依存）。</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
