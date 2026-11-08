using System.IO;
using Fuseo.App.Library;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>W9-4: フォルダの展開（直下だけ・隠し／システム除外・対応拡張子・論理順）。</summary>
public class FolderIntakeTests
{
    private static FolderEntry F(string name, FileAttributes a = FileAttributes.Normal) => new(@"C:\d\" + name, false, a);

    [Fact]
    public void ListFolder_FiltersAndSortsLogically()
    {
        var (files, unsupported) = FolderIntake.ListFolder(
        [
            F("scan10.jpg"), F("scan2.JPG"), F("scan1.pdf"), F("notes.txt"), F("desktop.ini", FileAttributes.Hidden | FileAttributes.System),
            F(".hidden.png", FileAttributes.Hidden), F("sys.png", FileAttributes.System), new FolderEntry(@"C:\d\sub", true, FileAttributes.Directory),
            F("photo.HEIC"), F("x.tiff"), F("readme"),
        ], FolderIntake.CompareLogical);
        Assert.Equal(["photo.HEIC", "scan1.pdf", "scan2.JPG", "scan10.jpg", "x.tiff"], files.Select(Path.GetFileName));
        Assert.Equal(2, unsupported); // notes.txt・readme（隠し・システム・サブフォルダは数えない）
    }

    [Fact]
    public void Expand_KeepsFilesInOrder_AndReplacesFolderWithItsFiles()
    {
        var r = FolderIntake.Expand(["a.png", "DIR", "z.txt"], p => p == "DIR",
                                    _ => [F("b.png"), F("c.doc")], string.CompareOrdinal);
        Assert.Equal(["a.png", @"C:\d\b.png", "z.txt"], r.Files);
        Assert.Equal(1, r.Unsupported);
        Assert.True(r.ContainsFolder);
        var files = FolderIntake.Expand(["a.png"], _ => false, _ => throw new InvalidOperationException(), string.CompareOrdinal);
        Assert.False(files.ContainsFolder);
        Assert.Equal(["a.png"], files.Files);
    }

    [Fact]
    public void RealFolder_DirectFilesOnly_HiddenExcluded()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("b10.png"), [1]);
        File.WriteAllBytes(dir.File("b9.png"), [1]);
        File.WriteAllBytes(dir.File("memo.txt"), [1]);
        File.WriteAllBytes(dir.File("secret.png"), [1]);
        File.SetAttributes(dir.File("secret.png"), FileAttributes.Hidden);
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllBytes(Path.Combine(dir.File("sub"), "inner.png"), [1]);
        var r = FolderIntake.Expand([dir.Path]);
        Assert.Equal(["b9.png", "b10.png"], r.Files.Select(Path.GetFileName));
        Assert.Equal(1, r.Unsupported);
        Assert.True(r.ContainsFolder);
    }

    [Fact]
    public void SupportedExtensions_MatchOpenFilesFilter_AndPageSource()
    {
        var filter = Strings.Get(nameof(Strings.OpenFilesFilter), null).Split('|')[1].Split(';').Select(p => p.TrimStart('*')).ToList();
        Assert.Equal(filter.Order(), FolderIntake.SupportedExtensions.Order());
        foreach (var ext in FolderIntake.SupportedExtensions) Assert.True(PageSource.IsSupported("x" + ext), ext);
    }
}

/// <summary>W9-4: 「書類ごとに分ける」の名前と、上書きしない書き込み。</summary>
public class SeparateExportNamingTests
{
    [Fact]
    public void SeparateTargets_AvoidExistingAndTaken()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\o\a-masked.pdf", @"C:\o\b-masked_2.jpg" };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal([@"C:\o\a-masked-2.pdf"], ExportNaming.SeparateTargets(@"C:\o", "a.jpg", ExportFormat.Pdf, 3, existing.Contains, taken));
        // 同じ回の同名の書類どうしも重ならない（大小文字無視）
        Assert.Equal([@"C:\o\A-masked-3.pdf"], ExportNaming.SeparateTargets(@"C:\o", "A.png", ExportFormat.Pdf, 1, existing.Contains, taken));
        // 複数ページの JPEG は _1, _2 のどれか 1 つでも既存なら次の番号
        Assert.Equal([@"C:\o\b-masked-2_1.jpg", @"C:\o\b-masked-2_2.jpg"],
                     ExportNaming.SeparateTargets(@"C:\o", "b.pdf", ExportFormat.Jpeg, 2, existing.Contains, taken));
        Assert.Equal([@"C:\o\c-masked.png"], ExportNaming.SeparateTargets(@"C:\o", "c.heic", ExportFormat.Png, 1, existing.Contains, taken));
    }

    [Fact]
    public void SafeStem_ReplacesInvalidChars()
    {
        Assert.Equal("a_b", ExportNaming.SafeStem("a:b.jpg"));
        Assert.Equal("_", ExportNaming.SafeStem(".pdf"));
        Assert.Equal("免許証 表", ExportNaming.SafeStem("免許証 表.jpeg"));
    }

    private static BurnedPage Burned() => new MaskRasterizer().BurnIn(PageBitmap.Solid(4, 4, 1, 2, 3), new ConfirmedMasks([], []));

    [Fact]
    public void Writer_NoOverwrite_LeavesExistingFilesAndTempsUntouched()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("x.png"), [9]);
        var ex = Assert.ThrowsAny<IOException>(() =>
            ExportWriter.Write(new RecordingExporter(), [Burned(), Burned()], new ExportOptions(ExportFormat.Png),
                               [dir.File("w.png"), dir.File("x.png")], overwrite: false));
        Assert.NotNull(ex);
        Assert.Equal([9], File.ReadAllBytes(dir.File("x.png")));
        Assert.False(File.Exists(dir.File("w.png")));                   // 途中まで置いた分も消す
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + ExportWriter.TempSuffix));

        // 一時ファイルの名前が既にあっても、それを消したり書き換えたりしない
        File.WriteAllBytes(dir.File("y.png" + ExportWriter.TempSuffix), [7]);
        Assert.ThrowsAny<IOException>(() =>
            ExportWriter.Write(new RecordingExporter(), [Burned()], new ExportOptions(ExportFormat.Png), [dir.File("y.png")], overwrite: false));
        Assert.Equal([7], File.ReadAllBytes(dir.File("y.png" + ExportWriter.TempSuffix)));
        Assert.False(File.Exists(dir.File("y.png")));
    }
}

/// <summary>W9-4: 書類のまとまり（取り込み・保存・旧形式の読み込み）。</summary>
public class SourceDocTests
{
    private static SourcePage P(string name, int index) => new(PageBitmap.Solid(8, 6, 9, 9, 9), name, index, index > 0);

    [Fact]
    public void Import_GroupsPagesPerSourceFile()
    {
        var ids = MainViewModel.SourceDocIdsFor([P("a.pdf", 0), P("a.pdf", 1), P("a.pdf", 2), P("a.pdf", 0), P("b.jpg", 0), P("c.tif", 0), P("c.tif", 1)]);
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[1], ids[2]);
        Assert.NotEqual(ids[2], ids[3]); // 同じ名前の別ファイル（ページ番号 0 から始まる）
        Assert.NotEqual(ids[3], ids[4]);
        Assert.Equal(ids[5], ids[6]);
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public void Restore_OldWork_GroupsConsecutiveSameSourceName_AndKeepsSavedIds()
    {
        WorkPage Wp(string name, Guid? id) => new() { SourceName = name, SourceDocId = id };
        var saved = Guid.NewGuid();
        var ids = WorkMapper.SourceDocIds([Wp("a.pdf", null), Wp("a.pdf", null), Wp("b.jpg", null), Wp("a.pdf", null), Wp("c.png", saved), Wp("c.png", null)]);
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[1], ids[2]);
        Assert.NotEqual(ids[0], ids[3]); // 離れていれば別の書類
        Assert.Equal(saved, ids[4]);
        Assert.NotEqual(saved, ids[5]);
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void Capture_Restore_RoundTripsSourceDocId()
    {
        var doc = Guid.NewGuid();
        var pages = new[]
        {
            new PageState(PageBitmap.Solid(8, 6, 1, 1, 1), "a.pdf", 0, true, sourceDocId: doc),
            new PageState(PageBitmap.Solid(8, 6, 1, 1, 1), "a.pdf", 1, true, sourceDocId: doc),
        };
        var snap = WorkMapper.Capture(Guid.NewGuid(), "t", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, WorkStatus.InProgress, null, pages);
        Assert.All(snap.Document.Pages, p => Assert.Equal(doc, p.SourceDocId));
        var json = WorkDocumentJson.Parse(WorkDocumentJson.Serialize(snap.Document));
        var restored = WorkMapper.Restore(new LoadedWork(json, pages.Select(p => p.Original).ToList()));
        Assert.All(restored, s => Assert.Equal(doc, s.SourceDocId));
    }
}

/// <summary>W9-4: フォルダの取り込み・一覧・書き出しシート（書類ごとに分ける）。</summary>
public class BatchViewModelTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Dictionary<DocumentType, DocumentPreset> _presets = PresetStore.LoadAll().ToDictionary(p => p.DocumentType);

    private MainViewModel NewVm(IExporter? exporter = null) =>
        new(_source, new MaskRasterizer(), exporter ?? new RecordingExporter(), PresetStore.LoadAll(), _dialogs, () => new DateTime(2026, 11, 8));

    private static void Touch(string path) => File.WriteAllBytes(path, [1]);

    [Fact]
    public async Task OpenFolder_OpensAsNewWork_WithSkippedCount()
    {
        using var dir = new TempDir();
        Touch(dir.File("card2.jpg"));
        Touch(dir.File("card10.jpg"));
        Touch(dir.File("memo.txt"));
        var vm = NewVm();
        await vm.ImportAsync(["old.png"]);
        _dialogs.Folder = dir.Path;
        vm.OpenFolderCommand.Execute(null);
        await WaitIdle(vm);

        Assert.Equal(["card2.jpg", "card10.jpg"], vm.Pages.Select(p => Path.GetFileName(p.State.SourceName)));
        Assert.Equal(0, _source.PagesAlreadyOpen[^1]); // 新しい作業なので開いているページは 0
        Assert.Contains(Strings.StatusOpenedAsNewWork, vm.StatusMessage);
        Assert.Contains(string.Format(Strings.StatusSkippedUnsupported, 1), vm.StatusMessage);
        Assert.Equal(2, vm.Pages.Select(p => p.State.SourceDocId).Distinct().Count());
        Assert.Single(_dialogs.FolderRequests);
    }

    [Fact]
    public async Task DroppedFolderWithoutSupportedFiles_DoesNotImport_AndKeepsWork()
    {
        using var dir = new TempDir();
        Touch(dir.File("memo.txt"));
        var vm = NewVm();
        await vm.ImportAsync(["old.png"]);
        await vm.ImportAsync([dir.Path]);
        Assert.Equal(["old.png"], vm.Pages.Select(p => p.State.SourceName));
        Assert.Single(_source.Calls);
        Assert.Contains("1", Assert.Single(_dialogs.Errors).Message);
    }

    [Fact]
    public async Task FilesOnly_StillAppend()
    {
        var vm = NewVm();
        await vm.ImportAsync(["a.png"]);
        await vm.ImportAsync(["b.png"]);
        Assert.Equal(2, vm.Pages.Count);
        Assert.DoesNotContain(Strings.StatusOpenedAsNewWork, vm.StatusMessage ?? "");
    }

    private static async Task WaitIdle(MainViewModel vm)
    {
        for (var i = 0; i < 200 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Grid_ShowsCellsWithDocPosition_NoMask_AndThumbnailsWithMasks()
    {
        _source.Handler = paths =>
        [
            new SourcePage(PageBitmap.Solid(40, 20, 255, 255, 255), "a.pdf", 0, true),
            new SourcePage(PageBitmap.Solid(40, 20, 255, 255, 255), "a.pdf", 1, true),
            new SourcePage(PageBitmap.Solid(40, 20, 255, 255, 255), "b.png", 0, false),
        ];
        var vm = NewVm();
        await vm.ImportAsync(["x"]);
        vm.SelectedPage = vm.Pages[0];
        vm.AddRect(new NormRect(0, 0, 1, 1)); // 1 ページ目は全面を塗る
        Assert.Empty(vm.GridCells);           // 1 枚ずつの間はセルを作らない

        vm.IsGridView = true;
        Assert.Equal(["a.pdf（1/2）", "a.pdf（2/2）", "b.png（1/1）"], vm.GridCells.Select(c => c.Title));
        Assert.Equal([false, true, true], vm.GridCells.Select(c => c.HasNoMask));
        await vm.WhenGridThumbnailsReadyAsync();
        var thumb = vm.Pages[0].GridThumbnail!;
        Assert.True(thumb.IsFrozen); // 別スレッドで作って UI に渡せる
        Assert.Equal((0, 0, 0), CenterPixel(thumb));                 // マスクが黒で重なっている
        Assert.Equal((255, 255, 255), CenterPixel(vm.Pages[2].GridThumbnail!));
        Assert.Equal((255, 255, 255, 255), ToTuple(vm.Pages[0].State.Base.GetPixel(5, 5))); // 元は書き換えない

        // 一覧の表示中に編集したら、そのページだけ作り直す
        var other = vm.Pages[2].GridThumbnail;
        vm.UndoCommand.Execute(null);
        Assert.Equal([true, true, true], vm.GridCells.Select(c => c.HasNoMask));
        await vm.WhenGridThumbnailsReadyAsync();
        Assert.Equal((255, 255, 255), CenterPixel(vm.Pages[0].GridThumbnail!));
        Assert.Same(other, vm.Pages[2].GridThumbnail);

        // セルを選ぶとそのページの「1 枚ずつ」に戻る
        vm.SelectedGridCell = vm.GridCells[2];
        Assert.Same(vm.Pages[2], vm.SelectedPage);
        Assert.True(vm.IsSingleView);
        Assert.Empty(vm.GridCells);
    }

    private static (byte, byte, byte) CenterPixel(System.Windows.Media.Imaging.BitmapSource b)
    {
        var px = new byte[4];
        b.CopyPixels(new System.Windows.Int32Rect(b.PixelWidth / 2, b.PixelHeight / 2, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
    }

    private static (byte, byte, byte, byte) ToTuple((byte B, byte G, byte R, byte A) p) => (p.B, p.G, p.R, p.A);

    // ---- 書き出しシート ----

    private static PageState Page(string name, Guid doc, bool masked)
    {
        var p = new PageState(PageBitmap.Solid(20, 10, 255, 255, 255), name, 0, false, sourceDocId: doc);
        if (masked) p.AddRect(new NormRect(0, 0, 0.5, 0.5));
        return p;
    }

    private ExportViewModel Export(IReadOnlyList<PageState> pages, IExporter exporter) =>
        new(pages, _presets, new MaskRasterizer(), exporter, _dialogs, () => new DateTime(2026, 11, 8), english: false);

    [Fact]
    public async Task Separate_WritesPerDocument_NeverOverwrites_AndKeepsOriginals()
    {
        using var dir = new TempDir();
        // 元のファイル（同じフォルダを保存先に選ぶ）と、重なる名前のファイルを先に置く
        Touch(dir.File("a.jpg"));
        File.WriteAllBytes(dir.File("a-masked.jpg"), [42]);
        File.WriteAllBytes(dir.File("b-masked_2.jpg"), [43]);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // b の 2 ページが離れていても 1 つの書類（画面の順）
        var pages = new[] { Page("b.pdf", b, true), Page("a.jpg", a, true), Page("b.pdf", b, true) };
        var exporter = new RecordingExporter();
        var vm = Export(pages, exporter);
        Assert.True(vm.HasMultipleDocuments);
        Assert.Equal(["b.pdf", "a.jpg"], vm.Documents.Select(d => d.Name));
        Assert.Equal([0, 2], vm.Documents[0].PageIndexes);
        Assert.True(vm.IsCombine);
        vm.IsSeparate = true;
        vm.IsJpeg = true;
        await vm.PrepareAsync();
        var previews = vm.Previews.Select(p => p.Page).ToList();
        _dialogs.Folder = dir.Path;
        await vm.SaveAsync();

        Assert.Empty(_dialogs.SaveRequests); // 分けるときは保存ダイアログではなくフォルダを選ぶ
        Assert.Single(_dialogs.FolderRequests);
        Assert.Equal([1], File.ReadAllBytes(dir.File("a.jpg")));             // 元のファイルはそのまま
        Assert.Equal([42], File.ReadAllBytes(dir.File("a-masked.jpg")));     // 既存は上書きしない
        Assert.Equal([43], File.ReadAllBytes(dir.File("b-masked_2.jpg")));
        Assert.True(File.Exists(dir.File("b-masked-2_1.jpg")));
        Assert.True(File.Exists(dir.File("b-masked-2_2.jpg")));
        Assert.True(File.Exists(dir.File("a-masked-2.jpg")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + ExportWriter.TempSuffix));
        Assert.Same(previews[1], Assert.Single(exporter.Pages!));            // プレビューしたのと同じ焼き込み結果
        Assert.True(vm.IsCompleted);
        Assert.False(vm.CanSave);
        Assert.Equal(dir.Path, vm.SavedFolder);
        Assert.Equal(string.Format(Strings.ExportSeparateCompleted, 2), vm.CompletedText);
        Assert.Empty(_dialogs.Errors);
        vm.OpenSavedFolderCommand.Execute(null);
        Assert.Equal([dir.Path], _dialogs.OpenedFolders);
    }

    [Fact]
    public async Task Separate_ContinuesAfterFailure_AndReportsNames()
    {
        using var dir = new TempDir();
        var pages = new[] { Page("bad.png", Guid.NewGuid(), true), Page("good.png", Guid.NewGuid(), true) };
        var exporter = new FailFirstExporter();
        var vm = Export(pages, exporter);
        vm.IsSeparate = true;
        await vm.PrepareAsync();
        _dialogs.Folder = dir.Path;
        await vm.SaveAsync();
        Assert.True(File.Exists(dir.File("good-masked.pdf")));
        Assert.False(File.Exists(dir.File("bad-masked.pdf")));
        Assert.Contains("bad.png", Assert.Single(_dialogs.Errors).Message);
        Assert.True(vm.IsCompleted);
        Assert.Equal(string.Format(Strings.ExportSeparateCompleted, 1), vm.CompletedText);
    }

    [Fact]
    public async Task Separate_CancelFolder_WritesNothing()
    {
        var pages = new[] { Page("a.png", Guid.NewGuid(), true), Page("b.png", Guid.NewGuid(), true) };
        var exporter = new RecordingExporter();
        var vm = Export(pages, exporter);
        vm.IsSeparate = true;
        await vm.PrepareAsync();
        await vm.SaveAsync();
        Assert.Null(exporter.Pages);
        Assert.False(vm.IsCompleted);
        Assert.Null(vm.SavedSummary);
    }

    [Fact]
    public void ZeroMaskDocuments_AreListedByName_AndSingleDocumentHasNoChoice()
    {
        var d = Guid.NewGuid();
        var vm = Export([Page("ok.png", Guid.NewGuid(), true), Page("doc.pdf", d, true), Page("doc.pdf", d, false)], new RecordingExporter());
        Assert.Equal(["doc.pdf"], vm.ZeroMaskDocumentNames);
        Assert.Contains("doc.pdf", vm.ZeroMaskDocumentsText);
        Assert.Equal(Strings.ExportSaveWithoutMask, vm.SaveButtonText);

        var single = Export([Page("one.pdf", d, true), Page("one.pdf", d, true)], new RecordingExporter());
        Assert.False(single.HasMultipleDocuments);
        single.IsSeparate = true; // 書類が 1 つなら分けられない＝今どおり 1 ファイル
        Assert.False(single.IsSeparate);
        Assert.False(single.HasZeroMaskDocuments);
    }

    [Fact]
    public async Task Combine_StillUsesDefaultNameWithoutSourceNames()
    {
        var vm = Export([Page("secret-a.png", Guid.NewGuid(), true), Page("secret-b.png", Guid.NewGuid(), true)], new RecordingExporter());
        await vm.PrepareAsync();
        await vm.SaveAsync(); // SavePath = null → 取り消し
        Assert.Equal("伏せ済み_20261108.pdf", Assert.Single(_dialogs.SaveRequests).DefaultName);
        Assert.Empty(_dialogs.FolderRequests);
    }

    /// <summary>最初の書類だけ書きかけで失敗させる書き出し。</summary>
    private sealed class FailFirstExporter : IExporter
    {
        private int _calls;
        public void Export(IReadOnlyList<BurnedPage> pages, ExportOptions options, Func<int, Stream> openDestination)
        {
            var s = openDestination(0);
            s.WriteByte(1);
            if (Interlocked.Increment(ref _calls) == 1) throw new IOException("disk error");
        }
    }
}
