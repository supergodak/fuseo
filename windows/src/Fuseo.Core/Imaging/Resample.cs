namespace Fuseo.Core.Imaging;

/// <summary>
/// 再サンプル（解析用の複製を作る・書き出しには使わない）。
/// </summary>
public static class Resample
{
    /// <summary>
    /// **高さそのまま幅だけ**変えて、幅/高さ = <paramref name="aspect"/> にした新しいビットマップ（双線形）を返す
    /// （§10.13: ID-1 カードの縦横比が崩れた写真を 1.586 に戻して再 OCR するため）。
    /// 新しい幅 = round(高さ × aspect)（最低 1px）。高さが変わらないので縦方向の補間は恒等になり、
    /// 実際は横方向の線形補間（画素中心どうしを対応させ、端は端の画素で延長）になる。
    /// 4 チャネル（B・G・R・A）をそれぞれ補間する（取り込み済みのページは不透明なので α は 255 のまま）。
    /// 元のビットマップは変更しない。幅が同じでも複製を返す。
    /// </summary>
    public static PageBitmap ToAspect(PageBitmap src, double aspect)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (!double.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect), "縦横比が正でない");
        int sw = src.Width, h = src.Height;
        var dw = Math.Max(1, (int)Math.Round(h * aspect, MidpointRounding.AwayFromZero));
        if (dw == sw) return src.Clone();

        // 出力の各列が参照する元の 2 列と重み（行によらず同じなので先に作る）。
        var x0s = new int[dw];
        var x1s = new int[dw];
        var ts = new double[dw];
        var scale = (double)sw / dw;
        for (var dx = 0; dx < dw; dx++)
        {
            var sx = Math.Clamp((dx + 0.5) * scale - 0.5, 0, sw - 1);
            var x0 = (int)Math.Floor(sx);
            x0s[dx] = x0;
            x1s[dx] = Math.Min(x0 + 1, sw - 1);
            ts[dx] = sx - x0;
        }

        var dst = new PageBitmap(dw, h);
        var s = src.Pixels;
        var d = dst.Pixels;
        Parallel.For(0, h, y =>
        {
            var srow = y * sw * 4;
            var drow = y * dw * 4;
            for (var dx = 0; dx < dw; dx++)
            {
                var a = srow + x0s[dx] * 4;
                var b = srow + x1s[dx] * 4;
                var t = ts[dx];
                var o = drow + dx * 4;
                for (var c = 0; c < 4; c++)
                    d[o + c] = (byte)Math.Clamp(Math.Round(s[a + c] + (s[b + c] - s[a + c]) * t, MidpointRounding.AwayFromZero), 0, 255);
            }
        });
        return dst;
    }

    /// <summary>
    /// 全体を <paramref name="scale"/>（0〜1）倍に縮めた新しいビットマップ（面積平均・0.4.0 の大きさの上限用）。
    /// 縮めた後の短辺が <paramref name="minShortSide"/> を下回る倍率は使わない（番号や氏名が読めなくなるため）。
    /// 縮めない（倍率が 1 以上になる）ときは元のビットマップをそのまま返す。
    /// </summary>
    public static PageBitmap Downscale(PageBitmap src, double scale, int minShortSide)
    {
        ArgumentNullException.ThrowIfNull(src);
        var shortSide = Math.Min(src.Width, src.Height);
        var floor = shortSide > 0 ? (double)minShortSide / shortSide : 1;
        var s = Math.Min(1, Math.Max(scale, floor));
        if (s >= 0.999) return src;
        var dw = Math.Max(1, (int)Math.Round(src.Width * s));
        var dh = Math.Max(1, (int)Math.Round(src.Height * s));
        var dst = new PageBitmap(dw, dh);
        var sp = src.Pixels;
        var dp = dst.Pixels;
        double fx = (double)src.Width / dw, fy = (double)src.Height / dh;
        Parallel.For(0, dh, dy =>
        {
            var y0 = dy * fy;
            var y1 = Math.Min(src.Height, (dy + 1) * fy);
            for (var dx = 0; dx < dw; dx++)
            {
                var x0 = dx * fx;
                var x1 = Math.Min(src.Width, (dx + 1) * fx);
                double b = 0, g = 0, r = 0, a = 0, wsum = 0;
                for (var sy = (int)y0; sy < Math.Ceiling(y1); sy++)
                {
                    var wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    if (wy <= 0) continue;
                    var row = sy * src.Stride;
                    for (var sx = (int)x0; sx < Math.Ceiling(x1); sx++)
                    {
                        var w = (Math.Min(sx + 1, x1) - Math.Max(sx, x0)) * wy;
                        if (w <= 0) continue;
                        var i = row + sx * 4;
                        b += sp[i] * w; g += sp[i + 1] * w; r += sp[i + 2] * w; a += sp[i + 3] * w;
                        wsum += w;
                    }
                }
                var o = (dy * dw + dx) * 4;
                dp[o] = (byte)Math.Round(b / wsum);
                dp[o + 1] = (byte)Math.Round(g / wsum);
                dp[o + 2] = (byte)Math.Round(r / wsum);
                dp[o + 3] = (byte)Math.Round(a / wsum);
            }
        });
        return dst;
    }
}
