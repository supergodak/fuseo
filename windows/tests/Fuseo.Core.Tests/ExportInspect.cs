using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Fuseo.Core.Tests;

/// <summary>書き出しファイルのバイト検査（ライブラリ非依存・Mac の許可リスト方式の Windows 版）。</summary>
public static class ExportInspect
{
    /// <summary>JPEG のマーカー列（SOS 以降のエントロピー符号は飛ばして EOI まで）。</summary>
    public static List<byte> JpegMarkers(byte[] j)
    {
        var markers = new List<byte>();
        if (j.Length < 4 || j[0] != 0xFF || j[1] != 0xD8) throw new FormatException("SOI がない");
        markers.Add(0xD8);
        var i = 2;
        while (i < j.Length)
        {
            if (j[i] != 0xFF) throw new FormatException($"マーカー位置 {i} が 0xFF でない");
            var m = j[i + 1];
            markers.Add(m);
            if (m == 0xD9) { if (i + 2 != j.Length) throw new FormatException("EOI の後ろにデータ"); break; }
            var len = BinaryPrimitives.ReadUInt16BigEndian(j.AsSpan(i + 2));
            i += 2 + len;
            if (m == 0xDA)
            {
                // エントロピー符号: 0xFF00（スタッフィング）以外の 0xFF xx が次のマーカー
                while (i < j.Length - 1 && !(j[i] == 0xFF && j[i + 1] != 0x00)) i++;
            }
        }
        return markers;
    }

    public static (int W, int H, int Comps) JpegSof(byte[] j)
    {
        for (var i = 2; i < j.Length - 9; i++)
            if (j[i] == 0xFF && j[i + 1] == 0xC0)
                return (BinaryPrimitives.ReadUInt16BigEndian(j.AsSpan(i + 7)),
                        BinaryPrimitives.ReadUInt16BigEndian(j.AsSpan(i + 5)), j[i + 9]);
        throw new FormatException("SOF0 がない");
    }

    public sealed record PngChunk(string Type, byte[] Data, bool CrcOk);

    public static List<PngChunk> PngChunks(byte[] p)
    {
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!p.AsSpan(0, 8).SequenceEqual(sig)) throw new FormatException("PNG シグネチャがない");
        var list = new List<PngChunk>();
        var i = 8;
        while (i < p.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i));
            var type = Encoding.ASCII.GetString(p, i + 4, 4);
            var data = p.AsSpan(i + 8, len).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i + 8 + len));
            var calc = Fuseo.Core.Export.Crc32.Update(Fuseo.Core.Export.Crc32.Update(0xFFFFFFFFu, p.AsSpan(i + 4, 4)), data) ^ 0xFFFFFFFFu;
            list.Add(new PngChunk(type, data, crc == calc));
            i += 12 + len;
        }
        return list;
    }

    /// <summary>テスト用の PNG デコーダ（RGB8・非インターレースのみ）。可逆なので画素の完全一致を見られる。</summary>
    public static (int W, int H, byte[] Rgb) DecodePng(byte[] p)
    {
        var chunks = PngChunks(p);
        var ihdr = chunks.Single(c => c.Type == "IHDR").Data;
        int w = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr), h = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4));
        if (ihdr[8] != 8 || ihdr[9] != 2 || ihdr[12] != 0) throw new FormatException("RGB8 非インターレースのみ対応");
        var z = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var zs = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zs.CopyTo(raw);
        var f = raw.ToArray();
        var rowLen = w * 3;
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            var t = f[y * (rowLen + 1)];
            for (var i = 0; i < rowLen; i++)
            {
                int a = i >= 3 ? rgb[y * rowLen + i - 3] : 0;
                int b = y > 0 ? rgb[(y - 1) * rowLen + i] : 0;
                int c = i >= 3 && y > 0 ? rgb[(y - 1) * rowLen + i - 3] : 0;
                int pred = t switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new FormatException("未知のフィルタ"),
                };
                rgb[y * rowLen + i] = (byte)(f[y * (rowLen + 1) + 1 + i] + pred);
            }
        }
        return (w, h, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public sealed record PdfObject(int Num, string Dict, byte[]? Stream, long Offset);

    public sealed record PdfFile(List<PdfObject> Objects, string NonStreamText, Dictionary<int, long> Xref, string Trailer);

    /// <summary>
    /// 最小の PDF 走査: 「N 0 obj … endobj」を先頭から順に読み、stream は /Length で飛ばす。
    /// 戻り値の NonStreamText＝ストリーム中身を除いた全テキスト（許可リスト検査の対象）。
    /// </summary>
    public static PdfFile ParsePdf(byte[] pdf)
    {
        var latin = Encoding.Latin1;
        var objs = new List<PdfObject>();
        var text = new StringBuilder();
        var head = latin.GetString(pdf, 0, Math.Min(64, pdf.Length));
        if (!head.StartsWith("%PDF-1.4\n", StringComparison.Ordinal)) throw new FormatException("ヘッダが %PDF-1.4 でない");
        var i = head.IndexOf('\n', 9) + 1;
        var objRe = new Regex(@"\G(\d+) 0 obj\n");
        while (true)
        {
            var window = latin.GetString(pdf, i, Math.Min(32, pdf.Length - i));
            var m = objRe.Match(window);
            if (!m.Success) break;
            var num = int.Parse(m.Groups[1].Value);
            var start = i;
            i += m.Length;
            var rest = latin.GetString(pdf, i, Math.Min(4096, pdf.Length - i));
            var endObj = rest.IndexOf("\nendobj\n", StringComparison.Ordinal);
            var streamKw = rest.IndexOf("\nstream\n", StringComparison.Ordinal);
            if (streamKw >= 0 && (endObj < 0 || streamKw < endObj))
            {
                var dict = rest[..streamKw];
                var len = int.Parse(Regex.Match(dict, @"/Length (\d+)").Groups[1].Value);
                var dataStart = i + streamKw + "\nstream\n".Length;
                var data = pdf.AsSpan(dataStart, len).ToArray();
                var after = latin.GetString(pdf, dataStart + len, "\nendstream\nendobj\n".Length);
                if (after != "\nendstream\nendobj\n") throw new FormatException($"obj {num}: /Length が実体と合わない");
                objs.Add(new PdfObject(num, dict, data, start));
                text.Append(dict).Append('\n');
                i = dataStart + len + after.Length;
            }
            else
            {
                if (endObj < 0) throw new FormatException($"obj {num}: endobj がない");
                var dict = rest[..endObj];
                objs.Add(new PdfObject(num, dict, null, start));
                text.Append(dict).Append('\n');
                i += endObj + "\nendobj\n".Length;
            }
        }
        var tail = latin.GetString(pdf, i, pdf.Length - i);
        if (!tail.StartsWith("xref\n", StringComparison.Ordinal)) throw new FormatException("オブジェクト列の直後が xref でない");
        text.Append(tail);
        var xref = new Dictionary<int, long>();
        var lines = tail.Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1]);
        for (var n = 1; n < count; n++)
        {
            var e = lines[2 + n];
            if (e.Length != 19 || !e.EndsWith(" 00000 n\r", StringComparison.Ordinal)) throw new FormatException($"xref 行 {n} の形式");
            xref[n] = long.Parse(e[..10]);
        }
        var startxref = long.Parse(Regex.Match(tail, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value);
        if (latin.GetString(pdf, (int)startxref, 5) != "xref\n") throw new FormatException("startxref が xref を指していない");
        var trailer = Regex.Match(tail, @"trailer\n(<<.*?>>)\n", RegexOptions.Singleline).Groups[1].Value;
        return new PdfFile(objs, text.ToString(), xref, trailer);
    }
}
