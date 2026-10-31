using System.IO;
using System.Text;
using Fuseo.App.Library;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>差し替え可能な待ち時間（テストで「1 秒たった」を起こす）。</summary>
internal sealed class ManualDelay
{
    private readonly List<TaskCompletionSource> _waits = [];
    public int Requested { get { lock (_waits) return _waits.Count; } }

    public Task Delay(TimeSpan span, CancellationToken ct)
    {
        Assert.Equal(AutoSaver.Debounce, span);
        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_waits) _waits.Add(tcs);
        return tcs.Task;
    }

    /// <summary>待っているものをすべて終わらせる（取り消し済みのものはそのまま）。</summary>
    public void FireAll()
    {
        List<TaskCompletionSource> all;
        lock (_waits) all = [.. _waits];
        foreach (var t in all) t.TrySetResult();
    }
}

/// <summary>書いた回数を数え、指定回目で失敗させられる codec。</summary>
internal sealed class CountingCodec : IPageImageCodec
{
    private readonly WicPageImageCodec _inner = new();
    private int _png, _thumb;
    public int PngWrites => Volatile.Read(ref _png);
    public int ThumbnailWrites => Volatile.Read(ref _thumb);
    public int? FailPngAt { get; set; }
    public int? FailThumbnailAt { get; set; }

    public void WritePng(PageBitmap bitmap, Stream destination)
    {
        var n = Interlocked.Increment(ref _png);
        if (n == FailPngAt)
        {
            destination.WriteByte(0x89); // 書きかけで落ちる
            throw new IOException("書き込みに失敗（テスト）");
        }
        _inner.WritePng(bitmap, destination);
    }

    public void WriteThumbnailJpeg(PageBitmap bitmap, int maxSide, Stream destination)
    {
        var n = Interlocked.Increment(ref _thumb);
        if (n == FailThumbnailAt) throw new IOException("サムネイルの書き込みに失敗（テスト）");
        _inner.WriteThumbnailJpeg(bitmap, maxSide, destination);
    }

    public PageBitmap ReadPng(Stream source) => _inner.ReadPng(source);
}

/// <summary>W5: 自動保存ライブラリ（ファイルストア・原子性・パス検証・復元の一致）。</summary>
public class FileWorkLibraryTests
{
    private static readonly Dictionary<DocumentType, DocumentPreset> Presets =
        PresetStore.LoadAll().ToDictionary(p => p.DocumentType);

    /// <summary>合成画像（模様＋透明・半透明の画素を含む）。実在の書類は使わない。</summary>
    internal static PageBitmap Synthetic(int w, int h, int seed = 0)
    {
        var bmp = new PageBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            byte a = (x + seed) % 17 == 0 ? (byte)0 : (y % 13 == 0 ? (byte)128 : (byte)255);
            bmp.SetPixel(x, y, (byte)(x * 7 + seed), (byte)(y * 5), (byte)((x ^ y) + seed), a);
        }
        return bmp;
    }

    /// <summary>編集済みのページ（回転・種別・候補・手動矩形・ストローク・チェック印つき）。</summary>
    internal static PageState EditedPage(string name = "card.png", int w = 60, int h = 40, int seed = 1)
    {
        var p = new PageState(Synthetic(w, h, seed), name, 2, fromPdf: true);
        p.InitializeDetections(DetectorId.QrBarcode, [new NormRect(0.6, 0.5, 0.2, 0.3)], Presets[DocumentType.Generic]);
        p.ChangeType(DocumentType.MyNumberCardBack, Presets[DocumentType.MyNumberCardBack]);
        p.AddRect(new NormRect(0.1, 0.1, 0.3, 0.2));
        p.RotateClockwise();
        p.AddStroke(new BrushStroke([new NormPoint(0.2, 0.7), new NormPoint(0.5, 0.75)], 0.06));
        p.SetCandidateOn(p.Candidates[0].Id, false);
        p.SetCandidateOn(p.Candidates[0].Id, true);
        p.CheckedRuleIds.Add("mynumB.name");
        return p;
    }

    private static WorkSnapshot Capture(Guid id, IReadOnlyList<PageState> pages, WorkStatus status = WorkStatus.InProgress) =>
        WorkMapper.Capture(id, WorkMapper.Title(pages, "{0} ほか {1} 件"), new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
                           DateTimeOffset.UtcNow, status, status == WorkStatus.Exported ? "out.pdf" : null, pages);

    private static void Save(FileWorkLibrary lib, WorkSnapshot s) => lib.Save(s.Document, s.Images);

    private static string[] Temps(string root) => Directory.GetFiles(root, "*" + FileWorkLibrary.TempSuffix, SearchOption.AllDirectories);

    [Fact]
    public async Task Save_WhileAnotherProcessBrieflyHoldsDocument_RetriesAndSucceeds()
    {
        // ウイルス対策などが document.json を一瞬開いていると置き換えが拒まれる。少し待ってやり直し、最後の編集を失わない。
        using var dir = new TempDir();
        var pages = new[] { EditedPage("a.png") };
        var id = Guid.NewGuid();
        var lib = new FileWorkLibrary(dir.Path);
        Save(lib, Capture(id, pages, WorkStatus.InProgress));
        var json = Path.Combine(dir.Path, id.ToString("D"), FileWorkLibrary.DocumentFileName);
        Assert.True(File.Exists(json));

        var holder = new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.Read); // File.ReadAllBytes と同じ開き方
        var release = Task.Run(async () => { await Task.Delay(300); holder.Dispose(); });
        Save(lib, Capture(id, pages, WorkStatus.Exported));
        await release;

        Assert.Equal(WorkStatus.Exported, new FileWorkLibrary(dir.Path).Load(id).Document.Status);
        Assert.Empty(Temps(dir.Path));
    }

    [Fact]
    public void SaveThenLoad_InNewInstance_RestoresEverything()
    {
        using var dir = new TempDir();
        var pages = new[] { EditedPage("a.png"), EditedPage("b.pdf", 30, 50, 7) };
        var id = Guid.NewGuid();
        Save(new FileWorkLibrary(dir.Path), Capture(id, pages, WorkStatus.Exported));

        var loaded = new FileWorkLibrary(dir.Path).Load(id);
        Assert.Equal(WorkStatus.Exported, loaded.Document.Status);
        Assert.Equal("out.pdf", loaded.Document.LastExportedName);
        Assert.Equal("a.png ほか 1 件", loaded.Document.Title);
        var restored = WorkMapper.Restore(loaded);
        Assert.Equal(pages.Length, restored.Count);
        var rasterizer = new MaskRasterizer();
        for (var i = 0; i < pages.Length; i++)
        {
            var a = pages[i];
            var b = restored[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.SourceName, b.SourceName);
            Assert.Equal(a.SourcePageIndex, b.SourcePageIndex);
            Assert.Equal(a.FromPdf, b.FromPdf);
            Assert.Equal(a.Type, b.Type);
            Assert.Equal(a.Rotation, b.Rotation);
            Assert.Equal(a.Manual.Rects, b.Manual.Rects);
            Assert.Equal(a.Manual.Strokes.Count, b.Manual.Strokes.Count);
            for (var k = 0; k < a.Manual.Strokes.Count; k++)
            {
                Assert.Equal(a.Manual.Strokes[k].Points, b.Manual.Strokes[k].Points);
                Assert.Equal(a.Manual.Strokes[k].Width, b.Manual.Strokes[k].Width);
            }
            Assert.Equal(a.Candidates, b.Candidates);
            Assert.Equal(a.RawDetections, b.RawDetections);
            Assert.Equal(a.RawDetector, b.RawDetector);
            Assert.Equal(a.CheckedRuleIds.OrderBy(x => x), b.CheckedRuleIds.OrderBy(x => x));
            Assert.False(b.CanUndo); // 履歴は保存しない
            Assert.False(b.CanRedo);

            // 元画像（α を含む）が画素単位で一致 → 回した基準画像も一致
            Assert.Equal(a.Original.Pixels, b.Original.Pixels);
            Assert.Equal((a.Base.Width, a.Base.Height), (b.Base.Width, b.Base.Height));
            Assert.Equal(a.Base.Pixels, b.Base.Pixels);

            // 焼き込みに渡すマスクと、焼き込みの結果が一致
            var ma = a.BuildConfirmedMasks();
            var mb = b.BuildConfirmedMasks();
            Assert.Equal(ma.Rects, mb.Rects);
            Assert.Equal(ma.Strokes.Select(s => (s.Width, string.Join(";", s.Points))),
                         mb.Strokes.Select(s => (s.Width, string.Join(";", s.Points))));
            Assert.Equal(rasterizer.BurnIn(a.Base, ma).Bitmap.Pixels, rasterizer.BurnIn(b.Base, mb).Bitmap.Pixels);
        }
    }

    [Fact]
    public void PageImages_AreWrittenOnlyOnce_JsonEveryTime()
    {
        using var dir = new TempDir();
        var codec = new CountingCodec();
        var lib = new FileWorkLibrary(dir.Path, codec);
        var page = EditedPage();
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [page]));
        Assert.Equal(1, codec.PngWrites);
        Assert.Equal(1, codec.ThumbnailWrites);
        var json1 = File.ReadAllText(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName));

        page.AddRect(new NormRect(0.5, 0.5, 0.1, 0.1));
        page.RotateClockwise();
        Save(lib, Capture(id, [page]));
        Assert.Equal(1, codec.PngWrites);       // 元画像は不変なので書き直さない
        Assert.Equal(1, codec.ThumbnailWrites);
        var json2 = File.ReadAllText(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName));
        Assert.NotEqual(json1, json2);
        Assert.Contains("\"manualRotation\": 2", json2);
        Assert.Empty(Temps(dir.Path));
    }

    [Fact]
    public void Layout_IsDocumentJsonPagesAndThumbs()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var page = EditedPage(w: 800, h: 400);
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [page]));
        var work = lib.DirectoryOf(id);
        Assert.True(File.Exists(Path.Combine(work, "document.json")));
        Assert.True(File.Exists(Path.Combine(work, "pages", $"{page.Id:D}.png")));
        var thumb = Path.Combine(work, "thumbs", $"{page.Id:D}.jpg");
        Assert.True(File.Exists(thumb));
        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(thumb)),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.Equal(FileWorkLibrary.ThumbnailMaxSide, dec.Frames[0].PixelWidth);
        Assert.Equal(160, dec.Frames[0].PixelHeight);
        // JSON は UTF-8・BOM なし・日本語をそのまま
        var bytes = File.ReadAllBytes(Path.Combine(work, "document.json"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("\"schemaVersion\": \"fuseo-win/1\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void FailureWhileWritingPageImage_KeepsExistingDocument_AndLeavesNoTemp()
    {
        using var dir = new TempDir();
        var codec = new CountingCodec();
        var lib = new FileWorkLibrary(dir.Path, codec);
        var first = EditedPage("a.png");
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [first]));
        var jsonPath = Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName);
        var before = File.ReadAllBytes(jsonPath);

        codec.FailPngAt = 2; // 2 ページ目の画像を書く途中で失敗
        var second = EditedPage("b.png", seed: 3);
        Assert.ThrowsAny<Exception>(() => Save(lib, Capture(id, [first, second])));
        Assert.Equal(before, File.ReadAllBytes(jsonPath));  // 既存の document.json は壊れない
        Assert.Empty(Temps(dir.Path));                       // 一時ファイルも残らない
        Assert.False(File.Exists(Path.Combine(lib.DirectoryOf(id), "pages", $"{second.Id:D}.png")));

        var summary = Assert.Single(lib.List());
        Assert.True(summary.IsReadable);
        Assert.Equal(1, summary.PageCount);
        Assert.Single(WorkMapper.Restore(lib.Load(id)));
    }

    [Fact]
    public void FailureWhileWritingThumbnail_KeepsExistingDocument()
    {
        using var dir = new TempDir();
        var codec = new CountingCodec { FailThumbnailAt = 1 };
        var lib = new FileWorkLibrary(dir.Path, codec);
        var id = Guid.NewGuid();
        Assert.ThrowsAny<Exception>(() => Save(lib, Capture(id, [EditedPage()])));
        Assert.False(File.Exists(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName)));
        Assert.Empty(Temps(dir.Path));
        // 初回保存の途中で落ちた作業は「読めない作業」として出る（削除できる）
        var row = Assert.Single(lib.List());
        Assert.False(row.IsReadable);
        lib.Delete(id);
        Assert.Empty(lib.List());
    }

    [Fact]
    public void LeftoverTemporaries_AreCleanedWhenListing()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [EditedPage()]));
        var work = lib.DirectoryOf(id);
        File.WriteAllText(Path.Combine(work, "document.json.0123" + FileWorkLibrary.TempSuffix), "{");
        File.WriteAllBytes(Path.Combine(work, "pages", "x.png.4567" + FileWorkLibrary.TempSuffix), [1, 2, 3]);
        Assert.Equal(2, Temps(dir.Path).Length);
        Assert.True(Assert.Single(lib.List()).IsReadable);
        Assert.Empty(Temps(dir.Path));
    }

    [Fact]
    public void OrphanImages_AreDeletedAfterSave()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var a = EditedPage("a.png");
        var b = EditedPage("b.png", seed: 5);
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [a, b]));
        var work = lib.DirectoryOf(id);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(work, "pages")).Length);

        Save(lib, Capture(id, [b])); // a を消した
        Assert.Equal([Path.Combine(work, "pages", $"{b.Id:D}.png")], Directory.GetFiles(Path.Combine(work, "pages")));
        Assert.Equal([Path.Combine(work, "thumbs", $"{b.Id:D}.jpg")], Directory.GetFiles(Path.Combine(work, "thumbs")));
    }

    [Fact]
    public void Save_RequiresImageForNewPage()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var s = Capture(Guid.NewGuid(), [EditedPage()]);
        Assert.Throws<WorkLibraryException>(() => lib.Save(s.Document, new Dictionary<string, PageBitmap>()));
        Assert.Empty(Temps(dir.Path));
    }

    // ---- パス検証 ----

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("pages/../../evil.png")]
    [InlineData("..\\evil.png")]
    [InlineData("C:/Windows/evil.png")]
    [InlineData("C:evil.png")]
    [InlineData("/evil.png")]
    [InlineData("\\\\server\\share\\evil.png")]
    [InlineData("pages/a.png:ads")]
    public void Resolve_RejectsPathsOutsideWorkFolder(string relative)
    {
        using var dir = new TempDir();
        Assert.Throws<WorkLibraryException>(() => FileWorkLibrary.Resolve(dir.Path, relative));
    }

    [Fact]
    public void Resolve_AcceptsPathsInsideWorkFolder()
    {
        using var dir = new TempDir();
        Assert.Equal(Path.Combine(dir.Path, "pages", "a.png"), FileWorkLibrary.Resolve(dir.Path, "pages/a.png"));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("C:/")]
    [InlineData("pages/../../")]
    public void WorkWithOutsidePath_IsUnreadable_AndCannotBeLoaded(string prefix)
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var id = Guid.NewGuid();
        Save(lib, Capture(id, [EditedPage()]));
        var jsonPath = Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName);
        File.WriteAllText(jsonPath, File.ReadAllText(jsonPath).Replace("\"imageFile\": \"pages/", $"\"imageFile\": \"{prefix}"));
        Assert.False(Assert.Single(lib.List()).IsReadable);
        Assert.Throws<WorkLibraryException>(() => lib.Load(id));
    }

    // ---- 壊れた作業 ----

    [Fact]
    public void BrokenWorks_AreListedAsUnreadable_AndCanBeDeleted()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var good = Guid.NewGuid();
        Save(lib, Capture(good, [EditedPage()]));

        Guid Broken(Action<string> damage)
        {
            var id = Guid.NewGuid();
            Save(lib, Capture(id, [EditedPage(seed: 9)]));
            damage(lib.DirectoryOf(id));
            return id;
        }
        var garbage = Broken(d => File.WriteAllText(Path.Combine(d, "document.json"), "{ not json"));
        var otherVersion = Broken(d =>
        {
            var p = Path.Combine(d, "document.json");
            File.WriteAllText(p, File.ReadAllText(p).Replace("fuseo-win/1", "fuseo-win/9"));
        });
        var noImage = Broken(d => File.Delete(Directory.GetFiles(Path.Combine(d, "pages"))[0]));
        var noJson = Broken(d => File.Delete(Path.Combine(d, "document.json")));
        var wrongId = Broken(d =>
        {
            var p = Path.Combine(d, "document.json");
            File.WriteAllText(p, File.ReadAllText(p).Replace(Path.GetFileName(d), Guid.NewGuid().ToString("D")));
        });
        Directory.CreateDirectory(Path.Combine(dir.Path, "not-a-work")); // 自分の物でないフォルダは無視

        var rows = lib.List();
        Assert.Equal(6, rows.Count);
        Assert.True(rows.Single(r => r.Id == good).IsReadable);
        foreach (var id in new[] { garbage, otherVersion, noImage, noJson, wrongId })
        {
            var row = rows.Single(r => r.Id == id);
            Assert.False(row.IsReadable);
            Assert.False(string.IsNullOrEmpty(row.Problem));
            Assert.Throws<WorkLibraryException>(() => lib.Load(id));
        }

        lib.Delete(garbage);
        Assert.Equal(5, lib.List().Count);
        lib.DeleteAll();
        Assert.Empty(lib.List());
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "not-a-work")));
    }

    [Fact]
    public void List_IsNewestFirst_AndEmptyWhenRootMissing()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(Path.Combine(dir.Path, "nothing-yet"));
        Assert.Empty(lib.List());
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var p = EditedPage();
        lib.Save(Capture(older, [p]).Document with { UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                 Capture(older, [p]).Images);
        lib.Save(Capture(newer, [p]).Document with { UpdatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero) },
                 Capture(newer, [p]).Images);
        Assert.Equal([newer, older], lib.List().Select(r => r.Id));
        var row = lib.List()[0];
        Assert.Equal(1, row.PageCount);
        Assert.Equal(p.Rotation, row.ThumbnailRotation);
        Assert.True(File.Exists(row.ThumbnailPath));
    }

    [Fact]
    public void Codec_PngRoundTrip_IsLossless_IncludingAlpha()
    {
        var codec = new WicPageImageCodec();
        var bmp = Synthetic(37, 23, 4);
        using var ms = new MemoryStream();
        codec.WritePng(bmp, ms);
        ms.Position = 0;
        var back = codec.ReadPng(ms);
        Assert.Equal((bmp.Width, bmp.Height), (back.Width, back.Height));
        Assert.Equal(bmp.Pixels, back.Pixels);
    }

    [Fact]
    public void DefaultRoot_IsUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Fuseo", "Library"), FileWorkLibrary.DefaultRoot);
    }

    [Theory]
    [InlineData(new[] { "a.png" }, "a.png")]
    [InlineData(new[] { "a.pdf", "a.pdf", "a.pdf" }, "a.pdf")]
    [InlineData(new[] { "a.pdf", "b.jpg", "a.pdf", "c.png" }, "a.pdf ほか 2 件")]
    public void Title_FirstNamePlusOtherFiles(string[] names, string expected)
    {
        var pages = names.Select(n => new PageState(PageBitmap.Solid(4, 4, 0, 0, 0), n, 0, false)).ToList();
        Assert.Equal(expected, WorkMapper.Title(pages, "{0} ほか {1} 件"));
    }

    /// <summary>
    /// 所要時間の計測（既定ではすぐ終わる）。FUSEO_PERF=1 のときだけ 50 ページ・4000px 級で、
    /// ページ画像の初回書き込みと JSON だけの保存の時間を出力する（合成画像のみ）。
    /// </summary>
    [Fact]
    public void Perf_FiftyLargePages()
    {
        if (Environment.GetEnvironmentVariable("FUSEO_PERF") != "1") return;
        var lines = new List<string>();
        foreach (var noisy in new[] { false, true }) lines.Add((noisy ? "noise " : "pattern ") + MeasureFifty(noisy));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fuseo-w5-perf.txt"), lines);
    }

    private static string MeasureFifty(bool noisy)
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var basePage = Synthetic(4000, 2829, 3);
        if (noisy)
        {
            // 写真のように圧縮が効きにくい画像（PNG が大きくなる側の目安）
            var rng = new Random(42);
            for (var i = 0; i < basePage.Pixels.Length; i += 4)
            {
                basePage.Pixels[i] = (byte)rng.Next(256); basePage.Pixels[i + 1] = (byte)rng.Next(256);
                basePage.Pixels[i + 2] = (byte)rng.Next(256); basePage.Pixels[i + 3] = 255;
            }
        }
        var pages = Enumerable.Range(0, 50).Select(i =>
        {
            var p = new PageState(basePage, $"p{i}.pdf", i, true);
            p.AddRect(new NormRect(0.1, 0.1, 0.2, 0.05));
            return p;
        }).ToList();
        var id = Guid.NewGuid();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = Capture(id, pages);
        var capture = sw.Elapsed;
        sw.Restart();
        Save(lib, snap);
        var first = sw.Elapsed;
        pages[0].AddRect(new NormRect(0.5, 0.5, 0.1, 0.1));
        sw.Restart();
        Save(lib, Capture(id, pages));
        var jsonOnly = sw.Elapsed;
        sw.Restart();
        var loaded = lib.Load(id);
        var load = sw.Elapsed;
        Assert.Equal(50, loaded.Images.Count);
        var size = Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return (
            $"capture={capture.TotalMilliseconds:F0}ms first={first.TotalMilliseconds:F0}ms jsonOnly={jsonOnly.TotalMilliseconds:F0}ms " +
            $"load={load.TotalMilliseconds:F0}ms bytes={size} cores={Environment.ProcessorCount} parallel={FileWorkLibrary.MaxWriteParallelism}");
    }
}

/// <summary>W5: アプリへの組み込み（デバウンス・状態遷移・新しい書類・一覧・開く・削除）。</summary>
public class AutoSaveViewModelTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ManualDelay _delay = new();

    public AutoSaveViewModelTests()
    {
        _source.Handler = paths => paths
            .Select((p, i) => new SourcePage(FileWorkLibraryTests.Synthetic(40, 30, i + p.Length), p, 0, false)).ToList();
    }

    private MainViewModel NewVm(FileWorkLibrary lib) =>
        new(_source, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs, () => new DateTime(2026, 10, 13),
            library: lib, saveDelay: _delay.Delay);

    /// <summary>「1 秒たった」を起こし、書き込みが終わるまで待つ。</summary>
    private async Task Tick(MainViewModel vm)
    {
        _delay.FireAll();
        await vm.FlushSaveAsync();
    }

    private static WorkDocument ReadDoc(FileWorkLibrary lib, Guid id) =>
        WorkDocumentJson.Parse(File.ReadAllBytes(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName)));

    [Fact]
    public async Task AutoSaver_Debounces_ToOneWrite()
    {
        var writes = 0;
        var captures = 0;
        var saver = new AutoSaver(() =>
        {
            captures++;
            return new WorkSnapshot(new WorkDocument(), new Dictionary<string, PageBitmap>());
        }, _ => Interlocked.Increment(ref writes), _delay.Delay);
        for (var i = 0; i < 5; i++) saver.Schedule();
        Assert.Equal(5, _delay.Requested);
        Assert.True(saver.HasPending);
        Assert.Equal(0, writes);
        _delay.FireAll();
        await saver.FlushAsync();
        Assert.Equal(1, writes);
        Assert.Equal(1, captures);
        Assert.False(saver.IsBusy);
    }

    [Fact]
    public async Task AutoSaver_Flush_SavesPendingImmediately_AndReportsFailure()
    {
        Exception? reported = null;
        var saver = new AutoSaver(() => new WorkSnapshot(new WorkDocument(), new Dictionary<string, PageBitmap>()),
                                  _ => throw new IOException("disk full"), _delay.Delay);
        saver.Saved += e => reported = e;
        saver.Schedule();
        await saver.FlushAsync();
        Assert.IsType<IOException>(reported);
        Assert.False(saver.HasPending);
        _delay.FireAll(); // 取り消し済みの予約は何もしない
        await saver.FlushAsync();
    }

    [Fact]
    public async Task Edits_AreSavedOnceAfterDebounce()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        vm.AddRect(new NormRect(0.5, 0.5, 0.2, 0.2));
        vm.RotateRightCommand.Execute(null);
        Assert.True(vm.HasPendingSave);
        Assert.Empty(lib.List()); // まだ書いていない（デバウンス中）

        await Tick(vm);
        Assert.False(vm.HasPendingSave);
        var doc = ReadDoc(lib, vm.CurrentWorkId!.Value);
        Assert.Equal(2, doc.Pages[0].Manual.Rects.Count);
        Assert.Equal(1, doc.Pages[0].ManualRotation);
        Assert.Equal(WorkStatus.InProgress, doc.Status);
        Assert.Equal("a.png", doc.Title);
    }

    [Fact]
    public async Task EveryKindOfChange_SchedulesSave()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png", "b.png"]);

        async Task Expect(Action change, Func<WorkDocument, bool> saved)
        {
            await Tick(vm);
            change();
            Assert.True(vm.HasPendingSave);
            await Tick(vm);
            Assert.True(saved(ReadDoc(lib, vm.CurrentWorkId!.Value)));
        }

        vm.SelectedPage = vm.Pages[0];
        await Expect(() => vm.AddStroke([new NormPoint(0.2, 0.2), new NormPoint(0.3, 0.3)]),
                     d => d.Pages[0].Manual.Strokes.Count == 1);
        await Expect(() => vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardFront),
                     d => d.Pages[0].DocumentType == DocumentType.MyNumberCardFront);
        await Expect(() => vm.Checklist.First(c => c.HasCheckBox).IsChecked = true,
                     d => d.Pages[0].CheckedRuleIds.Count == 1);
        await Expect(() => vm.RotateLeftCommand.Execute(null), d => d.Pages[0].ManualRotation == 3);
        await Expect(() => vm.UndoCommand.Execute(null), d => d.Pages[0].ManualRotation == 0);
        await Expect(() => vm.MovePageDownCommand.Execute(null), d => d.Pages[0].SourceName == "b.png");
        vm.SelectedPage = vm.Pages[0];
        await Expect(() => vm.RemovePageCommand.Execute(null), d => d.Pages.Count == 1 && d.Pages[0].SourceName == "a.png");
    }

    [Fact]
    public async Task CandidateToggle_IsSaved()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = new MainViewModel(_source, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs,
                                   detector: new FakeDetector(new NormRect(0.4, 0.4, 0.2, 0.2)), library: lib, saveDelay: _delay.Delay);
        await vm.ImportAsync(["back.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        await Tick(vm);
        var cand = Assert.Single(vm.CandidateItems);
        var initial = cand.IsOn;
        cand.IsOn = !initial;
        await Tick(vm);
        var saved = Assert.Single(ReadDoc(lib, vm.CurrentWorkId!.Value).Pages[0].Candidates);
        Assert.Equal(!initial, saved.IsOn);
        Assert.Equal(DetectorId.QrBarcode, ReadDoc(lib, vm.CurrentWorkId!.Value).Pages[0].RawDetector);
    }

    [Fact]
    public async Task Status_InProgress_Exported_ThenEditedBackToInProgress()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        await Tick(vm);
        var id = vm.CurrentWorkId!.Value;
        Assert.Equal(WorkStatus.InProgress, ReadDoc(lib, id).Status);

        _dialogs.SavePath = outDir.File("提出用.pdf");
        _dialogs.OnShowExport = e =>
        {
            e.PrepareAsync().GetAwaiter().GetResult();
            e.SaveAsync().GetAwaiter().GetResult();
        };
        vm.ExportCommand.Execute(null);
        Assert.Equal(WorkStatus.Exported, vm.CurrentWorkStatus);
        await Tick(vm);
        var exported = ReadDoc(lib, id);
        Assert.Equal(WorkStatus.Exported, exported.Status);
        Assert.Equal("提出用.pdf", exported.LastExportedName);

        vm.AddRect(new NormRect(0.5, 0.5, 0.2, 0.2));
        await Tick(vm);
        var edited = ReadDoc(lib, id);
        Assert.Equal(WorkStatus.InProgress, edited.Status);
        Assert.Equal("提出用.pdf", edited.LastExportedName); // 直近の書き出し名は残す
        Assert.Equal(exported.CreatedAt, edited.CreatedAt);
        Assert.True(edited.UpdatedAt >= exported.UpdatedAt);
    }

    [Fact]
    public async Task CancelledExport_DoesNotMarkExported()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        _dialogs.OnShowExport = _ => { }; // 保存せずに閉じた
        vm.ExportCommand.Execute(null);
        Assert.Equal(WorkStatus.InProgress, vm.CurrentWorkStatus);
    }

    [Fact]
    public async Task NewDocument_KeepsWorkInLibrary_ThenOpenRestoresMasks()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png", "b.jpg"]);
        vm.AddRect(new NormRect(0.1, 0.2, 0.3, 0.2));
        vm.RotateRightCommand.Execute(null);
        var beforeBase = vm.Pages[0].State.Base.Pixels;
        var beforeMasks = vm.Pages[0].State.BuildConfirmedMasks();
        var id = vm.CurrentWorkId!.Value;
        Assert.True(vm.NewDocumentCommand.CanExecute(null));

        await vm.NewDocumentAsync(); // デバウンスを待たずに確定する
        Assert.Empty(vm.Pages);
        Assert.Null(vm.CurrentWorkId);
        Assert.False(vm.NewDocumentCommand.CanExecute(null));
        var item = Assert.Single(vm.LibraryItems);
        Assert.True(vm.IsLibraryVisible);
        Assert.Equal(id, item.Id);
        Assert.Equal("a.png ほか 1 件", item.Title);
        Assert.Equal(2, item.PageCount);
        Assert.Equal(WorkStatus.InProgress, item.Status);
        Assert.NotNull(item.Thumbnail);
        Assert.False(string.IsNullOrEmpty(item.UpdatedText));
        Assert.True(item.OpenCommand.CanExecute(null));

        // 2 件目
        await vm.ImportAsync(["c.png"]);
        await vm.NewDocumentAsync();
        Assert.Equal(2, vm.LibraryItems.Count);
        Assert.Equal("c.png", vm.LibraryItems[0].Title); // 新しい順

        // 開く: 塗りが残っている・基準画像とマスクが一致・検出は走らない・保存も起きない
        var calls = _source.Calls.Count;
        await vm.OpenWorkAsync(vm.LibraryItems.Single(i => i.Id == id));
        Assert.Equal(calls, _source.Calls.Count);
        Assert.Equal(2, vm.Pages.Count);
        Assert.Equal(id, vm.CurrentWorkId);
        Assert.Same(vm.Pages[0], vm.SelectedPage);
        Assert.Equal(1, vm.Pages[0].State.Rotation);
        Assert.Equal(beforeBase, vm.Pages[0].State.Base.Pixels);
        Assert.Equal(beforeMasks.Rects, vm.Pages[0].State.BuildConfirmedMasks().Rects);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.HasPendingSave);
        Assert.False(vm.IsLibraryVisible);
        Assert.False(item.OpenCommand.CanExecute(null)); // 開いている間は一覧を操作しない

        // 開いた作業を編集すると同じ作業に保存される（増えない）
        vm.AddRect(new NormRect(0.6, 0.6, 0.1, 0.1));
        await vm.NewDocumentAsync();
        Assert.Equal(2, vm.LibraryItems.Count);
        Assert.Equal(id, vm.LibraryItems[0].Id);
        Assert.Equal(2, ReadDoc(lib, id).Pages[0].Manual.Rects.Count);
    }

    [Fact]
    public async Task LibraryCommands_DisabledWhileImporting()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        await vm.NewDocumentAsync();
        var item = Assert.Single(vm.LibraryItems);
        var gate = new TaskCompletionSource<IReadOnlyList<SourcePage>>();
        var blocking = new BlockingSource(gate.Task);
        var vm2 = new MainViewModel(blocking, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs,
                                    library: lib, saveDelay: _delay.Delay);
        await vm2.RefreshLibraryAsync();
        var item2 = Assert.Single(vm2.LibraryItems);
        Assert.True(item2.OpenCommand.CanExecute(null));
        var importing = vm2.ImportAsync(["slow.png"]);
        Assert.True(vm2.IsBusy);
        Assert.False(item2.OpenCommand.CanExecute(null));
        Assert.False(item2.DeleteCommand.CanExecute(null));
        Assert.False(vm2.DeleteAllWorksCommand.CanExecute(null));
        gate.SetResult([new SourcePage(PageBitmap.Solid(4, 4, 1, 1, 1), "slow.png", 0, false)]);
        await importing;
        Assert.False(item2.OpenCommand.CanExecute(null)); // ページが開いている
        Assert.NotNull(item);
    }

    private sealed class BlockingSource(Task<IReadOnlyList<SourcePage>> result) : IPageSource
    {
        public Task<IReadOnlyList<SourcePage>> LoadAsync(IReadOnlyList<string> paths,
            Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct, int pagesAlreadyOpen = 0) => result;
    }

    [Fact]
    public async Task Delete_AndDeleteAll_AskFirst()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        foreach (var n in new[] { "a.png", "b.png", "c.png" })
        {
            await vm.ImportAsync([n]);
            await vm.NewDocumentAsync();
        }
        Assert.Equal(3, vm.LibraryItems.Count);

        _dialogs.ConfirmResult = false;
        await vm.DeleteWorkAsync(vm.LibraryItems[0]);
        Assert.Equal(3, vm.LibraryItems.Count);
        Assert.Single(_dialogs.Confirms);

        _dialogs.ConfirmResult = true;
        var target = vm.LibraryItems[1];
        await vm.DeleteWorkAsync(target);
        Assert.Equal(2, vm.LibraryItems.Count);
        Assert.DoesNotContain(vm.LibraryItems, i => i.Id == target.Id);
        Assert.False(Directory.Exists(lib.DirectoryOf(target.Id)));

        _dialogs.ConfirmResult = false;
        await vm.DeleteAllWorksAsync();
        Assert.Equal(2, vm.LibraryItems.Count);
        _dialogs.ConfirmResult = true;
        Assert.True(vm.DeleteAllWorksCommand.CanExecute(null));
        await vm.DeleteAllWorksAsync();
        Assert.Empty(vm.LibraryItems);
        Assert.False(vm.IsLibraryVisible);
        Assert.Empty(lib.List());
        Assert.Contains("2", _dialogs.Confirms[^1]);
    }

    [Fact]
    public async Task UnreadableWork_IsListed_CannotOpen_CanDelete()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        var id = vm.CurrentWorkId!.Value;
        await vm.NewDocumentAsync();
        File.WriteAllText(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName), "broken");
        await vm.RefreshLibraryAsync();
        var item = Assert.Single(vm.LibraryItems);
        Assert.False(item.IsReadable);
        Assert.Equal(Fuseo.App.Properties.Strings.LibraryUnreadable, item.Title);
        Assert.False(item.OpenCommand.CanExecute(null));
        Assert.True(item.DeleteCommand.CanExecute(null));
        await vm.OpenWorkAsync(item); // 何もしない（落ちない）
        Assert.Empty(vm.Pages);
        await vm.DeleteWorkAsync(item);
        Assert.Empty(vm.LibraryItems);
    }

    [Fact]
    public async Task OpenFailure_ShowsError_AndRefreshesList()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        var id = vm.CurrentWorkId!.Value;
        await vm.NewDocumentAsync();
        var item = Assert.Single(vm.LibraryItems);
        Directory.Delete(Path.Combine(lib.DirectoryOf(id), "pages"), true); // 一覧を出した後で画像が消えた
        await vm.OpenWorkAsync(item);
        Assert.Empty(vm.Pages);
        Assert.Single(_dialogs.Errors);
        Assert.False(Assert.Single(vm.LibraryItems).IsReadable);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemovingLastPage_RemovesWorkFromLibrary()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        await Tick(vm);
        Assert.Single(lib.List());
        vm.RemovePageCommand.Execute(null);
        await vm.FlushSaveAsync();
        Assert.Empty(lib.List());
        Assert.Null(vm.CurrentWorkId);
    }

    [Fact]
    public async Task SaveFailure_IsShownInStatusBar()
    {
        using var dir = new TempDir();
        var blocker = dir.File("library-is-a-file");
        File.WriteAllText(blocker, "x"); // 保存先がファイル = フォルダを作れない
        var vm = NewVm(new FileWorkLibrary(blocker));
        await vm.ImportAsync(["a.png"]);
        await Tick(vm);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("自動保存に失敗", vm.StatusMessage);
    }

    [Fact]
    public async Task Flush_SavesPendingEdits_BeforeClose()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var vm = NewVm(lib);
        await vm.ImportAsync(["a.png"]);
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        Assert.True(vm.HasPendingSave);
        await vm.FlushSaveAsync(); // 終了時（タイマーを待たない）
        Assert.False(vm.HasPendingSave);
        Assert.Single(ReadDoc(lib, vm.CurrentWorkId!.Value).Pages[0].Manual.Rects);
    }

    [Fact]
    public async Task WithoutLibrary_NothingIsSaved()
    {
        var vm = new MainViewModel(_source, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs);
        await vm.ImportAsync(["a.png"]);
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        Assert.False(vm.HasPendingSave);
        Assert.Null(vm.CurrentWorkId);
        Assert.False(vm.IsLibraryVisible);
    }
}
