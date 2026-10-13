using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using ZXing;

namespace Fuseo.Input.Detection;

/// <summary>
/// QR コード・2 次元コード・1 次元バーコードの検出（W4・v0.1 で唯一の動的検出）。ZXing.Net を使う。
///
/// 返す箱は**左上原点の正規化座標・padding 前**。候補にするときに呼び出し側がルールの padding を足す。
/// ZXing の ResultPoints は記号の外枠ではないので、種類ごとに外へ広げて**記号全体を覆う**箱にする（Fable 決定・安全側）:
/// - QR: 点はファインダーパターンの中心。外接矩形を「幅・高さの大きい方 × <see cref="QrGrow"/>」だけ四方へ広げる。
/// - DATA_MATRIX / AZTEC / PDF_417: 外接矩形を「大きい方 × <see cref="TwoDGrow"/>」だけ四方へ広げる。
/// - 1D: 点はほぼ 1 本の線上（開始・終了パターンの**中心**を結ぶ走査線）。線の長さ L として、
///   線方向は L×<see cref="LinearGrowAlong"/> ずつ外へ（最短の Code128 でも端のパターン半分＋余裕を覆う量）。
///   線と直交する方向は**画像を見てバーが続く範囲を測る**（走査線はバーの中央とは限らないため、比率で決め打ちしない）:
///   走査線上の明暗の並びを基準に、1px ずつ外へずらして同じ並びが続く限り伸ばし、そこから L×<see cref="LinearMargin"/>
///   （最低 <see cref="LinearMarginMinPx"/>px）を足す。並びが読めない（明暗差が小さい）ときは ±L×<see cref="LinearFallbackHalf"/>。
/// 大きな画像で取りこぼすことがあるので、原寸と長辺 <see cref="DownscaledLongSide"/>px への縮小版の両方で探し、
/// 重なる箱（IoU &gt; 0.3 か、片方が片方をほぼ含む）は和集合 1 つにまとめる。
/// 通信は一切しない（ZXing.Net はデコードのみ）。
/// </summary>
public sealed class QrDetector : IDetector
{
    public const double QrGrow = 0.30;
    public const double TwoDGrow = 0.12;
    public const double LinearGrowAlong = 0.25;
    public const double LinearMargin = 0.04;
    public const int LinearMarginMinPx = 3;
    public const double LinearFallbackHalf = 0.6;
    /// <summary>走査線と同じ並びとみなす一致率。</summary>
    private const double LinearMatch = 0.8;
    public const int DownscaledLongSide = 1600;

    /// <summary>「ほぼ含む」の基準（小さい方の面積に対する重なりの割合）。</summary>
    private const double ContainRatio = 0.85;
    private const double MergeIoU = 0.3;

    private static readonly IList<BarcodeFormat> Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417, BarcodeFormat.AZTEC,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.EAN_13,
        BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    ];

    public DetectorId Id => DetectorId.QrBarcode;

    public IReadOnlyList<NormRect> Detect(PageBitmap page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var boxes = new List<NormRect>(DetectAt(page));
        var small = PixelOps.DownscaleToFit(page, DownscaledLongSide);
        if (!ReferenceEquals(small, page)) boxes.AddRange(DetectAt(small));
        return Merge(boxes);
    }

    /// <summary>
    /// 1 つの解像度で検出し、正規化した箱を返す。2 回読む:
    /// 1. 全フォーマット（ZXing の汎用の複数検出）。
    /// 2. QR だけ（ZXing.Net は QR 単独指定のとき QR 専用の複数検出器を使う）。汎用の経路では読めない QR
    ///    （例: 合成した version 1 の QR）を専用の経路が読めることがあるため（2026-10-13 実測）。
    /// </summary>
    private static IEnumerable<NormRect> DetectAt(PageBitmap bmp)
    {
        var source = new RGBLuminanceSource(bmp.Pixels, bmp.Width, bmp.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        foreach (var formats in new[] { Formats, QrOnly })
        {
            // リーダーはスレッドセーフでないので呼び出しごとに作る（ページ並列で使われる）。
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options =
                {
                    TryHarder = true,
                    PossibleFormats = formats,
                },
            };
            var results = reader.DecodeMultiple(source);
            if (results is null) continue;
            foreach (var r in results)
            {
                if (BoxFor(r.BarcodeFormat, r.ResultPoints, bmp.Width, bmp.Height, bmp) is { } box) yield return box;
            }
        }
    }

    private static readonly IList<BarcodeFormat> QrOnly = [BarcodeFormat.QR_CODE];

    /// <summary>ResultPoints（画素座標）から記号全体を覆う正規化矩形を作る。点が足りなければ null。</summary>
    internal static NormRect? BoxFor(BarcodeFormat format, ResultPoint[]? points, int width, int height, PageBitmap? image = null)
    {
        var pts = points?.Where(p => p is not null && float.IsFinite(p.X) && float.IsFinite(p.Y)).ToList() ?? [];
        if (pts.Count == 0) return null;
        double x0, y0, x1, y1;
        if (IsTwoD(format))
        {
            x0 = pts.Min(p => (double)p.X); x1 = pts.Max(p => (double)p.X);
            y0 = pts.Min(p => (double)p.Y); y1 = pts.Max(p => (double)p.Y);
            var grow = Math.Max(x1 - x0, y1 - y0) * (format == BarcodeFormat.QR_CODE ? QrGrow : TwoDGrow);
            if (grow <= 0) return null;
            x0 -= grow; y0 -= grow; x1 += grow; y1 += grow;
        }
        else
        {
            // 最も離れた 2 点を線の両端とみなす（通常は 2 点）。
            var (a, b) = FarthestPair(pts);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return null;
            double ux = dx / len, uy = dy / len;   // 線方向
            double nx = -uy, ny = ux;               // 直交方向
            var along = len * LinearGrowAlong;
            var margin = Math.Max(len * LinearMargin, LinearMarginMinPx);
            var (up, down) = image is null ? ((double?)null, (double?)null) : BarExtent(image, a, b, nx, ny, len);
            var hp = up is { } u ? u + margin : len * LinearFallbackHalf;   // +n 側
            var hm = down is { } d ? d + margin : len * LinearFallbackHalf; // −n 側
            double ax = a.X - ux * along, ay = a.Y - uy * along;
            double bx = b.X + ux * along, by = b.Y + uy * along;
            double[] xs = [ax + nx * hp, ax - nx * hm, bx + nx * hp, bx - nx * hm];
            double[] ys = [ay + ny * hp, ay - ny * hm, by + ny * hp, by - ny * hm];
            x0 = xs.Min(); x1 = xs.Max(); y0 = ys.Min(); y1 = ys.Max();
        }
        // 画素座標 → 正規化（0..1 にクランプ）。
        var nx0 = Math.Clamp(x0 / width, 0, 1);
        var ny0 = Math.Clamp(y0 / height, 0, 1);
        var nx1 = Math.Clamp(x1 / width, 0, 1);
        var ny1 = Math.Clamp(y1 / height, 0, 1);
        if (nx1 <= nx0 || ny1 <= ny0) return null;
        return new NormRect(nx0, ny0, nx1 - nx0, ny1 - ny0);
    }

    /// <summary>
    /// 走査線 a→b の明暗の並びが、直交方向（±n）に何 px 続くか。並びが読めなければ (null, null)。
    /// 1 行だけ一致しない行（汚れ・印字のかすれ）は飛ばして続ける。上限は ±1.5L。
    /// </summary>
    internal static (double? Plus, double? Minus) BarExtent(PageBitmap img, ResultPoint a, ResultPoint b,
                                                            double nx, double ny, double len)
    {
        var n = Math.Clamp((int)Math.Ceiling(len), 32, 4096);
        var baseLum = Profile(img, a, b, nx, ny, 0, n);
        double min = baseLum.Min(), max = baseLum.Max();
        if (max - min < 40) return (null, null);
        var threshold = (min + max) / 2;
        var bits = baseLum.Select(v => v < threshold).ToArray();
        var limit = (int)Math.Ceiling(len * 1.5);
        int Walk(int sign)
        {
            int last = 0, misses = 0;
            for (var k = 1; k <= limit; k++)
            {
                var row = Profile(img, a, b, nx, ny, sign * k, n);
                var same = 0;
                for (var i = 0; i < n; i++) if (row[i] < threshold == bits[i]) same++;
                if ((double)same / n >= LinearMatch) { last = k; misses = 0; }
                else if (++misses >= 2) break;
            }
            return last;
        }
        return (Walk(+1), Walk(-1));
    }

    /// <summary>a→b を n 等分した点（を直交方向に offset px ずらした点）の輝度。画像外は白。</summary>
    private static double[] Profile(PageBitmap img, ResultPoint a, ResultPoint b, double nx, double ny, int offset, int n)
    {
        var lum = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = (i + 0.5) / n;
            var x = (int)Math.Floor(a.X + (b.X - a.X) * t + nx * offset);
            var y = (int)Math.Floor(a.Y + (b.Y - a.Y) * t + ny * offset);
            if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) { lum[i] = 255; continue; }
            var (bb, g, r, _) = img.GetPixel(x, y);
            lum[i] = 0.299 * r + 0.587 * g + 0.114 * bb;
        }
        return lum;
    }

    private static bool IsTwoD(BarcodeFormat f) =>
        f is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX or BarcodeFormat.AZTEC or BarcodeFormat.PDF_417;

    private static (ResultPoint A, ResultPoint B) FarthestPair(List<ResultPoint> pts)
    {
        var best = (pts[0], pts[^1]);
        var bestD = -1.0;
        for (var i = 0; i < pts.Count; i++)
            for (var j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                var d = dx * dx + dy * dy;
                if (d > bestD) { bestD = d; best = (pts[i], pts[j]); }
            }
        return best;
    }

    /// <summary>重なる箱を和集合にまとめる（まとめた結果が別の箱と重なればさらにまとめる）。</summary>
    internal static IReadOnlyList<NormRect> Merge(IEnumerable<NormRect> input)
    {
        var list = input.ToList();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < list.Count && !merged; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (!ShouldMerge(list[i], list[j])) continue;
                    list[i] = Union(list[i], list[j]);
                    list.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
        return list;
    }

    private static bool ShouldMerge(NormRect a, NormRect b)
    {
        var ix = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (ix <= 0 || iy <= 0) return false;
        var inter = ix * iy;
        double areaA = a.W * a.H, areaB = b.W * b.H;
        var union = areaA + areaB - inter;
        if (union > 0 && inter / union > MergeIoU) return true;
        var smaller = Math.Min(areaA, areaB);
        return smaller > 0 && inter / smaller >= ContainRatio;
    }

    private static NormRect Union(NormRect a, NormRect b)
    {
        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        return new NormRect(x0, y0, Math.Max(a.Right, b.Right) - x0, Math.Max(a.Bottom, b.Bottom) - y0);
    }
}
