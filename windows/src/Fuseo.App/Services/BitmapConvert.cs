using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;

namespace Fuseo.App.Services;

/// <summary>
/// Core の <see cref="PageBitmap"/>（BGRA32・プリマルチプライ無し）を WPF の表示用 BitmapSource にする。
/// **表示専用**。書き出しには使わない（書き出しは BurnIn → IExporter だけ）。
/// 96dpi で作るので、表示上の大きさ（DIP）は画素数と一致する。
/// </summary>
public static class BitmapConvert
{
    public static BitmapSource ToBitmapSource(PageBitmap bitmap)
    {
        var bs = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null,
                                     bitmap.Pixels, bitmap.Stride);
        bs.Freeze();
        return bs;
    }

    /// <summary>ページレール用の縮小画像（長辺 maxSide px 以下）。</summary>
    public static BitmapSource Thumbnail(PageBitmap bitmap, int maxSide)
    {
        var src = ToBitmapSource(bitmap);
        var s = Math.Min(1.0, maxSide / (double)Math.Max(bitmap.Width, bitmap.Height));
        if (s >= 1.0) return src;
        var scaled = new TransformedBitmap(src, new ScaleTransform(s, s));
        var wb = new WriteableBitmap(scaled);
        wb.Freeze();
        return wb;
    }
}
