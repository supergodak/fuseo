using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;
using ZXing;
using ZXing.Common;

namespace Fuseo.E2E;

/// <summary>
/// テスト入力の合成（**すべて作り物**。実物の書類・fixtures-private は使わない）。
/// 目印の文字列 <see cref="Marker"/> を画像のメタデータ（EXIF・XMP・PNG の tEXt）や PDF のテキスト・文書情報に埋め、
/// 書き出したファイルのバイト列にそれが残っていないことを確かめる。
/// </summary>
internal static class Synth
{
    /// <summary>入力にだけ入れる目印（書き出しに出てきたらメタデータかテキストが漏れている）。</summary>
    public const string Marker = "FUSEO-E2E-SECRET-7Q4Z";

    /// <summary>位置の目印になる黒い四角（正規化・左上原点）。回転・切り抜きの確認に使う。</summary>
    public static readonly (double X, double Y, double W, double H) Badge = (0.06, 0.08, 0.14, 0.2);

    /// <summary>
    /// なめらかな色のグラデーション（R が横、G が縦で変わる）に、左上寄りの黒い四角（<see cref="Badge"/>）を置いた画像。
    /// 塗っていない所が元のままか（色が保たれているか）と、向き（四角がどの隅にあるか）を見分けられる。
    /// </summary>
    public static PageBitmap Card(int w = 1200, int h = 800)
    {
        var bmp = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (b, g, r) = CardColor(x / (double)w, y / (double)h);
                bmp.SetPixel(x, y, b, g, r, 255);
            }
        FillNorm(bmp, Badge.X, Badge.Y, Badge.W, Badge.H, 0, 0, 0);
        return bmp;
    }

    /// <summary><see cref="Card"/> の背景色（u, v は 0..1）。</summary>
    public static (byte B, byte G, byte R) CardColor(double u, double v) =>
        (170, (byte)(40 + 190 * v), (byte)(40 + 190 * u));

    public static PageBitmap Solid(int w, int h, byte r, byte g, byte b) => PageBitmap.Solid(w, h, b, g, r);

    public static void FillNorm(PageBitmap bmp, double x, double y, double w, double h, byte r, byte g, byte b)
    {
        int x0 = (int)(x * bmp.Width), y0 = (int)(y * bmp.Height), x1 = (int)((x + w) * bmp.Width), y1 = (int)((y + h) * bmp.Height);
        for (var yy = Math.Max(0, y0); yy < Math.Min(bmp.Height, y1); yy++)
            for (var xx = Math.Max(0, x0); xx < Math.Min(bmp.Width, x1); xx++) bmp.SetPixel(xx, yy, b, g, r, 255);
    }

    /// <summary>白地に QR コード（1 モジュール = scale px）を置いた画像。返り値の Box は記号の外枠（正規化）。</summary>
    public static (PageBitmap Bitmap, (double X, double Y, double W, double H) Box) WithQr(int w = 1200, int h = 800, int scale = 10)
    {
        var bmp = PageBitmap.Solid(w, h, 255, 255, 255);
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        BitMatrix m = new MultiFormatWriter().encode("FUSEO-E2E-QR-SAMPLE", BarcodeFormat.QR_CODE, 0, 0, hints);
        int x0 = (int)(w * 0.6), y0 = (int)(h * 0.3);
        for (var my = 0; my < m.Height; my++)
            for (var mx = 0; mx < m.Width; mx++)
                if (m[mx, my])
                    for (var yy = 0; yy < scale; yy++)
                        for (var xx = 0; xx < scale; xx++) bmp.SetPixel(x0 + mx * scale + xx, y0 + my * scale + yy, 0, 0, 0, 255);
        return (bmp, ((double)x0 / w, (double)y0 / h, (double)m.Width * scale / w, (double)m.Height * scale / h));
    }

    // ---- 画像ファイル ----

    private static BitmapSource ToSource(PageBitmap b)
    {
        var src = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgra32, null, b.Pixels, b.Stride);
        src.Freeze();
        return src;
    }

    /// <summary>PNG（tEXt チャンクに目印を入れる）。</summary>
    public static string WritePng(string path, PageBitmap b, bool withMarker = true)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToSource(b)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        if (withMarker) bytes = InsertPngText(bytes, "Comment", Marker);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>JPEG（EXIF / XMP に目印を入れる・品質 95）。</summary>
    public static string WriteJpeg(string path, PageBitmap b, bool withMarker = true)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        BitmapMetadata? meta = null;
        if (withMarker)
        {
            meta = new BitmapMetadata("jpg")
            {
                Title = Marker,
                Subject = Marker,
                Comment = Marker,
                Author = new System.Collections.ObjectModel.ReadOnlyCollection<string>([Marker]),
                CameraModel = Marker,
            };
        }
        enc.Frames.Add(BitmapFrame.Create(ToSource(b), null, meta, null));
        using (var fs = File.Create(path)) enc.Save(fs);
        if (withMarker && !Contains(File.ReadAllBytes(path), Marker))
            throw new InvalidOperationException("JPEG に目印が入らなかった（合成の誤り）");
        return path;
    }

    private static byte[] InsertPngText(byte[] png, string key, string text)
    {
        // IHDR（8 + 25 バイト）の直後に tEXt を入れる。
        var data = Encoding.Latin1.GetBytes(key + "\0" + text);
        var type = Encoding.ASCII.GetBytes("tEXt");
        var chunk = new List<byte>();
        chunk.AddRange(BigEndian(data.Length));
        chunk.AddRange(type);
        chunk.AddRange(data);
        chunk.AddRange(BigEndian((int)Crc32([.. type, .. data])));
        const int afterIhdr = 8 + 25;
        return [.. png[..afterIhdr], .. chunk, .. png[afterIhdr..]];
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    public static bool Contains(byte[] haystack, string text)
    {
        foreach (var enc in new Encoding[] { Encoding.Latin1, Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            if (haystack.AsSpan().IndexOf(enc.GetBytes(text)) >= 0) return true;
        return false;
    }

    // ---- PDF ----

    /// <summary>1 ページの中身。Fill = ページ全体の地色（RGB 0..1）、Text = 目印などの文字（黒・Helvetica）。</summary>
    public sealed record PdfPage(double R, double G, double B, string? Text = null);

    /// <summary>
    /// 最小の PDF（外部ライブラリ無し・xref も正しく書く）。各ページ 600x400pt に地色を塗り、左上に黒い四角、Text があれば文字を置く。
    /// 文書情報（/Info /Title）にも目印を入れる。
    /// </summary>
    public static string WritePdf(string path, IReadOnlyList<PdfPage> pages, bool withInfo = true)
    {
        var bodies = new SortedDictionary<int, string>();
        var kids = new List<int>();
        var next = 4; // 1=Catalog 2=Pages 3=Font
        foreach (var p in pages)
        {
            int pageNo = next++, contentNo = next++;
            kids.Add(pageNo);
            var content = $"{F(p.R)} {F(p.G)} {F(p.B)} rg 0 0 600 400 re f 0 g 36 284 84 80 re f";
            if (p.Text is { } t) content += $" BT /F1 28 Tf 160 320 Td ({t}) Tj ET";
            bodies[contentNo] = $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";
            bodies[pageNo] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents {contentNo} 0 R " +
                             "/Resources << /Font << /F1 3 0 R >> >> >>";
        }
        bodies[1] = "<< /Type /Catalog /Pages 2 0 R >>";
        bodies[2] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";
        bodies[3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        var infoNo = next;
        if (withInfo) bodies[next++] = $"<< /Title ({Marker}) /Author ({Marker}) /Producer (E2E) >>";

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[next];
        foreach (var (no, body) in bodies)
        {
            offsets[no] = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"{no} 0 obj\n{body}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {next}\n0000000000 65535 f \n");
        for (var i = 1; i < next; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {next} /Root 1 0 R{(withInfo ? $" /Info {infoNo} 0 R" : "")} >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
