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
}
