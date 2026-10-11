using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>§4.1: マスク内は全画素 #000000FF、マスク外は原画とバイト一致、元バッファは不変。</summary>
public class MaskRasterizerTests
{
    private static readonly MaskRasterizer R = new();

    /// <summary>画素ごとに値が違う原画（どこが変わったかをバイト比較で検出できるように）。</summary>
    internal static PageBitmap Noise(int w, int h, int seed = 1)
    {
        var bmp = new PageBitmap(w, h);
        new Random(seed).NextBytes(bmp.Pixels);
        for (var i = 3; i < bmp.Pixels.Length; i += 4) bmp.Pixels[i] = 255;
        return bmp;
    }

    private static bool IsBlack(PageBitmap b, int x, int y) => b.GetPixel(x, y) == (0, 0, 0, 255);

    [Fact]
    public void Rect_InsideIsOpaqueBlack_OutsideIsByteIdentical_SourceUntouched()
    {
        var src = Noise(200, 100);
        var before = (byte[])src.Pixels.Clone();
        var rect = new NormRect(0.1, 0.2, 0.3, 0.4);
        var outBmp = R.BurnIn(src, new ConfirmedMasks([rect], [])).Bitmap;

        Assert.NotSame(src.Pixels, outBmp.Pixels);
        Assert.Equal(before, src.Pixels);
        var pr = CoordinateSpace.ToPixel(rect, 200, 100);
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 200; x++)
        {
            var inside = x >= pr.X && x < pr.Right && y >= pr.Y && y < pr.Bottom;
            if (inside) Assert.True(IsBlack(outBmp, x, y), $"({x},{y}) が黒でない");
            else Assert.Equal(src.GetPixel(x, y), outBmp.GetPixel(x, y));
        }
    }

    [Fact]
    public void Rect_FractionalEdges_RoundOutward()
    {
        // 10px 幅で 0.25〜0.55 → 画素 2.5〜5.5 に掛かる 2..5 の 4 画素を塗る（欠けない側）
        var outBmp = R.BurnIn(Noise(10, 10), new ConfirmedMasks([new NormRect(0.25, 0.25, 0.3, 0.3)], [])).Bitmap;
        for (var x = 2; x <= 5; x++) Assert.True(IsBlack(outBmp, x, 3));
        Assert.False(IsBlack(outBmp, 1, 3));
        Assert.False(IsBlack(outBmp, 6, 3));
    }

    [Fact]
    public void Rect_TransparentSource_BecomesOpaque()
    {
        var src = PageBitmap.Solid(8, 8, 50, 60, 70, a: 0);
        var outBmp = R.BurnIn(src, new ConfirmedMasks([new NormRect(0, 0, 1, 1)], [])).Bitmap;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++) Assert.True(IsBlack(outBmp, x, y));
    }

    [Fact]
    public void Stroke_CoversCapsuleIncludingRoundCaps_AndNothingFar()
    {
        // 200x100・短辺 100・Width 0.1 → 半径 5px。水平線 (40,50)→(160,50)
        var src = Noise(200, 100);
        var stroke = new BrushStroke([new NormPoint(0.2, 0.5), new NormPoint(0.8, 0.5)], 0.1);
        var outBmp = R.BurnIn(src, new ConfirmedMasks([], [stroke])).Bitmap;
        const double r = 5;
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 200; x++)
        {
            double cx = x + 0.5, cy = y + 0.5;
            var t = Math.Clamp((cx - 40) / 120, 0, 1);
            var d = Math.Sqrt(Math.Pow(cx - (40 + 120 * t), 2) + Math.Pow(cy - 50, 2));
            if (d <= r) Assert.True(IsBlack(outBmp, x, y), $"カプセル内 ({x},{y}) d={d:F2} が黒でない");
            if (d > r + 1.5) Assert.Equal(src.GetPixel(x, y), outBmp.GetPixel(x, y));
        }
        // 丸端: 始点の左 4.5px（線分の外）も塗られている
        Assert.True(IsBlack(outBmp, 35, 49));
        Assert.True(IsBlack(outBmp, 164, 50));
    }

    [Fact]
    public void Stroke_SinglePoint_IsDisc()
    {
        var outBmp = R.BurnIn(Noise(100, 100), new ConfirmedMasks([], [new BrushStroke([new NormPoint(0.5, 0.5)], 0.2)])).Bitmap;
        Assert.True(IsBlack(outBmp, 50, 50));
        Assert.True(IsBlack(outBmp, 50, 41)); // 半径 10
        Assert.True(IsBlack(outBmp, 41, 50));
        Assert.False(IsBlack(outBmp, 50, 37));
    }

    [Fact]
    public void Stroke_TinyWidth_StillPaintsAtLeastAPixel_AndEdgesClip()
    {
        var src = Noise(50, 50);
        var outBmp = R.BurnIn(src, new ConfirmedMasks([], [
            new BrushStroke([new NormPoint(0.5, 0.5), new NormPoint(0.5, 0.5)], 0.0001),
            new BrushStroke([new NormPoint(-0.2, 0), new NormPoint(1.3, 1.2)], 0.1),
        ])).Bitmap;
        Assert.True(IsBlack(outBmp, 25, 25));
        Assert.True(IsBlack(outBmp, 0, 0));
        Assert.True(IsBlack(outBmp, 49, 49));
    }

    [Fact]
    public void NoMasks_ReturnsByteIdenticalCopyInNewBuffer()
    {
        var src = Noise(30, 20);
        var outBmp = R.BurnIn(src, ConfirmedMasks.None).Bitmap;
        Assert.NotSame(src.Pixels, outBmp.Pixels);
        Assert.Equal(src.Pixels, outBmp.Pixels);
    }
}
