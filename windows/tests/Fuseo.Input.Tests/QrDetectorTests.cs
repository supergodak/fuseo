using System.Diagnostics;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Input.Detection;
using Xunit;
using Xunit.Abstractions;
using ZXing;
using ZXing.Common;

namespace Fuseo.Input.Tests;

/// <summary>
/// QR／バーコード検出（W4）。記号は ZXing の符号化器で作った <see cref="BitMatrix"/> を自前で画素に描く（合成のみ・実物の書類は使わない）。
/// 「描いた記号の全モジュールを含む矩形（クワイエットゾーン除く）」を 1px も欠けずに覆うこと＝安全側の箱であることを固定する。
/// </summary>
public class QrDetectorTests(ITestOutputHelper output)
{
    /// <summary>描いた記号（画素矩形・左上原点・右端/下端は含まない）。</summary>
    internal readonly record struct Drawn(int X, int Y, int W, int H)
    {
        public NormRect Norm(int width, int height) => new((double)X / width, (double)Y / height, (double)W / width, (double)H / height);
    }

    internal static BitMatrix Encode(BarcodeFormat format, string content)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        return new MultiFormatWriter().encode(content, format, 0, 0, hints);
    }

    /// <summary>2 次元コードを 1 モジュール = scale px で (x, y) に描く。</summary>
    internal static Drawn DrawMatrix(PageBitmap bmp, BitMatrix m, int x, int y, int scale)
    {
        for (var my = 0; my < m.Height; my++)
            for (var mx = 0; mx < m.Width; mx++)
                if (m[mx, my]) Fill(bmp, x + mx * scale, y + my * scale, scale, scale);
        return new Drawn(x, y, m.Width * scale, m.Height * scale);
    }

    /// <summary>1 次元バーコード（高さ 1 の BitMatrix）を幅 scale px・高さ barHeight px のバーで描く。</summary>
    internal static Drawn DrawLinear(PageBitmap bmp, BitMatrix m, int x, int y, int scale, int barHeight)
    {
        int first = int.MaxValue, last = -1;
        for (var mx = 0; mx < m.Width; mx++)
        {
            if (!m[mx, 0]) continue;
            Fill(bmp, x + mx * scale, y, scale, barHeight);
            first = Math.Min(first, mx);
            last = mx;
        }
        return new Drawn(x + first * scale, y, (last - first + 1) * scale, barHeight);
    }

    private static void Fill(PageBitmap bmp, int x, int y, int w, int h)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++) bmp.SetPixel(xx, yy, 0, 0, 0, 255);
    }

    internal static PageBitmap White(int w, int h) => PageBitmap.Solid(w, h, 255, 255, 255);

    /// <summary>box（正規化）が drawn（画素）を完全に覆い、面積が 3 倍以下であること。</summary>
    private void AssertCovers(NormRect box, Drawn drawn, int width, int height, double maxAreaRatio = 3.0)
    {
        var px = CoordinateSpace.ToPixel(box, width, height);
        output.WriteLine($"  box px=({px.X},{px.Y},{px.Width}x{px.Height}) symbol=({drawn.X},{drawn.Y},{drawn.W}x{drawn.H})");
        // 正規化座標の段階で判定する（ToPixel は外側へ切り上げるので、ここで欠けていないことを厳密に見る）。
        Assert.True(box.X * width <= drawn.X + 1e-6, $"左が欠けている: {box.X * width} > {drawn.X}");
        Assert.True(box.Y * height <= drawn.Y + 1e-6, $"上が欠けている: {box.Y * height} > {drawn.Y}");
        Assert.True(box.Right * width >= drawn.X + drawn.W - 1e-6, $"右が欠けている: {box.Right * width} < {drawn.X + drawn.W}");
        Assert.True(box.Bottom * height >= drawn.Y + drawn.H - 1e-6, $"下が欠けている: {box.Bottom * height} < {drawn.Y + drawn.H}");
        var ratio = box.W * width * box.H * height / ((double)drawn.W * drawn.H);
        output.WriteLine($"  area ratio={ratio:0.00}");
        Assert.True(ratio <= maxAreaRatio, $"箱が大きすぎる: 面積比 {ratio:0.00}");
    }

    private IReadOnlyList<NormRect> TimedDetect(PageBitmap bmp, string label)
    {
        var sw = Stopwatch.StartNew();
        var boxes = new QrDetector().Detect(bmp);
        sw.Stop();
        output.WriteLine($"{label}: {bmp.Width}x{bmp.Height} → {boxes.Count} 件 / {sw.ElapsedMilliseconds} ms");
        return boxes;
    }

    /// <summary>(画像, 記号) を turns だけ回したもの。記号の矩形も同じ回数だけ回す。</summary>
    private static (PageBitmap Bmp, Drawn Symbol) Rotated(PageBitmap bmp, Drawn d, int turns)
    {
        var rb = QuarterTurn.Rotate(bmp, turns);
        var r = QuarterTurn.Rotate(d.Norm(bmp.Width, bmp.Height), turns);
        var p = CoordinateSpace.ToPixel(r, rb.Width, rb.Height);
        return (rb, new Drawn(p.X, p.Y, p.Width, p.Height));
    }

    private static (PageBitmap Bmp, Drawn Symbol) QrScene()
    {
        var bmp = White(900, 600);
        var d = DrawMatrix(bmp, Encode(BarcodeFormat.QR_CODE, "FUSEO-SYNTHETIC-QR-0001"), 520, 140, 8);
        return (bmp, d);
    }

    private static (PageBitmap Bmp, Drawn Symbol) Code128Scene()
    {
        var bmp = White(1000, 500);
        var m = Encode(BarcodeFormat.CODE_128, "FUSEO-SYNTH-BARCODE-42");
        // 高さは線の長さの 2 割程度（一般的な横長バーコード）。
        var height = (int)(m.Width * 3 * 0.2);
        var d = DrawLinear(bmp, m, 120, 180, 3, height);
        return (bmp, d);
    }

    [Fact]
    public void Id_IsQrBarcode() => Assert.Equal(DetectorId.QrBarcode, new QrDetector().Id);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Qr_BoxCoversWholeSymbol_InEveryQuarterTurn(int turns)
    {
        var (bmp0, d0) = QrScene();
        var (bmp, d) = Rotated(bmp0, d0, turns);
        var box = Assert.Single(TimedDetect(bmp, $"QR turns={turns}"));
        AssertCovers(box, d, bmp.Width, bmp.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Code128_BoxCoversWholeSymbol_InEveryQuarterTurn(int turns)
    {
        var (bmp0, d0) = Code128Scene();
        var (bmp, d) = Rotated(bmp0, d0, turns);
        var box = Assert.Single(TimedDetect(bmp, $"Code128 turns={turns}"));
        AssertCovers(box, d, bmp.Width, bmp.Height);
    }

    [Theory]
    [InlineData(700, 250)]
    [InlineData(500, 250)]
    public void Version1Qr_IsDetected(int x, int y)
    {
        // ZXing の汎用経路では読めず、QR 専用の経路でだけ読める合成 QR（version 1・21 モジュール）。2 回読む理由の回帰テスト。
        var bmp = White(1200, 800);
        var m = Encode(BarcodeFormat.QR_CODE, "FUSEO-W4-SYNTHETIC");
        Assert.Equal(21, m.Width);
        var d = DrawMatrix(bmp, m, x, y, 8);
        var box = Assert.Single(TimedDetect(bmp, $"QR v1 @({x},{y})"));
        AssertCovers(box, d, bmp.Width, bmp.Height);
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    [InlineData(BarcodeFormat.PDF_417)]
    public void OtherTwoD_BoxCoversWholeSymbol(BarcodeFormat format)
    {
        // ZXing の DATA_MATRIX / AZTEC 検出器は画像の中心から探すため、中心に掛かっていない記号は見つけられない
        // （既知の制約・ベストエフォート）。ここでは中心に置いて「見つかれば記号全体を覆う」ことだけを固定する。
        var bmp = White(900, 700);
        var m = Encode(format, "FUSEO-2D-0001");
        var d = DrawMatrix(bmp, m, 450 - m.Width * 6 / 2, 350 - m.Height * 6 / 2, 6);
        var box = Assert.Single(TimedDetect(bmp, format.ToString()));
        AssertCovers(box, d, bmp.Width, bmp.Height, maxAreaRatio: 3.0);
    }

    [Theory]
    [InlineData(10)]   // 小さな QR（記号 ≒ 250px）
    [InlineData(48)]   // 大きな QR（記号 ≒ 1200px）
    public void LargePage_DetectsSmallAndLargeQr(int scale)
    {
        var bmp = White(4000, 2800);
        var d = DrawMatrix(bmp, Encode(BarcodeFormat.QR_CODE, "FUSEO-LARGE-PAGE"), 2300, 900, scale);
        var box = Assert.Single(TimedDetect(bmp, $"4000x2800 QR scale={scale}"));
        AssertCovers(box, d, bmp.Width, bmp.Height);
    }

    [Fact]
    public void LargePage_WithoutCode_TimingIsLogged()
    {
        // 何も無い最大サイズのページ（検出が最も粘る＝最悪に近い所要時間）。
        var bmp = White(4000, 2800);
        Assert.Empty(TimedDetect(bmp, "4000x2800 白紙"));
    }

    [Fact]
    public void NoCode_BlankAndNoise_ReturnNothing()
    {
        Assert.Empty(TimedDetect(White(1200, 800), "白紙"));
        var noise = new PageBitmap(1200, 800);
        var rng = new Random(20261013);
        rng.NextBytes(noise.Pixels);
        for (var i = 3; i < noise.Pixels.Length; i += 4) noise.Pixels[i] = 255;
        Assert.Empty(TimedDetect(noise, "ノイズ"));
    }

    [Fact]
    public void TwoQrCodes_AreTwoBoxes()
    {
        var bmp = White(1600, 900);
        var a = DrawMatrix(bmp, Encode(BarcodeFormat.QR_CODE, "FUSEO-A"), 150, 200, 9);
        var b = DrawMatrix(bmp, Encode(BarcodeFormat.QR_CODE, "FUSEO-B-SECOND"), 1050, 350, 9);
        var boxes = TimedDetect(bmp, "QR×2").OrderBy(r => r.X).ToList();
        Assert.Equal(2, boxes.Count);
        AssertCovers(boxes[0], a, bmp.Width, bmp.Height);
        AssertCovers(boxes[1], b, bmp.Width, bmp.Height);
    }

    [Fact]
    public void Merge_UnitesOverlappingAndContainedBoxes()
    {
        var a = new NormRect(0.10, 0.10, 0.20, 0.20);
        var b = new NormRect(0.12, 0.11, 0.20, 0.20);   // IoU > 0.3
        var c = new NormRect(0.60, 0.60, 0.30, 0.30);
        var d = new NormRect(0.62, 0.62, 0.05, 0.05);   // c にほぼ含まれる
        var e = new NormRect(0.00, 0.80, 0.05, 0.05);   // 離れている
        var merged = QrDetector.Merge([a, b, c, d, e]).OrderBy(r => r.X).ToList();
        Assert.Equal(3, merged.Count);
        Assert.Equal(e, merged[0]);
        Assert.Equal(0.10, merged[1].X, 9);
        Assert.Equal(0.32, merged[1].Right, 9);
        Assert.Equal(0.31, merged[1].Bottom, 9);
        Assert.Equal(c.X, merged[2].X, 9);
        Assert.Equal(c.Right, merged[2].Right, 9);
        Assert.Equal(c.Bottom, merged[2].Bottom, 9);
    }
}
