using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;

namespace Fuseo.App.Library;

/// <summary>
/// ライブラリのページ画像の読み書き（テストで差し替えられるように小さく切り出す）。
/// ページ画像は**可逆**（PNG・BGRA のまま）＝開き直した元画像が画素単位で保存前と一致すること。
/// これは作業の保存用で、提出用の書き出し（Core の Export・メタデータ無しの自前エンコーダ）とは別物。
/// </summary>
public interface IPageImageCodec
{
    void WritePng(PageBitmap bitmap, Stream destination);

    /// <summary>一覧用の縮小 JPEG（長辺 <paramref name="maxSide"/> px 以下・透明は白に合成）。</summary>
    void WriteThumbnailJpeg(PageBitmap bitmap, int maxSide, Stream destination);

    PageBitmap ReadPng(Stream source);
}

/// <summary>WIC（WPF の PngBitmapEncoder / BitmapDecoder）による実装。どのスレッドからでも呼べる。</summary>
public sealed class WicPageImageCodec : IPageImageCodec
{
    public void WritePng(PageBitmap bitmap, Stream destination)
    {
        var src = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Pixels, bitmap.Stride);
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(destination);
    }

    public void WriteThumbnailJpeg(PageBitmap bitmap, int maxSide, Stream destination)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(bitmap.Width, bitmap.Height));
        var w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        BitmapSource src = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Pixels, bitmap.Stride);
        if (w != bitmap.Width || h != bitmap.Height)
            src = new TransformedBitmap(src, new ScaleTransform(w / (double)bitmap.Width, h / (double)bitmap.Height));
        var bgra = new byte[src.PixelWidth * src.PixelHeight * 4];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(bgra, src.PixelWidth * 4, 0);
        // 透明部分は白に合成（JPEG は α を持たない）。
        var bgr = new byte[src.PixelWidth * src.PixelHeight * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            var a = bgra[i + 3];
            bgr[j] = (byte)((bgra[i] * a + 255 * (255 - a) + 127) / 255);
            bgr[j + 1] = (byte)((bgra[i + 1] * a + 255 * (255 - a) + 127) / 255);
            bgr[j + 2] = (byte)((bgra[i + 2] * a + 255 * (255 - a) + 127) / 255);
        }
        var thumb = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Bgr24, null, bgr, src.PixelWidth * 3);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(thumb));
        enc.Save(destination);
    }

    public PageBitmap ReadPng(Stream source)
    {
        var dec = new PngBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                       BitmapCacheOption.OnLoad);
        if (dec.Frames.Count != 1) throw new InvalidDataException("PNG のフレーム数が 1 でない");
        BitmapSource frame = dec.Frames[0];
        if (frame.PixelWidth is <= 0 or > WorkDocumentJson.MaxPixelSide || frame.PixelHeight is <= 0 or > WorkDocumentJson.MaxPixelSide)
            throw new InvalidDataException("PNG の寸法が範囲外");
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bmp = new PageBitmap(frame.PixelWidth, frame.PixelHeight);
        frame.CopyPixels(bmp.Pixels, bmp.Stride, 0);
        return bmp;
    }
}
