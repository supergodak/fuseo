using Fuseo.Core.Imaging;

namespace Fuseo.Core.Geometry;

/// <summary>
/// 台形補正（手動 4 隅切り抜き・W4.5）。四隅 <see cref="Quad"/> の内側を長方形に引き伸ばした**新しい**画像を作る。
/// - 写像は単位正方形 → 四角形の射影変換（Heckbert の square-to-quad）。出力の各画素中心を元画像へ逆写像し、双線形補間で読む。
/// - 出力寸法は「上下の辺の長い方 × 左右の辺の長い方」（元画像の画素で測る）。長辺は <paramref name="maxLongSide"/> まで
///   （取り込みと同じ 4000px）。
/// - 画素の範囲外を読むときは端の画素で埋める（四隅は 0..1 に収めてある前提）。
/// - マスクはこの出力（を回した画像）の上で持つので、補間は見た目にしか効かない（塗る位置の正しさとは無関係）。
/// </summary>
public static class Perspective
{
    /// <summary>取り込みと同じ長辺の上限。</summary>
    public const int DefaultMaxLongSide = 4000;

    /// <summary>出力の寸法（元画像の画素で測った辺の長さから決める）。</summary>
    public static (int Width, int Height) OutputSize(Quad q, int srcWidth, int srcHeight, int maxLongSide = DefaultMaxLongSide)
    {
        double Len(NormPoint a, NormPoint b) =>
            Math.Sqrt(Math.Pow((b.X - a.X) * srcWidth, 2) + Math.Pow((b.Y - a.Y) * srcHeight, 2));
        var w = Math.Max(Len(q.TopLeft, q.TopRight), Len(q.BottomLeft, q.BottomRight));
        var h = Math.Max(Len(q.TopLeft, q.BottomLeft), Len(q.TopRight, q.BottomRight));
        var scale = Math.Min(1.0, maxLongSide / Math.Max(1.0, Math.Max(w, h)));
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    public static PageBitmap Warp(PageBitmap src, Quad q, int maxLongSide = DefaultMaxLongSide)
    {
        if (!q.IsConvex) throw new ArgumentException("四隅が凸四角形でない（交差・つぶれ・裏返し）", nameof(q));
        var (dw, dh) = OutputSize(q, src.Width, src.Height, maxLongSide);
        var m = SquareToQuad(q, src.Width, src.Height);
        int sw = src.Width, sh = src.Height;
        var s = src.Pixels;
        var dst = new byte[checked(dw * dh * 4)];
        Parallel.For(0, dh, j =>
        {
            var v = (j + 0.5) / dh;
            for (var i = 0; i < dw; i++)
            {
                var u = (i + 0.5) / dw;
                var den = m.G * u + m.H * v + 1;
                // 画素中心を 0.5 ずらして「画素の格子」座標にする（画素 k の中心 = k）。
                var x = Math.Clamp((m.A * u + m.B * v + m.C) / den - 0.5, 0, sw - 1);
                var y = Math.Clamp((m.D * u + m.E * v + m.F) / den - 0.5, 0, sh - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Math.Min(x0 + 1, sw - 1), y1 = Math.Min(y0 + 1, sh - 1);
                double fx = x - x0, fy = y - y0;
                int i00 = (y0 * sw + x0) * 4, i10 = (y0 * sw + x1) * 4, i01 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                var di = (j * dw + i) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var top = s[i00 + c] + (s[i10 + c] - s[i00 + c]) * fx;
                    var bottom = s[i01 + c] + (s[i11 + c] - s[i01 + c]) * fx;
                    dst[di + c] = (byte)Math.Clamp((int)(top + (bottom - top) * fy + 0.5), 0, 255);
                }
            }
        });
        return new PageBitmap(dw, dh, dst);
    }

    /// <summary>射影変換の係数: x = (A u + B v + C) / (G u + H v + 1)、y = (D u + E v + F) / (同)。</summary>
    public readonly record struct Homography(double A, double B, double C, double D, double E, double F, double G, double H)
    {
        public (double X, double Y) Map(double u, double v)
        {
            var den = G * u + H * v + 1;
            return ((A * u + B * v + C) / den, (D * u + E * v + F) / den);
        }
    }

    /// <summary>
    /// 単位正方形 (0,0),(1,0),(1,1),(0,1) → 四隅 TL, TR, BR, BL（元画像の画素座標・画素の左上角が 0）。
    /// </summary>
    public static Homography SquareToQuad(Quad q, int srcWidth, int srcHeight)
    {
        double x0 = q.TopLeft.X * srcWidth, y0 = q.TopLeft.Y * srcHeight;
        double x1 = q.TopRight.X * srcWidth, y1 = q.TopRight.Y * srcHeight;
        double x2 = q.BottomRight.X * srcWidth, y2 = q.BottomRight.Y * srcHeight;
        double x3 = q.BottomLeft.X * srcWidth, y3 = q.BottomLeft.Y * srcHeight;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
            return new Homography(x1 - x0, x2 - x1, x0, y1 - y0, y2 - y1, y0, 0, 0); // 平行四辺形（アフィン）
        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        var det = dx1 * dy2 - dx2 * dy1;
        var g = (sx * dy2 - dx2 * sy) / det;
        var h = (dx1 * sy - sx * dy1) / det;
        return new Homography(x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h);
    }
}
