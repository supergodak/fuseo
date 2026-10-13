using System.IO;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

public class ExportViewModelTests
{
    private static readonly DateTime Today = new(2026, 10, 11, 9, 30, 0);
    private readonly FakeDialogs _dialogs = new();
    private readonly Dictionary<DocumentType, DocumentPreset> _presets = PresetStore.LoadAll().ToDictionary(p => p.DocumentType);

    private static PageState Page(string name = "a.png")
    {
        var p = new PageState(PageBitmap.Solid(50, 40, 255, 255, 255), name, 0, false);
        return p;
    }

    private ExportViewModel Vm(IReadOnlyList<PageState> pages, IExporter exporter) =>
        new(pages, _presets, new MaskRasterizer(), exporter, _dialogs, () => Today);

    [Fact]
    public async Task Preview_IsTheBurnedImage_AndSaveExportsTheSameInstances()
    {
        var p = Page();
        p.AddRect(new NormRect(0, 0, 0.5, 0.5));
        p.ReplaceCandidates([PageStateTests.Cand(new NormRect(0.5, 0.5, 0.5, 0.5), false)]);
        var exporter = new RecordingExporter();
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        var vm = Vm([p], exporter);
        Assert.False(vm.CanSave); // 焼き込み前は保存できない

        await vm.PrepareAsync();
        var preview = Assert.Single(vm.Previews).Page;
        Assert.Equal((0, 0, 0, 255), ToTuple(preview.Bitmap.GetPixel(10, 10)));       // 手動矩形 = 黒
        Assert.Equal((255, 255, 255, 255), ToTuple(preview.Bitmap.GetPixel(40, 35))); // OFF の候補 = 塗らない
        Assert.Equal((255, 255, 255, 255), ToTuple(p.Base.GetPixel(10, 10)));          // 基準は書き換えない

        await vm.SaveAsync();
        Assert.Same(preview, Assert.Single(exporter.Pages!));
        Assert.Equal(ExportFormat.Pdf, exporter.Options!.Format);
        Assert.True(File.Exists(dir.File("out.pdf")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + ExportWriter.TempSuffix));
        Assert.NotNull(vm.SavedSummary);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task DefaultFileName_DoesNotContainSourceName()
    {
        var vm = Vm([Page("my-secret-card.jpg")], new RecordingExporter());
        await vm.PrepareAsync();
        vm.Format = ExportFormat.Jpeg;
        await vm.SaveAsync(); // SavePath = null → 取り消し
        var req = Assert.Single(_dialogs.SaveRequests);
        Assert.Equal(ExportFormat.Jpeg, req.Format);
        Assert.Equal("伏せ済み_20261011.jpg", req.DefaultName);
        Assert.DoesNotContain("secret", req.DefaultName);
        Assert.Null(vm.SavedSummary);
    }

    [Fact]
    public async Task MultiPageJpeg_WritesOneFilePerPage_WithSuffix()
    {
        var exporter = new RecordingExporter();
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("提出用.jpg");
        var vm = Vm([Page(), Page(), Page()], exporter);
        vm.IsJpeg = true;
        await vm.PrepareAsync();
        await vm.SaveAsync();
        for (var i = 1; i <= 3; i++)
            Assert.Equal([(byte)i], File.ReadAllBytes(dir.File($"提出用_{i}.jpg")));
        Assert.False(File.Exists(dir.File("提出用.jpg")));
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public async Task MultiPage_ExistingSuffixedFiles_AskBeforeOverwrite()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("x_2.png"), [9]);
        _dialogs.SavePath = dir.File("x.png");
        _dialogs.ConfirmResult = false;
        var exporter = new RecordingExporter();
        var vm = Vm([Page(), Page()], exporter);
        vm.IsPng = true;
        await vm.PrepareAsync();
        await vm.SaveAsync();
        Assert.Contains("x_2.png", Assert.Single(_dialogs.Confirms));
        Assert.Null(exporter.Pages);                         // 断ったら書かない
        Assert.Equal([9], File.ReadAllBytes(dir.File("x_2.png")));
    }

    [Fact]
    public async Task ExporterFailure_ShowsError_AndLeavesNoFiles()
    {
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("y.png");
        var exporter = new RecordingExporter { Throw = new InvalidOperationException("boom") };
        var vm = Vm([Page(), Page()], exporter);
        vm.IsPng = true;
        await vm.PrepareAsync();
        await vm.SaveAsync();
        Assert.Contains("boom", Assert.Single(_dialogs.Errors).Message);
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Null(vm.SavedSummary);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task RealExporter_WritesPdf()
    {
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("z.pdf");
        var p = Page();
        p.AddRect(new NormRect(0.1, 0.1, 0.3, 0.3));
        var vm = Vm([p, Page()], new Exporter());
        await vm.PrepareAsync();
        await vm.SaveAsync();
        var bytes = File.ReadAllBytes(dir.File("z.pdf"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Warnings_AreUnionOfPageTypes_AndZeroMaskIsFlagged()
    {
        var a = Page(); a.ChangeType(DocumentType.MyNumberCardBack, null);
        var b = Page(); b.ChangeType(DocumentType.MyNumberCardBack, null);
        var c = Page(); c.ChangeType(DocumentType.Generic, null);
        a.AddRect(new NormRect(0, 0, 0.1, 0.1));
        var vm = Vm([a, b, c], new RecordingExporter());
        Assert.Equal(_presets[DocumentType.MyNumberCardBack].Warnings, vm.Warnings);
        Assert.True(vm.HasZeroMaskPage);
        Assert.NotEqual(vm.SaveButtonText, Vm([a], new RecordingExporter()).SaveButtonText);
        Assert.Contains("3", vm.SummaryText);
    }

    [Fact]
    public void Naming_Targets()
    {
        Assert.Equal([@"C:\d\a.pdf"], ExportNaming.Targets(@"C:\d\a.pdf", ExportFormat.Pdf, 3));
        Assert.Equal([@"C:\d\a.jpeg"], ExportNaming.Targets(@"C:\d\a.jpeg", ExportFormat.Jpeg, 1));
        Assert.Equal([@"C:\d\a_1.jpeg", @"C:\d\a_2.jpeg"], ExportNaming.Targets(@"C:\d\a.jpeg", ExportFormat.Jpeg, 2));
        Assert.Equal([@"C:\d\a_1.png", @"C:\d\a_2.png"], ExportNaming.Targets(@"C:\d\a", ExportFormat.Png, 2));
        Assert.Equal("伏せ済み_20261011.pdf", ExportNaming.DefaultFileName(Today, ExportFormat.Pdf));
    }

    private static (byte, byte, byte, byte) ToTuple((byte B, byte G, byte R, byte A) p) => (p.B, p.G, p.R, p.A);
}
