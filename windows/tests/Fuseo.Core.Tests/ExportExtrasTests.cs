using System.Text;
using System.Text.RegularExpressions;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>0.4.0: 大きさの上限・PDF のパスワード（AES-256）・透かしの配置・層の重ね合わせ。</summary>
public class ExportExtrasTests
{
    private static readonly Exporter E = new();

    private static byte[] Export(IReadOnlyList<BurnedPage> pages, ExportOptions o)
    {
        using var ms = new MemoryStream();
        E.Export(pages, o, _ => ms);
        return ms.ToArray();
    }

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    /// <summary>写真のような（ノイズよりは縮むが、無地よりは縮みにくい）合成画像を焼き込んだページ。</summary>
    private static BurnedPage Photo(int w, int h)
    {
        var b = new PageBitmap(w, h);
        var px = b.Pixels;
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = 128 + 60 * Math.Sin(x * 0.031) * Math.Cos(y * 0.017) + rnd.Next(-24, 25);
            px[i] = (byte)Math.Clamp(v, 0, 255);
            px[i + 1] = (byte)Math.Clamp(v + 20 * Math.Sin(y * 0.05), 0, 255);
            px[i + 2] = (byte)Math.Clamp(255 - v, 0, 255);
            px[i + 3] = 255;
        }
        return new Fuseo.Core.Rendering.MaskRasterizer().BurnIn(b, ConfirmedMasks.None);
    }

    // ---- パスワード ----

    [Fact]
    public void Password_EncryptsStreams_AndKeyIsRecoverableOnlyWithThePassword()
    {
        var page = ExportTests.Burned(300, 200);
        var pdf = Export([page], new ExportOptions(ExportFormat.Pdf, PdfPassword: "pass-1234"));
        var f = ExportInspect.ParsePdf(pdf);
        Assert.StartsWith("%PDF-1.7\n", Encoding.Latin1.GetString(pdf, 0, 9));
        Assert.Matches(@"/Encrypt \d+ 0 R /ID \[<[0-9A-F]{32}> <[0-9A-F]{32}>\]", f.Trailer);
        var enc = f.Objects.Single(o => o.Dict.Contains("/Filter /Standard"));
        Assert.Contains("/V 5 /R 6", enc.Dict);
        Assert.Contains("/CFM /AESV3", enc.Dict);
        var u = Hex(Regex.Match(enc.Dict, @"/U <([0-9A-F]+)>").Groups[1].Value);
        var ue = Hex(Regex.Match(enc.Dict, @"/UE <([0-9A-F]+)>").Groups[1].Value);
        Assert.Equal(48, u.Length);
        Assert.Equal(32, ue.Length);
        Assert.Null(PdfEncryption.RecoverFileKey("wrong", u, ue));
        var key = PdfEncryption.RecoverFileKey("pass-1234", u, ue);
        Assert.NotNull(key);
        // 中身のストリームは暗号化されていて、鍵で戻すと元の描画命令になる
        var contents = f.Objects.Single(o => o.Stream is not null && !o.Dict.Contains("/Subtype /Image"));
        Assert.DoesNotContain("/Im0 Do", Encoding.Latin1.GetString(contents.Stream!));
        Assert.Contains("/Im0 Do", Encoding.ASCII.GetString(PdfEncryption.DecryptStream(key!, contents.Stream!)));
        // 平文の文字列（Producer）を持たない
        Assert.Contains("3 0 obj\n<< >>", Encoding.Latin1.GetString(pdf));
        Assert.DoesNotContain("Fuseo for Windows", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void Password_IsIgnoredForImagesAndWhenEmpty()
    {
        Assert.Null(new ExportOptions(ExportFormat.Jpeg, PdfPassword: "x").EffectivePdfPassword);
        Assert.Null(new ExportOptions(ExportFormat.Pdf, PdfPassword: "").EffectivePdfPassword);
        var pdf = Export([ExportTests.Burned(40, 30)], new ExportOptions(ExportFormat.Pdf, PdfPassword: ""));
        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(pdf));
        Assert.Contains("Fuseo for Windows", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void Hash2B_IsDeterministic_AndSensitiveToEveryInput()
    {
        var pw = PdfEncryption.PreparePassword("abc");
        var salt = new byte[8];
        var h1 = PdfEncryption.Hash(pw, salt, []);
        Assert.Equal(32, h1.Length);
        Assert.Equal(h1, PdfEncryption.Hash(pw, salt, []));
        Assert.NotEqual(h1, PdfEncryption.Hash(PdfEncryption.PreparePassword("abd"), salt, []));
        Assert.NotEqual(h1, PdfEncryption.Hash(pw, [1, 0, 0, 0, 0, 0, 0, 0], []));
        Assert.NotEqual(h1, PdfEncryption.Hash(pw, salt, new byte[48]));
        // 全角の英数字は NFKC で半角と同じパスワードになる（Acrobat の SASLprep と同じ扱い）
        Assert.Equal(pw, PdfEncryption.PreparePassword("ａｂｃ"));
    }

    // ---- 大きさの上限 ----

    [Fact]
    public void SizeLimit_Pdf_FitsAndKeepsPaperSize()
    {
        var page = Photo(2480, 3508);
        var free = ExportInspect.ParsePdf(Export([page], new ExportOptions(ExportFormat.Pdf)));
        var limited = Export([page, page], new ExportOptions(ExportFormat.Pdf, MaxBytes: 1_500_000));
        Assert.True(limited.Length <= 1_500_000, $"{limited.Length}");
        var f = ExportInspect.ParsePdf(limited);
        string Box(ExportInspect.PdfFile x) => Regex.Match(x.NonStreamText, @"/MediaBox \[[^\]]+\]").Value;
        Assert.Equal(Box(free), Box(f));
        Assert.Contains("/DCTDecode", f.NonStreamText);
    }

    [Fact]
    public void SizeLimit_Jpeg_FitsAndNeverGoesBelowMinimumShortSide()
    {
        var bytes = Export([Photo(2000, 1300)], new ExportOptions(ExportFormat.Jpeg, 1.0, MaxBytes: 300_000));
        Assert.True(bytes.Length <= 300_000, $"{bytes.Length}");
        var (w, h, _) = ExportInspect.JpegSof(bytes);
        Assert.True(Math.Min(w, h) >= Exporter.MinShortSidePx, $"{w}x{h}");
    }

    [Fact]
    public void SizeLimit_Unreachable_Throws_AndOpensNoDestination()
    {
        var opened = 0;
        var ex = Assert.Throws<SizeLimitUnreachableException>(() =>
            E.Export([Photo(2480, 3508)], new ExportOptions(ExportFormat.Pdf, MaxBytes: 5_000),
                     _ => { opened++; return new MemoryStream(); }));
        Assert.Equal(5_000, ex.LimitBytes);
        Assert.True(ex.SmallestBytes > 5_000);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void SizeLimit_WithPassword_BothApply()
    {
        var pdf = Export([Photo(2480, 3508)], new ExportOptions(ExportFormat.Pdf, MaxBytes: 1_000_000, PdfPassword: "abc"));
        Assert.True(pdf.Length <= 1_000_000, $"{pdf.Length}");
        Assert.Contains("/Encrypt", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void Downscale_RespectsFloor_AndReturnsSameWhenNotShrinking()
    {
        var src = new PageBitmap(1000, 700);
        Assert.Same(src, Resample.Downscale(src, 1.0, 600));
        var half = Resample.Downscale(src, 0.5, 600);
        Assert.Equal(600, Math.Min(half.Width, half.Height));
        var small = new PageBitmap(500, 400);
        Assert.Same(small, Resample.Downscale(small, 0.5, 600));
    }

    // ---- 透かし ----

    [Fact]
    public void WatermarkLayout_Tiled_CoversAllQuadrants_Diagonal_IsCentered()
    {
        var t = WatermarkLayout.Placements(WatermarkLayoutKind.Tiled, 1200, 800, 200, 30);
        Assert.Contains(t, p => p.CenterX < 600 && p.CenterY < 400);
        Assert.Contains(t, p => p.CenterX >= 600 && p.CenterY < 400);
        Assert.Contains(t, p => p.CenterX < 600 && p.CenterY >= 400);
        Assert.Contains(t, p => p.CenterX >= 600 && p.CenterY >= 400);
        Assert.All(t, p => Assert.Equal(WatermarkLayout.TiledRiseDegrees, p.RiseDegrees));
        var d = Assert.Single(WatermarkLayout.Placements(WatermarkLayoutKind.Diagonal, 1000, 600, 300, 50));
        Assert.Equal((500.0, 300.0), (d.CenterX, d.CenterY));
        Assert.Equal(Math.Atan2(600, 1000) * 180 / Math.PI, d.RiseDegrees, 9);
        Assert.Empty(WatermarkLayout.Placements(WatermarkLayoutKind.Tiled, 100, 100, 0, 0));
    }

    [Fact]
    public void Watermark_TrimsText_AndClampsOpacity()
    {
        Assert.Equal("", new Watermark("  ").TrimmedText);
        Assert.Equal(Watermark.MaxOpacity, new Watermark("a", Opacity: 5).ClampedOpacity);
        Assert.Equal(Watermark.MinOpacity, new Watermark("a", Opacity: 0).ClampedOpacity);
        Assert.Equal(Watermark.DefaultOpacity, new Watermark("a", Opacity: double.NaN).ClampedOpacity);
    }

    [Fact]
    public void WithLayer_OverlaysWithoutChangingTheOriginal_AndBlackStaysDark()
    {
        var page = ExportTests.Burned(10, 10, new Fuseo.Core.Geometry.NormRect(0, 0, 1, 1)); // 全面が黒
        var layer = new byte[10 * 10 * 4];
        for (var i = 0; i < layer.Length; i += 4) { layer[i] = layer[i + 1] = layer[i + 2] = 64; layer[i + 3] = 64; } // 25% の白っぽい灰
        var over = page.WithLayer(layer);
        Assert.Equal((byte)0, page.Bitmap.Pixels[0]);
        var px = over.Bitmap.GetPixel(5, 5);
        Assert.True(px.R <= 70 && px.G <= 70 && px.B <= 70, $"{px}");
        Assert.Throws<ArgumentException>(() => page.WithLayer(new byte[4]));
    }

    [Fact]
    public void Password_Rule_AsciiOnly_UpTo32()
    {
        Assert.True(ExportOptions.IsValidPdfPassword("abc-1234 !~"));
        Assert.True(ExportOptions.IsValidPdfPassword(new string('a', 32)));
        Assert.False(ExportOptions.IsValidPdfPassword(new string('a', 33)));
        Assert.False(ExportOptions.IsValidPdfPassword("pässwörd"));
        Assert.False(ExportOptions.IsValidPdfPassword("パスワード"));
        Assert.False(ExportOptions.IsValidPdfPassword("ａｂｃ"));
        Assert.False(ExportOptions.IsValidPdfPassword(""));
        Assert.Throws<ArgumentException>(() => Export([ExportTests.Burned(20, 20)], new ExportOptions(ExportFormat.Pdf, PdfPassword: "日本語")));
    }
}
