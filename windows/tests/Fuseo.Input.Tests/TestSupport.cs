using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;

namespace Fuseo.Input.Tests;

/// <summary>テスト内で最小 PDF を組み立てる（外部ライブラリ無し・xref のオフセットも正しく書く）。</summary>
internal static class MiniPdf
{
    /// <param name="AnnotAsWidget">注釈を記入済みのフォーム欄（/Widget・/FT /Tx・カタログに /AcroForm）として置く。</param>
    public sealed record Page(double Width, double Height, string Content, int Rotate = 0, string? AnnotAppearance = null,
                              bool AnnotAsWidget = false);

    public static byte[] Build(IReadOnlyList<Page> pages)
    {
        // オブジェクト番号: 1=Catalog, 2=Pages, 以降ページごとに Page / Contents（/ 注釈 / 注釈の外観）
        var kids = new List<int>();
        var next = 3;
        var bodies = new SortedDictionary<int, string>();
        var fields = new List<int>();
        foreach (var p in pages)
        {
            int pageNo = next++, contentNo = next++;
            kids.Add(pageNo);
            var content = p.Content;
            bodies[contentNo] = Stream(content, "");
            var extra = "";
            if (p.Rotate != 0) extra += $" /Rotate {p.Rotate}";
            if (p.AnnotAppearance is not null)
            {
                int annotNo = next++, apNo = next++;
                bodies[annotNo] = p.AnnotAsWidget
                    ? $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (field{annotNo}) /V (filled) /P {pageNo} 0 R " +
                      $"/Rect [0 0 {F(p.Width)} {F(p.Height)}] /F 4 /AP << /N {apNo} 0 R >> >>"
                    : $"<< /Type /Annot /Subtype /Square /Rect [0 0 {F(p.Width)} {F(p.Height)}] /F 4 /AP << /N {apNo} 0 R >> >>";
                if (p.AnnotAsWidget) fields.Add(annotNo);
                bodies[apNo] = Stream(p.AnnotAppearance,
                    $"/Type /XObject /Subtype /Form /BBox [0 0 {F(p.Width)} {F(p.Height)}] ");
                extra += $" /Annots [{annotNo} 0 R]";
            }
            bodies[pageNo] =
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(p.Width)} {F(p.Height)}] /Contents {contentNo} 0 R /Resources << >>{extra} >>";
        }
        bodies[1] = fields.Count == 0
            ? "<< /Type /Catalog /Pages 2 0 R >>"
            : $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{string.Join(" ", fields.Select(f => $"{f} 0 R"))}] >> >>";
        bodies[2] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[next];
        foreach (var (no, body) in bodies)
        {
            offsets[no] = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"{no} 0 obj\n{body}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {next}\n0000000000 65535 f \n");
        for (var i = 1; i < next; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {next} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string data, string dictPrefix) =>
        $"<< {dictPrefix}/Length {Encoding.ASCII.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    public static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>黒い太枠（外周から inset pt・太さ thick pt）を描く内容ストリーム。</summary>
    public static string Frame(double w, double h, double inset = 5, double thick = 12) =>
        $"0 g {F(inset)} {F(inset)} {F(w - 2 * inset)} {F(thick)} re f " +                    // 下
        $"{F(inset)} {F(h - inset - thick)} {F(w - 2 * inset)} {F(thick)} re f " +             // 上
        $"{F(inset)} {F(inset)} {F(thick)} {F(h - 2 * inset)} re f " +                         // 左
        $"{F(w - inset - thick)} {F(inset)} {F(thick)} {F(h - 2 * inset)} re f";               // 右

    /// <summary>塗りつぶし矩形（PDF 座標・左下原点）。</summary>
    public static string Rect(double x, double y, double w, double h) => $"0 g {F(x)} {F(y)} {F(w)} {F(h)} re f";
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuseo-input-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, byte[] bytes)
    {
        var p = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Img
{
    /// <summary>暗画素（R,G,B すべて &lt; 128）の外接矩形。無ければ null。</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY)? DarkBounds(PageBitmap b)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < b.Height; y++)
        for (var x = 0; x < b.Width; x++)
        {
            var (bl, g, r, _) = b.GetPixel(x, y);
            if (r < 128 && g < 128 && bl < 128)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }

    public static bool IsDark(PageBitmap b, int x, int y)
    {
        var (bl, g, r, _) = b.GetPixel(x, y);
        return r < 128 && g < 128 && bl < 128;
    }

    public static bool IsLight(PageBitmap b, int x, int y)
    {
        var (bl, g, r, _) = b.GetPixel(x, y);
        return r > 200 && g > 200 && bl > 200;
    }

    public static void AssertOpaque(PageBitmap b)
    {
        for (var i = 3; i < b.Pixels.Length; i += 4)
            if (b.Pixels[i] != 255) throw new Xunit.Sdk.XunitException($"不透明でない画素がある（index {i / 4}）");
    }

    /// <summary>白地に、左上 (x0,y0) から size 四方の黒い四角を置いた BGRA32 ビットマップ。</summary>
    public static BitmapSource WhiteWithBlackSquare(int w, int h, int x0, int y0, int size)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var black = x >= x0 && x < x0 + size && y >= y0 && y < y0 + size;
            byte v = black ? (byte)0 : (byte)255;
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] EncodeJpeg(BitmapSource src, ushort? orientation)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        BitmapMetadata? md = null;
        if (orientation is { } o)
        {
            md = new BitmapMetadata("jpg");
            md.SetQuery("/app1/ifd/{ushort=274}", o);
        }
        enc.Frames.Add(BitmapFrame.Create(src, null, md, null));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodeTiff(IEnumerable<BitmapSource> frames)
    {
        var enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        foreach (var f in frames) enc.Frames.Add(BitmapFrame.Create(f));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
