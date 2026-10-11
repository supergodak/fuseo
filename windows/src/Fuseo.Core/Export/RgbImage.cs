using Fuseo.Core.Imaging;

namespace Fuseo.Core.Export;

/// <summary>
/// 書き出し用の不透明 RGB（8bit×3・行の詰め物なし）。焼き込み済み BGRA を**白に合成**して作る。
/// α を捨てることで「透明画素の裏に色データが残る」類の持ち越しも無くす（§4）。
/// </summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }

    private RgbImage(int width, int height, byte[] rgb)
    {
        Width = width; Height = height; Rgb = rgb;
    }

    public static RgbImage FromBgraOnWhite(PageBitmap bmp)
    {
        var src = bmp.Pixels;
        var dst = new byte[checked(bmp.Width * bmp.Height * 3)];
        Parallel.For(0, bmp.Height, y =>
        {
            var si = y * bmp.Stride;
            var di = y * bmp.Width * 3;
            for (var x = 0; x < bmp.Width; x++, si += 4, di += 3)
            {
                int a = src[si + 3];
                if (a == 255)
                {
                    dst[di] = src[si + 2]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si];
                }
                else
                {
                    // 非プリマルチプライ: out = c·a + 255·(1−a)（整数で丸め）
                    var inv = 255 - a;
                    dst[di] = (byte)((src[si + 2] * a + 255 * inv + 127) / 255);
                    dst[di + 1] = (byte)((src[si + 1] * a + 255 * inv + 127) / 255);
                    dst[di + 2] = (byte)((src[si] * a + 255 * inv + 127) / 255);
                }
            }
        });
        return new RgbImage(bmp.Width, bmp.Height, dst);
    }
}
