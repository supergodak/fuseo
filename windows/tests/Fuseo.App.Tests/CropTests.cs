using Fuseo.App.Library;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// W4.5 手動 4 隅切り抜き。確かめること:
/// - 切り抜いた外側は書き出しに入らない（Base = 台形補正した画像）。
/// - 切り抜くと手動マスクは消え（確認あり）、QR は切り抜いた画像で探し直す。Undo で切り抜き・マスクとも戻る。
/// - 回転してから切り抜いても、画面で指定した四隅どおりの画像になる（四隅を回転 0 に戻して保存）。
/// - 自動保存から戻しても同じ切り抜き。
/// </summary>
public class CropTests
{
    private static readonly MaskRasterizer Rasterizer = new();
    private static readonly IReadOnlyList<DocumentPreset> AllPresets = PresetStore.LoadAll();
    private static readonly Dictionary<DocumentType, DocumentPreset> Presets = AllPresets.ToDictionary(p => p.DocumentType);

    /// <summary>斜めに写った書類（の代わり）の四隅。</summary>
    private static readonly Quad Card = new(new(0.2, 0.15), new(0.85, 0.25), new(0.8, 0.85), new(0.15, 0.8));

    private static bool IsBlack(PageBitmap b, int x, int y) => b.GetPixel(x, y) == (0, 0, 0, 255);

    /// <summary>白地で、四角形の外側（左上の角）にだけ赤い印がある画像。</summary>
    private static PageBitmap PhotoWithRedOutside(int w = 200, int h = 140)
    {
        var bmp = PageBitmap.Solid(w, h, 240, 240, 240);
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++) bmp.SetPixel(x, y, 0, 0, 255, 255);
        return bmp;
    }

    private static bool HasRed(PageBitmap b)
    {
        for (var i = 0; i < b.Pixels.Length; i += 4)
            if (b.Pixels[i + 2] > 200 && b.Pixels[i + 1] < 60 && b.Pixels[i] < 60) return true;
        return false;
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, FakeDetector Detector) Vm(PageBitmap bmp, params NormRect[] detections)
    {
        var dialogs = new FakeDialogs();
        var detector = new FakeDetector(detections);
        var source = new FakePageSource { Handler = _ => [new Fuseo.Core.Contracts.SourcePage(bmp, "card.jpg", 0, false)] };
        var vm = new MainViewModel(source, Rasterizer, new Exporter(), AllPresets, dialogs, () => new DateTime(2026, 10, 15), detector);
        return (vm, dialogs, detector);
    }

    [Fact]
    public async Task Crop_RemovesOutsideFromExport_AndRedetectsOnCroppedImage()
    {
        var (vm, dialogs, detector) = Vm(PhotoWithRedOutside(), new NormRect(0.4, 0.4, 0.2, 0.2));
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;
        Assert.True(HasRed(page.Base));
        Assert.Equal(1, detector.Calls);

        dialogs.CropResult = Card;
        await vm.CropSelectedPageAsync();

        Assert.Equal(Card, page.Crop);
        Assert.Equal(2, detector.Calls); // 切り抜いた画像で探し直した
        Assert.Equal(Perspective.OutputSize(Card, 200, 140), (page.Base.Width, page.Base.Height));
        Assert.False(HasRed(page.Base));
        Assert.Single(page.Candidates);
        Assert.Empty(dialogs.Confirms); // 手で塗っていなければ確認しない

        var export = vm.CreateExportViewModel();
        await export.PrepareAsync();
        var burned = export.Previews.Single().Page.Bitmap;
        Assert.Equal((page.Base.Width, page.Base.Height), (burned.Width, burned.Height));
        Assert.False(HasRed(burned));
    }

    [Fact]
    public async Task PageThumbnail_FollowsCropAndUndo()
    {
        var (vm, dialogs, _) = Vm(PhotoWithRedOutside());
        await vm.ImportAsync(["card.jpg"]);
        var item = vm.SelectedPage!;
        Assert.Equal(200, item.Thumbnail.PixelWidth);
        var changed = 0;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.Thumbnail)) changed++; };

        dialogs.CropResult = Card;
        await vm.CropSelectedPageAsync();
        Assert.True(changed > 0);
        Assert.Equal(item.State.Base.Width, item.Thumbnail.PixelWidth); // 切り抜いた画像の縮小

        vm.UndoCommand.Execute(null);
        Assert.Equal(200, item.Thumbnail.PixelWidth);
    }

    [Fact]
    public async Task Crop_WithManualMasks_AsksFirst_ClearsMasks_AndUndoRestoresEverything()
    {
        var (vm, dialogs, _) = Vm(PhotoWithRedOutside());
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;
        vm.AddRect(new NormRect(0.3, 0.3, 0.2, 0.1));
        var before = page.Base;

        // 取り消すと何も変わらない
        dialogs.CropResult = Card;
        dialogs.ConfirmResult = false;
        await vm.CropSelectedPageAsync();
        Assert.Single(dialogs.Confirms);
        Assert.Null(page.Crop);
        Assert.Equal(1, page.ManualMaskCount);

        dialogs.ConfirmResult = true;
        await vm.CropSelectedPageAsync();
        Assert.Equal(Card, page.Crop);
        Assert.Equal(0, page.ManualMaskCount);

        vm.UndoCommand.Execute(null);
        Assert.Null(page.Crop);
        Assert.Equal(1, page.ManualMaskCount);
        Assert.Same(before, page.Base); // 切り抜きなし・回転 0 は元画像そのもの

        vm.RedoCommand.Execute(null);
        Assert.Equal(Card, page.Crop);
        Assert.Equal(0, page.ManualMaskCount);
    }

    [Fact]
    public async Task Cancel_And_SameQuad_DoNothing()
    {
        var (vm, dialogs, detector) = Vm(PhotoWithRedOutside());
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;

        dialogs.CropResult = null; // 取り消し
        await vm.CropSelectedPageAsync();
        dialogs.CropResult = Quad.FullImage; // 切り抜きなしのまま
        await vm.CropSelectedPageAsync();
        Assert.Null(page.Crop);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal(1, detector.Calls);
    }

    [Fact]
    public async Task UseWholeImage_RemovesCrop()
    {
        var (vm, dialogs, _) = Vm(PhotoWithRedOutside());
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;
        dialogs.CropResult = Card;
        await vm.CropSelectedPageAsync();
        Assert.Equal(Quad.FullImage, dialogs.CropRequests[0].Initial); // 1 回目は全体から始まる

        dialogs.CropResult = Quad.FullImage;
        await vm.CropSelectedPageAsync();
        Assert.Equal(Card, dialogs.CropRequests[1].Initial); // 2 回目は今の切り抜きから始まる
        Assert.Null(page.Crop);
        Assert.Same(page.Original, page.Base);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CropAfterRotation_MatchesWhatTheUserSaw(int turns)
    {
        var bmp = PhotoWithRedOutside(160, 100);
        var (vm, dialogs, _) = Vm(bmp);
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;
        for (var i = 0; i < turns; i++) vm.RotateRightCommand.Execute(null);

        dialogs.CropResult = Card; // 画面（いまの向き）の上で指定した四隅
        await vm.CropSelectedPageAsync();

        var shown = dialogs.CropRequests.Single().Image;
        Assert.Equal(QuarterTurn.Rotate(bmp, turns).Pixels, shown.Pixels); // 画面には回した元画像を出す
        Assert.Equal(Quad.Rotate(Card, -turns), page.Crop);                 // 保存は回転 0 の向き
        Assert.Equal(turns, page.Rotation);
        var expected = Perspective.Warp(shown, Card);
        Assert.Equal((expected.Width, expected.Height), (page.Base.Width, page.Base.Height));
        for (var i = 0; i < expected.Pixels.Length; i++)
            Assert.True(Math.Abs(expected.Pixels[i] - page.Base.Pixels[i]) <= 1, $"byte {i}");
    }

    [Fact]
    public async Task MasksAfterCrop_BurnOnCroppedImage_AndSurviveRotation()
    {
        var (vm, dialogs, _) = Vm(PhotoWithRedOutside());
        await vm.ImportAsync(["card.jpg"]);
        var page = vm.SelectedPage!.State;
        dialogs.CropResult = Card;
        await vm.CropSelectedPageAsync();

        var rect = new NormRect(0.25, 0.25, 0.5, 0.5);
        vm.AddRect(rect);
        vm.RotateRightCommand.Execute(null);
        Assert.Equal(Card, page.Crop); // 回転しても切り抜きはそのまま
        var burned = Rasterizer.BurnIn(page.Base, page.BuildConfirmedMasks()).Bitmap;
        var px = CoordinateSpace.ToPixel(QuarterTurn.Rotate(rect, 1), burned.Width, burned.Height);
        for (var y = px.Y; y < px.Bottom; y++)
            for (var x = px.X; x < px.Right; x++)
                Assert.True(IsBlack(burned, x, y), $"欠け at ({x},{y})");
    }

    [Fact]
    public void ChangeType_AfterCrop_UsesCroppedDetections_AndUndoRestoresOldOnes()
    {
        var p = new PageState(PhotoWithRedOutside(), "a.jpg", 0, false);
        var before = new NormRect(0.7, 0.7, 0.2, 0.2);
        var after = new NormRect(0.1, 0.1, 0.3, 0.3);
        p.InitializeDetections(DetectorId.QrBarcode, [before], Presets[DocumentType.Generic]);
        p.ApplyCrop(Card, DetectorId.QrBarcode, [after], Presets[DocumentType.Generic]);
        Assert.Equal([after], p.RawDetections);
        p.ChangeType(DocumentType.MyNumberCardBack, Presets[DocumentType.MyNumberCardBack]);
        Assert.Equal(after.Padded(Presets[DocumentType.MyNumberCardBack].Rules.First(r => r.Detector == DetectorId.QrBarcode).Padding ?? 0.01),
                     p.Candidates.Single().Box);
        p.Undo(); // 種別
        p.Undo(); // 切り抜き
        Assert.Null(p.Crop);
        Assert.Equal([before], p.RawDetections);
    }

    [Fact]
    public void ApplyCrop_RejectsNonConvex()
    {
        var p = new PageState(PhotoWithRedOutside(), "a.jpg", 0, false);
        var crossed = new Quad(Card.TopLeft, Card.BottomRight, Card.TopRight, Card.BottomLeft);
        Assert.Throws<ArgumentException>(() => p.ApplyCrop(crossed, null, [], null));
        Assert.Null(p.Crop);
    }

    [Fact]
    public void Library_RoundTripsCrop()
    {
        using var dir = new TempDir();
        var p = new PageState(PhotoWithRedOutside(), "a.jpg", 0, false);
        p.ApplyCrop(Card, DetectorId.QrBarcode, [new NormRect(0.1, 0.1, 0.2, 0.2)], Presets[DocumentType.Generic]);
        p.RotateClockwise();
        p.AddRect(new NormRect(0.3, 0.3, 0.2, 0.2));

        var lib = new FileWorkLibrary(dir.Path);
        var id = Guid.NewGuid();
        var snap = WorkMapper.Capture(id, "a.jpg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, WorkStatus.InProgress, null, [p]);
        Assert.Equal(Card, snap.Document.Pages[0].ManualQuad);
        Assert.Same(p.Original, snap.Images.Values.Single()); // 保存する画像は切り抜く前の元画像
        lib.Save(snap.Document, snap.Images);

        var r = WorkMapper.Restore(lib.Load(id)).Single();
        Assert.Equal(Card, r.Crop);
        Assert.Equal(1, r.Rotation);
        Assert.Equal(p.RawDetections, r.RawDetections);
        Assert.Equal(p.Base.Pixels, r.Base.Pixels);
        Assert.Equal(Rasterizer.BurnIn(p.Base, p.BuildConfirmedMasks()).Bitmap.Pixels,
                     Rasterizer.BurnIn(r.Base, r.BuildConfirmedMasks()).Bitmap.Pixels);
    }
}
