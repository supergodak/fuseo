using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fuseo.Core.Contracts;

namespace Fuseo.Core.Export;

/// <summary>
/// 自前の最小 PDF 1.4 ライタ（docs/windows-design.md §4.3・Fable 所管）。
/// 各ページ = MediaBox（pt = px×72/300）＋画像 XObject 1 つ＋描画命令 <c>q W 0 0 H 0 0 cm /Im0 Do Q</c> のみ。
/// /Info は /Producer だけ。XMP・フォント・テキスト・注釈・アクション・しおり・/ID は**書かない**
/// ＝元 PDF の文字・注釈・メタデータは構造的に持ち越せない（元ページは入力時に捨ててある）。
/// </summary>
public static class RasterPdfWriter
{
    public const string Producer = "Fuseo for Windows";
    public const double PointsPerPixel = 72.0 / 300.0;

    public static void Write(IReadOnlyList<BurnedPage> pages, PdfImageEncoding encoding, double jpegQuality, Stream dest)
    {
        if (pages.Count == 0) throw new ArgumentException("ページがありません", nameof(pages));
        var w = new Writer(dest);
        w.Raw("%PDF-1.4\n");
        w.Bytes([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // バイナリを含む印（慣例）

        // 1=Catalog 2=Pages 3=Info、ページ i は 4+3i=Page, 5+3i=Contents, 6+3i=Image
        var total = 3 + 3 * pages.Count;
        var offsets = new long[total + 1];

        w.Obj(offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + 3 * i} 0 R"));
        w.Obj(offsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        w.Obj(offsets, 3, $"<< /Producer ({Producer}) >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var bmp = pages[i].Bitmap;
            int pageObj = 4 + 3 * i, contentsObj = pageObj + 1, imageObj = pageObj + 2;
            var wPt = Num(bmp.Width * PointsPerPixel);
            var hPt = Num(bmp.Height * PointsPerPixel);
            w.Obj(offsets, pageObj,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wPt} {hPt}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentsObj} 0 R >>");

            var content = Encoding.ASCII.GetBytes($"q {wPt} 0 0 {hPt} 0 0 cm /Im0 Do Q");
            w.Stream(offsets, contentsObj, $"<< /Length {content.Length} >>", content);

            var rgb = RgbImage.FromBgraOnWhite(bmp);
            byte[] data;
            string filter;
            if (encoding == PdfImageEncoding.Jpeg)
            {
                data = JpegEncoder.Encode(rgb, jpegQuality);
                filter = "/DCTDecode";
            }
            else
            {
                data = Deflate(rgb.Rgb);
                filter = "/FlateDecode";
            }
            w.Stream(offsets, imageObj,
                $"<< /Type /XObject /Subtype /Image /Width {bmp.Width} /Height {bmp.Height} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} /Length {data.Length} >>", data);
        }

        var xref = w.Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {total + 1}\n");
        sb.Append("0000000000 65535 f\r\n");
        for (var n = 1; n <= total; n++) sb.Append(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {total + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        w.Raw(sb.ToString());
        w.Flush();
    }

    /// <summary>pt 値の書式（不変カルチャ・小数 3 桁まで・末尾の 0 なし）。</summary>
    internal static string Num(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>書いたバイト数を自分で数える（宛先 Stream が Position を持たなくても xref を正しく書くため）。</summary>
    private sealed class Writer(Stream dest)
    {
        private readonly BufferedStream _s = new(dest, 1 << 16);
        public long Position { get; private set; }

        public void Bytes(ReadOnlySpan<byte> b) { _s.Write(b); Position += b.Length; }
        public void Raw(string ascii) => Bytes(Encoding.ASCII.GetBytes(ascii));

        public void Obj(long[] offsets, int n, string body)
        {
            offsets[n] = Position;
            Raw($"{n} 0 obj\n{body}\nendobj\n");
        }

        public void Stream(long[] offsets, int n, string dict, byte[] data)
        {
            offsets[n] = Position;
            Raw($"{n} 0 obj\n{dict}\nstream\n");
            Bytes(data);
            Raw("\nendstream\nendobj\n");
        }

        public void Flush() => _s.Flush();
    }
}
