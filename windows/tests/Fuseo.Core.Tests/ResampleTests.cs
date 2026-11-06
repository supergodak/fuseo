using Fuseo.Core.Imaging;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>ID-1 再 OCR 用の再サンプル（高さそのまま・幅だけ・双線形・§10.13）。</summary>
public class ResampleTests
{
    [Theory]
    [InlineData(2000, 1000, 1586)]   // 1000 × 1.58577 = 1585.77 → 1586
    [InlineData(1000, 500, 793)]
    [InlineData(300, 1000, 1586)]    // 広げる方向も
    public void ToAspect_KeepsHeight_ChangesOnlyWidth(int w, int h, int expectedW)
    {
        var src = PageBitmap.Solid(w, h, 10, 20, 30);
        var dst = Resample.ToAspect(src, 85.60 / 53.98);
        Assert.Equal(expectedW, dst.Width);
        Assert.Equal(h, dst.Height);
        Assert.Equal(dst.Width * 4, dst.Stride);
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), dst.GetPixel(dst.Width / 2, h / 2));   // 単色は単色のまま
    }

    [Fact]
    public void ToAspect_DoesNotTouchSource_AndReturnsNewBuffer()
    {
        var src = PageBitmap.Solid(20, 10, 1, 2, 3);
        var before = (byte[])src.Pixels.Clone();
        var same = Resample.ToAspect(src, 2.0);            // 幅が変わらなくても複製
        Assert.NotSame(src, same);
        Assert.NotSame(src.Pixels, same.Pixels);
        Assert.Equal(src.Pixels, same.Pixels);
        same.Pixels[0] = 99;
        var other = Resample.ToAspect(src, 1.0);
        Assert.NotSame(src.Pixels, other.Pixels);
        Assert.Equal(before, src.Pixels);
    }

    [Fact]
    public void ToAspect_RowsStayIndependent()
    {
        // 行ごとに色を変えた画像: 縦方向は混ざらない（高さが同じ＝縦の補間は恒等）。
        var src = new PageBitmap(8, 3);
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 8; x++) src.SetPixel(x, y, (byte)(y * 100), 0, 0, 255);
        var dst = Resample.ToAspect(src, 5.0 / 3);   // 幅 5
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < dst.Width; x++) Assert.Equal((byte)(y * 100), dst.GetPixel(x, y).B);
    }

    [Fact]
    public void ToAspect_InterpolatesHorizontallyBetweenPixelCenters()
    {
        // 2px（0 と 200）を 4px に: 画素中心の対応で sx = (dx+0.5)/2 − 0.5 = −0.25(端で 0), 0.25, 0.75, 1.25(端で 1)。
        var src = new PageBitmap(2, 1);
        src.SetPixel(0, 0, 0, 0, 0, 255);
        src.SetPixel(1, 0, 200, 200, 200, 255);
        var dst = Resample.ToAspect(src, 4.0);
        Assert.Equal(4, dst.Width);
        Assert.Equal(new byte[] { 0, 50, 150, 200 }, Enumerable.Range(0, 4).Select(x => dst.GetPixel(x, 0).B).ToArray());
        Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(255, dst.GetPixel(x, 0).A));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ToAspect_RejectsInvalidAspect(double aspect) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Resample.ToAspect(PageBitmap.Solid(4, 4, 0, 0, 0), aspect));
}
