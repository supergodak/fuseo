using System.IO;
using Fuseo.Core.Contracts;
using Xunit;

namespace Fuseo.Input.Tests;

/// <summary>上限（ラスタライズ前に数えて拒否）・パスワード・壊れたファイル・取り消し。</summary>
public class ImportPolicyTests
{
    private static readonly Func<string, Task<string?>> NoPassword =
        _ => throw new Xunit.Sdk.XunitException("パスワードを聞くはずがない");

    private static byte[] TinyPdf(int pages) =>
        MiniPdf.Build(Enumerable.Range(0, pages).Select(_ => new MiniPdf.Page(10, 10, "")).ToList());

    private static byte[] TinyPng() => Img.EncodePng(Img.WhiteWithBlackSquare(4, 4, 0, 0, 1));

    // ---- 上限 ----

    [Fact]
    public async Task Pdf51Pages_RejectedBeforeRasterizing()
    {
        using var dir = new TempDir();
        var path = dir.Write("big.pdf", TinyPdf(51));
        var source = new PageSource();
        var ex = await Assert.ThrowsAsync<PageLimitExceededException>(
            () => source.LoadAsync([path], NoPassword, CancellationToken.None));
        Assert.Equal(51, ex.PageCount);
        Assert.Equal(IPageSource.MaxPdfPagesPerImport, ex.Limit);
        Assert.Equal("big.pdf", ex.FileName);
        Assert.Equal(0, source.RasterizedPageCount);
        Assert.Contains("51", ex.Message);
        Assert.Contains("50", ex.Message);
    }

    [Fact]
    public async Task Pdf50Pages_Accepted()
    {
        using var dir = new TempDir();
        var path = dir.Write("ok.pdf", TinyPdf(50));
        var pages = await new PageSource().LoadAsync([path], NoPassword, CancellationToken.None);
        Assert.Equal(50, pages.Count);
        Assert.Equal(49, pages[^1].PageIndex);
    }

    [Fact]
    public async Task TwoPdfs_TotalOver50_RejectedBeforeRasterizing()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.pdf", TinyPdf(30));
        var b = dir.Write("b.pdf", TinyPdf(21));
        var source = new PageSource();
        var ex = await Assert.ThrowsAsync<PageLimitExceededException>(
            () => source.LoadAsync([a, b], NoPassword, CancellationToken.None));
        Assert.Equal(51, ex.PageCount);
        Assert.Null(ex.FileName);
        Assert.Equal(0, source.RasterizedPageCount);
    }

    [Fact]
    public async Task PdfPlusImages_CountedTogether()
    {
        // Mac と同じ: PDF を含む取り込みは画像 1 枚も 1 ページとして合計に数える。
        using var dir = new TempDir();
        var paths = new List<string> { dir.Write("doc.pdf", TinyPdf(2)) };
        var png = TinyPng();
        for (var i = 0; i < 49; i++) paths.Add(dir.Write($"img{i:D2}.png", png));
        var source = new PageSource();
        var ex = await Assert.ThrowsAsync<PageLimitExceededException>(
            () => source.LoadAsync(paths, NoPassword, CancellationToken.None));
        Assert.Equal(51, ex.PageCount);
        Assert.Equal(0, source.RasterizedPageCount);

        paths.RemoveAt(paths.Count - 1); // 2 + 48 = 50 は通る
        Assert.Equal(50, (await new PageSource().LoadAsync(paths, NoPassword, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ImagesOnly_60_AreNotLimited()
    {
        using var dir = new TempDir();
        var png = TinyPng();
        var paths = Enumerable.Range(0, 60).Select(i => dir.Write($"img{i:D2}.png", png)).ToList();
        var pages = await new PageSource().LoadAsync(paths, NoPassword, CancellationToken.None);
        Assert.Equal(60, pages.Count);
    }

    [Fact]
    public async Task MultiPageTiff_CountsLikePdf()
    {
        using var dir = new TempDir();
        var frames = Enumerable.Range(0, 51).Select(_ => Img.WhiteWithBlackSquare(4, 4, 0, 0, 1)).ToList();
        var path = dir.Write("scan.tif", Img.EncodeTiff(frames));
        var source = new PageSource();
        var ex = await Assert.ThrowsAsync<PageLimitExceededException>(
            () => source.LoadAsync([path], NoPassword, CancellationToken.None));
        Assert.Equal(51, ex.PageCount);
        Assert.Equal("scan.tif", ex.FileName);
        Assert.Equal(0, source.RasterizedPageCount);
    }

    // ---- パスワード ----

    private const string CorrectPassword = "fuseo-test-pw";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("encrypted-aes256.pdf")]
    [InlineData("encrypted-rc4-128.pdf")]
    public async Task EncryptedPdf_WrongThenRight_Succeeds(string fixture)
    {
        var answers = new Queue<string?>(["wrong-pw-1", "wrong-pw-2", CorrectPassword]);
        var asked = new List<string>();
        var pages = await new PageSource().LoadAsync([Fixture(fixture)], name =>
        {
            asked.Add(name);
            return Task.FromResult(answers.Dequeue());
        }, CancellationToken.None);

        Assert.Equal(new[] { fixture, fixture, fixture }, asked); // 回数制限なしで聞き直す（ファイル名だけを渡す）
        var p = Assert.Single(pages);
        Assert.True(p.FromPdf);
        // 200×100pt・左に 80pt 四方の黒（10..90pt）
        Assert.Equal(833, p.Bitmap.Width);
        Assert.True(Img.IsDark(p.Bitmap, (int)(50 * 300 / 72.0), (int)(50 * 300 / 72.0)));
        Assert.True(Img.IsLight(p.Bitmap, (int)(150 * 300 / 72.0), (int)(50 * 300 / 72.0)));
    }

    [Fact]
    public async Task EncryptedPdf_Cancel_AbortsWholeImport_AndNeverLeaksPassword()
    {
        using var dir = new TempDir();
        var before = dir.Write("before.png", TinyPng());
        var after = dir.Write("after.png", TinyPng());
        var answers = new Queue<string?>(["wrong-secret-pw", null]);
        var source = new PageSource();
        var ex = await Assert.ThrowsAsync<ImportCancelledException>(() => source.LoadAsync(
            [before, Fixture("encrypted-aes256.pdf"), after],
            _ => Task.FromResult(answers.Dequeue()), CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal("encrypted-aes256.pdf", ex.FileName);
        Assert.Equal(0, source.RasterizedPageCount);
        Assert.DoesNotContain("wrong-secret-pw", ex.ToString());
        Assert.DoesNotContain(CorrectPassword, ex.ToString());
    }

    [Fact]
    public async Task UnencryptedPdf_NeverAsksPassword()
    {
        using var dir = new TempDir();
        var path = dir.Write("plain.pdf", TinyPdf(1));
        Assert.Single(await new PageSource().LoadAsync([path], NoPassword, CancellationToken.None));
    }

    // ---- 壊れた・対応外のファイル ----

    private static readonly byte[] Garbage = Enumerable.Range(0, 512).Select(i => (byte)(i * 37 + 11)).ToArray();

    [Theory]
    [InlineData("broken.pdf")]
    [InlineData("broken.png")]
    [InlineData("broken.jpg")]
    [InlineData("broken.tif")]
    public async Task Garbage_IsUnreadable(string name)
    {
        using var dir = new TempDir();
        var path = dir.Write(name, Garbage);
        var ex = await Assert.ThrowsAsync<UnreadableFileException>(
            () => new PageSource().LoadAsync([path], NoPassword, CancellationToken.None));
        Assert.Equal(name, ex.FileName);
    }

    [Fact]
    public async Task EmptyPdf_IsUnreadable()
    {
        using var dir = new TempDir();
        var path = dir.Write("empty.pdf", []);
        await Assert.ThrowsAsync<UnreadableFileException>(
            () => new PageSource().LoadAsync([path], NoPassword, CancellationToken.None));
    }

    [Fact]
    public async Task MissingFile_IsUnreadable()
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UnreadableFileException>(() => new PageSource().LoadAsync(
            [Path.Combine(dir.Path, "nope.png")], NoPassword, CancellationToken.None));
        Assert.Equal("nope.png", ex.FileName);
    }

    [Fact]
    public async Task UndecodableHeic_ReportsHeicSpecificError()
    {
        // HEIF 拡張の有無に関わらず、デコードできない .heic は専用の例外になる。
        using var dir = new TempDir();
        var path = dir.Write("IMG_0001.HEIC", Garbage);
        var ex = await Assert.ThrowsAsync<HeicNotSupportedException>(
            () => new PageSource().LoadAsync([path], NoPassword, CancellationToken.None));
        Assert.Equal("IMG_0001.HEIC", ex.FileName);
        Assert.Contains("HEIF", ex.Message);
    }

    [Theory]
    [InlineData("anim.gif", ".gif")]
    [InlineData("notes.txt", ".txt")]
    [InlineData("noext", "")]
    public async Task UnsupportedExtension_IsRejected(string name, string ext)
    {
        using var dir = new TempDir();
        var path = dir.Write(name, Garbage);
        var ex = await Assert.ThrowsAsync<UnsupportedFileTypeException>(
            () => new PageSource().LoadAsync([path], NoPassword, CancellationToken.None));
        Assert.Equal(ext, ex.Extension);
        Assert.False(PageSource.IsSupported(path));
    }

    [Theory]
    [InlineData("a.PDF")] [InlineData("a.jpeg")] [InlineData("a.JPG")] [InlineData("a.png")]
    [InlineData("a.bmp")] [InlineData("a.tif")] [InlineData("a.tiff")] [InlineData("a.heic")] [InlineData("a.heif")]
    public void SupportedExtensions(string name) => Assert.True(PageSource.IsSupported(name));

    // ---- 取り消し ----

    [Fact]
    public async Task CancelledToken_Throws()
    {
        using var dir = new TempDir();
        var path = dir.Write("ok.pdf", TinyPdf(3));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PageSource().LoadAsync([path], NoPassword, cts.Token));
    }

    [Fact]
    public async Task EmptyList_ReturnsEmpty()
    {
        Assert.Empty(await new PageSource().LoadAsync([], NoPassword, CancellationToken.None));
    }
}
