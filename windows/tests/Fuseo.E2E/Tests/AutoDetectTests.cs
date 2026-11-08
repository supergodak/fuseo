using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>
/// 文字認識による自動検出（W9-3・受け入れは W9-5 §10.19-2）: 種類の自動判定と「自動」表示・伏せた番号の候補（生の番号は画面の
/// どこにも出ない）・逆さの写真の自動正立・「文字を読み直す」と元に戻す・切り抜きの案内と固定領域の候補。
/// 入力は実行時に WPF で描いた合成カード（<see cref="SynthCards"/>・番号は見るからに合成の本体＋検査用数字）。
/// 日本語の文字認識が入っている Windows が前提（VM は日本語版）。
/// </summary>
public class AutoDetectTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void MyNumberBack_AutoType_Badge_MaskedNumber_RawNumberNowhereInUi() => Run(s =>
    {
        var (digits, printed) = SynthCards.MyNumber();
        var card = SynthCards.MyNumberBack(printed);
        var png = Synth.WritePng(s.InPath("back.png"), card.Bitmap);
        var app = s.Launch();
        app.Import(1, png);

        app.WaitDocumentType(Ja.TypeMyNumberBack);
        var badge = app.Main.Get("side.autoTypeBadge");
        Assert.True(badge.IsShown(), "「自動」の表示が無い");
        Assert.Equal("自動", badge.Text());
        var masked = SynthCards.Masked(digits);
        Wait.Until(() => app.CandidateTexts().Contains($"{Ja.RuleMyNumber}（{masked}）"), "伏せた番号の候補が出る");
        app.WaitMaskCount(2); // 個人番号と QR（どちらも既定で ON）
        Assert.False(app.Main.Find("side.cropHint").IsShown(), "カード 1 枚なのに切り抜きの案内が出る");
        Assert.False(app.Button("main.undo").IsEnabled(), "取り込み時の自動判定が元に戻す対象になっている");
        // 右ペインのチェックリスト（W9-5）: QR は「自動で探します」、位置推定を持つ個人番号は先に文字で探し、カード 1 枚なので見つからなければ位置で。
        Assert.Contains(Ja.NoteAuto, app.ChecklistNotes());
        Assert.Contains(Ja.NoteTextThenPosition, app.ChecklistNotes());

        // 生の番号（続けた形・印字の形・空白なしの本体）は、メイン画面にも書き出しシートにも出ない。
        void AssertNoRawNumber(string where)
        {
            var texts = app.AllUiText();
            Output.WriteLine($"{where}: UIA の文字 {texts.Count} 件");
            Assert.True(texts.Count > 30, "UIA の文字が少なすぎる（集め方の誤り）");
            Assert.Contains(texts, t => t.Contains(masked, StringComparison.Ordinal));
            foreach (var raw in new[] { digits, printed, digits[..11], digits[4..] })
                Assert.DoesNotContain(texts, t => t.Replace(" ", "").Replace("　", "").Contains(raw.Replace(" ", ""), StringComparison.Ordinal));
        }
        AssertNoRawNumber("メイン画面");
        var w = app.OpenExport();
        AssertNoRawNumber("書き出しシート");
        Actions.ChooseFormat(w, "png");
        var outPng = s.OutPath("back-out.png");
        app.SaveExport(w, outPng);
        OutputCheck.AssertClean(outPng);
        // 書き出した画像では番号の印字が黒く塗られている。
        OutputCheck.AssertPainted(OutputCheck.LoadSingle(outPng), card.Ink["number"], "番号の印字", inset: 0);
    });

    [Fact]
    public void UpsideDownCard_IsTurnedUprightAutomatically_NotAnUndoStep() => Run(s =>
    {
        var number = SynthCards.LicenseNumber();
        var card = SynthCards.LicenseFront(number);
        var png = Synth.WritePng(s.InPath("upside-down.png"), SynthCards.Rotate180(card.Bitmap));
        var app = s.Launch();
        app.Import(1, png);

        app.WaitDocumentType(Ja.TypeLicenseFront);
        Wait.Until(() => app.CandidateTexts().Contains($"{Ja.RuleLicenseNumber}（{SynthCards.Masked(number)}）"), "免許証番号の候補が出る");
        Assert.False(app.Button("main.undo").IsEnabled(), "自動の向きが元に戻す対象になっている");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 2, "向き（180°）が保存される");

        // 目印の黒い四角が、正立したカードの位置（右下寄り）に来ている＝ 180° 回して表示・書き出しされる。
        var outPng = s.OutPath("upright.png");
        app.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        OutputCheck.AssertPainted(bmp, SynthCards.Badge, "正立した位置の目印", inset: 0.01);
        // 逆さのままなら目印が来る位置（左上寄り）は塗りつぶされていない（そこには正立したカードの文字が少し掛かるだけ）。
        var wrong = OutputCheck.DarkFraction(bmp, SynthCards.Rotate180(SynthCards.Badge).Inset(0.01));
        Assert.True(wrong < 0.3, $"目印が逆さのままの位置にある（黒 {wrong:P1}）");
        OutputCheck.AssertPainted(bmp, card.Ink["number"].Inset(0.002), "免許証番号（正立した位置）", inset: 0);
    });

    [Fact]
    public void Reread_AfterRotating_RebuildsCandidates_OneUndoRestores() => Run(s =>
    {
        var (digits, printed) = SynthCards.MyNumber();
        var png = Synth.WritePng(s.InPath("back.png"), SynthCards.MyNumberBack(printed).Bitmap);
        var app = s.Launch();
        app.Import(1, png);
        var numberLabel = $"{Ja.RuleMyNumber}（{SynthCards.Masked(digits)}）";
        Wait.Until(() => app.CandidateTexts().Contains(numberLabel), "伏せた番号の候補が出る");
        var reread = app.Button("side.rereadText");
        Assert.True(reread.IsEnabled(), "「文字を読み直す」が押せない");

        app.Button("main.rotateRight").Click(); // 回すだけでは読み直さない（候補は一緒に回る）
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "回転が保存される");
        var before = app.CandidateTexts();
        Assert.Contains(numberLabel, before);

        // 横倒しの向きで読み直す → 種類の判定と候補が、その向きで読んだ結果で作り直される
        // （実測: 種類は「その他の書類」になり、縦に読めた番号が汎用の「マイナンバー（12桁）」の候補として出る）。
        reread.Click();
        Wait.Until(() => app.Status() == Ja.StatusReread, "読み直しの表示", TimeSpan.FromSeconds(30));
        Wait.Until(() => !app.CandidateTexts().SequenceEqual(before), "候補が作り直される");
        Output.WriteLine($"読み直し後: 種類={app.DocumentTypeName()} 候補=[{string.Join(" / ", app.CandidateTexts())}]");

        app.Key(VKey.Z, VKey.Control); // 読み直しは 1 回の変更として戻る（回転はそのまま）
        Wait.Until(() => app.CandidateTexts().SequenceEqual(before), "元に戻すと候補が読み直す前に戻る");
        app.WaitDocumentType(Ja.TypeMyNumberBack);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1 && d.Pages[0].DocumentType == Fuseo.Core.Model.DocumentType.MyNumberCardBack,
                            "回転は残り、種類が戻って保存される");
        app.Key(VKey.Z, VKey.Control); // もう 1 回で回転が戻る
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 0, "回転が戻る");
        Assert.False(app.Button("main.undo").IsEnabled(), "取り込み時の状態より前に戻れる");
    });

    [Fact]
    public void CardOnDesk_ShowsCropHint_CropMakesFixedRegionCandidates() => Run(s =>
    {
        var card = SynthCards.MyNumberFront();
        var place = new Box(0.19, 0.25, 0.625, 0.5255); // 机 1600×1200 の中に 1000×631px（カードの縦横比）
        var desk = SynthCards.OnDesk(card.Bitmap, 1600, 1200, place);
        var jpg = Synth.WriteJpeg(s.InPath("desk.jpg"), desk);
        var app = s.Launch();
        app.Import(1, jpg);

        app.WaitDocumentType(Ja.TypeMyNumberFront);
        Wait.Until(() => app.Main.Find("side.cropHint").IsShown(), "切り抜きの案内が出る");
        Assert.DoesNotContain(app.CandidateTexts(), t => t.StartsWith(Ja.RuleGender, StringComparison.Ordinal));
        Assert.Contains(Ja.NoteCropForPosition, app.ChecklistNotes());
        Assert.DoesNotContain(Ja.NotePositioned, app.ChecklistNotes());

        app.Button("main.crop").Click();
        var crop = app.WaitWindow(Ja.CropWindow);
        Wait.Until(() => crop.Find("crop.image").IsShown(), "切り抜きの画像が出る");
        var img = crop.Get("crop.image");
        Mouse.Drag(img.At(0, 0), img.At(place.X, place.Y));
        Mouse.Drag(img.At(1, 0), img.At(place.Right, place.Y));
        Mouse.Drag(img.At(1, 1), img.At(place.Right, place.Bottom));
        Mouse.Drag(img.At(0, 1), img.At(place.X, place.Bottom));
        crop.Get("crop.apply").Click();
        app.WaitWindowClosed(Ja.CropWindow);

        Wait.Until(() => !app.Main.Find("side.cropHint").IsShown(), "切り抜くと案内が消える", TimeSpan.FromSeconds(30));
        Wait.Until(() => app.CandidateTexts().Contains(Ja.RuleGender) && app.CandidateTexts().Contains(Ja.RuleOrganDonation),
                   "性別・臓器提供の固定領域が候補になる", TimeSpan.FromSeconds(30));
        app.WaitDocumentType(Ja.TypeMyNumberFront);
        Wait.Until(() => app.ChecklistNotes().Count(n => n == Ja.NotePositioned) == 2, "チェックリストが「位置で候補にしています」になる");
        app.WaitMaskCount(2); // 性別・臓器提供（既定で ON）

        app.Key(VKey.Z, VKey.Control); // 切り抜き（と読み直し）を 1 回で戻す
        Wait.Until(() => app.Main.Find("side.cropHint").IsShown(), "元に戻すと案内がまた出る");
        Wait.Until(() => !app.CandidateTexts().Contains(Ja.RuleGender), "固定領域の候補が消える");
    });
}
