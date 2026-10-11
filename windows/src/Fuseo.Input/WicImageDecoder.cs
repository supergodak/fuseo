using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;

namespace Fuseo.Input;

/// <summary>
/// WIC（WPF の BitmapDecoder）による画像デコード。
/// 出力は常に BGRA32・不透明（α は白に合成）・EXIF Orientation 焼き込み済み・長辺 <see cref="PageSource.MaxImageLongSidePx"/> 以内。
/// デコード時のフレーム（メタデータ・サムネイル付き）は外へ出さない＝生の画素だけを PageBitmap にコピーする。
/// </summary>
internal static class WicImageDecoder
{
    /// <summary>フレーム数（マルチページ TIFF の上限判定用）。画素は読まない。</summary>
    public static int CountFrames(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                                           BitmapCacheOption.None);
        return decoder.Frames.Count;
    }

    /// <summary>
    /// デコードする。<paramref name="allFrames"/> が false なら先頭フレームだけ。
    /// フレームごとに <paramref name="ct"/> を確認する。
    /// </summary>
    public static List<PageBitmap> Decode(byte[] bytes, bool allFrames, int maxLongSide, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        // 色プロファイルは無視する（壊れたプロファイルで WPF が例外を投げる既知の問題を避ける。色の厳密さより確実に開くことを優先）。
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                                           BitmapCacheOption.OnLoad);
        var count = allFrames ? decoder.Frames.Count : Math.Min(1, decoder.Frames.Count);
        if (count == 0) throw new InvalidDataException("フレームが無い");
        var pages = new List<PageBitmap>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var frame = decoder.Frames[i];
            var bmp = ToOpaqueBgra(frame);
            bmp = PixelOps.ApplyExifOrientation(bmp, ReadOrientation(frame));
            bmp = PixelOps.DownscaleToFit(bmp, maxLongSide);
            pages.Add(bmp);
        }
        return pages;
    }

    private static PageBitmap ToOpaqueBgra(BitmapSource frame)
    {
        BitmapSource src = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) throw new InvalidDataException("寸法が 0");
        var pixels = new byte[checked(w * h * 4)];
        src.CopyPixels(pixels, w * 4, 0);
        PixelOps.FlattenOnWhite(pixels);
        return new PageBitmap(w, h, pixels);
    }

    /// <summary>EXIF / TIFF の Orientation（1〜8）。読めなければ 1。</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata md) return 1;
            var v = md.GetQuery("System.Photo.Orientation");
            return v switch
            {
                ushort u => u,
                short s => s,
                int n => n,
                uint n => (int)n,
                byte b => b,
                _ => 1,
            };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return 1; // メタデータを持たない形式（BMP 等）
        }
    }
}
