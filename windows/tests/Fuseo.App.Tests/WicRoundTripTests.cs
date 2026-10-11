using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// 自前エンコーダの出力を Windows 標準のデコーダ（WIC）で読み戻す往復検証（§4.2・§4.3）。
/// 環境変数 FUSEO_SAMPLE_OUT があれば、外部ビューア確認用のサンプル（合成画像のみ）をそこへ書く。
/// </summary>
public class WicRoundTripTests
{
    private static readonly MaskRasterizer R = new();
    private static readonly Exporter E = new();
    private static readonly NormRect Mask = new(0.2, 0.3, 0.35, 0.2);

    /// <summary>書類っぽい合成画像: なめらかなグラデーション＋細い黒線（文字の代わり）。</summary>
    private static PageBitmap Synthetic(int w, int h)
    {
        var bmp = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var line = (y % 40 is >= 20 and < 22) && x % 60 < 45;
            byte r = (byte)(line ? 20 : 230 - x * 40 / w), g = (byte)(line ? 20 : 220 - y * 30 / h), b = (byte)(line ? 30 : 200 + x * 50 / w);
            bmp.SetPixel(x, y, b, g, r, 255);
        }
        return bmp;
    }

    private static PageBitmap Decode(byte[] bytes)
    {
        var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        var bmp = new PageBitmap(conv.PixelWidth, conv.PixelHeight);
        conv.CopyPixels(bmp.Pixels, bmp.Stride, 0);
        return bmp;
    }

    private static double Psnr(PageBitmap a, PageBitmap b, PixelRect? exclude = null)
    {
        double se = 0; long n = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            if (exclude is { } ex && x >= ex.X - 8 && x < ex.Right + 8 && y >= ex.Y - 8 && y < ex.Bottom + 8) continue;
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            se += Math.Pow(p.R - q.R, 2) + Math.Pow(p.G - q.G, 2) + Math.Pow(p.B - q.B, 2);
            n += 3;
        }
        return 10 * Math.Log10(255.0 * 255 / (se / n));
    }

    /// <summary>
    /// マスク内部が黒いこと。縁の近くは JPEG のリンギング（マスク外＝もともと見えている画素からの漏れ）で
    /// わずかに明るくなるので inset だけ内側を見る。品質が低いほど漏れが広い（0.5 では 1 ブロック＝8px）。
    /// </summary>
    private static void AssertMaskInteriorBlack(PageBitmap decoded, PixelRect pr, int maxChannel, int inset = 2)
    {
        for (var y = pr.Y + inset; y < pr.Bottom - inset; y++)
        for (var x = pr.X + inset; x < pr.Right - inset; x++)
        {
            var p = decoded.GetPixel(x, y);
            Assert.True(p.R <= maxChannel && p.G <= maxChannel && p.B <= maxChannel, $"({x},{y}) = {p}");
        }
    }

    private static List<byte> HeaderMarkers(byte[] j)
    {
        var list = new List<byte>();
        var i = 2;
        while (i < j.Length - 3 && j[i] == 0xFF)
        {
            list.Add(j[i + 1]);
            if (j[i + 1] == 0xDA) break;
            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(j.AsSpan(i + 2));
        }
        return list;
    }

    private static void SaveSample(string name, byte[] bytes)
    {
        var dir = Environment.GetEnvironmentVariable("FUSEO_SAMPLE_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }

    [Theory]
    [InlineData(1001, 707, 0.95, 38)]
    [InlineData(64, 64, 0.95, 38)]
    [InlineData(333, 211, 0.5, 30)]
    public void Jpeg_DecodesInWic_WithGoodQuality_AndBlackMask(int w, int h, double quality, double minPsnr)
    {
        var page = R.BurnIn(Synthetic(w, h), new ConfirmedMasks([Mask], []));
        using var ms = new MemoryStream();
        E.Export([page], new ExportOptions(ExportFormat.Jpeg, quality), _ => ms);
        var bytes = ms.ToArray();
        if (w == 1001) SaveSample("sample.jpg", bytes);

        var decoded = Decode(bytes);
        Assert.Equal((w, h), (decoded.Width, decoded.Height));
        var pr = CoordinateSpace.ToPixel(Mask, w, h);
        var psnr = Psnr(page.Bitmap, decoded, pr);
        Assert.True(psnr >= minPsnr, $"PSNR {psnr:F1}dB < {minPsnr}");
        AssertMaskInteriorBlack(decoded, pr, 12, quality >= 0.9 ? 2 : 8);
    }

    [Fact]
    public void Png_DecodesInWic_PixelExact()
    {
        var page = R.BurnIn(Synthetic(257, 129), new ConfirmedMasks([Mask], []));
        using var ms = new MemoryStream();
        E.Export([page], new ExportOptions(ExportFormat.Png), _ => ms);
        SaveSample("sample.png", ms.ToArray());
        Assert.Equal(page.Bitmap.Pixels, Decode(ms.ToArray()).Pixels);
    }

    /// <summary>
    /// §4.2 の要: EXIF（撮影日時・GPS・端末名）とサムネイルを**わざと持たせた**入力を通しても、出力には何も残らない。
    /// </summary>
    [Fact]
    public void Jpeg_InputWithExifGpsAndThumbnail_LeavesNothingInOutput()
    {
        // 1) メタデータ付きの入力 JPEG を WIC で作る
        var src = Synthetic(400, 300);
        var bs = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, src.Pixels, src.Stride);
        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=271}", "FuseoTestMaker");          // Make
        meta.SetQuery("/app1/ifd/{ushort=272}", "FuseoTestCamera");         // Model
        meta.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:10:11 09:00:00"); // DateTimeOriginal
        meta.SetQuery("/app1/ifd/gps/{ushort=1}", "N");                       // GPSLatitudeRef
        meta.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { 35ul | (1ul << 32), 41ul | (1ul << 32), 0ul | (1ul << 32) });
        meta.Comment = "secret-comment";
        var thumb = new TransformedBitmap(bs, new ScaleTransform(0.25, 0.25));
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(bs, thumb, meta, null));
        using var inMs = new MemoryStream();
        enc.Save(inMs);
        var input = inMs.ToArray();
        var inText = Encoding.Latin1.GetString(input);
        Assert.Contains((byte)0xE1, HeaderMarkers(input));       // 前提: 入力には APP1 がある
        Assert.Contains("FuseoTestCamera", inText);

        // 2) 取り込み相当（WIC デコード→BGRA）→焼き込み→書き出し
        var page = R.BurnIn(Decode(input), new ConfirmedMasks([Mask], []));
        using var outMs = new MemoryStream();
        E.Export([page], new ExportOptions(ExportFormat.Jpeg), _ => outMs);
        var output = outMs.ToArray();

        // 3) 出力: APP0(JFIF) 以外の APPn・COM が無く、入力の文字列が一切残っていない
        var markers = HeaderMarkers(output);
        Assert.Equal(new byte[] { 0xE0, 0xDB, 0xDB, 0xC0, 0xC4, 0xC4, 0xC4, 0xC4, 0xDA }, markers);
        var outText = Encoding.Latin1.GetString(output);
        foreach (var s in new[] { "Exif", "FuseoTestMaker", "FuseoTestCamera", "2026:10:11", "secret-comment", "http://ns.adobe.com" })
            Assert.DoesNotContain(s, outText);
        // WIC で読み戻してもメタデータが無い
        var dec = BitmapDecoder.Create(new MemoryStream(output), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var outMeta = dec.Frames[0].Metadata as BitmapMetadata;
        Assert.True(outMeta is null || (outMeta.CameraModel is null && outMeta.DateTaken is null && outMeta.Comment is null));
        Assert.Null(dec.Frames[0].Thumbnail);
    }

    [Fact]
    public void Pdf_EmbeddedJpeg_DecodesInWic_AndMaskIsBlack()
    {
        var page = R.BurnIn(Synthetic(1240, 1754), new ConfirmedMasks([Mask], []));
        var page2 = R.BurnIn(Synthetic(1754, 1240), new ConfirmedMasks([], []));
        using var ms = new MemoryStream();
        E.Export([page, page2], new ExportOptions(ExportFormat.Pdf), _ => ms);
        var pdf = ms.ToArray();
        SaveSample("sample.pdf", pdf);
        using (var fl = new MemoryStream())
        {
            E.Export([page, page2], new ExportOptions(ExportFormat.Pdf, PdfImages: PdfImageEncoding.Flate), _ => fl);
            SaveSample("sample-flate.pdf", fl.ToArray());
        }

        // 最初の画像ストリームを取り出して WIC で読む
        var text = Encoding.Latin1.GetString(pdf);
        var m = Regex.Match(text, @"/Subtype /Image /Width 1240 /Height 1754 .*?/Length (\d+) >>\nstream\n");
        Assert.True(m.Success);
        var len = int.Parse(m.Groups[1].Value);
        var jpeg = pdf.AsSpan(m.Index + m.Length, len).ToArray();
        var decoded = Decode(jpeg);
        AssertMaskInteriorBlack(decoded, CoordinateSpace.ToPixel(Mask, 1240, 1754), 12);
    }
}
