using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>
/// 手動 4 隅切り抜き（W4.5）の台形補正。核心は 2 つ:
/// 1. 四隅が出力の四隅に写る（写像の向き・左上原点を取り違えない）。
/// 2. 「回してから切り抜く」と「切り抜いてから回す」が同じ画像になる（回転と切り抜きを重ねても位置がずれない）。
/// </summary>
public class PerspectiveTests
{
    private static readonly Quad Skewed = new(new(0.12, 0.08), new(0.91, 0.15), new(0.83, 0.94), new(0.05, 0.81));

    [Fact]
    public void SquareToQuad_MapsUnitCornersToQuadCorners()
    {
        foreach (var q in new[] { Skewed, Quad.FullImage, new Quad(new(0.1, 0.1), new(0.6, 0.2), new(0.7, 0.7), new(0.2, 0.6)) })
        {
            const int w = 300, h = 200;
            var m = Perspective.SquareToQuad(q, w, h);
            var expected = q.Corners;
            var uv = new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) };
            for (var k = 0; k < 4; k++)
            {
                var (x, y) = m.Map(uv[k].Item1, uv[k].Item2);
                Assert.Equal(expected[k].X * w, x, 9);
                Assert.Equal(expected[k].Y * h, y, 9);
            }
        }
    }

    [Fact]
    public void FullImage_ReturnsSameBytes()
    {
        var src = MaskRasterizerTests.Noise(37, 23);
        var dst = Perspective.Warp(src, Quad.FullImage);
        Assert.Equal((37, 23), (dst.Width, dst.Height));
        Assert.Equal(src.Pixels, dst.Pixels);
        Assert.NotSame(src.Pixels, dst.Pixels);
    }

    [Fact]
    public void AxisAlignedQuad_IsPlainCrop()
    {
        // 画素の境目に揃った長方形は、ただの切り出しと 1 バイトも違わない（補間が混ざらない）。
        var src = MaskRasterizerTests.Noise(40, 50);
        var q = new Quad(new(0.25, 0.2), new(0.75, 0.2), new(0.75, 0.6), new(0.25, 0.6));
        var dst = Perspective.Warp(src, q);
        Assert.Equal((20, 20), (dst.Width, dst.Height));
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 20; x++)
            Assert.Equal(src.GetPixel(10 + x, 10 + y), dst.GetPixel(x, y));
    }

    [Fact]
    public void Corners_LandOnOutputCorners()
    {
        // 四隅に色の違う印を置くと、出力の四隅にその色が来る（TL→左上、TR→右上…）。
        const int w = 400, h = 300;
        var src = PageBitmap.Solid(w, h, 255, 255, 255);
        var colors = new (byte B, byte G, byte R)[] { (0, 0, 255), (0, 255, 0), (255, 0, 0), (0, 255, 255) };
        var corners = Skewed.Corners;
        for (var k = 0; k < 4; k++)
        {
            var cx = (int)(corners[k].X * w);
            var cy = (int)(corners[k].Y * h);
            for (var y = cy - 6; y <= cy + 6; y++)
            for (var x = cx - 6; x <= cx + 6; x++)
                src.SetPixel(x, y, colors[k].B, colors[k].G, colors[k].R, 255);
        }
        var dst = Perspective.Warp(src, Skewed);
        var outCorners = new[] { (0, 0), (dst.Width - 1, 0), (dst.Width - 1, dst.Height - 1), (0, dst.Height - 1) };
        for (var k = 0; k < 4; k++)
        {
            var (b, g, r, _) = dst.GetPixel(outCorners[k].Item1, outCorners[k].Item2);
            Assert.Equal(colors[k], (b, g, r));
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RotateThenWarp_EqualsWarpThenRotate(int turns)
    {
        var src = Smooth(157, 113);
        var a = QuarterTurn.Rotate(Perspective.Warp(src, Skewed), turns);
        var b = Perspective.Warp(QuarterTurn.Rotate(src, turns), Quad.Rotate(Skewed, turns));
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        for (var i = 0; i < a.Pixels.Length; i++)
            Assert.True(Math.Abs(a.Pixels[i] - b.Pixels[i]) <= 1, $"画素値の差が 1 を超える at byte {i}");
    }

    [Fact]
    public void QuadRotate_FourTurnsIsIdentity_AndFullImageStaysFull()
    {
        var q = Skewed;
        for (var i = 0; i < 4; i++) q = Quad.Rotate(q, 1);
        for (var k = 0; k < 4; k++)
        {
            Assert.Equal(Skewed.Corners[k].X, q.Corners[k].X, 12);
            Assert.Equal(Skewed.Corners[k].Y, q.Corners[k].Y, 12);
        }
        Assert.True(Quad.Rotate(Quad.FullImage, 1).IsFullImage);
        Assert.True(Quad.Rotate(Skewed, 3).IsConvex);
        Assert.Equal(Quad.Rotate(Skewed, -1), Quad.Rotate(Skewed, 3));
    }

    [Fact]
    public void IsConvex_RejectsCrossedCollapsedAndReversed()
    {
        Assert.True(Quad.FullImage.IsConvex);
        Assert.True(Skewed.IsConvex);
        // TR と BR を入れ替えた＝辺が交差
        Assert.False(new Quad(new(0, 0), new(1, 1), new(1, 0), new(0, 1)).IsConvex);
        // 反時計回り（裏返し）
        Assert.False(new Quad(new(0, 0), new(0, 1), new(1, 1), new(1, 0)).IsConvex);
        // 3 点が一直線
        Assert.False(new Quad(new(0, 0), new(0.5, 0), new(1, 0), new(0, 1)).IsConvex);
        // 全点が同じ
        Assert.False(new Quad(new(0.5, 0.5), new(0.5, 0.5), new(0.5, 0.5), new(0.5, 0.5)).IsConvex);
        Assert.False(new Quad(new(double.NaN, 0), new(1, 0), new(1, 1), new(0, 1)).IsConvex);
        // へこんだ四角形（BR が内側に入り込む）
        Assert.False(new Quad(new(0, 0), new(1, 0), new(0.3, 0.3), new(0, 1)).IsConvex);
        Assert.Throws<ArgumentException>(() =>
            Perspective.Warp(PageBitmap.Solid(4, 4, 0, 0, 0), new Quad(new(0, 0), new(1, 1), new(1, 0), new(0, 1))));
    }

    [Fact]
    public void OutputSize_UsesLongerEdges_AndCapsLongSide()
    {
        // 上辺 80px・下辺 100px・左辺 50px・右辺 √(20²+50²)≈53.9px → 100x54（長い方を取る）
        var q = new Quad(new(0.0, 0.0), new(0.8, 0.0), new(1.0, 0.5), new(0.0, 0.5));
        Assert.Equal((100, 54), Perspective.OutputSize(q, 100, 100));
        Assert.Equal((4000, 2154), Perspective.OutputSize(q, 10000, 10000));
        Assert.Equal((800, 431), Perspective.OutputSize(q, 10000, 10000, maxLongSide: 800));
    }

    /// <summary>なめらかな模様（補間の丸めが 1 を超えて揺れないように、乱数ではなく緩やかな勾配）。</summary>
    private static PageBitmap Smooth(int w, int h)
    {
        var b = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            b.SetPixel(x, y, (byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), (byte)((x + y) % 256), 255);
        return b;
    }
}
