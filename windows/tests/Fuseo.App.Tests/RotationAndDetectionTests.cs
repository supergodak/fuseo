using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input.Detection;
using Xunit;
using ZXing;

namespace Fuseo.App.Tests;

/// <summary>決まった箱を返すだけの検出器（呼ばれた回数も数える）。</summary>
internal sealed class FakeDetector(params NormRect[] boxes) : IDetector
{
    private int _calls;
    public DetectorId Id => DetectorId.QrBarcode;
    public int Calls => Volatile.Read(ref _calls);
    public bool Throw { get; set; }

    public IReadOnlyList<NormRect> Detect(PageBitmap page)
    {
        Interlocked.Increment(ref _calls);
        if (Throw) throw new InvalidOperationException("検出に失敗");
        return boxes;
    }
}

/// <summary>
/// W4: 90° 回転（画像とマスクを同じ回数だけ回す・Undo で戻る）と、検出結果 → 候補（種別の defaultOn・ON/OFF が焼き込みに反映）。
/// </summary>
public class RotationAndDetectionTests
{
    private static readonly MaskRasterizer Rasterizer = new();
    private static readonly Dictionary<DocumentType, DocumentPreset> Presets =
        PresetStore.LoadAll().ToDictionary(p => p.DocumentType);

    private static readonly NormRect RectA = new(0.1, 0.2, 0.3, 0.25);
    private static readonly BrushStroke StrokeA = new([new(0.05, 0.9), new(0.5, 0.7), new(0.9, 0.95)], 0.03);
    private static readonly NormRect RawQr = new(0.6, 0.1, 0.2, 0.3);

    private static bool IsBlack(PageBitmap b, int x, int y) => b.GetPixel(x, y) == (0, 0, 0, 255);

    private static PageState NewPage(int w = 120, int h = 70) => new(Noise(w, h), "a.png", 0, false);

    private static PageBitmap Noise(int w, int h)
    {
        var bmp = new PageBitmap(w, h);
        new Random(42).NextBytes(bmp.Pixels);
        for (var i = 3; i < bmp.Pixels.Length; i += 4) bmp.Pixels[i] = 255;
        // 黒は「塗った」印に使うので、元画像に純黒を残さない。
        for (var i = 0; i < bmp.Pixels.Length; i += 4) bmp.Pixels[i] = Math.Max(bmp.Pixels[i], (byte)1);
        return bmp;
    }

    /// <summary>手動矩形・ストローク・ON の検出候補を持つページ（種別はマイナンバーカード裏＝QR が defaultOn）。</summary>
    private static PageState PageWithMasks(int w = 120, int h = 70)
    {
        var p = NewPage(w, h);
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        p.ChangeType(DocumentType.MyNumberCardBack, Presets[DocumentType.MyNumberCardBack]);
        p.AddRect(RectA);
        p.AddStroke(StrokeA);
        return p;
    }

    private static void AssertNear(NormRect expected, NormRect actual)
    {
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.W, actual.W, 12);
        Assert.Equal(expected.H, actual.H, 12);
    }

    // ---- 回転 ----

    [Fact]
    public void RotateClockwise_RotatesImageAndEveryMaskTogether()
    {
        var p = PageWithMasks();
        var cand = p.Candidates.Single();
        p.RotateClockwise();

        Assert.Equal(1, p.Rotation);
        Assert.Equal((70, 120), (p.Base.Width, p.Base.Height));
        Assert.Equal((120, 70), (p.Original.Width, p.Original.Height)); // 元画像は不変
        AssertNear(QuarterTurn.Rotate(RectA, 1), p.Manual.Rects.Single());
        Assert.Equal(StrokeA.Points.Select(pt => QuarterTurn.Rotate(pt, 1)), p.Manual.Strokes.Single().Points);
        var rc = p.Candidates.Single();
        AssertNear(QuarterTurn.Rotate(cand.Box, 1), rc.Box);
        Assert.Equal(cand.Id, rc.Id);
        Assert.True(rc.IsOn);
        Assert.Equal(p.Original.GetPixel(0, 69), p.Base.GetPixel(0, 0)); // 左下 → 時計回りで左上
    }

    [Fact]
    public void RotateCounterClockwise_IsInverseOfClockwise()
    {
        var p = PageWithMasks();
        p.RotateCounterClockwise();
        Assert.Equal(3, p.Rotation);
        AssertNear(QuarterTurn.Rotate(RectA, -1), p.Manual.Rects.Single());
        p.RotateClockwise();
        Assert.Equal(0, p.Rotation);
        AssertNear(RectA, p.Manual.Rects.Single());
        Assert.Same(p.Original, p.Base);
    }

    [Fact]
    public void Undo_RestoresRotationAndMasks_RedoReappliesThem()
    {
        var p = PageWithMasks();
        var manual = p.Manual;
        var candidates = p.Candidates;
        p.RotateClockwise();
        p.RotateClockwise();
        Assert.Equal(2, p.Rotation);

        Assert.True(p.Undo());
        Assert.Equal(1, p.Rotation);
        Assert.True(p.Undo());
        Assert.Equal(0, p.Rotation);
        Assert.Same(manual, p.Manual);
        Assert.Same(candidates, p.Candidates);
        Assert.Same(p.Original, p.Base);
        Assert.Equal((120, 70), (p.Base.Width, p.Base.Height));

        Assert.True(p.Redo());
        Assert.Equal(1, p.Rotation);
        Assert.Equal((70, 120), (p.Base.Width, p.Base.Height));
        AssertNear(QuarterTurn.Rotate(RectA, 1), p.Manual.Rects.Single());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FourTurns_ReturnToOriginal(bool clockwise)
    {
        var p = PageWithMasks();
        var cand = p.Candidates.Single().Box;
        for (var i = 0; i < 4; i++)
        {
            if (clockwise) p.RotateClockwise(); else p.RotateCounterClockwise();
        }
        Assert.Equal(0, p.Rotation);
        Assert.Same(p.Original, p.Base);
        AssertNear(RectA, p.Manual.Rects.Single());
        AssertNear(cand, p.Candidates.Single().Box);
        var pts = p.Manual.Strokes.Single().Points;
        for (var i = 0; i < pts.Count; i++)
        {
            Assert.Equal(StrokeA.Points[i].X, pts[i].X, 12);
            Assert.Equal(StrokeA.Points[i].Y, pts[i].Y, 12);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BurnAfterRotate_CoversSamePixelsAsRotatingTheBurnedOriginal(int turns)
    {
        // 「塗ってから画像を回す」と「ページを回してから塗る」が 1px 以内で同じ画素を覆う。
        var before = PageWithMasks(157, 93);
        var expected = QuarterTurn.Rotate(Rasterizer.BurnIn(before.Base, before.BuildConfirmedMasks()).Bitmap, turns);

        var rotated = PageWithMasks(157, 93);
        for (var i = 0; i < turns; i++) rotated.RotateClockwise();
        var actual = Rasterizer.BurnIn(rotated.Base, rotated.BuildConfirmedMasks()).Bitmap;

        AssertCoversWithin1px(expected, actual);
        AssertCoversWithin1px(actual, expected);
        // 塗っていない画素は元画像を回したものとバイト単位で同じ。
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
                if (!IsBlack(actual, x, y)) Assert.Equal(rotated.Base.GetPixel(x, y), actual.GetPixel(x, y));
    }

    [Fact]
    public async Task Export_OfRotatedPage_SwapsWidthAndHeight()
    {
        var p = PageWithMasks(120, 70);
        p.RotateClockwise();
        var exporter = new RecordingExporter();
        var vm = new ExportViewModel([p], Presets, Rasterizer, exporter, new FakeDialogs(), () => new DateTime(2026, 10, 13));
        await vm.PrepareAsync();
        var burned = Assert.Single(vm.Previews).Page.Bitmap;
        Assert.Equal((70, 120), (burned.Width, burned.Height));
    }

    private static void AssertCoversWithin1px(PageBitmap a, PageBitmap b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                if (!IsBlack(a, x, y) || IsBlack(b, x, y)) continue;
                var near = false;
                for (var dy = -1; dy <= 1 && !near; dy++)
                    for (var dx = -1; dx <= 1 && !near; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        near = nx >= 0 && ny >= 0 && nx < b.Width && ny < b.Height && IsBlack(b, nx, ny);
                    }
                Assert.True(near, $"1px を超える食い違い at ({x},{y})");
            }
    }

    // ---- 検出 → 候補 ----

    [Fact]
    public void InitialCandidates_UsePresetDefaultOn_AndAreNotInUndoHistory()
    {
        var p = NewPage();
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        var c = Assert.Single(p.Candidates);
        var rule = Presets[DocumentType.Generic].Rules.Single(r => r.Detector == DetectorId.QrBarcode);
        Assert.Equal(rule.Id, c.RuleId);
        Assert.Equal(rule.Label, c.Label);
        Assert.Equal(rule.Basis, c.Basis);
        Assert.Equal(rule.DefaultOn, c.IsOn);
        Assert.False(c.IsOn); // generic の QR は既定 OFF
        Assert.Equal(CandidateSource.Detector, c.Source);
        Assert.Equal(DetectorId.QrBarcode, c.Detector);
        AssertNear(RawQr.Padded(rule.Padding ?? 0.01), c.Box);
        Assert.False(p.CanUndo);
        Assert.False(p.Undo());
        Assert.Single(p.Candidates);
    }

    [Fact]
    public void TypeWithoutQrRule_HasNoCandidates()
    {
        var p = NewPage();
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        p.ChangeType(DocumentType.MenkyoshoFront, Presets[DocumentType.MenkyoshoFront]);
        Assert.DoesNotContain(Presets[DocumentType.MenkyoshoFront].Rules, r => r.Detector == DetectorId.QrBarcode);
        Assert.Empty(p.Candidates);
        Assert.Empty(p.BuildConfirmedMasks().Rects);
    }

    [Fact]
    public void ChangeType_RebuildsWithNewDefaultOn_AndUndoRestoresTypeAndCandidates()
    {
        var p = NewPage();
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        p.AddRect(RectA);
        var genericCandidates = p.Candidates;

        p.ChangeType(DocumentType.MyNumberCardBack, Presets[DocumentType.MyNumberCardBack]);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        var c = Assert.Single(p.Candidates);
        Assert.True(c.IsOn); // マイナンバーカード裏の QR は既定 ON
        Assert.Equal(Presets[DocumentType.MyNumberCardBack].Rules.Single(r => r.Detector == DetectorId.QrBarcode).Id, c.RuleId);
        Assert.Single(p.Manual.Rects); // 手動マスクは消さない

        Assert.True(p.Undo());
        Assert.Equal(DocumentType.Generic, p.Type);
        Assert.Same(genericCandidates, p.Candidates);
        Assert.True(p.Redo());
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.True(p.Candidates.Single().IsOn);
    }

    [Fact]
    public void ChangeType_AfterRotation_RotatesRawBoxesBeforePadding()
    {
        var p = NewPage();
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        p.RotateClockwise();
        p.ChangeType(DocumentType.Hokensho, Presets[DocumentType.Hokensho]);
        var rule = Presets[DocumentType.Hokensho].Rules.Single(r => r.Detector == DetectorId.QrBarcode);
        AssertNear(QuarterTurn.Rotate(RawQr, 1).Padded(rule.Padding ?? 0.01), p.Candidates.Single().Box);
        Assert.Equal(rule.DefaultOn, p.Candidates.Single().IsOn);
    }

    [Fact]
    public void CandidateOnOff_IsReflectedInBurnIn()
    {
        var p = NewPage(200, 100);
        p.InitializeAnalysis(PageFindings.FromQr([RawQr]), 0, DocumentType.Generic, Presets[DocumentType.Generic]);
        var center = CoordinateSpace.ToPixel(RawQr, 200, 100);
        int cx = center.X + center.Width / 2, cy = center.Y + center.Height / 2;

        Assert.False(IsBlack(Rasterizer.BurnIn(p.Base, p.BuildConfirmedMasks()).Bitmap, cx, cy)); // generic = OFF
        p.ChangeType(DocumentType.MyNumberCardBack, Presets[DocumentType.MyNumberCardBack]);
        Assert.True(IsBlack(Rasterizer.BurnIn(p.Base, p.BuildConfirmedMasks()).Bitmap, cx, cy));  // 既定 ON
        p.SetCandidateOn(p.Candidates.Single().Id, false);
        Assert.False(IsBlack(Rasterizer.BurnIn(p.Base, p.BuildConfirmedMasks()).Bitmap, cx, cy)); // 利用者が OFF
    }

    // ---- MainViewModel 経由 ----

    private static MainViewModel Vm(FakePageSource source, IDetector? detector) =>
        new(source, Rasterizer, new Exporter(), PresetStore.LoadAll(), new FakeDialogs(), () => new DateTime(2026, 10, 13), detector);

    [Fact]
    public async Task Import_RunsDetectorOnEveryPage_AndCandidatesSurviveUndo()
    {
        var detector = new FakeDetector(RawQr);
        var vm = Vm(new FakePageSource(), detector);
        await vm.ImportAsync(["a.png", "b.png", "c.png"]);
        Assert.Equal(3, detector.Calls);
        Assert.All(vm.Pages, p => Assert.Single(p.State.Candidates));
        Assert.Single(vm.CandidateItems);
        Assert.False(vm.UndoCommand.CanExecute(null)); // 初期候補は履歴に積まない
    }

    [Fact]
    public async Task Import_DetectorFailure_KeepsPagesWithoutCandidates_AndSaysSo()
    {
        var detector = new FakeDetector(RawQr) { Throw = true };
        var vm = Vm(new FakePageSource(), detector);
        await vm.ImportAsync(["a.png"]);
        Assert.Single(vm.Pages);
        Assert.Empty(vm.Pages[0].State.Candidates);
        Assert.Contains("手で塗って", vm.StatusMessage);
    }

    [Fact]
    public async Task PickingType_RebuildsCandidates_AndUndoRestoresPicker()
    {
        var vm = Vm(new FakePageSource(), new FakeDetector(RawQr));
        await vm.ImportAsync(["a.png"]);
        Assert.False(vm.CandidateItems.Single().IsOn);

        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        Assert.True(vm.CandidateItems.Single().IsOn);
        Assert.Equal(1, vm.SelectedPage!.State.ActiveCandidateCount);

        vm.UndoCommand.Execute(null);
        Assert.Equal(DocumentType.Generic, vm.SelectedDocumentType!.Type);
        Assert.False(vm.CandidateItems.Single().IsOn);
    }

    [Fact]
    public async Task RotateCommands_RotateSelectedPage_AndAreUndoable()
    {
        var vm = Vm(new FakePageSource(), null);
        Assert.False(vm.RotateRightCommand.CanExecute(null));
        await vm.ImportAsync(["a.png"]); // 40×20
        vm.AddRect(RectA);
        vm.RotateRightCommand.Execute(null);
        Assert.Equal(1, vm.SelectedPage!.State.Rotation);
        Assert.Equal((20, 40), (vm.SelectedPage.State.Base.Width, vm.SelectedPage.State.Base.Height));
        vm.RotateLeftCommand.Execute(null);
        vm.RotateLeftCommand.Execute(null);
        Assert.Equal(3, vm.SelectedPage.State.Rotation);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.SelectedPage.State.Rotation);
        AssertNear(RectA, vm.SelectedPage.State.Manual.Rects.Single());
    }

    // ---- 本物の検出器で通し（合成 QR のみ・実物の書類は使わない） ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SyntheticQr_EndToEnd_CandidateOn_BlacksOutWholeSymbol(int rotateTurns)
    {
        const int w = 1200, h = 800, scale = 8, ox = 700, oy = 250;
        var bmp = PageBitmap.Solid(w, h, 255, 255, 255);
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        var m = new MultiFormatWriter().encode("FUSEO-W4-SYNTHETIC", BarcodeFormat.QR_CODE, 0, 0, hints);
        for (var my = 0; my < m.Height; my++)
            for (var mx = 0; mx < m.Width; mx++)
                if (m[mx, my])
                    for (var y = 0; y < scale; y++)
                        for (var x = 0; x < scale; x++) bmp.SetPixel(ox + mx * scale + x, oy + my * scale + y, 0, 0, 0, 255);
        var symbol = new NormRect((double)ox / w, (double)oy / h, (double)m.Width * scale / w, (double)m.Height * scale / h);

        var source = new FakePageSource { Handler = _ => [new SourcePage(bmp, "qr.png", 0, false)] };
        var vm = Vm(source, new QrDetector());
        await vm.ImportAsync(["qr.png"]);
        var page = vm.SelectedPage!.State;
        Assert.True(page.Candidates.Count == 1, vm.StatusMessage);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        Assert.True(page.Candidates.Single().IsOn);
        for (var i = 0; i < rotateTurns; i++) vm.RotateRightCommand.Execute(null);

        var exportVm = vm.CreateExportViewModel();
        await exportVm.PrepareAsync();
        var burned = exportVm.Previews.Single().Page.Bitmap;
        var px = CoordinateSpace.ToPixel(QuarterTurn.Rotate(symbol, rotateTurns), burned.Width, burned.Height);
        for (var y = px.Y; y < px.Bottom; y++)
            for (var x = px.X; x < px.Right; x++)
                Assert.True(IsBlack(burned, x, y), $"QR の画素が塗られていない at ({x},{y})");
    }
}
