using Fuseo.Core.Contracts;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;
using Xunit;

namespace Fuseo.Input.Tests;

/// <summary>画像入力（WIC）: EXIF Orientation の焼き込み・α の白合成・長辺 8000 の縮小・マルチページ TIFF。</summary>
public class ImageInputTests
{
    private static readonly Func<PasswordRequest, Task<string?>> NoPassword =
        _ => throw new Xunit.Sdk.XunitException("画像でパスワードを聞いた");

    private static async Task<IReadOnlyList<Fuseo.Core.Contracts.SourcePage>> Load(params string[] paths) =>
        await new PageSource().LoadAsync(paths, NoPassword, CancellationToken.None);

    // 保存画素: 60×40 の白地、左上 (4,4) から 12 px の黒い四角（中心 (10,10)）。
    private const int W = 60, H = 40;

    [Theory]
    [InlineData(null, 60, 40, 10, 10)]   // Orientation 無し
    [InlineData((ushort)1, 60, 40, 10, 10)]
    [InlineData((ushort)3, 60, 40, 49, 29)] // 180°: (W−1−u, H−1−v)
    [InlineData((ushort)6, 40, 60, 29, 10)] // 時計回り 90°: (H−1−v, u) ＝ 右上
    [InlineData((ushort)8, 40, 60, 10, 49)] // 反時計回り 90°: (v, W−1−u) ＝ 左下
    public async Task Jpeg_ExifOrientation_IsBakedIntoPixels(ushort? orientation, int w, int h, int markX, int markY)
    {
        using var dir = new TempDir();
        var jpg = Img.EncodeJpeg(Img.WhiteWithBlackSquare(W, H, 4, 4, 12), orientation);
        var path = dir.Write("photo.jpg", jpg);
        var page = Assert.Single(await Load(path));
        var b = page.Bitmap;
        Assert.Equal(w, b.Width);
        Assert.Equal(h, b.Height);
        Assert.False(page.FromPdf);
        Assert.Equal("photo.jpg", page.SourceName);
        Assert.True(Img.IsDark(b, markX, markY), $"目印が ({markX},{markY}) に無い");
        // 対角の隅は白（向きを取り違えていないこと）
        Assert.True(Img.IsLight(b, w - 1 - markX, h - 1 - markY));
        Img.AssertOpaque(b);
    }

    [Fact]
    public async Task Png_WithAlpha_IsFlattenedOnWhite()
    {
        // 4×1: 透明な赤／不透明な赤／半透明の黒／透明な黒
        byte[] px =
        [
            0, 0, 255, 0,
            0, 0, 255, 255,
            0, 0, 0, 128,
            0, 0, 0, 0,
        ];
        var src = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Bgra32, null, px, 16);
        using var dir = new TempDir();
        var path = dir.Write("alpha.png", Img.EncodePng(src));
        var b = Assert.Single(await Load(path)).Bitmap;
        Img.AssertOpaque(b);
        Assert.Equal((255, 255, 255, 255), b.GetPixel(0, 0));
        Assert.Equal((0, 0, 255, 255), b.GetPixel(1, 0));
        var (gb, gg, gr, _) = b.GetPixel(2, 0);
        Assert.InRange(gb, 125, 129); Assert.InRange(gg, 125, 129); Assert.InRange(gr, 125, 129);
        Assert.Equal((255, 255, 255, 255), b.GetPixel(3, 0));
    }

    [Fact]
    public async Task Bmp_IsDecodedOpaque()
    {
        var src = Img.WhiteWithBlackSquare(20, 10, 0, 0, 5);
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        using var dir = new TempDir();
        var b = Assert.Single(await Load(dir.Write("x.bmp", ms.ToArray()))).Bitmap;
        Assert.Equal(20, b.Width);
        Assert.True(Img.IsDark(b, 2, 2));
        Assert.True(Img.IsLight(b, 15, 8));
        Img.AssertOpaque(b);
    }

    [Fact]
    public async Task HugeImage_LongSideScaledTo8000()
    {
        var src = Img.WhiteWithBlackSquare(9000, 100, 0, 0, 50);
        using var dir = new TempDir();
        var b = Assert.Single(await Load(dir.Write("wide.png", Img.EncodePng(src)))).Bitmap;
        Assert.Equal(PageSource.MaxImageLongSidePx, b.Width);
        Assert.Equal(89, b.Height); // 100 × 8000/9000 = 88.9
        Assert.True(Img.IsDark(b, 10, 10));
        Assert.True(Img.IsLight(b, 4000, 50));
    }

    [Fact]
    public async Task ImageAtExactly8000_IsNotScaled()
    {
        var src = Img.WhiteWithBlackSquare(8000, 10, 0, 0, 5);
        using var dir = new TempDir();
        var b = Assert.Single(await Load(dir.Write("edge.png", Img.EncodePng(src)))).Bitmap;
        Assert.Equal(8000, b.Width);
        Assert.Equal(10, b.Height);
    }

    [Fact]
    public async Task MultiPageTiff_AllFramesImported()
    {
        var frames = Enumerable.Range(0, 3).Select(i => Img.WhiteWithBlackSquare(30, 20, i * 10, 0, 5)).ToList();
        using var dir = new TempDir();
        var pages = await Load(dir.Write("scan.tiff", Img.EncodeTiff(frames)));
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p.PageIndex));
        Assert.All(pages, p => { Assert.Equal("scan.tiff", p.SourceName); Assert.False(p.FromPdf); });
        for (var i = 0; i < 3; i++) Assert.True(Img.IsDark(pages[i].Bitmap, i * 10 + 2, 2));
    }

    [Fact]
    public async Task MixedImport_KeepsOrder_AndUsesFileNameOnly()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.png", Img.EncodePng(Img.WhiteWithBlackSquare(10, 10, 0, 0, 2)));
        var pdf = dir.Write("b.pdf", MiniPdf.Build([new(20, 20, ""), new(20, 20, "")]));
        var c = dir.Write("c.jpg", Img.EncodeJpeg(Img.WhiteWithBlackSquare(10, 10, 0, 0, 2), null));
        var pages = await Load(a, pdf, c);
        Assert.Equal(new[] { "a.png", "b.pdf", "b.pdf", "c.jpg" }, pages.Select(p => p.SourceName));
        Assert.Equal(new[] { 0, 0, 1, 0 }, pages.Select(p => p.PageIndex));
        Assert.Equal(new[] { false, true, true, false }, pages.Select(p => p.FromPdf));
    }
}

/// <summary><see cref="PixelOps"/> の純関数（Orientation 1〜8 を独立実装と画素単位で突き合わせる）。</summary>
public class PixelOpsTests
{
    private static PageBitmap Distinct(int w, int h)
    {
        var b = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            b.SetPixel(x, y, (byte)x, (byte)y, (byte)(x * 16 + y), 255);
        return b;
    }

    private static PageBitmap Map(PageBitmap s, int w, int h, Func<int, int, (int X, int Y)> srcOf)
    {
        var d = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (sx, sy) = srcOf(x, y);
            var p = s.GetPixel(sx, sy);
            d.SetPixel(x, y, p.B, p.G, p.R, p.A);
        }
        return d;
    }

    private static PageBitmap FlipH(PageBitmap s) => Map(s, s.Width, s.Height, (x, y) => (s.Width - 1 - x, y));
    private static PageBitmap FlipV(PageBitmap s) => Map(s, s.Width, s.Height, (x, y) => (x, s.Height - 1 - y));
    private static PageBitmap RotCw(PageBitmap s) => Map(s, s.Height, s.Width, (x, y) => (y, s.Height - 1 - x));
    private static PageBitmap RotCcw(PageBitmap s) => Map(s, s.Height, s.Width, (x, y) => (s.Width - 1 - y, x));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ExifOrientation_MatchesDefinition(int o)
    {
        var src = Distinct(5, 3);
        // EXIF の定義: 2=左右反転, 3=180°, 4=上下反転, 5=左右反転→反時計90°, 6=時計90°, 7=左右反転→時計90°, 8=反時計90°
        var expected = o switch
        {
            1 => src,
            2 => FlipH(src),
            3 => FlipV(FlipH(src)),
            4 => FlipV(src),
            5 => RotCcw(FlipH(src)),
            6 => RotCw(src),
            7 => RotCw(FlipH(src)),
            _ => RotCcw(src),
        };
        var actual = PixelOps.ApplyExifOrientation(src, o);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(0)] [InlineData(9)] [InlineData(-1)]
    public void ExifOrientation_OutOfRange_IsIdentity(int o)
    {
        var src = Distinct(4, 2);
        Assert.Same(src, PixelOps.ApplyExifOrientation(src, o));
    }

    [Fact]
    public void FlattenOnWhite_ProducesOpaque()
    {
        byte[] px = [10, 20, 30, 0, 10, 20, 30, 255, 0, 0, 0, 51];
        PixelOps.FlattenOnWhite(px);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 10, 20, 30, 255, 204, 204, 204, 255 }, px);
    }

    [Fact]
    public void Downscale_AveragesArea()
    {
        // 4×2: 左半分が黒、右半分が白 → 2×1: 黒・白
        var b = new PageBitmap(4, 2);
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 4; x++)
        {
            var v = x < 2 ? (byte)0 : (byte)255;
            b.SetPixel(x, y, v, v, v, 255);
        }
        var d = PixelOps.DownscaleToFit(b, 2);
        Assert.Equal(2, d.Width);
        Assert.Equal(1, d.Height);
        Assert.Equal((0, 0, 0, 255), d.GetPixel(0, 0));
        Assert.Equal((255, 255, 255, 255), d.GetPixel(1, 0));
    }
}
