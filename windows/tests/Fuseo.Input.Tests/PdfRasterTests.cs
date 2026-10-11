using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;
using Xunit;

namespace Fuseo.Input.Tests;

/// <summary>
/// PDF ラスタライズの契約（§4.4）。最重要は**内容範囲テスト**:
/// Mac で起きた「内容が約 57% に縮小して中央に描かれる」バグを、寸法ではなく暗画素の範囲で検出する。
/// </summary>
public class PdfRasterTests
{
    private const double S = 300.0 / 72.0;

    private static readonly Func<string, Task<string?>> NoPassword =
        _ => throw new Xunit.Sdk.XunitException("暗号化されていない PDF でパスワードを聞いた");

    private static int Px(double pt) => (int)Math.Round(pt * S, MidpointRounding.AwayFromZero);

    private static async Task<PageBitmap> RenderSingle(MiniPdf.Page page)
    {
        using var dir = new TempDir();
        var path = dir.Write("page.pdf", MiniPdf.Build([page]));
        var pages = await new PageSource().LoadAsync([path], NoPassword, CancellationToken.None);
        var p = Assert.Single(pages);
        Assert.True(p.FromPdf);
        Assert.Equal("page.pdf", p.SourceName);
        Assert.Equal(0, p.PageIndex);
        return p.Bitmap;
    }

    private static void AssertContentSpans(PageBitmap b)
    {
        var bounds = Img.DarkBounds(b);
        Assert.NotNull(bounds);
        var (minX, minY, maxX, maxY) = bounds.Value;
        var spanW = (maxX - minX + 1) / (double)b.Width;
        var spanH = (maxY - minY + 1) / (double)b.Height;
        Assert.True(spanW >= 0.95, $"暗画素の横の範囲が {spanW:P1}（≥95% のはず。縮小して描かれている疑い）");
        Assert.True(spanH >= 0.95, $"暗画素の縦の範囲が {spanH:P1}（≥95% のはず。縮小して描かれている疑い）");
    }

    // 目印: ページ（回転前）の左上に 60pt 四方の黒い四角（左上から 40pt）。
    private static string MarkerTopLeft(double w, double h) => MiniPdf.Rect(40, h - 40 - 60, 60, 60);

    [Fact]
    public async Task A4Portrait_ContentFillsPage_AndSizeIs300Dpi()
    {
        const double w = 595, h = 842;
        var b = await RenderSingle(new(w, h, MiniPdf.Frame(w, h) + " " + MarkerTopLeft(w, h)));
        Assert.Equal(Px(w), b.Width);   // 2479
        Assert.Equal(Px(h), b.Height);  // 3508
        AssertContentSpans(b);
        Img.AssertOpaque(b);
        // 目印は左上（PDF の左下原点 → ビットマップの左上原点で上下が反転していないこと）
        Assert.True(Img.IsDark(b, Px(70), Px(70)));
        Assert.True(Img.IsLight(b, Px(w - 70), Px(70)));
        Assert.True(Img.IsLight(b, Px(70), Px(h - 70)));
        Assert.True(Img.IsLight(b, Px(w / 2), Px(h / 2)));
    }

    [Fact]
    public async Task A4LandscapeMediaBox_ContentFillsPage()
    {
        const double w = 842, h = 595;
        var b = await RenderSingle(new(w, h, MiniPdf.Frame(w, h) + " " + MarkerTopLeft(w, h)));
        Assert.Equal(Px(w), b.Width);
        Assert.Equal(Px(h), b.Height);
        AssertContentSpans(b);
        Assert.True(Img.IsDark(b, Px(70), Px(70)));
        Assert.True(Img.IsLight(b, Px(w - 70), Px(70)));
    }

    [Fact]
    public async Task Rotate90_SwapsSize_ContentFillsPage_MarkerMovesToTopRight()
    {
        // 縦長の MediaBox に /Rotate 90（表示時に時計回り 90°）。
        const double w = 595, h = 842;
        var b = await RenderSingle(new(w, h, MiniPdf.Frame(w, h) + " " + MarkerTopLeft(w, h), Rotate: 90));
        Assert.Equal(Px(h), b.Width);   // 縦横が入れ替わる
        Assert.Equal(Px(w), b.Height);
        AssertContentSpans(b);
        // 回転前の左上 (u=70, v=70) は、時計回り 90° で表示上の右上 (x = h−70, y = 70) へ移る。
        Assert.True(Img.IsDark(b, Px(h - 70), Px(70)), "目印が右上に無い（/Rotate の向きが違う／二重適用の疑い）");
        Assert.True(Img.IsLight(b, Px(70), Px(70)));
        Assert.True(Img.IsLight(b, Px(70), Px(w - 70)));
        Assert.True(Img.IsLight(b, Px(h - 70), Px(w - 70)));
    }

    [Fact]
    public async Task Rotate270_MarkerMovesToBottomLeft()
    {
        const double w = 595, h = 842;
        var b = await RenderSingle(new(w, h, MiniPdf.Frame(w, h) + " " + MarkerTopLeft(w, h), Rotate: 270));
        Assert.Equal(Px(h), b.Width);
        Assert.Equal(Px(w), b.Height);
        AssertContentSpans(b);
        // 反時計回り 90°: (u, v) → (v, W0−u) ＝ 左下
        Assert.True(Img.IsDark(b, Px(70), Px(w - 70)));
        Assert.True(Img.IsLight(b, Px(h - 70), Px(70)));
    }

    [Fact]
    public async Task EmptyPage_IsOpaqueWhite()
    {
        var b = await RenderSingle(new(200, 100, ""));
        for (var i = 0; i < b.Pixels.Length; i += 4)
        {
            Assert.True(b.Pixels[i] == 255 && b.Pixels[i + 1] == 255 && b.Pixels[i + 2] == 255 && b.Pixels[i + 3] == 255,
                $"白不透明でない画素（index {i / 4}）");
        }
    }

    [Fact]
    public async Task Annotations_AreNotDrawn()
    {
        // 外観ストリーム付きの注釈（ページ全面の黒）。FPDF_ANNOT を付けないので描かれない。
        var b = await RenderSingle(new(200, 100, "", AnnotAppearance: "0 g 0 0 200 100 re f"));
        Assert.Null(Img.DarkBounds(b));
    }

    [Fact]
    public async Task LargePage_LongSideClampedTo4000_KeepsAspect()
    {
        const double w = 2000, h = 1000; // 300dpi なら 8333×4167
        var b = await RenderSingle(new(w, h, MiniPdf.Frame(w, h, inset: 10, thick: 30)));
        Assert.Equal(IPageSource.MaxLongSidePx, b.Width);
        Assert.Equal(2000, b.Height);
        AssertContentSpans(b);
    }

    [Theory]
    [InlineData(595, 842, 2479, 3508)]
    [InlineData(72, 72, 300, 300)]
    [InlineData(2000, 1000, 4000, 2000)]
    [InlineData(1000, 2000, 2000, 4000)]
    [InlineData(5000, 10, 4000, 8)]
    public void PixelSize_Is300DpiWithLongSideClamp(double wPt, double hPt, int w, int h)
    {
        Assert.Equal((w, h), PdfPageGeometry.PixelSize(wPt, hPt));
    }

    [Fact]
    public async Task MultiPage_IndicesAndOrder()
    {
        using var dir = new TempDir();
        var pdf = dir.Write("doc.pdf", MiniPdf.Build([
            new(100, 100, MiniPdf.Rect(0, 0, 100, 100)), // 1 ページ目: 全面黒
            new(100, 100, ""),                           // 2 ページ目: 白
            new(50, 100, ""),                            // 3 ページ目: 寸法違い
        ]));
        var pages = await new PageSource().LoadAsync([pdf], NoPassword, CancellationToken.None);
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p.PageIndex));
        Assert.All(pages, p => Assert.Equal("doc.pdf", p.SourceName));
        Assert.True(Img.IsDark(pages[0].Bitmap, 50, 50));
        Assert.True(Img.IsLight(pages[1].Bitmap, 50, 50));
        Assert.Equal(Px(50), pages[2].Bitmap.Width);
    }
}
