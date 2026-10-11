using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>§4.2・§4.3: 書き出しファイルのバイト検査（許可リスト方式）と可逆形式の往復。</summary>
public class ExportTests
{
    private static readonly MaskRasterizer R = new();
    private static readonly Exporter E = new();

    internal static BurnedPage Burned(int w, int h, params NormRect[] rects) =>
        R.BurnIn(MaskRasterizerTests.Noise(w, h), new ConfirmedMasks(rects, []));

    internal static byte[] ExportOne(BurnedPage page, ExportOptions o)
    {
        using var ms = new MemoryStream();
        E.Export([page], o, _ => ms);
        return ms.ToArray();
    }

    // ---- JPEG ----

    [Fact]
    public void Jpeg_HasOnlyAllowedMarkers()
    {
        var j = ExportOne(Burned(123, 45, new NormRect(0.1, 0.1, 0.3, 0.3)), new ExportOptions(ExportFormat.Jpeg));
        var markers = JpegMarkersOrThrow(j);
        Assert.Equal(new byte[] { 0xD8, 0xE0, 0xDB, 0xDB, 0xC0, 0xC4, 0xC4, 0xC4, 0xC4, 0xDA, 0xD9 }, markers);
        Assert.Equal((123, 45, 3), ExportInspect.JpegSof(j));
    }

    private static byte[] JpegMarkersOrThrow(byte[] j)
    {
        var m = ExportInspect.JpegMarkers(j);
        // APP1〜APP15（EXIF/XMP/ICC/サムネイル等）と COM が無いこと（上の完全一致で既に保証されるが、意図を明記）
        Assert.DoesNotContain(m, x => x is >= 0xE1 and <= 0xEF);
        Assert.DoesNotContain((byte)0xFE, m);
        return m.ToArray();
    }

    [Fact]
    public void Jpeg_App0IsPlainJfifWithoutThumbnail()
    {
        var j = ExportOne(Burned(16, 16), new ExportOptions(ExportFormat.Jpeg));
        Assert.Equal(new byte[] { 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00 }, j[2..11]);
        Assert.Equal(0, j[18]); // Xthumbnail
        Assert.Equal(0, j[19]); // Ythumbnail
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 9)]
    [InlineData(8, 8)]
    [InlineData(17, 3)]
    public void Jpeg_OddSizes_AreWellFormed(int w, int h)
    {
        var j = ExportOne(Burned(w, h), new ExportOptions(ExportFormat.Jpeg, 0.5));
        JpegMarkersOrThrow(j);
        Assert.Equal((w, h, 3), ExportInspect.JpegSof(j));
    }

    [Fact]
    public void Jpeg_HuffmanTables_AreCompleteAndPrefixFree()
    {
        foreach (var (bits, vals) in new[]
                 {
                     (JpegEncoder.DcLumaBits, JpegEncoder.DcVals), (JpegEncoder.DcChromaBits, JpegEncoder.DcVals),
                     (JpegEncoder.AcLumaBits, JpegEncoder.AcLumaVals), (JpegEncoder.AcChromaBits, JpegEncoder.AcChromaVals),
                 })
        {
            Assert.Equal(vals.Length, bits.Sum(b => b));
            Assert.Equal(vals.Length, vals.Distinct().Count());
            var (code, size) = JpegEncoder.BuildHuffman(bits, vals);
            var codes = vals.Select(v => (C: code[v], L: size[v])).ToList();
            // 全 1 の符号は禁止（T.81 C.2）
            Assert.DoesNotContain(codes, c => c.C == (1 << c.L) - 1);
            for (var a = 0; a < codes.Count; a++)
            for (var b = 0; b < codes.Count; b++)
            {
                if (a == b || codes[a].L > codes[b].L) continue;
                Assert.NotEqual(codes[a].C, codes[b].C >> (codes[b].L - codes[a].L)); // 接頭辞にならない
            }
        }
        // AC 表は (run, size) の全記号 0x00, 0xF0, 0x01..0xFA（size 1..10）を含む
        foreach (var vals in new[] { JpegEncoder.AcLumaVals, JpegEncoder.AcChromaVals })
            for (var run = 0; run < 16; run++)
            for (var s = 1; s <= 10; s++)
                Assert.Contains((byte)((run << 4) | s), vals);
    }

    [Fact]
    public void Jpeg_ZigZag_IsPermutation()
    {
        Assert.Equal(Enumerable.Range(0, 64), JpegEncoder.ZigZag.Select(b => (int)b).Order());
    }

    [Fact]
    public void Jpeg_RejectsOutOfRangeQuality()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportOne(Burned(8, 8), new ExportOptions(ExportFormat.Jpeg, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportOne(Burned(8, 8), new ExportOptions(ExportFormat.Jpeg, 1.5)));
    }

    // ---- PNG ----

    [Fact]
    public void Png_HasOnlyIhdrIdatIend_WithValidCrc()
    {
        var p = ExportOne(Burned(300, 200, new NormRect(0.2, 0.2, 0.2, 0.2)), new ExportOptions(ExportFormat.Png));
        var chunks = ExportInspect.PngChunks(p);
        Assert.All(chunks, c => Assert.True(c.CrcOk, $"{c.Type} の CRC 不一致"));
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IEND", chunks[^1].Type);
        Assert.All(chunks, c => Assert.Contains(c.Type, new[] { "IHDR", "IDAT", "IEND" }));
    }

    [Fact]
    public void Png_RoundTrip_IsPixelExact_AndMaskIsBlack()
    {
        var page = Burned(97, 61, new NormRect(0.3, 0.3, 0.4, 0.4));
        var (w, h, rgb) = ExportInspect.DecodePng(ExportOne(page, new ExportOptions(ExportFormat.Png)));
        Assert.Equal((97, 61), (w, h));
        var src = page.Bitmap;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (b, g, r, _) = src.GetPixel(x, y);
            var i = (y * w + x) * 3;
            Assert.Equal((r, g, b), (rgb[i], rgb[i + 1], rgb[i + 2]));
        }
        var pr = CoordinateSpace.ToPixel(new NormRect(0.3, 0.3, 0.4, 0.4), w, h);
        var c = ((pr.Y + 1) * w + pr.X + 1) * 3;
        Assert.Equal(new byte[] { 0, 0, 0 }, rgb[c..(c + 3)]);
    }

    [Fact]
    public void Transparent_PixelsAreFlattenedToWhite_HiddenColorDoesNotSurvive()
    {
        // α=0 の画素の裏に色（＝見えない情報）を持たせる
        var src = PageBitmap.Solid(10, 10, 11, 22, 33, a: 0);
        src.SetPixel(0, 0, 200, 100, 50, 128);
        var page = R.BurnIn(src, ConfirmedMasks.None);
        var (_, _, rgb) = ExportInspect.DecodePng(ExportOne(page, new ExportOptions(ExportFormat.Png)));
        Assert.Equal(new byte[] { 255, 255, 255 }, rgb[3..6]);
        // α=128 の画素は白と合成: (c·128 + 255·127 + 127) / 255 → R 50→152, G 100→177, B 200→227
        Assert.Equal(new byte[] { 152, 177, 227 }, rgb[0..3]);
    }

    // ---- PDF ----

    private static readonly HashSet<string> AllowedPdfNames =
    [
        "/Type", "/Catalog", "/Pages", "/Kids", "/Count", "/Page", "/Parent", "/MediaBox", "/Resources",
        "/XObject", "/Im0", "/Contents", "/Length", "/Subtype", "/Image", "/Width", "/Height", "/ColorSpace",
        "/DeviceRGB", "/BitsPerComponent", "/Filter", "/DCTDecode", "/FlateDecode", "/Producer", "/Size",
        "/Root", "/Info",
    ];

    private static byte[] Pdf(PdfImageEncoding enc, params BurnedPage[] pages)
    {
        using var ms = new MemoryStream();
        E.Export(pages, new ExportOptions(ExportFormat.Pdf, PdfImages: enc), _ => ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(PdfImageEncoding.Jpeg)]
    [InlineData(PdfImageEncoding.Flate)]
    public void Pdf_ContainsOnlyAllowlistedNames_AndNoTextFontAnnotOrMetadata(PdfImageEncoding enc)
    {
        var pdf = Pdf(enc, Burned(300, 450, new NormRect(0.1, 0.1, 0.5, 0.1)), Burned(600, 300));
        var f = ExportInspect.ParsePdf(pdf);
        var names = Regex.Matches(f.NonStreamText, @"/[A-Za-z0-9]+").Select(m => m.Value).ToHashSet();
        var extra = names.Except(AllowedPdfNames).ToList();
        Assert.True(extra.Count == 0, "許可リスト外の名前: " + string.Join(" ", extra));
        foreach (var bad in new[] { "/Font", "/Annots", "/Metadata", "/JavaScript", "/OpenAction", "/AA", "/AcroForm", "/ID", "/Encrypt" })
            Assert.DoesNotContain(bad, names);

        // 内容ストリームは画像を置く 1 行だけ（BT/Tj 等のテキスト演算子を持たない）
        var contents = f.Objects.Where(o => o.Stream != null && !o.Dict.Contains("/Subtype /Image")).ToList();
        Assert.Equal(2, contents.Count);
        Assert.All(contents, o =>
            Assert.Matches(@"^q [\d.]+ 0 0 [\d.]+ 0 0 cm /Im0 Do Q$", Encoding.ASCII.GetString(o.Stream!)));

        Assert.Contains("<< /Producer (Fuseo for Windows) >>", f.NonStreamText);
    }

    [Fact]
    public void Pdf_Structure_XrefOffsetsAndPageGeometry()
    {
        var pdf = Pdf(PdfImageEncoding.Jpeg, Burned(2480, 3508), Burned(3508, 2480), Burned(1001, 333));
        var f = ExportInspect.ParsePdf(pdf);
        Assert.Equal(3 + 3 * 3, f.Objects.Count);
        Assert.Equal(Enumerable.Range(1, 12), f.Objects.Select(o => o.Num));
        foreach (var o in f.Objects) Assert.Equal(o.Offset, f.Xref[o.Num]);
        Assert.Contains("/Size 13 /Root 1 0 R /Info 3 0 R", f.Trailer);
        Assert.Contains("/Count 3", f.Objects.Single(o => o.Num == 2).Dict);

        // A4 300dpi（2480×3508px）→ 595.2×841.92pt。画像の寸法は px のまま
        var pages = f.Objects.Where(o => o.Dict.Contains("/Type /Page ")).ToList();
        Assert.Contains("/MediaBox [0 0 595.2 841.92]", pages[0].Dict);
        Assert.Contains("/MediaBox [0 0 841.92 595.2]", pages[1].Dict);
        Assert.Contains("/MediaBox [0 0 240.24 79.92]", pages[2].Dict);
        var images = f.Objects.Where(o => o.Dict.Contains("/Subtype /Image")).ToList();
        Assert.Contains("/Width 2480 /Height 3508", images[0].Dict);
        Assert.Contains("/Width 1001 /Height 333", images[2].Dict);
        // DCT の中身は自前 JPEG（APP1 等なし）
        Assert.All(images, im => JpegMarkersOrThrow(im.Stream!));
    }

    [Fact]
    public void Pdf_Flate_RoundTrip_ImageIsBurnedPixelsExactly()
    {
        var page = Burned(80, 50, new NormRect(0.25, 0.2, 0.5, 0.6));
        var f = ExportInspect.ParsePdf(Pdf(PdfImageEncoding.Flate, page));
        var im = f.Objects.Single(o => o.Dict.Contains("/Subtype /Image"));
        using var zs = new ZLibStream(new MemoryStream(im.Stream!), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zs.CopyTo(raw);
        var rgb = raw.ToArray();
        Assert.Equal(80 * 50 * 3, rgb.Length);
        var expected = RgbImage.FromBgraOnWhite(page.Bitmap).Rgb;
        Assert.Equal(expected, rgb);
        var pr = CoordinateSpace.ToPixel(new NormRect(0.25, 0.2, 0.5, 0.6), 80, 50);
        for (var y = pr.Y; y < pr.Bottom; y++)
        for (var x = pr.X; x < pr.Right; x++)
            Assert.Equal(new byte[] { 0, 0, 0 }, rgb[((y * 80 + x) * 3)..((y * 80 + x) * 3 + 3)]);
    }

    [Fact]
    public void Pdf_NumberFormat_IsCultureInvariant()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // 小数点がカンマの地域
            var f = ExportInspect.ParsePdf(Pdf(PdfImageEncoding.Jpeg, Burned(1001, 333)));
            Assert.Contains("/MediaBox [0 0 240.24 79.92]", f.NonStreamText);
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    // ---- Exporter の振り分け ----

    [Fact]
    public void Exporter_Pdf_OpensOneDestination_Images_OnePerPage()
    {
        var pages = new[] { Burned(10, 10), Burned(12, 8), Burned(5, 5) };
        var opened = new List<int>();
        var streams = new List<MemoryStream>();
        Stream Open(int i) { opened.Add(i); var s = new MemoryStream(); streams.Add(s); return s; }

        E.Export(pages, new ExportOptions(ExportFormat.Pdf), Open);
        Assert.Equal(new[] { 0 }, opened);

        opened.Clear(); streams.Clear();
        E.Export(pages, new ExportOptions(ExportFormat.Png), Open);
        Assert.Equal(new[] { 0, 1, 2 }, opened);
        Assert.Equal((12, 8), ExportInspect.DecodePng(streams[1].ToArray()) is var d ? (d.W, d.H) : default);

        Assert.Throws<ArgumentException>(() => E.Export([], new ExportOptions(ExportFormat.Pdf), Open));
    }
}
