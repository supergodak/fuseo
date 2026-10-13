using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.Core.Geometry;

/// <summary>
/// 90° 単位の回転（docs/windows-design.md §3.3・Fable 所管）。**turns は時計回りの 90° の回数**（負なら反時計回り）。
/// 画像とマスク座標を**必ず同じ turns で一緒に**回す（片方だけ回すと塗る位置がずれる＝Mac で踏んだ二重適用の類）。
/// - 正規化座標（左上原点）の時計回り 90°: (x, y) → (1 − y, x)。矩形 (X, Y, W, H) → (1 − Y − H, X, H, W)。
/// - 画素は 1:1 の並べ替えのみ（補間なし）。新しい幅 = 元の高さ。
/// - ブラシの Width は「画像短辺に対する比」なので 90° 回転で変わらない。
/// 回転後の矩形を <see cref="CoordinateSpace.ToPixel"/> に通した結果は、元の画素矩形を回したものと数学的に一致し、
/// 浮動小数の誤差は外側（安全側）にしか出ない（テストで固定）。
/// </summary>
public static class QuarterTurn
{
    /// <summary>0..3 に正規化。</summary>
    public static int Normalize(int turns) => ((turns % 4) + 4) % 4;

    public static NormPoint Rotate(NormPoint p, int turns)
    {
        for (var i = Normalize(turns); i > 0; i--) p = new NormPoint(1 - p.Y, p.X);
        return p;
    }

    public static NormRect Rotate(NormRect r, int turns)
    {
        for (var i = Normalize(turns); i > 0; i--) r = new NormRect(1 - r.Y - r.H, r.X, r.H, r.W);
        return r;
    }

    public static BrushStroke Rotate(BrushStroke s, int turns) =>
        Normalize(turns) == 0 ? s : s with { Points = s.Points.Select(p => Rotate(p, turns)).ToList() };

    public static ManualMask Rotate(ManualMask m, int turns) =>
        Normalize(turns) == 0 ? m : new ManualMask
        {
            Rects = m.Rects.Select(r => Rotate(r, turns)).ToList(),
            Strokes = m.Strokes.Select(s => Rotate(s, turns)).ToList(),
        };

    public static MaskCandidate Rotate(MaskCandidate c, int turns) =>
        Normalize(turns) == 0 ? c : c with { Box = Rotate(c.Box, turns) };

    /// <summary>画像を回した**新しい**ビットマップ（turns=0 でも複製を返す＝呼び出し側が元を書き換えても安全）。</summary>
    public static PageBitmap Rotate(PageBitmap src, int turns)
    {
        var t = Normalize(turns);
        if (t == 0) return src.Clone();
        int w = src.Width, h = src.Height;
        var (dw, dh) = t == 2 ? (w, h) : (h, w);
        var dst = new byte[src.Pixels.Length];
        var s = src.Pixels;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = t switch
                {
                    1 => (h - 1 - y, x),           // 時計回り 90°
                    2 => (w - 1 - x, h - 1 - y),   // 180°
                    _ => (y, w - 1 - x),           // 時計回り 270°（＝反時計回り 90°）
                };
                var si = (y * w + x) * 4;
                var di = (dy * dw + dx) * 4;
                dst[di] = s[si]; dst[di + 1] = s[si + 1]; dst[di + 2] = s[si + 2]; dst[di + 3] = s[si + 3];
            }
        });
        return new PageBitmap(dw, dh, dst);
    }
}
