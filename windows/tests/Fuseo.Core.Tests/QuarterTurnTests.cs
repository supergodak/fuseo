using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>
/// §3.3 の 90° 回転。核心は「回してから塗る」と「塗ってから回す」が同じ画素を黒にすること
/// （＝画像とマスクを一緒に回せば、塗る位置がずれない）。浮動小数の誤差は外側に 1px までしか許さない。
/// </summary>
public class QuarterTurnTests
{
    private static readonly MaskRasterizer R = new();

    private static bool IsBlack(PageBitmap b, int x, int y) => b.GetPixel(x, y) == (0, 0, 0, 255);

    [Theory]
    [InlineData(0, 0)] [InlineData(4, 0)] [InlineData(5, 1)] [InlineData(-1, 3)] [InlineData(-6, 2)]
    public void Normalize_WrapsBothDirections(int turns, int expected) =>
        Assert.Equal(expected, QuarterTurn.Normalize(turns));

    [Fact]
    public void Clockwise_MovesTopLeftCornerToTopRight()
    {
        // 左上の小さな印は、時計回り 90° で右上へ行く。
        var src = PageBitmap.Solid(4, 2, 255, 255, 255);
        src.SetPixel(0, 0, 0, 0, 255, 255); // 左上を赤
        var dst = QuarterTurn.Rotate(src, 1);
        Assert.Equal((2, 4), (dst.Width, dst.Height));
        Assert.Equal((0, 0, 255, 255), dst.GetPixel(1, 0));
        Assert.Equal(new NormPoint(1, 0), QuarterTurn.Rotate(new NormPoint(0, 0), 1));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FourTurns_ReturnToSourceBytes(int step)
    {
        var src = MaskRasterizerTests.Noise(37, 23);
        var b = src;
        for (var i = 0; i < 4; i++) b = QuarterTurn.Rotate(b, step);
        Assert.Equal(src.Pixels, b.Pixels);
        Assert.Equal((37, 23), (b.Width, b.Height));
    }

    [Fact]
    public void RotateReturnsNewBuffer_SourceUntouched()
    {
        var src = MaskRasterizerTests.Noise(10, 6);
        var before = (byte[])src.Pixels.Clone();
        foreach (var t in new[] { 0, 1, 2, 3 })
        {
            var dst = QuarterTurn.Rotate(src, t);
            Assert.NotSame(src.Pixels, dst.Pixels);
        }
        Assert.Equal(before, src.Pixels);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(-1)]
    public void RectAndPointTransforms_AgreeWithPixelTransform(int turns)
    {
        // 正規化座標の変換と画素の並べ替えが同じ写像であること（画素中心で照合）。
        const int w = 30, h = 20;
        var src = MaskRasterizerTests.Noise(w, h);
        var dst = QuarterTurn.Rotate(src, turns);
        for (var y = 0; y < h; y += 3)
        for (var x = 0; x < w; x += 4)
        {
            var p = QuarterTurn.Rotate(new NormPoint((x + 0.5) / w, (y + 0.5) / h), turns);
            var dx = (int)Math.Floor(p.X * dst.Width);
            var dy = (int)Math.Floor(p.Y * dst.Height);
            Assert.Equal(src.GetPixel(x, y), dst.GetPixel(dx, dy));
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        // 端に接する矩形・1px 未満の矩形・割り切れない寸法を含める。
        var rects = new[]
        {
            new NormRect(0.1, 0.2, 0.3, 0.4), new NormRect(0, 0, 0.25, 0.1), new NormRect(0.7, 0.85, 0.3, 0.15),
            new NormRect(0.333, 0.517, 0.0011, 0.0013), new NormRect(0.2, 0.3, 0.6, 0.4),
        };
        foreach (var (w, h) in new[] { (200, 100), (157, 263), (4000, 2829) })
        foreach (var t in new[] { 1, 2, 3 })
            yield return [w, h, t, rects];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void BurnThenRotate_EqualsRotateThenBurn_ForRects(int w, int h, int turns, NormRect[] rects)
    {
        // 正解 = decimal（誤差なし）で求めた元の向きの画素矩形を、画素の並べ替えで回したもの。
        // 「塗ってから回す」「回してから塗る」のどちらも正解を欠けなく覆い、はみ出しは 1px 以内。
        var src = PageBitmap.Solid(w, h, 255, 255, 255);
        var exact = PageBitmap.Solid(w, h, 255, 255, 255);
        foreach (var r in rects)
        {
            var (x0, y0, x1, y1) = ExactPixel(r, w, h);
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++) exact.SetPixel(x, y, 0, 0, 0, 255);
        }
        var truth = QuarterTurn.Rotate(exact, turns);
        var a = QuarterTurn.Rotate(R.BurnIn(src, new ConfirmedMasks(rects, [])).Bitmap, turns);
        var b = R.BurnIn(QuarterTurn.Rotate(src, turns),
            new ConfirmedMasks(rects.Select(r => QuarterTurn.Rotate(r, turns)).ToList(), [])).Bitmap;
        AssertCoversWithin1px(truth, a);
        AssertCoversWithin1px(truth, b);
    }

    private static (int X0, int Y0, int X1, int Y1) ExactPixel(NormRect r, int w, int h)
    {
        decimal X = (decimal)r.X, Y = (decimal)r.Y, W = (decimal)r.W, H = (decimal)r.H;
        return ((int)decimal.Floor(X * w), (int)decimal.Floor(Y * h),
                (int)decimal.Ceiling(Math.Min(1m, X + W) * w), (int)decimal.Ceiling(Math.Min(1m, Y + H) * h));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BurnThenRotate_EqualsRotateThenBurn_ForStrokes(int turns)
    {
        const int w = 301, h = 177;
        var stroke = new BrushStroke([new(0.05, 0.1), new(0.4, 0.35), new(0.42, 0.9), new(0.97, 0.6)], 0.03);
        var dot = new BrushStroke([new(0.8, 0.15)], 0.01);
        var src = PageBitmap.Solid(w, h, 255, 255, 255);
        var a = QuarterTurn.Rotate(R.BurnIn(src, new ConfirmedMasks([], [stroke, dot])).Bitmap, turns);
        var b = R.BurnIn(QuarterTurn.Rotate(src, turns),
            new ConfirmedMasks([], [QuarterTurn.Rotate(stroke, turns), QuarterTurn.Rotate(dot, turns)])).Bitmap;
        AssertCoversWithin1px(a, b);
        AssertCoversWithin1px(b, a);
        Assert.Equal(stroke.Width, QuarterTurn.Rotate(stroke, turns).Width);
    }

    [Fact]
    public void ManualMaskAndCandidate_RotateEveryCoordinate()
    {
        var m = new ManualMask
        {
            Rects = [new NormRect(0.1, 0.2, 0.3, 0.4)],
            Strokes = [new BrushStroke([new(0.1, 0.1), new(0.5, 0.2)], 0.02)],
        };
        var r = QuarterTurn.Rotate(m, 1);
        Assert.Equal(new NormRect(0.4, 0.1, 0.4, 0.3), Round(r.Rects[0]));
        Assert.Equal(new NormPoint(0.9, 0.1), r.Strokes[0].Points[0]);
        Assert.Equal(new NormPoint(0.8, 0.5), r.Strokes[0].Points[1]);
        Assert.Same(m, QuarterTurn.Rotate(m, 4));

        var c = new MaskCandidate(Guid.NewGuid(), "qr", "QR", new NormRect(0, 0, 0.2, 0.1),
            CandidateSource.Detector, DetectorId.QrBarcode, null, true, "basis");
        var rc = QuarterTurn.Rotate(c, 1);
        Assert.Equal(new NormRect(0.9, 0, 0.1, 0.2), Round(rc.Box));
        Assert.Equal(c with { Box = rc.Box }, rc); // 箱以外（Id・IsOn など）は変えない
    }

    private static NormRect Round(NormRect r) =>
        new(Math.Round(r.X, 12), Math.Round(r.Y, 12), Math.Round(r.W, 12), Math.Round(r.H, 12));

    /// <summary>
    /// b は a の黒を**すべて**含み（欠けは 0）、a より多く塗った画素は a の黒から 1px 以内
    /// （浮動小数の誤差が外側に出た分だけ）。</summary>
    private static void AssertCoversWithin1px(PageBitmap a, PageBitmap b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var ka = IsBlack(a, x, y);
            var kb = IsBlack(b, x, y);
            if (ka) Assert.True(kb, $"欠け at ({x},{y})");
            else if (kb) Assert.True(NearBlack(a, x, y), $"1px を超える塗り増し at ({x},{y})");
        }
    }

    private static bool NearBlack(PageBitmap a, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && ny >= 0 && nx < a.Width && ny < a.Height && IsBlack(a, nx, ny)) return true;
        }
        return false;
    }
}
