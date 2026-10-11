using System.IO;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.Input.Tests;

/// <summary>
/// §4.3 の往復検証: 自前ライタの PDF を PDFium（取り込み経路そのもの）で読み戻し、
/// ページ数・寸法（px→pt→px が一致）・マスク領域が黒いことを確認する。
/// </summary>
public class ExportRoundTripTests
{
    private static PageBitmap Pattern(int w, int h)
    {
        var bmp = PageBitmap.Solid(w, h, 240, 240, 240);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (y % 30 < 3) bmp.SetPixel(x, y, 40, 40, 40, 255); // 文字行の代わり
        return bmp;
    }

    [Theory]
    [InlineData(PdfImageEncoding.Jpeg)]
    [InlineData(PdfImageEncoding.Flate)]
    public async Task WrittenPdf_ReadsBackInPdfium_WithSameSize_AndBlackMask(PdfImageEncoding enc)
    {
        var mask = new NormRect(0.1, 0.6, 0.5, 0.15);
        var r = new MaskRasterizer();
        var pages = new[]
        {
            r.BurnIn(Pattern(1240, 1754), new ConfirmedMasks([mask], [])),
            r.BurnIn(Pattern(1754, 1240), ConfirmedMasks.None),
        };
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "out.pdf");
        using (var fs = File.Create(path))
            new Exporter().Export(pages, new ExportOptions(ExportFormat.Pdf, PdfImages: enc), _ => fs);

        var back = await new PageSource().LoadAsync([path], _ => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Equal(2, back.Count);
        Assert.Equal((1240, 1754), (back[0].Bitmap.Width, back[0].Bitmap.Height));
        Assert.Equal((1754, 1240), (back[1].Bitmap.Width, back[1].Bitmap.Height));

        // 目視確認用（環境変数があるときだけ・合成画像のみ）
        if (Environment.GetEnvironmentVariable("FUSEO_SAMPLE_OUT") is { Length: > 0 } outDir)
            for (var i = 0; i < back.Count; i++)
                File.WriteAllBytes(Path.Combine(outDir, $"roundtrip-{enc}-{i}.png"),
                    PngEncoder.Encode(RgbImage.FromBgraOnWhite(back[i].Bitmap)));

        var pr = CoordinateSpace.ToPixel(mask, 1240, 1754);
        var b = back[0].Bitmap;
        for (var y = pr.Y + 2; y < pr.Bottom - 2; y++)
        for (var x = pr.X + 2; x < pr.Right - 2; x++)
        {
            var p = b.GetPixel(x, y);
            Assert.True(p.R <= 12 && p.G <= 12 && p.B <= 12, $"({x},{y}) = {p}");
        }
        // マスク外の地は明るいまま（ページ全体が黒く潰れていない）
        var bg = b.GetPixel(5, 10);
        Assert.True(bg.R > 200, $"地の色 {bg}");
    }
}
