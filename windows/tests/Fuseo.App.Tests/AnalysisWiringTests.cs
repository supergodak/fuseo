using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Threading;
using Fuseo.App.Library;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Analysis;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// W9-3: 文字認識（OCR）・顔の検出を画面に組み込む（docs/windows-design.md §10.15）。確かめること:
/// - 取り込み時の解析（向き・種別・候補）は Undo 履歴に積まない。写真だけ向きを判定し、PDF はしない。
/// - 切り抜き・読み直し・種別の変更はそれぞれ 1 回の変更として Undo。回転では読み直さない。
/// - 利用者が選んだ種別は、切り抜き・読み直しで自動判定に戻さない。
/// - 文字認識・顔の検出は UI スレッドで呼ばない。解析中は編集できない。
/// - 文字認識が無い環境は今までどおり（QR のみ）＋案内。OCR の結果は保存しない。生の番号は画面に出さない。
/// すべて合成のデータ（番号はチェックデジットの通る合成の本体から実行時に作る・実物の書類は使わない）。
/// </summary>
public class AnalysisWiringTests
{
    private static readonly IReadOnlyList<DocumentPreset> AllPresets = PresetStore.LoadAll();
    private static readonly Dictionary<DocumentType, DocumentPreset> Presets = AllPresets.ToDictionary(p => p.DocumentType);

    /// <summary>合成の個人番号（見るからに作り物の本体＋検査用数字）。</summary>
    private static readonly string Number = Enumerable.Range(0, 10).Select(c => "01234567890" + c)
        .First(Checkdigits.IsValidMyNumber);

    private static OcrItem Item(string text, double x, double midY, double w = 0.3, double h = 0.06)
    {
        var box = new NormRect(x, midY - h / 2, w, h);
        return new OcrItem(text, box, [new OcrWord(text, box)]);
    }

    /// <summary>マイナンバーカード（裏）らしい OCR（キーワード＋番号の行）。</summary>
    private static readonly NormRect NumberBox = new(0.1, 0.47, 0.5, 0.06);
    private static IReadOnlyList<OcrItem> MyNumberBackOcr() =>
    [
        Item("コールセンター", 0.1, 0.1),
        Item("拾得されたかたへ", 0.1, 0.2),
        new OcrItem(Number, NumberBox, [new OcrWord(Number, NumberBox)]),
    ];

    /// <summary>マイナンバーカード（表）らしい OCR（固定領域のあるプリセット）。</summary>
    private static IReadOnlyList<OcrItem> MyNumberFrontOcr() =>
        [Item("電子証明書", 0.1, 0.1), Item("性別", 0.1, 0.3)];

    private static bool IsRed(PageBitmap b, int x, int y) => b.GetPixel(x, y) is (0, 0, 255, 255);

    /// <summary>40×20 の灰色地で、左下の隅だけ赤い画像（時計回りに 1 回すと赤が左上に来る）。</summary>
    private static PageBitmap MarkedBottomLeft()
    {
        var bmp = PageBitmap.Solid(40, 20, 200, 200, 200);
        for (var y = 16; y < 20; y++)
            for (var x = 0; x < 4; x++) bmp.SetPixel(x, y, 0, 0, 255, 255);
        return bmp;
    }

    private static (MainViewModel Vm, FakeTextRecognizer Ocr, FakeDialogs Dialogs) Vm(
        Func<PageBitmap, IReadOnlyList<OcrItem>> ocr, IDetector? qr = null, IDetector? face = null, FakePageSource? source = null,
        bool withRecognizer = true, FileWorkLibrary? library = null, ManualDelay? delay = null)
    {
        var dialogs = new FakeDialogs();
        var recognizer = new FakeTextRecognizer { Handler = ocr };
        var vm = new MainViewModel(source ?? new FakePageSource(), new MaskRasterizer(), new Exporter(), AllPresets, dialogs,
                                   () => new DateTime(2026, 11, 8), qr, library, delay is null ? null : delay.Delay,
                                   english: false, recognizer: withRecognizer ? recognizer : null, faceDetector: face);
        return (vm, recognizer, dialogs);
    }

    private static async Task WaitIdle(MainViewModel vm)
    {
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy, "解析が終わらない");
    }

    // ---- 取り込み ----

    [Fact]
    public async Task Import_AutoType_NumberCandidate_NotInUndo_AndMasksTheNumber()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["back.png"]);
        var page = vm.SelectedPage!.State;

        Assert.Equal(DocumentType.MyNumberCardBack, page.Type);
        Assert.False(page.TypeIsManual);
        Assert.True(vm.IsAutoTypeVisible);
        Assert.False(vm.IsOcrUnavailableNoticeVisible);
        Assert.False(page.CanUndo); // 取り込み時の解析は履歴に積まない
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(ocr.Calls >= 1);

        var rule = Presets[DocumentType.MyNumberCardBack].Rules.Single(r => r.Id == "mynumB.number");
        var c = Assert.Single(page.Candidates, c => c.RuleId == rule.Id);
        Assert.Equal(rule.DefaultOn, c.IsOn);
        Assert.Equal(NumberBox.Padded(rule.EffectivePadding), c.Box);
        // 生の番号は出さない（先頭 2 桁＋末尾 2 桁だけ）。表示の文言も同じ。
        var item = Assert.Single(vm.CandidateItems, i => i.Id == c.Id);
        Assert.DoesNotContain(Number, item.Label);
        Assert.Equal($"{rule.Label}（{Number[..2]}********{Number[^2..]}）", item.Label);
    }

    [Fact]
    public async Task Import_Photo_ChoosesUprightRotation_NotUndoable_PdfIsNotRotated()
    {
        // 赤い隅が左上に来る向き（時計回り 1 回）だけ文字が読める写真。
        IReadOnlyList<OcrItem> Read(PageBitmap b) => IsRed(b, 1, 1) ? MyNumberBackOcr() : [];
        var face = new NormRect(0.6, 0.1, 0.2, 0.3);
        var source = new FakePageSource
        {
            Handler = _ => [new SourcePage(MarkedBottomLeft(), "photo.jpg", 0, false), new SourcePage(MarkedBottomLeft(), "doc.pdf", 0, true)],
        };
        var (vm, _, _) = Vm(Read, face: new UprightFaceDetector(face), source: source);
        await vm.ImportAsync(["photo.jpg", "doc.pdf"]);

        var photo = vm.Pages[0].State;
        Assert.Equal(1, photo.Rotation);
        Assert.Equal(1, photo.Findings.OcrTurns);
        Assert.Equal(DocumentType.MyNumberCardBack, photo.Type);
        Assert.False(photo.CanUndo); // 自動の回転も取り込みの一部
        Assert.Equal([QuarterTurn.Rotate(face, -1)], photo.Findings.Faces); // 正立の向きで検出し、R0（回転 0）に戻して持つ

        var pdf = vm.Pages[1].State;
        Assert.Equal(0, pdf.Rotation); // PDF は向きを判定しない
        Assert.Equal(DocumentType.Generic, pdf.Type);
        Assert.Empty(pdf.Findings.Faces); // 横倒しのまま（R0）で探す＝この検出器では見つからない
    }

    [Fact]
    public async Task Import_PdfOrNoOcr_DetectsFacesOnR0()
    {
        var face = new NormRect(0.6, 0.1, 0.2, 0.3);
        var source = new FakePageSource { Handler = _ => [new SourcePage(MarkedBottomLeft(), "doc.pdf", 0, true)] };
        var (vm, _, _) = Vm(_ => [], face: new FakeFaceDetector(face), source: source);
        await vm.ImportAsync(["doc.pdf"]);
        var pdf = vm.SelectedPage!.State;
        Assert.Equal([face], pdf.Findings.Faces);
        // generic の顔のルール（既定 OFF）: R0 の箱がそのまま（回転 0）で候補になる。
        var genericFace = Presets[DocumentType.Generic].Rules.Single(r => r.Detector == DetectorId.Face);
        var fc = Assert.Single(pdf.Candidates, c => c.Detector == DetectorId.Face);
        Assert.False(fc.IsOn);
        Assert.Equal(face.Padded(genericFace.EffectivePadding), fc.Box);
    }

    [Fact]
    public async Task Import_SidewaysPhoto_FaceFoundUpright_CandidateAtScreenPosition()
    {
        // 横倒しの写真。顔の検出器は正立した向きでだけ顔を返す（実際の FaceDetector も横倒しの顔は苦手）。
        IReadOnlyList<OcrItem> Read(PageBitmap b) => IsRed(b, 1, 1) ? [Item("見本の書類", 0.1, 0.1)] : [];
        var face = new NormRect(0.6, 0.1, 0.2, 0.3); // 正立の向き（＝画面の向き）での位置
        var source = new FakePageSource { Handler = _ => [new SourcePage(MarkedBottomLeft(), "photo.jpg", 0, false)] };
        var (vm, _, _) = Vm(Read, face: new UprightFaceDetector(face), source: source);
        await vm.ImportAsync(["photo.jpg"]);
        var p = vm.SelectedPage!.State;
        Assert.Equal(1, p.Rotation);
        var rule = Presets[DocumentType.Generic].Rules.Single(r => r.Detector == DetectorId.Face);
        AssertNear(face.Padded(rule.EffectivePadding), Assert.Single(p.Candidates).Box); // 画面の向きで正しい位置
        Assert.Equal([QuarterTurn.Rotate(face, -1)], p.Findings.Faces);
    }

    [Fact]
    public async Task Crop_DetectsFacesAtCurrentRotation_AndStoresR0()
    {
        // PDF（向きを判定しない）を利用者が回して正立させ、ほぼ全体を切り抜く（赤い隅は残る）。
        var face = new NormRect(0.6, 0.1, 0.2, 0.3);
        var source = new FakePageSource { Handler = _ => [new SourcePage(MarkedBottomLeft(), "doc.pdf", 0, true)] };
        var (vm, _, _) = Vm(_ => [], face: new UprightFaceDetector(face), source: source);
        await vm.ImportAsync(["doc.pdf"]);
        var p = vm.SelectedPage!.State;
        Assert.Empty(p.Findings.Faces);
        vm.RotateRightCommand.Execute(null);
        await vm.ApplyCropAsync(vm.SelectedPage, new Quad(new(0, 0), new(0.95, 0), new(0.95, 1), new(0, 1)));
        Assert.Equal([QuarterTurn.Rotate(face, -1)], p.Findings.Faces);
        var rule = Presets[DocumentType.Generic].Rules.Single(r => r.Detector == DetectorId.Face);
        AssertNear(face.Padded(rule.EffectivePadding), Assert.Single(p.Candidates, c => c.Detector == DetectorId.Face).Box);
    }

    /// <summary>赤い隅が左上にある（＝正立の）画像でだけ顔を返す検出器。</summary>
    private sealed class UprightFaceDetector(NormRect face) : IDetector
    {
        public DetectorId Id => DetectorId.Face;
        public IReadOnlyList<NormRect> Detect(PageBitmap page) => IsRed(page, 1, 1) ? [face] : [];
    }

    [Fact]
    public void Import_OcrAndFaceRunOffTheUiThread_AndProgressEndsWithResult() => RunOnDispatcher(async () =>
    {
        var ui = Environment.CurrentManagedThreadId;
        var face = new FakeFaceDetector(new NormRect(0.1, 0.1, 0.2, 0.2));
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr(), face: face);
        await vm.ImportAsync(["a.png", "b.png", "c.png"]);
        Assert.Equal(Environment.CurrentManagedThreadId, ui);
        Assert.NotEqual(ui, ocr.LastThreadId);
        Assert.NotEqual(ui, face.LastThreadId);
        // 遅れて届いた進捗が最後の表示を上書きしない
        await Task.Delay(50);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(string.Format(Properties.Strings.StatusImported, 3), vm.StatusMessage);
    });

    [Fact]
    public async Task Import_OcrFailure_CountsAsFailedPage_QrStillWorks()
    {
        var qr = new NormRect(0.6, 0.1, 0.2, 0.3);
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr(), qr: new FakeDetector(qr));
        ocr.Throw = true;
        await vm.ImportAsync(["a.png", "b.png"]);
        Assert.Equal(string.Format(Properties.Strings.StatusDetectFailed, 2, 2), vm.StatusMessage);
        Assert.All(vm.Pages, p =>
        {
            Assert.Null(p.State.Findings.Ocr);
            Assert.Equal(DocumentType.Generic, p.State.Type);
            Assert.Equal(0, p.State.Rotation);
            Assert.Single(p.State.Candidates); // QR は出る
        });
    }

    [Fact]
    public async Task WithoutRecognizer_BehavesAsBefore_AndShowsDismissableNotice()
    {
        var qr = new NormRect(0.6, 0.1, 0.2, 0.3);
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr(), qr: new FakeDetector(qr), withRecognizer: false);
        Assert.True(vm.IsOcrUnavailableNoticeVisible);
        await vm.ImportAsync(["a.png"]);
        var p = vm.SelectedPage!.State;
        Assert.Equal(DocumentType.Generic, p.Type);
        Assert.Equal(0, p.Rotation);
        Assert.False(vm.IsAutoTypeVisible);
        Assert.False(vm.RereadTextCommand.CanExecute(null));
        Assert.Equal(0, ocr.Calls);
        Assert.Single(p.Candidates);
        Assert.Equal(string.Format(Properties.Strings.StatusImported, 1), vm.StatusMessage);

        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        Assert.False(vm.IsBusy); // OCR が無ければ同期で作り直す
        Assert.True(p.TypeIsManual);
        Assert.True(p.Candidates.Single().IsOn);

        vm.DismissOcrNoticeCommand.Execute(null);
        Assert.False(vm.IsOcrUnavailableNoticeVisible);
    }

    // ---- 回転・読み直し ----

    [Fact]
    public async Task Rotate_DoesNotReread()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        var calls = ocr.Calls;
        var findings = vm.SelectedPage!.State.Findings;
        vm.RotateRightCommand.Execute(null);
        Assert.Equal(calls, ocr.Calls);
        Assert.Same(findings, vm.SelectedPage.State.Findings);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Reread_UsesCurrentRotation_KeepsManualMasks_AndIsOneUndoStep()
    {
        // 正立の向き（赤が左上）以外では何も読めない。取り込み時は PDF（向きを判定しない）なので読めない。
        IReadOnlyList<OcrItem> Read(PageBitmap b) => IsRed(b, 1, 1) ? MyNumberBackOcr() : [];
        var source = new FakePageSource { Handler = _ => [new SourcePage(MarkedBottomLeft(), "doc.pdf", 0, true)] };
        var (vm, ocr, _) = Vm(Read, source: source);
        await vm.ImportAsync(["doc.pdf"]);
        var p = vm.SelectedPage!.State;
        Assert.Equal(DocumentType.Generic, p.Type);

        vm.RotateRightCommand.Execute(null); // 利用者が向きを直す（読み直さない）
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        var before = ocr.Calls;
        Assert.True(vm.RereadTextCommand.CanExecute(null));
        vm.RereadTextCommand.Execute(null);
        await WaitIdle(vm);

        Assert.True(ocr.Calls > before);
        Assert.Equal(1, p.Findings.OcrTurns);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type); // 自動のままなので判定結果に置き換わる
        Assert.False(p.TypeIsManual);
        Assert.Single(p.Manual.Rects); // 手で塗った箇所はそのまま
        var number = Assert.Single(p.Candidates, c => c.RuleId == "mynumB.number");
        // OCR の枠（OcrTurns = 1）＝いまの向き（Rotation = 1）なので箱は回さない
        Assert.Equal(NumberBox.Padded(Presets[DocumentType.MyNumberCardBack].Rules.Single(r => r.Id == "mynumB.number").EffectivePadding),
                     number.Box);
        Assert.Equal(Properties.Strings.StatusReread, vm.StatusMessage);

        vm.UndoCommand.Execute(null); // 読み直しを 1 回で戻す
        Assert.Equal(DocumentType.Generic, p.Type);
        Assert.Equal(1, p.Rotation);
        Assert.Single(p.Manual.Rects);
        Assert.Empty(p.Findings.Ocr!);
    }

    [Fact]
    public async Task Reread_KeepsManualType()
    {
        var (vm, _, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.Hokensho);
        Assert.False(vm.IsBusy); // OCR 済みなので読み直さない
        Assert.True(vm.SelectedPage!.State.TypeIsManual);
        Assert.False(vm.IsAutoTypeVisible);
        vm.RereadTextCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal(DocumentType.Hokensho, vm.SelectedPage.State.Type);
    }

    [Fact]
    public async Task Reread_Failure_ChangesNothing_AndSaysSo()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        var p = vm.SelectedPage!.State;
        ocr.Throw = true;
        vm.RereadTextCommand.Execute(null);
        await WaitIdle(vm);
        Assert.False(p.CanUndo);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.Equal(Properties.Strings.StatusRereadFailed, vm.StatusMessage);
    }

    [Fact]
    public async Task WhileAnalysing_EditsAreIgnored_AndUndoIsDisabled()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        var p = vm.SelectedPage!.State;
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        ocr.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RereadTextCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsIdle);

        vm.AddRect(new NormRect(0.5, 0.5, 0.2, 0.2));
        vm.AddStroke([new NormPoint(0.2, 0.2), new NormPoint(0.3, 0.3)]);
        vm.SetCandidateOn(p.Candidates[0].Id, !p.Candidates[0].IsOn);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.Hokensho);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RereadTextCommand.CanExecute(null));
        Assert.False(vm.RotateRightCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        ocr.Gate.SetResult();
        await WaitIdle(vm);
        Assert.Single(p.Manual.Rects);
        Assert.Empty(p.Manual.Strokes);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.False(p.TypeIsManual);
    }

    // ---- 種別の変更 ----

    [Fact]
    public async Task ChangeType_WithoutOcr_ReadsFirstOffThread_ThenIsOneUndoStep()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        ocr.Throw = true; // 取り込み時は読めなかった（Ocr = null）
        await vm.ImportAsync(["a.png"]);
        var p = vm.SelectedPage!.State;
        Assert.Null(p.Findings.Ocr);
        ocr.Throw = false;
        var before = ocr.Calls;

        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        await WaitIdle(vm);
        Assert.True(ocr.Calls > before);
        Assert.NotNull(p.Findings.Ocr);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.True(p.TypeIsManual);
        Assert.Contains(p.Candidates, c => c.RuleId == "mynumB.number");
        Assert.Equal(DocumentType.MyNumberCardBack, vm.SelectedDocumentType!.Type);

        vm.UndoCommand.Execute(null);
        Assert.Equal(DocumentType.Generic, p.Type);
        Assert.False(p.TypeIsManual);
        Assert.Null(p.Findings.Ocr);
        Assert.False(p.CanUndo);
    }

    [Fact]
    public async Task ChangeType_OcrFailure_StillChangesType_WithoutOcr()
    {
        var (vm, ocr, _) = Vm(_ => MyNumberBackOcr());
        ocr.Throw = true;
        await vm.ImportAsync(["a.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        await WaitIdle(vm);
        var p = vm.SelectedPage!.State;
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.True(p.TypeIsManual);
        Assert.Null(p.Findings.Ocr);
        Assert.Equal(Properties.Strings.StatusRereadFailed, vm.StatusMessage);
    }

    // ---- 切り抜き ----

    private static readonly Quad Card = new(new(0.1, 0.1), new(0.9, 0.1), new(0.9, 0.9), new(0.1, 0.9));

    [Fact]
    public async Task Crop_RereadsAndReplacesAutoType_IsOneUndoStep_AndHidesCropHint()
    {
        var reads = 0;
        // 取り込み時は表、切り抜いた後は裏と読める（自動の種別が置き換わることを見る）。
        var (vm, ocr, _) = Vm(_ => Volatile.Read(ref reads) == 0 ? MyNumberFrontOcr() : MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]); // 40×20 = カードの縦横比ではない
        var p = vm.SelectedPage!.State;
        Assert.Equal(DocumentType.MyNumberCardFront, p.Type);
        Assert.False(p.CardFramed);
        Assert.True(vm.IsCropHintVisible); // 固定領域のある種別なのに切り抜いていない
        Assert.DoesNotContain(p.Candidates, c => c.Source == CandidateSource.FixedRegion);

        Interlocked.Increment(ref reads);
        var before = ocr.Calls;
        await vm.ApplyCropAsync(vm.SelectedPage, Card);
        Assert.True(ocr.Calls > before);
        Assert.True(p.CardFramed);
        Assert.False(vm.IsCropHintVisible);
        Assert.Equal(DocumentType.MyNumberCardBack, p.Type);
        Assert.False(p.TypeIsManual);
        Assert.Contains(p.Candidates, c => c.RuleId == "mynumB.number" && c.Source == CandidateSource.Detector);

        vm.UndoCommand.Execute(null); // 切り抜き（と読み直し・種別）を 1 回で戻す
        Assert.Null(p.Crop);
        Assert.Equal(DocumentType.MyNumberCardFront, p.Type);
        Assert.True(vm.IsCropHintVisible);
        Assert.False(p.CanUndo);
    }

    [Fact]
    public async Task Crop_KeepsManualType_AndAddsFixedRegions()
    {
        var (vm, _, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardFront);
        var p = vm.SelectedPage!.State;
        Assert.True(vm.IsCropHintVisible);
        await vm.ApplyCropAsync(vm.SelectedPage, Card);
        Assert.Equal(DocumentType.MyNumberCardFront, p.Type); // 利用者が選んだ種別は戻さない
        Assert.True(p.TypeIsManual);
        Assert.Contains(p.Candidates, c => c.RuleId == "mynumF.gender" && c.Source == CandidateSource.FixedRegion);
        Assert.Contains(p.Candidates, c => c.RuleId == "mynumF.organDonation");
    }

    [Fact]
    public async Task Checklist_Notes_FollowRecognizerAndCrop_AndUndo()
    {
        string NoteOf(MainViewModel vm, string ruleId) => vm.Checklist.Single(c => c.Row.RuleId == ruleId).Note!;

        var (vm, _, _) = Vm(_ => MyNumberFrontOcr());
        await vm.ImportAsync(["a.png"]); // カードの縦横比ではない
        Assert.Equal(DocumentType.MyNumberCardFront, vm.SelectedPage!.State.Type);
        Assert.Equal("切り抜くと位置で候補になります", NoteOf(vm, "mynumF.gender"));
        Assert.Equal("自動で探します", NoteOf(vm, "mynumF.face"));
        Assert.True(vm.Checklist.Single(c => c.Row.RuleId == "mynumF.gender").HasCheckBox); // チェックボックスの規則は変えない

        await vm.ApplyCropAsync(vm.SelectedPage, Card);
        Assert.Equal("位置で候補にしています", NoteOf(vm, "mynumF.gender"));
        vm.UndoCommand.Execute(null);
        Assert.Equal("切り抜くと位置で候補になります", NoteOf(vm, "mynumF.gender"));

        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MenkyoshoFront);
        await WaitIdle(vm);
        Assert.Equal("自動で探します（見つからなければ手で塗ってください）", NoteOf(vm, "menkyo.number"));

        var (noOcr, _, _) = Vm(_ => [], withRecognizer: false);
        await noOcr.ImportAsync(["a.png"]);
        noOcr.SelectedDocumentType = noOcr.DocumentTypes.Single(t => t.Type == DocumentType.MenkyoshoFront);
        await WaitIdle(noOcr);
        Assert.Equal("手動で塗ってください", NoteOf(noOcr, "menkyo.number"));
        Assert.Equal("自動で探します", NoteOf(noOcr, "menkyo.face"));
    }

    [Fact]
    public async Task Crop_ReadsAtCurrentRotation()
    {
        var (vm, _, _) = Vm(_ => MyNumberBackOcr());
        await vm.ImportAsync(["a.png"]);
        vm.RotateRightCommand.Execute(null);
        await vm.ApplyCropAsync(vm.SelectedPage!, Card);
        Assert.Equal(1, vm.SelectedPage!.State.Findings.OcrTurns);
    }

    // ---- 保存 ----

    [Fact]
    public async Task Library_SavesFacesAndManualType_NeverOcrText_AndRestoredPageRereadsOnTypeChange()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var delay = new ManualDelay();
        var face = new NormRect(0.6, 0.1, 0.2, 0.3);
        var (vm, _, _) = Vm(_ => MyNumberBackOcr(), face: new FakeFaceDetector(face), library: lib, delay: delay);
        await vm.ImportAsync(["back.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.ZairyuCardFront);
        delay.FireAll();
        await vm.FlushSaveAsync();
        var id = vm.CurrentWorkId!.Value;

        var bytes = File.ReadAllBytes(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName));
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Number, json);
        Assert.DoesNotContain("コールセンター", json);
        var doc = WorkDocumentJson.Parse(bytes);
        Assert.Equal([face], doc.Pages[0].RawFaces);
        Assert.True(doc.Pages[0].TypeIsManual);
        Assert.Empty(doc.Pages[0].Ocr);

        // 再開: OCR は無い（OcrTurns = 0）。種別を変えると先に読み直す。
        var restored = WorkMapper.Restore(lib.Load(id)).Single();
        Assert.Null(restored.Findings.Ocr);
        Assert.Equal(0, restored.Findings.OcrTurns);
        Assert.Equal([face], restored.Findings.Faces);
        Assert.True(restored.TypeIsManual);

        await vm.NewDocumentAsync();
        await vm.RefreshLibraryAsync();
        await vm.OpenWorkAsync(Assert.Single(vm.LibraryItems));
        var p = vm.SelectedPage!.State;
        Assert.Null(p.Findings.Ocr);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        await WaitIdle(vm);
        Assert.NotNull(p.Findings.Ocr);
        Assert.Contains(p.Candidates, c => c.RuleId == "mynumB.number");
    }

    // ---- CandidateBuilder ----

    [Fact]
    public void Builder_MapsFromOcrFrameToCurrentRotation()
    {
        var preset = Presets[DocumentType.MyNumberCardBack];
        var pad = preset.Rules.Single(r => r.Id == "mynumB.number").EffectivePadding;
        var qrPad = preset.Rules.Single(r => r.Detector == DetectorId.QrBarcode).EffectivePadding;
        var qr = new NormRect(0.7, 0.6, 0.2, 0.3); // R0
        var findings = new PageFindings(1, MyNumberBackOcr(), [qr], []);
        for (var rotation = 0; rotation < 4; rotation++)
        {
            var cs = CandidateBuilder.Build(preset, findings, rotation, cardFramed: false);
            var number = cs.Single(c => c.RuleId == "mynumB.number");
            Assert.Equal(QuarterTurn.Rotate(NumberBox.Padded(pad), rotation - 1), number.Box);
            var q = cs.Single(c => c.Detector == DetectorId.QrBarcode);
            AssertNear(QuarterTurn.Rotate(QuarterTurn.Rotate(qr, 1).Padded(qrPad), rotation - 1), q.Box);
            AssertNear(QuarterTurn.Rotate(qr, rotation).Padded(qrPad), q.Box); // = いまの向きに回してから padding
        }
    }

    [Fact]
    public void Builder_WithoutOcr_PutsFixedRegionsInTheVisibleOrientation()
    {
        // 文字を読んでいないページを利用者が回して正立させたとき、固定領域は「見えている向き」の欄の位置に出す。
        var preset = Presets[DocumentType.MyNumberCardFront];
        var gender = preset.Rules.Single(r => r.Id == "mynumF.gender");
        var expected = gender.Region!.ToNormRect().Padded(gender.EffectivePadding);
        for (var rotation = 0; rotation < 4; rotation++)
        {
            var cs = CandidateBuilder.Build(preset, PageFindings.Empty, rotation, cardFramed: true);
            Assert.Equal(expected, cs.Single(c => c.RuleId == gender.Id).Box);
        }
    }

    [Fact]
    public void Builder_NoFixedRegions_UnlessCardFramed()
    {
        var preset = Presets[DocumentType.MyNumberCardFront];
        Assert.Empty(CandidateBuilder.Build(preset, PageFindings.Empty, 0, cardFramed: false));
        Assert.Empty(CandidateBuilder.Build(null, PageFindings.Empty, 0, cardFramed: true));
    }

    [Fact]
    public void DisplayText_KeepsDetectionNote_AndFollowsLanguage()
    {
        var preset = Presets[DocumentType.MyNumberCardBack];
        var rule = preset.Rules.Single(r => r.Id == "mynumB.number");
        var detected = CandidateBuilder.Build(preset, new PageFindings(0, MyNumberBackOcr(), [], []), 0, false, english: false)
            .Single(c => c.RuleId == rule.Id);
        var masked = $"{Number[..2]}********{Number[^2..]}";
        Assert.Equal($"{rule.Label}（{masked}）", CandidateBuilder.DisplayText(preset, detected, english: false).Label);
        Assert.Equal($"{rule.LocalizedLabel(true)} ({masked})", CandidateBuilder.DisplayText(preset, detected, english: true).Label);

        var estimated = CandidateBuilder.Build(preset, PageFindings.Empty, 0, cardFramed: true, english: true)
            .Single(c => c.RuleId == rule.Id);
        Assert.Equal($"{rule.LocalizedLabel(true)} (position estimated)", estimated.Label);
        Assert.Equal($"{rule.Label}（位置推定）", CandidateBuilder.DisplayText(preset, estimated, english: false).Label);
        Assert.Equal(rule.Basis, CandidateBuilder.DisplayText(preset, estimated, english: false).Basis);

        var unknown = detected with { Label = "別の文言" };
        Assert.Equal("別の文言", CandidateBuilder.DisplayText(preset, unknown, english: false).Label);
    }

    private static void AssertNear(NormRect expected, NormRect actual)
    {
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.W, actual.W, 12);
        Assert.Equal(expected.H, actual.H, 12);
    }

    /// <summary>WPF の UI スレッド（STA・Dispatcher の同期コンテキスト）で非同期の本体を回す。</summary>
    private static void RunOnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(); }
                catch (Exception e) { error = e; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI スレッドのテストが終わらない");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
