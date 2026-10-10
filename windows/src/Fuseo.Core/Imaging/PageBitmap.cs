namespace Fuseo.Core.Imaging;

/// <summary>
/// 1 ページ分の画素（**BGRA32・プリマルチプライ無し・左上原点**・行の詰め物なし: Stride = Width*4）。
/// WPF の型を Core に持ち込まないための生バッファ（§3.2）。回転は取り込み時に焼き込み済みの前提（§3.3）。
/// </summary>
public sealed class PageBitmap
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; }

    public PageBitmap(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "寸法が 0 以下");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[checked(width * height * 4)];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("画素数が寸法と合わない", nameof(pixels));
    }

    /// <summary>新しいバッファへの完全な複製（焼き込みは必ず複製に対して行う）。</summary>
    public PageBitmap Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = a;
    }

    /// <summary>単色で塗った画像（テスト・白下地用）。</summary>
    public static PageBitmap Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var bmp = new PageBitmap(width, height);
        for (var i = 0; i < bmp.Pixels.Length; i += 4)
        {
            bmp.Pixels[i] = b; bmp.Pixels[i + 1] = g; bmp.Pixels[i + 2] = r; bmp.Pixels[i + 3] = a;
        }
        return bmp;
    }
}
