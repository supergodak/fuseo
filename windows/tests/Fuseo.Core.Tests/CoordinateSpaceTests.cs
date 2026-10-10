using Fuseo.Core.Geometry;
using Xunit;

namespace Fuseo.Core.Tests;

public class CoordinateSpaceTests
{
    [Fact]
    public void ToPixel_RoundsOutward_SoMaskNeverLosesAPixel()
    {
        // 0.101*1000=101.0 → 101、0.2049*1000=204.9 → 切り上げて 205（外側へ）
        var px = CoordinateSpace.ToPixel(new NormRect(0.101, 0.1009, 0.1039, 0.05), 1000, 1000);
        Assert.Equal(101, px.X);
        Assert.Equal(100, px.Y);        // 100.9 → floor 100
        Assert.Equal(205, px.Right);    // 204.9 → ceil 205
        Assert.Equal(151, px.Bottom);   // 150.9 → ceil 151
    }

    [Fact]
    public void ToPixel_ClampsToImage()
    {
        var px = CoordinateSpace.ToPixel(new NormRect(-0.2, 0.9, 0.5, 0.5), 200, 100);
        Assert.Equal(new PixelRect(0, 90, 60, 10), px);
    }

    [Fact]
    public void TopOrigin_YIncreasesDownward()
    {
        // 左上原点: Y=0 は画像の最上段。上端の帯は最初の行から始まる（Mac 版とは逆）
        var px = CoordinateSpace.ToPixel(new NormRect(0, 0, 1, 0.1), 10, 100);
        Assert.Equal(0, px.Y);
        Assert.Equal(10, px.Height);
    }

    [Fact]
    public void ViewRoundTrip_WithLetterbox()
    {
        // 横長画像（2:1）を正方形の表示領域へ → 上下に余白
        var view = CoordinateSpace.ToView(new NormRect(0.25, 0.5, 0.5, 0.25), 2000, 1000, 400, 400);
        Assert.Equal(100, view.X, 6);
        Assert.Equal(100 + 0.5 * 200, view.Y, 6);  // 余白 100 + 0.5*高さ200
        Assert.Equal(200, view.W, 6);
        Assert.Equal(50, view.H, 6);

        var p = CoordinateSpace.FromView(view.X, view.Y, 2000, 1000, 400, 400);
        Assert.Equal(0.25, p.X, 6);
        Assert.Equal(0.5, p.Y, 6);
    }

    [Fact]
    public void FromView_ClampsOutsideImage()
    {
        var p = CoordinateSpace.FromView(-50, 10, 2000, 1000, 400, 400);  // 余白（上）の中・左外
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void FromPoints_OrderIndependent_AndPaddedClamps()
    {
        var r = NormRect.FromPoints(new NormPoint(0.6, 0.8), new NormPoint(0.2, 0.3));
        Assert.Equal(new NormRect(0.2, 0.3, 0.6 - 0.2, 0.8 - 0.3), r);
        var padded = new NormRect(0.005, 0.5, 0.1, 0.1).Padded(0.01);
        Assert.Equal(0, padded.X);
        Assert.Equal(0.49, padded.Y, 9);
    }

    [Fact]
    public void Intersects_PartialOverlapCounts()
    {
        var a = new NormRect(0.1, 0.1, 0.3, 0.3);
        Assert.True(a.Intersects(new NormRect(0.35, 0.35, 0.2, 0.2)));
        Assert.False(a.Intersects(new NormRect(0.4, 0.1, 0.1, 0.1)));  // 辺で接するだけは交差しない
    }
}
