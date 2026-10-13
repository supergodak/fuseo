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
/// - 1D: 点はほぼ 1 本の線上。線の長さ L として、線方向に L×<see cref="LinearGrowAlong"/> ずつ外へ、
///   線と直交する方向に中心から ±L×<see cref="LinearHalfHeight"/>。
/// 大きな画像で取りこぼすことがあるので、原寸と長辺 <see cref="DownscaledLongSide"/>px への縮小版の両方で探し、
/// 重なる箱（IoU &gt; 0.3 か、片方が片方をほぼ含む）は和集合 1 つにまとめる。
/// 通信は一切しない（ZXing.Net はデコードのみ）。
/// </summary>
public sealed class QrDetector : IDetector
{
    public const double QrGrow = 0.30;
    public const double TwoDGrow = 0.12;
    public const double LinearGrowAlong = 0.08;
    public const double LinearHalfHeight = 0.25;
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
                if (BoxFor(r.BarcodeFormat, r.ResultPoints, bmp.Width, bmp.Height) is { } box) yield return box;
            }
        }
    }

    private static readonly IList<BarcodeFormat> QrOnly = [BarcodeFormat.QR_CODE];

    /// <summary>ResultPoints（画素座標）から記号全体を覆う正規化矩形を作る。点が足りなければ null。</summary>
    internal static NormRect? BoxFor(BarcodeFormat format, ResultPoint[]? points, int width, int height)
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
            var half = len * LinearHalfHeight;
            double ax = a.X - ux * along, ay = a.Y - uy * along;
            double bx = b.X + ux * along, by = b.Y + uy * along;
            double[] xs = [ax + nx * half, ax - nx * half, bx + nx * half, bx - nx * half];
            double[] ys = [ay + ny * half, ay - ny * half, by + ny * half, by - ny * half];
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
