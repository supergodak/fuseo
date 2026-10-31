using System.Text;
using Fuseo.Core.Imaging;
using Fuseo.Input;
using Xunit;

namespace Fuseo.E2E;

/// <summary>正規化矩形（左上原点・0..1）。</summary>
internal readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public Box Inset(double d) => new(X + d, Y + d, W - 2 * d, H - 2 * d);
    public bool Contains(double u, double v) => u >= X && u < Right && v >= Y && v < Bottom;
    public static Box FromPoints(double u0, double v0, double u1, double v1) =>
        new(Math.Min(u0, u1), Math.Min(v0, v1), Math.Abs(u1 - u0), Math.Abs(v1 - v0));
}

/// <summary>
/// 書き出したファイルの検査: 形式（許可したチャンク・セグメントだけ）・目印の文字列が無いこと・ページ数・画素（塗った所が黒く、
/// 塗っていない所が元のまま）。読み戻しは製品と同じ取り込み部品（Fuseo.Input の PageSource＝WIC・PDFium）を使う。
/// </summary>
internal static class OutputCheck
{
    public static IReadOnlyList<PageBitmap> Load(string path) =>
        new PageSource().LoadAsync([path], _ => Task.FromResult<string?>(null), CancellationToken.None)
            .GetAwaiter().GetResult().Select(p => p.Bitmap).ToList();

    public static PageBitmap LoadSingle(string path)
    {
        var pages = Load(path);
        Assert.Single(pages);
        return pages[0];
    }

    /// <summary>形式の検査（contract.md §1 の許可リスト）と、入力の目印・メタデータが残っていないこと。</summary>
    public static void AssertClean(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.False(Synth.Contains(bytes, Synth.Marker), $"{Path.GetFileName(path)} に入力の目印（メタデータ・テキスト）が残っている");
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                AssertJpegSegments(bytes);
                break;
            case ".png":
                AssertPngChunks(bytes);
                break;
            case ".pdf":
                AssertPdf(bytes);
                break;
            default:
                throw new ArgumentException(path);
        }
    }

    private static void AssertJpegSegments(byte[] b)
    {
        Assert.True(b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8, "JPEG の SOI が無い");
        Assert.True(b[^2] == 0xFF && b[^1] == 0xD9, "JPEG の EOI が無い");
        var seen = new List<byte>();
        var i = 2;
        while (i < b.Length - 1)
        {
            Assert.Equal(0xFF, b[i]);
            var marker = b[i + 1];
            seen.Add(marker);
            var len = (b[i + 2] << 8) | b[i + 3];
            if (marker == 0xDA) break; // SOS の後は画像データ
            i += 2 + len;
        }
        var allowed = new byte[] { 0xE0, 0xDB, 0xC0, 0xC4, 0xDA };
        var bad = seen.Where(m => !allowed.Contains(m)).ToList();
        Assert.True(bad.Count == 0, $"JPEG に許可していないセグメント: {string.Join(",", bad.Select(m => $"FF{m:X2}"))}");
        Assert.Contains((byte)0xE0, seen);
        Assert.Contains((byte)0xDA, seen);
    }

    private static void AssertPngChunks(byte[] b)
    {
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(b.AsSpan(0, 8).SequenceEqual(sig), "PNG の署名が無い");
        var types = new List<string>();
        var i = 8;
        while (i + 8 <= b.Length)
        {
            var len = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
            types.Add(Encoding.ASCII.GetString(b, i + 4, 4));
            i += 12 + len;
        }
        var bad = types.Where(t => t is not ("IHDR" or "IDAT" or "IEND")).Distinct().ToList();
        Assert.True(bad.Count == 0, $"PNG に許可していないチャンク: {string.Join(",", bad)}");
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
    }

    private static void AssertPdf(byte[] b)
    {
        var text = Encoding.Latin1.GetString(b);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Producer (Fuseo for Windows)", text);
        foreach (var banned in new[] { "/Font", "/Annots", "/Metadata", "/JavaScript", "/OpenAction", "/AcroForm", "/Title", "/Author", " Tj", "/ID " })
            Assert.False(text.Contains(banned, StringComparison.Ordinal), $"PDF に {banned} がある");
    }

    // ---- 画素 ----

    public static bool IsDark((byte B, byte G, byte R, byte A) p, int threshold = 48) => p.R < threshold && p.G < threshold && p.B < threshold;

    /// <summary>矩形の中の暗い画素の割合。</summary>
    public static double DarkFraction(PageBitmap b, Box r, int threshold = 48)
    {
        int x0 = (int)Math.Floor(r.X * b.Width), y0 = (int)Math.Floor(r.Y * b.Height);
        int x1 = (int)Math.Ceiling(r.Right * b.Width), y1 = (int)Math.Ceiling(r.Bottom * b.Height);
        long n = 0, dark = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(b.Width, x1); x++)
            {
                n++;
                if (IsDark(b.GetPixel(x, y), threshold)) dark++;
            }
        return n == 0 ? 0 : (double)dark / n;
    }

    /// <summary>矩形（の内側）がほぼ全部黒く塗られていること。</summary>
    public static void AssertPainted(PageBitmap b, Box r, string what, double inset = 0.01)
    {
        var f = DarkFraction(b, r.Inset(inset));
        Assert.True(f > 0.98, $"{what} が塗られていない（黒 {f:P1}）");
    }

    public static void AssertNotPainted(PageBitmap b, Box r, string what)
    {
        var f = DarkFraction(b, r);
        Assert.True(f < 0.02, $"{what} が塗られている（黒 {f:P1}）");
    }

    /// <summary>
    /// <see cref="Synth.Card"/> の画像として、塗った所（masks）と目印の四角の周り以外が元の色のままか（平均の差が小さいか）。
    /// toSource は出力の正規化座標 → 元画像の正規化座標（回転・切り抜きを戻す）。
    /// </summary>
    public static void AssertCardIntact(PageBitmap b, IReadOnlyList<Box> masks, Func<double, double, (double U, double V)>? toSource = null,
                                        string what = "塗っていない所")
    {
        toSource ??= (u, v) => (u, v);
        double sum = 0;
        long n = 0, bad = 0;
        var badge = new Box(Synth.Badge.X, Synth.Badge.Y, Synth.Badge.W, Synth.Badge.H);
        for (var gy = 1; gy < 60; gy++)
            for (var gx = 1; gx < 60; gx++)
            {
                double u = gx / 60.0, v = gy / 60.0;
                if (masks.Any(m => m.Inset(-0.02).Contains(u, v))) continue;
                var (su, sv) = toSource(u, v);
                if (badge.Inset(-0.02).Contains(su, sv)) continue;
                var (eb, eg, er) = Synth.CardColor(su, sv);
                var p = b.GetPixel(Math.Min(b.Width - 1, (int)(u * b.Width)), Math.Min(b.Height - 1, (int)(v * b.Height)));
                var d = (Math.Abs(p.B - eb) + Math.Abs(p.G - eg) + Math.Abs(p.R - er)) / 3.0;
                sum += d;
                n++;
                if (d > 40) bad++;
            }
        Assert.True(n > 500, "比べる点が少なすぎる（マスクが大きすぎる）");
        var mean = sum / n;
        Assert.True(mean < 10 && bad < n / 100 + 1, $"{what}が元のままでない（平均の差 {mean:0.0}・外れ {bad}/{n}）");
    }

    /// <summary>時計回り 90°×turns 回した出力の座標 → 元画像の座標。</summary>
    public static Func<double, double, (double U, double V)> Unrotate(int turns)
    {
        Func<double, double, (double U, double V)> f = (((turns % 4) + 4) % 4) switch
        {
            0 => (u, v) => (u, v),
            1 => (u, v) => (v, 1 - u),
            2 => (u, v) => (1 - u, 1 - v),
            _ => (u, v) => (1 - v, u),
        };
        return f;
    }

    /// <summary>平均の色（RGB）。</summary>
    public static (double R, double G, double B) Mean(PageBitmap b, Box r)
    {
        int x0 = (int)(r.X * b.Width), y0 = (int)(r.Y * b.Height), x1 = (int)(r.Right * b.Width), y1 = (int)(r.Bottom * b.Height);
        double sr = 0, sg = 0, sb = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var p = b.GetPixel(x, y);
                sr += p.R; sg += p.G; sb += p.B; n++;
            }
        return (sr / n, sg / n, sb / n);
    }

    /// <summary>x の列で、v 付近（±window）の連続した黒の長さ（px）。ブラシの太さの測定用。</summary>
    public static int DarkRunHeight(PageBitmap b, double u, double v, double window = 0.12)
    {
        var x = (int)(u * b.Width);
        int y0 = (int)((v - window) * b.Height), y1 = (int)((v + window) * b.Height);
        var best = 0;
        var run = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++)
        {
            run = IsDark(b.GetPixel(x, y)) ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }
}
