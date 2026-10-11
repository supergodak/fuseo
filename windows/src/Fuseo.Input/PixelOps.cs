using Fuseo.Core.Imaging;

namespace Fuseo.Input;

/// <summary>
/// 取り込み時の画素処理（WPF 非依存・純 C#）。
/// - <see cref="ApplyExifOrientation"/>: EXIF Orientation（1〜8）を画素へ焼き込む（WPF は自動適用しない）。
/// - <see cref="FlattenOnWhite"/>: α を白に合成して不透明（A=255）にする。
/// - <see cref="DownscaleToFit"/>: 長辺が上限を超える画像を面積平均で縮小する（メモリ保護）。
/// </summary>
public static class PixelOps
{
    /// <summary>
    /// EXIF Orientation を焼き込んだ新しいビットマップを返す（1・範囲外はそのまま＝複製しない）。
    /// 5〜8 は縦横が入れ替わる。値の意味は EXIF 2.3 / TIFF 6.0 の定義どおり
    /// （6 = 表示には時計回り 90°、8 = 反時計回り 90°、3 = 180°、2/4 = 左右/上下反転、5/7 = 転置/反転置）。
    /// </summary>
    public static PageBitmap ApplyExifOrientation(PageBitmap src, int orientation)
    {
        if (orientation is < 2 or > 8) return src;
        int w = src.Width, h = src.Height;
        var swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var dst = new PageBitmap(dw, dh);
        var s = src.Pixels;
        var d = dst.Pixels;
        Parallel.For(0, dh, dy =>
        {
            for (var dx = 0; dx < dw; dx++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - dx; sy = dy; break;
                    case 3: sx = w - 1 - dx; sy = h - 1 - dy; break;
                    case 4: sx = dx; sy = h - 1 - dy; break;
                    case 5: sx = dy; sy = dx; break;
                    case 6: sx = dy; sy = h - 1 - dx; break;
                    case 7: sx = w - 1 - dy; sy = h - 1 - dx; break;
                    default: sx = w - 1 - dy; sy = dx; break; // 8
                }
                var si = (sy * w + sx) * 4;
                var di = (dy * dw + dx) * 4;
                d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = s[si + 3];
            }
        });
        return dst;
    }

    /// <summary>
    /// BGRA（プリマルチプライ無し）のバッファを白に合成して A=255 にする（その場で書き換える）。
    /// c' = (c·a + 255·(255−a)) / 255（四捨五入）。
    /// </summary>
    public static void FlattenOnWhite(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            var inv = 255 - a;
            bgra[i] = (byte)((bgra[i] * a + 255 * inv + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * inv + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * inv + 127) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>縮小後の寸法（縦横比維持・長辺 = maxLongSide・四捨五入・最低 1px）。収まっていれば元の寸法。</summary>
    public static (int Width, int Height) FitLongSide(int width, int height, int maxLongSide)
    {
        var longSide = Math.Max(width, height);
        if (longSide <= maxLongSide) return (width, height);
        var scale = (double)maxLongSide / longSide;
        var w = width >= height ? maxLongSide : Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        var h = height > width ? maxLongSide : Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        return (Math.Min(w, maxLongSide), Math.Min(h, maxLongSide));
    }

    /// <summary>
    /// 長辺が <paramref name="maxLongSide"/> を超える不透明画像を面積平均（ボックスフィルタ）で縮小する。
    /// 収まっていれば元のビットマップをそのまま返す。
    /// </summary>
    public static PageBitmap DownscaleToFit(PageBitmap src, int maxLongSide)
    {
        var (tw, th) = FitLongSide(src.Width, src.Height, maxLongSide);
        if (tw == src.Width && th == src.Height) return src;
        int sw = src.Width, sh = src.Height;

        // 横方向 → 縦方向の 2 パス（各出力画素は元画像の対応区間の平均）。
        var tmp = new uint[checked(tw * sh * 4)];
        var s = src.Pixels;
        Parallel.For(0, sh, y =>
        {
            var row = y * sw * 4;
            for (var x = 0; x < tw; x++)
            {
                var x0 = (int)((long)x * sw / tw);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / tw));
                uint b = 0, g = 0, r = 0, a = 0;
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = row + sx * 4;
                    b += s[i]; g += s[i + 1]; r += s[i + 2]; a += s[i + 3];
                }
                var n = (uint)(x1 - x0);
                var o = (y * tw + x) * 4;
                // 横方向の平均は 256 倍して精度を保つ（縦方向で割り戻す）。
                tmp[o] = (b * 256 + n / 2) / n; tmp[o + 1] = (g * 256 + n / 2) / n;
                tmp[o + 2] = (r * 256 + n / 2) / n; tmp[o + 3] = (a * 256 + n / 2) / n;
            }
        });

        var dst = new PageBitmap(tw, th);
        var d = dst.Pixels;
        Parallel.For(0, th, y =>
        {
            var y0 = (int)((long)y * sh / th);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / th));
            var n = (ulong)(y1 - y0) * 256;
            for (var x = 0; x < tw; x++)
            {
                ulong b = 0, g = 0, r = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var i = (sy * tw + x) * 4;
                    b += tmp[i]; g += tmp[i + 1]; r += tmp[i + 2]; a += tmp[i + 3];
                }
                var o = (y * tw + x) * 4;
                d[o] = (byte)Math.Min(255UL, (b + n / 2) / n); d[o + 1] = (byte)Math.Min(255UL, (g + n / 2) / n);
                d[o + 2] = (byte)Math.Min(255UL, (r + n / 2) / n); d[o + 3] = (byte)Math.Min(255UL, (a + n / 2) / n);
            }
        });
        return dst;
    }
}
