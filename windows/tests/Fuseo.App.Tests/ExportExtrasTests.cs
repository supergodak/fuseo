using System.IO;
using Fuseo.App.Configuration;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>0.4.0: 書き出しシートの「提出用の設定」（透かし・大きさの上限・PDF のパスワード）。Mac 版 ExportExtrasAppTests と同じ決めごと。</summary>
public class ExportExtrasTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();

    public ExportExtrasTests()
    {
        _source.Handler = paths => paths.Select(p => new SourcePage(PageBitmap.Solid(600, 400, 255, 255, 255), p, 0, false)).ToList();
    }

    private async Task<MainViewModel> Vm(IExporter? exporter = null, SettingsStore? settings = null)
    {
        var vm = new MainViewModel(_source, new MaskRasterizer(), exporter ?? new Exporter(), PresetStore.LoadAll(), _dialogs,
                                   () => new DateTime(2026, 11, 11), settings: settings);
        await vm.ImportAsync(["a.png"]);
        vm.SelectedPage = vm.Pages[0];
        return vm;
    }

    private static int GrayPixels(PageBitmap b)
    {
        var n = 0;
        for (var i = 0; i < b.Pixels.Length; i += 4)
            if (b.Pixels[i] < 240 && b.Pixels[i] > 60) n++;
        return n;
    }

    [Fact]
    public async Task Password_GatesSave_AndAppliesOnlyToPdf()
    {
        var ex = (await Vm()).CreateExportViewModel();
        await ex.PrepareAsync();
        Assert.True(ex.CanSave);
        ex.PasswordEnabled = true;
        Assert.Equal(PdfPasswordState.Empty, ex.PasswordState);
        Assert.False(ex.CanSave);
        ex.Password = "パスワード"; ex.PasswordConfirm = "パスワード";
        Assert.Equal(PdfPasswordState.Invalid, ex.PasswordState);
        Assert.False(ex.CanSave);
        Assert.True(ex.HasPasswordMessage);
        ex.Password = "abc"; ex.PasswordConfirm = "abd";
        Assert.Equal(PdfPasswordState.Mismatch, ex.PasswordState);
        Assert.False(ex.CanSave);
        Assert.Null(ex.BuildOptions(ExportFormat.Pdf).PdfPassword);
        ex.PasswordConfirm = "abc";
        Assert.True(ex.CanSave);
        Assert.Equal("abc", ex.BuildOptions(ExportFormat.Pdf).PdfPassword);
        ex.IsJpeg = true;
        Assert.Equal(PdfPasswordState.Off, ex.PasswordState);
        Assert.False(ex.ShowPasswordOptions);
        Assert.Null(ex.BuildOptions(ExportFormat.Jpeg).EffectivePdfPassword);
    }

    [Fact]
    public async Task Watermark_PreviewAndSave_ArePixelsOnTopOfTheBurnedPage()
    {
        var exporter = new RecordingExporter();
        var ex = (await Vm(exporter)).CreateExportViewModel();
        await ex.PrepareAsync();
        Assert.Null(ex.ActiveWatermark);
        ex.WatermarkEnabled = true;
        Assert.True(ex.ShowWatermarkEmpty);
        Assert.False(ex.HasWatermarkPreview);
        ex.WatermarkText = "テスト提出用";
        Assert.NotNull(ex.ActiveWatermark);
        Assert.True(ex.HasWatermarkPreview);
        ex.IsWatermarkDiagonal = true;
        Assert.Equal(WatermarkLayoutKind.Diagonal, ex.ActiveWatermark!.Layout);

        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        await ex.SaveAsync();
        var written = Assert.Single(exporter.Pages!).Bitmap;
        Assert.True(GrayPixels(written) > 200, "書き出した画像に透かしが無い");
        Assert.Equal(0, GrayPixels(Assert.Single(ex.Previews).Page.Bitmap));   // 仕上がりの一覧は焼き込みのまま
    }

    [Fact]
    public async Task AddDate_AppendsOnce()
    {
        var ex = (await Vm()).CreateExportViewModel();
        ex.WatermarkText = "〇〇提出用";
        ex.AddDateCommand.Execute(null);
        ex.AddDateCommand.Execute(null);
        Assert.Equal("〇〇提出用 2026/11/11", ex.WatermarkText);
    }

    [Fact]
    public async Task SizeLimit_IsHiddenAndIgnoredForPng()
    {
        var ex = (await Vm()).CreateExportViewModel();
        ex.SelectedSizeLimit = ex.SizeLimitOptions.Single(o => o.Bytes == 2_000_000);
        Assert.True(ex.ShowSizeLimitNote);
        Assert.Equal(2_000_000, ex.BuildOptions(ExportFormat.Jpeg).MaxBytes);
        ex.IsPng = true;
        Assert.False(ex.ShowSizeLimit);
        Assert.Null(ex.BuildOptions(ExportFormat.Png).MaxBytes);
        Assert.Equal("2 MB 以内", ex.SizeLimitOptions[2].Label);
        Assert.Equal("上限なし", ex.SizeLimitOptions[0].Label);
    }

    [Fact]
    public async Task SizeLimitFailure_ShowsTheReason()
    {
        var exporter = new RecordingExporter { Throw = new SizeLimitUnreachableException(1_000_000, 2_500_000) };
        var ex = (await Vm(exporter)).CreateExportViewModel();
        await ex.PrepareAsync();
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        await ex.SaveAsync();
        var (_, message) = Assert.Single(_dialogs.Errors);
        Assert.Contains("1 MB", message);
        Assert.Contains("2.5 MB", message);
    }

    /// <summary>保存後: 配置・濃さ・上限は次回へ。文言とパスワードは持ち越さない。言語の設定は消さない。</summary>
    [Fact]
    public async Task Remember_KeepsLayoutAndLimit_NotTextOrPassword_AndKeepsLanguage()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Save(new AppSettings { Language = LanguageSetting.English });
        var vm = await Vm(new RecordingExporter(), store);
        var ex = vm.CreateExportViewModel();
        await ex.PrepareAsync();
        ex.WatermarkEnabled = true;
        ex.WatermarkText = "A社提出用";
        ex.IsWatermarkDiagonal = true;
        ex.WatermarkOpacity = 0.4;
        ex.SelectedSizeLimit = ex.SizeLimitOptions.Single(o => o.Bytes == 3_000_000);
        _dialogs.SavePath = dir.File("out.pdf");
        await ex.SaveAsync();

        var saved = store.Load();
        Assert.Equal(LanguageSetting.English, saved.Language);
        Assert.Equal(WatermarkLayoutKind.Diagonal, saved.WatermarkLayout);
        Assert.Equal(0.4, saved.WatermarkOpacity, 9);
        Assert.Equal(3_000_000, saved.ExportSizeLimit);
        Assert.DoesNotContain("A社", File.ReadAllText(store.FilePath));

        var next = vm.CreateExportViewModel();
        Assert.False(next.WatermarkEnabled);
        Assert.Equal("", next.WatermarkText);
        Assert.True(next.IsWatermarkDiagonal);
        Assert.Equal(3_000_000, next.SelectedSizeLimit.Bytes);
        Assert.Equal(PdfPasswordState.Off, next.PasswordState);
    }

    [Fact]
    public void Settings_RejectUnknownLimit_AndClampOpacity()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "language": "ja", "exportSizeLimit": 1234, "watermarkOpacity": 5 }""");
        var s = new SettingsStore(path).Load();
        Assert.Null(s.ExportSizeLimit);
        Assert.Equal(Watermark.MaxOpacity, s.WatermarkOpacity);
        Assert.Equal(WatermarkLayoutKind.Tiled, s.WatermarkLayout);
    }

    [Fact]
    public async Task EndToEnd_WatermarkAndPassword_WritesEncryptedPdf()
    {
        var ex = (await Vm()).CreateExportViewModel();   // 本物の Exporter
        await ex.PrepareAsync();
        ex.WatermarkEnabled = true;
        ex.WatermarkText = "提出用";
        ex.PasswordEnabled = true;
        ex.Password = "pw-1234"; ex.PasswordConfirm = "pw-1234";
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        await ex.SaveAsync();
        Assert.Empty(_dialogs.Errors);
        var latin = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(dir.File("out.pdf")));
        Assert.Contains("/Encrypt", latin);
        Assert.DoesNotContain("提出用", latin);
        Assert.Equal("", ex.Password);   // 保存後に消す
    }

    [Fact]
    public void Painter_RenderLayer_IsTransparentOutsideText_AndNullForBlank()
    {
        Assert.Null(WatermarkPainter.RenderLayer(new Watermark("  "), 100, 100));
        var layer = WatermarkPainter.RenderLayer(new Watermark("提出用", WatermarkLayoutKind.Diagonal, 0.5), 600, 400)!;
        var opaque = 0;
        for (var i = 3; i < layer.Length; i += 4) if (layer[i] > 0) opaque++;
        Assert.InRange(opaque, 500, 600 * 400 / 2);   // 文字の所だけ
        Assert.Equal(0, layer[3]);                     // 左上の隅は透明
    }
}
