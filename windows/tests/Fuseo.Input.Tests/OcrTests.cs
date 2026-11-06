using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Input.Detection;
using Fuseo.Input.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.Input.Tests;

/// <summary>
/// 合成カードの描画（WPF・STA スレッド・<see cref="FormattedText"/>＋<see cref="RenderTargetBitmap"/>）。
/// **画像はメモリ上だけで作り、ファイルに書かない**。番号はテスト実行時に見るからに合成の本体から作る（チェックデジットだけ正しい）。
/// </summary>
internal static class SyntheticCard
{
    /// <summary>ID-1 を約 508dpi で描いた寸法（W9-0 の合成カードと同じ）。</summary>
    public const int Width = 1712, Height = 1080;

    public sealed record Text(string Key, string Value, double X, double Y, double Size, bool Bold = false);

    /// <summary>描いた画像と、各文字列のインクの外接矩形（画素・左上原点）。</summary>
    public sealed record Card(PageBitmap Bitmap, IReadOnlyDictionary<string, Rect> Ink, string FontFamily);

    public static Card Render(IReadOnlyList<Text> texts, int width = Width, int height = Height) => RunSta(() =>
    {
        var family = PickFont();
        var visual = new DrawingVisual();
        var ink = new Dictionary<string, Rect>();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 244, 248)), null, new Rect(0, 0, width, height));
            foreach (var t in texts)
            {
                var typeface = new Typeface(new FontFamily(family), FontStyles.Normal,
                                            t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(t.Value, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                                           typeface, t.Size, Brushes.Black, 1.0);
                var origin = new Point(t.X, t.Y);
                dc.DrawText(ft, origin);
                ink[t.Key] = ft.BuildGeometry(origin).Bounds;
            }
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[width * height * 4];
        rtb.CopyPixels(px, width * 4, 0);   // 背景が不透明なのでプリマルチプライ＝ストレート
        return new Card(new PageBitmap(width, height, px), ink, family);
    });

    /// <summary>Yu Gothic UI が無ければ Meiryo（どちらも Windows 標準の日本語フォント）。</summary>
    private static string PickFont()
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Yu Gothic UI") ? "Yu Gothic UI" : "Meiryo";
    }

    private static T RunSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return result;
    }

    /// <summary>末尾（または指定の位置）の 1 桁を 0..9 で試し、検証が通る番号を返す。</summary>
    public static string MakeValid(Func<char, string> build, Func<string, bool> validate)
    {
        for (var c = '0'; c <= '9'; c++)
            if (validate(build(c))) return build(c);
        throw new InvalidOperationException("チェックデジットが見つからない本体（テストの作りの誤り）");
    }
}

/// <summary>
/// Windows 標準 OCR のアダプタ（W9-2・§10.13）。合成カードを実行時に描いて OCR → 種別判定・番号検出・正立判定・箱の位置を確かめる。
/// 日本語の OCR が入っていない環境では OCR を使うテストを早期 return で合格扱いにし、理由を出力する。
/// </summary>
public class OcrTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<DocumentPreset> Presets = PresetStore.LoadAll();

    private WindowsOcrRecognizer? Recognizer()
    {
        var r = WindowsOcrRecognizer.TryCreate();
        if (r is null) output.WriteLine("日本語の文字認識（OCR）がこの環境に無いため、OCR を使う確認を省略した（合格扱い）。");
        return r;
    }

    /// <summary>個人番号（本体は 01234567890・検査用数字だけ実行時に決める）を 4 桁ずつ空けた表記。</summary>
    private static (string Digits, string Printed) SyntheticMyNumber()
    {
        var n = SyntheticCard.MakeValid(c => "01234567890" + c, Checkdigits.IsValidMyNumber);
        return (n, $"{n[..4]} {n[4..8]} {n[8..]}");
    }

    private static SyntheticCard.Card MyNumberBack(string printed) => SyntheticCard.Render(
    [
        new("label", "個人番号", 80, 70, 56, Bold: true),
        new("number", printed, 80, 170, 110, Bold: true),
        new("name", "氏名　見本　花子", 80, 380, 56),
        new("found", "このカードを拾得された方は", 80, 600, 48),
        new("call", "お近くの警察署またはコールセンターへ", 80, 690, 48),
    ]);

    private string Dump(IReadOnlyList<OcrItem> ocr) =>
        // 合成の値しか無い画像なので行の文字列を出してよい（失敗時の調査用）。
        string.Join(" / ", ocr.Select(i => i.Text));

    [Fact]
    public async Task MyNumberCardBack_ClassifiedAndNumberFound_BoxMatchesInk()
    {
        if (Recognizer() is not { } ocr) return;
        var (digits, printed) = SyntheticMyNumber();
        var card = MyNumberBack(printed);
        output.WriteLine($"font={card.FontFamily}");

        var items = await ocr.RecognizeAsync(card.Bitmap, CancellationToken.None);
        output.WriteLine(Dump(items));

        Assert.Equal(DocumentType.MyNumberCardBack, KeywordClassifier.Classify(items, Presets).Type);
        var fields = FieldDetector.Detect(DetectorId.MyNumber12, items);
        var field = Assert.Single(fields);
        Assert.Equal(digits[..2] + "********" + digits[^2..], field.MaskedDescription);

        // OCR の箱が描いた文字（インクの外接矩形）に合う（各辺 2% 以内）。
        var ink = card.Ink["number"];
        var expected = new NormRect(ink.X / card.Bitmap.Width, ink.Y / card.Bitmap.Height,
                                    ink.Width / card.Bitmap.Width, ink.Height / card.Bitmap.Height);
        output.WriteLine($"ink={expected} ocr={field.Box}");
        Assert.InRange(field.Box.X, expected.X - 0.02, expected.X + 0.02);
        Assert.InRange(field.Box.Y, expected.Y - 0.02, expected.Y + 0.02);
        Assert.InRange(field.Box.Right, expected.Right - 0.02, expected.Right + 0.02);
        Assert.InRange(field.Box.Bottom, expected.Bottom - 0.02, expected.Bottom + 0.02);

        // 候補まで通す（ルールの初期 ON/OFF はプリセットどおり）。
        var preset = Presets.Single(p => p.DocumentType == DocumentType.MyNumberCardBack);
        var candidates = RuleEngine.Candidates(preset, items, new Dictionary<DetectorId, IReadOnlyList<NormRect>>(),
                                               cardFramed: true, english: false);
        Assert.Contains(candidates, c => c.Label.Contains(field.MaskedDescription!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LicenseFront_ClassifiedAndNumberFound()
    {
        if (Recognizer() is not { } ocr) return;
        var number = SyntheticCard.MakeValid(c => "1234567890" + c + "0", Checkdigits.IsValidLicenseNumber);
        var card = SyntheticCard.Render(
        [
            new("title", "運転免許証", 80, 50, 64, Bold: true),
            new("name", "氏名　見本　太郎", 80, 180, 56),
            new("issue", "交付　令和元年　1月　1日", 80, 290, 48),
            new("number", $"第 {number} 号", 80, 420, 64),
            new("cond", "免許の条件等　眼鏡等", 80, 560, 48),
            new("pref", "見本県公安委員会", 80, 880, 56),
        ]);

        var items = await ocr.RecognizeAsync(card.Bitmap, CancellationToken.None);
        output.WriteLine(Dump(items));

        Assert.Equal(DocumentType.MenkyoshoFront, KeywordClassifier.Classify(items, Presets).Type);
        var field = Assert.Single(FieldDetector.Detect(DetectorId.LicenseNumber12, items));
        Assert.False(field.Partial);
        Assert.Equal(number[..2] + "********" + number[^2..], field.MaskedDescription);
    }

    [Fact]
    public async Task ZairyuCard_ClassifiedAndNumberFound()
    {
        if (Recognizer() is not { } ocr) return;
        var number = "AB" + "12345678" + "CD";   // 見るからに合成（在留カード番号はチェックデジットの公開規定が無い）
        var card = SyntheticCard.Render(
        [
            new("title", "在留カード", 80, 50, 64, Bold: true),
            new("number", number, 80, 170, 64),
            new("name", "氏名　SAMPLE TARO", 80, 300, 52),
            new("status", "在留資格　見本", 80, 420, 52),
            new("period", "在留期間　見本", 80, 540, 52),
        ]);

        var items = await ocr.RecognizeAsync(card.Bitmap, CancellationToken.None);
        output.WriteLine(Dump(items));

        Assert.Equal(DocumentType.ZairyuCardFront, KeywordClassifier.Classify(items, Presets).Type);
        var field = Assert.Single(FieldDetector.Detect(DetectorId.ZairyuNumber, items));
        Assert.Equal("AB********CD", field.MaskedDescription);
    }

    [Fact]
    public async Task UpsideDownCard_PageAnalyzerChooses180()
    {
        if (Recognizer() is not { } ocr) return;
        var (digits, printed) = SyntheticMyNumber();
        var card = MyNumberBack(printed);
        var upsideDown = QuarterTurn.Rotate(card.Bitmap, 2);

        var a = await PageAnalyzer.AnalyzeAsync(upsideDown, ocr, Presets, detectUpright: true, CancellationToken.None);
        output.WriteLine($"turns={a.QuarterTurns} type={a.Classification.Type}");

        Assert.Equal(2, a.QuarterTurns);
        Assert.Equal(DocumentType.MyNumberCardBack, a.Classification.Type);
        var field = Assert.Single(FieldDetector.Detect(DetectorId.MyNumber12, a.Ocr));
        Assert.Equal(digits[..2] + "********" + digits[^2..], field.MaskedDescription);
        // 座標は回した（＝正立に戻した）画像の上: 描いた位置に合う。
        var ink = card.Ink["number"];
        Assert.InRange(field.Box.X, ink.X / SyntheticCard.Width - 0.02, ink.X / SyntheticCard.Width + 0.02);
        Assert.InRange(field.Box.Y, ink.Y / SyntheticCard.Height - 0.02, ink.Y / SyntheticCard.Height + 0.02);
    }

    [Fact]
    public async Task BlankPage_ReturnsNoLines()
    {
        if (Recognizer() is not { } ocr) return;
        var items = await ocr.RecognizeAsync(PageBitmap.Solid(800, 500, 255, 255, 255), CancellationToken.None);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        if (Recognizer() is not { } ocr) return;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ocr.RecognizeAsync(PageBitmap.Solid(100, 100, 255, 255, 255), cts.Token));
    }

    // ---- OCR 無しで確かめられる部分 ----

    [Fact]
    public void ToItems_NormalizesByPassedImageSize_AndDropsEmptyLines()
    {
        var lines = new[]
        {
            new[] { ("個", 100.0, 50.0, 20.0, 30.0), ("人", 120.0, 50.0, 20.0, 30.0), ("12", 150.0, 52.0, 30.0, 26.0), ("34", 190.0, 52.0, 30.0, 26.0) },
            Array.Empty<(string, double, double, double, double)>(),
            new[] { ("", 0.0, 0.0, 10.0, 10.0) },
            new[] { ("端", 390.0, 190.0, 30.0, 30.0) },   // はみ出しは 0..1 に収める
        };
        var items = WindowsOcrRecognizer.ToItems(lines, 400, 200);

        Assert.Equal(2, items.Count);
        Assert.Equal("個人12 34", items[0].Text);        // 両側が ASCII の単語の間だけ空白（OcrText.JoinWords）
        Assert.Equal(4, items[0].Words.Count);
        Assert.Equal(new NormRect(0.25, 0.25, 0.3, 0.15), Round(items[0].Box));
        Assert.Equal(new NormRect(0.975, 0.95, 0.025, 0.05), Round(items[1].Box));
    }

    private static NormRect Round(NormRect r) =>
        new(Math.Round(r.X, 9), Math.Round(r.Y, 9), Math.Round(r.W, 9), Math.Round(r.H, 9));

    [Fact]
    public void FitForEngine_DownscalesOnlyAboveLimit()
    {
        var page = PageBitmap.Solid(300, 100, 255, 255, 255);
        Assert.Same(page, WindowsOcrRecognizer.FitForEngine(page, 300));
        var small = WindowsOcrRecognizer.FitForEngine(page, 150);
        Assert.Equal((150, 50), (small.Width, small.Height));
    }

    // ---- 顔 ----

    [Fact]
    public void FaceDetector_BlankImageHasNoFaces()
    {
        output.WriteLine($"FaceDetector.IsSupported={WindowsFaceDetector.IsSupported}");
        var detector = new WindowsFaceDetector();
        Assert.Equal(DetectorId.Face, detector.Id);
        Assert.Empty(detector.Detect(PageBitmap.Solid(800, 500, 255, 255, 255)));
        // 長辺の縮小（4000 → 1600）を通る大きさでも落ちない。
        Assert.Empty(detector.Detect(PageBitmap.Solid(4000, 2500, 240, 240, 240)));
    }

    [Fact]
    public void FaceDetector_GrayAndNormalize()
    {
        var p = new PageBitmap(2, 1);
        p.SetPixel(0, 0, 0, 0, 255, 255);     // 赤 → 0.299×255
        p.SetPixel(1, 0, 255, 255, 255, 255); // 白
        Assert.Equal(new byte[] { 76, 255 }, WindowsFaceDetector.ToGray8(p));
        Assert.Equal(new NormRect(0.25, 0.5, 0.5, 0.25), WindowsFaceDetector.Normalize(100, 100, 200, 50, 400, 200));
        Assert.Null(WindowsFaceDetector.Normalize(10, 10, 0, 5, 400, 200));
        Assert.Equal(new NormRect(0.75, 0.0, 0.25, 0.5), WindowsFaceDetector.Normalize(300, 0, 500, 100, 400, 200));
    }
}
