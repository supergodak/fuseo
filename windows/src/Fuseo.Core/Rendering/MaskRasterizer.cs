using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.Core.Rendering;

/// <summary>
/// 焼き込み（docs/windows-design.md §4.1・Fable 所管）。
/// - 元画像を**新しいバッファへ複製**してから塗る（元のバッファには触らない）。
/// - マスク画素は #000000FF の**直接書き込み**。描画 API・α合成・アンチエイリアスを通さない＝半透明の縁ができない。
/// - 矩形は <see cref="CoordinateSpace.ToPixel"/>（外側へ切り上げ）で画素化する。
/// - ブラシは丸端の太線（カプセルの和集合）。画素の中心がカプセルから半対角線（√2/2 px）以内なら塗る
///   ＝画素の正方形がカプセルに少しでも掛かれば塗る、という安全側の判定（1px も欠けない）。
///   設計当初の「WPF DrawingVisual を Aliased で描く」は Core を WPF 非依存に保つため採らない（決定 2026-10-11）。
/// </summary>
public sealed class MaskRasterizer : IMaskRasterizer
{
    /// <summary>画素の正方形が判定円に掛かるかを中心距離で測るための余裕（半対角線）。</summary>
    private const double HalfDiagonal = 0.70710678118654757;

    public BurnedPage BurnIn(PageBitmap page, ConfirmedMasks masks)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(masks);
        var outBmp = page.Clone();
        foreach (var r in masks.Rects) FillRect(outBmp, CoordinateSpace.ToPixel(r, outBmp.Width, outBmp.Height));
        foreach (var s in masks.Strokes) FillStroke(outBmp, s);
        return new BurnedPage(outBmp);
    }

    private static void FillRect(PageBitmap bmp, PixelRect pr)
    {
        if (pr.IsEmpty) return;
        var px = bmp.Pixels;
        for (var y = pr.Y; y < pr.Bottom; y++)
        {
            var row = y * bmp.Stride;
            for (var x = pr.X; x < pr.Right; x++) Black(px, row + x * 4);
        }
    }

    private static void FillStroke(PageBitmap bmp, BrushStroke stroke)
    {
        if (stroke.Points.Count == 0 || !(stroke.Width > 0)) return;
        double w = bmp.Width, h = bmp.Height;
        var radius = Math.Max(0.5, stroke.Width * Math.Min(w, h) / 2);
        var reach = radius + HalfDiagonal;
        var reach2 = reach * reach;

        // 画素座標の点列（画素の中心が x+0.5 の座標系）。1 点だけなら長さ 0 の線分＝円。
        var pts = stroke.Points.Select(p => (X: Math.Clamp(p.X, 0, 1) * w, Y: Math.Clamp(p.Y, 0, 1) * h)).ToArray();
        var segCount = Math.Max(1, pts.Length - 1);
        for (var i = 0; i < segCount; i++)
        {
            var a = pts[i];
            var b = pts[Math.Min(i + 1, pts.Length - 1)];
            var x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - reach));
            var y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - reach));
            var x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + reach));
            var y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + reach));
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            var px = bmp.Pixels;
            for (var y = y0; y <= y1; y++)
            {
                var cy = y + 0.5;
                var row = y * bmp.Stride;
                for (var x = x0; x <= x1; x++)
                {
                    var cx = x + 0.5;
                    var t = len2 == 0 ? 0 : Math.Clamp(((cx - a.X) * dx + (cy - a.Y) * dy) / len2, 0, 1);
                    var ex = cx - (a.X + t * dx);
                    var ey = cy - (a.Y + t * dy);
                    if (ex * ex + ey * ey <= reach2) Black(px, row + x * 4);
                }
            }
        }
    }

    private static void Black(byte[] px, int i)
    {
        px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255;
    }
}
