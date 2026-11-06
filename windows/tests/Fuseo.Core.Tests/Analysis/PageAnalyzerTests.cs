using Fuseo.Core.Analysis;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>
/// ページ解析の流れ（§10.13）: 正立判定で選ぶ向き・ID-1 再 OCR の条件（横長のみ・5% の境界・カード以外はしない）・
/// OCR の座標が「回した画像の上」であること。OCR は画像の印（赤い四角＝上端の左・黒い四角＝番号欄）を見て結果を決めるフェイク。
/// </summary>
public class PageAnalyzerTests
{
    private static readonly IReadOnlyList<DocumentPreset> Presets = PresetStore.LoadAll();

    /// <summary>マイナンバーカード裏（ID-1）に分類される文字列。</summary>
    private static readonly string[] CardText = ["個人番号", "コールセンター", "拾得"];

    /// <summary>保険証（ID-1 ではない）に分類される文字列。</summary>
    private static readonly string[] HokenText = ["保険者番号", "被保険者"];

    /// <summary>
    /// 画像を見て結果を決めるフェイク OCR。
    /// - 赤い印の中心が左上の 1/4 にあれば「正立」とみなし、<see cref="Texts"/> の行＋黒い四角の位置の数字行を返す。
    ///   それ以外の向きは文字化けを模して「ー」1 行だけ（<see cref="SameForAllOrientations"/> なら向きによらず同じ）。
    /// - 呼ばれた画像の寸法を記録する。<see cref="TextsAfterFirst"/> があれば 2 回目以降はそれを返す。
    /// </summary>
    private sealed class FakeOcr(string[] texts) : ITextRecognizer
    {
        public string[] Texts { get; } = texts;
        public string[]? TextsAfterFirst { get; init; }
        public bool SameForAllOrientations { get; init; }
        public bool EmptyAfterFirst { get; init; }
        public List<(int W, int H)> Calls { get; } = [];

        public Task<IReadOnlyList<OcrItem>> RecognizeAsync(PageBitmap page, CancellationToken ct)
        {
            var call = Calls.Count;
            Calls.Add((page.Width, page.Height));
            if (call > 0 && EmptyAfterFirst) return Task.FromResult<IReadOnlyList<OcrItem>>([]);
            var red = Find(page, (b, g, r) => r > 200 && g < 60 && b < 60);
            var black = Find(page, (b, g, r) => r < 100 && g < 100 && b < 100);
            var upright = SameForAllOrientations || red is { } m && m.X + m.W / 2 < 0.5 && m.Y + m.H / 2 < 0.5;
            var items = new List<OcrItem>();
            if (upright)
            {
                var texts = call > 0 && TextsAfterFirst is not null ? TextsAfterFirst : Texts;
                for (var i = 0; i < texts.Length; i++) items.Add(Line(texts[i], new NormRect(0.05, 0.1 + i * 0.1, 0.3, 0.05)));
                if (black is { } box) items.Add(Line($"番号{call}", box));
            }
            else
            {
                items.Add(Line("ー", new NormRect(0.4, 0.4, 0.05, 0.05)));
            }
            return Task.FromResult<IReadOnlyList<OcrItem>>(items);
        }

        private static OcrItem Line(string text, NormRect box) => new(text, box, [new OcrWord(text, box)]);

        /// <summary>条件に合う画素の外接矩形（正規化・右端/下端は画素の外側まで）。無ければ null。</summary>
        private static NormRect? Find(PageBitmap p, Func<byte, byte, byte, bool> match)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (var y = 0; y < p.Height; y++)
                for (var x = 0; x < p.Width; x++)
                {
                    var (b, g, r, _) = p.GetPixel(x, y);
                    if (!match(b, g, r)) continue;
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
            if (x1 < 0) return null;
            return new NormRect((double)x0 / p.Width, (double)y0 / p.Height, (double)(x1 + 1 - x0) / p.Width, (double)(y1 + 1 - y0) / p.Height);
        }
    }

    /// <summary>正立の合成ページ（白地・左上に赤い印・右下寄りに黒い四角）と、黒い四角の正規化位置。</summary>
    private static (PageBitmap Page, NormRect Black) Upright(int w, int h)
    {
        var p = PageBitmap.Solid(w, h, 255, 255, 255);
        Fill(p, w / 40, h / 40, w / 20, h / 20, 0, 0, 255);                 // 赤（BGR）
        int bx = w / 2, by = h * 3 / 5, bw = w / 4, bh = h / 8;
        Fill(p, bx, by, bw, bh, 0, 0, 0);                                    // 黒
        return (p, new NormRect((double)bx / w, (double)by / h, (double)bw / w, (double)bh / h));
    }

    private static void Fill(PageBitmap p, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++) p.SetPixel(xx, yy, b, g, r, 255);
    }

    private static NormRect NumberBox(PageAnalysis a) => a.Ocr.Single(i => i.Text.StartsWith("番号", StringComparison.Ordinal)).Box;

    private static void Near(NormRect expected, NormRect actual, double tol)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.W, expected.W - tol, expected.W + tol);
        Assert.InRange(actual.H, expected.H - tol, expected.H + tol);
    }

    // ---- 正立判定 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]   // 時計回り 90° で取り込まれた → あと 270° 回すと正立
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    public async Task DetectUpright_ChoosesTurnThatRestoresUpright_AndOcrIsOnRotatedImage(int rotatedBy, int expectedTurns)
    {
        var (upright, black) = Upright(400, 252);
        var page = QuarterTurn.Rotate(upright, rotatedBy);
        var ocr = new FakeOcr(CardText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: true, CancellationToken.None);

        Assert.Equal(expectedTurns, a.QuarterTurns);
        Assert.Equal(4, ocr.Calls.Count);                       // 4 方向を 1 回ずつ（選んだ向きは読み直さない）
        Assert.Equal(DocumentType.MyNumberCardBack, a.Classification.Type);
        // 箱は QuarterTurn.Rotate(page, QuarterTurns) の画像の上（＝正立の画像の上）。
        Near(black, NumberBox(a), 1e-9);
        var blackOnInput = QuarterTurn.Rotate(black, rotatedBy);
        Near(QuarterTurn.Rotate(blackOnInput, a.QuarterTurns), NumberBox(a), 1e-9);
        Assert.Contains(a.Ocr, i => i.Text == "番号" + a.QuarterTurns);   // t 回目（0 始まり）の呼び出し＝向き t の結果
    }

    [Fact]
    public async Task DetectUpright_TieKeepsOriginalOrientation()
    {
        var (upright, _) = Upright(400, 252);
        var page = QuarterTurn.Rotate(upright, 2);
        var ocr = new FakeOcr(CardText) { SameForAllOrientations = true };

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: true, CancellationToken.None);

        Assert.Equal(0, a.QuarterTurns);
        Assert.Equal(4, ocr.Calls.Count);
        Assert.Equal("番号0", a.Ocr.Single(i => i.Text.StartsWith("番号", StringComparison.Ordinal)).Text);  // 0° の結果をそのまま使う
    }

    [Fact]
    public async Task NoUprightDetection_ReadsOnceAsIs()
    {
        var (upright, _) = Upright(400, 252);
        var page = QuarterTurn.Rotate(upright, 2);   // 逆さでも回さない（PDF ページは /Rotate 適用済み）
        var ocr = new FakeOcr(CardText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(0, a.QuarterTurns);
        Assert.Equal([(400, 252)], ocr.Calls);
        Assert.Equal(DocumentType.Generic, a.Classification.Type);   // 文字化けした結果のまま
    }

    [Fact]
    public async Task UprightPage_IsReadWithoutRotation()
    {
        var (page, black) = Upright(400, 252);
        var ocr = new FakeOcr(CardText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(0, a.QuarterTurns);
        Assert.Equal(DocumentType.MyNumberCardBack, a.Classification.Type);
        Near(black, NumberBox(a), 1e-9);
    }

    // ---- ID-1 の再 OCR ----

    [Theory]
    [InlineData(1665, 1000, false)]   // 1.665 ≤ 1.58577×1.05 = 1.66506（境界の内側）
    [InlineData(1666, 1000, true)]    // 1.666 > 1.66506
    [InlineData(1507, 1000, false)]   // 1.507 ≥ 1.58577×0.95 = 1.50648
    [InlineData(1506, 1000, true)]    // 1.506 < 1.50648
    [InlineData(1586, 1000, false)]   // ほぼカード
    [InlineData(2000, 1000, true)]
    [InlineData(1000, 2000, false)]   // 縦長はしない
    [InlineData(1000, 1586, false)]
    [InlineData(1000, 1000, false)]   // 正方形は横長ではない
    public async Task Id1Card_ResamplesOnlyLandscapeOutsideTolerance(int w, int h, bool expectResample)
    {
        var (page, _) = Upright(w, h);
        var ocr = new FakeOcr(CardText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(DocumentType.MyNumberCardBack, a.Classification.Type);
        if (expectResample)
        {
            Assert.Equal(2, ocr.Calls.Count);
            var expectedW = (int)Math.Round(h * PipelineTuning.Default.CardAspect, MidpointRounding.AwayFromZero);
            Assert.Equal((expectedW, h), ocr.Calls[1]);                                  // 高さそのまま・幅だけ合わせる
            Assert.Contains(a.Ocr, i => i.Text == "番号1");                             // 再 OCR の結果に置き換わる
            Assert.DoesNotContain(a.Ocr, i => i.Text == "番号0");
        }
        else
        {
            Assert.Single(ocr.Calls);
            Assert.Contains(a.Ocr, i => i.Text == "番号0");
        }
    }

    [Fact]
    public async Task Id1Card_ResampledOcrReadsNothing_KeepsFirstOcr()
    {
        var (page, _) = Upright(2000, 1000);
        var ocr = new FakeOcr(CardText) { EmptyAfterFirst = true };

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(2, ocr.Calls.Count);
        Assert.Contains(a.Ocr, i => i.Text == "番号0");
    }

    [Fact]
    public async Task NonCardType_IsNotResampled()
    {
        var (page, _) = Upright(2000, 1000);
        var ocr = new FakeOcr(HokenText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(DocumentType.Hokensho, a.Classification.Type);
        Assert.Single(ocr.Calls);
    }

    [Fact]
    public async Task Resample_KeepsNormalizedCoordinates_AndClassificationFromFirstRead()
    {
        var (page, black) = Upright(2000, 1000);
        // 2 回目は保険証の文字列を返す → 種別は 1 回目（マイナ裏）のまま。
        var ocr = new FakeOcr(CardText) { TextsAfterFirst = HokenText };

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: false, CancellationToken.None);

        Assert.Equal(2, ocr.Calls.Count);
        Assert.Equal(DocumentType.MyNumberCardBack, a.Classification.Type);
        Assert.Contains(a.Ocr, i => i.Text == "保険者番号");
        // 横だけ縮めた画像の上の正規化座標は元の画像の上でも同じ（補間の端の分だけ 1px 程度の差を許す）。
        Near(black, NumberBox(a), 2.0 / 1586);
    }

    [Fact]
    public async Task UprightThenResample_UsesRotatedDimensions()
    {
        var (upright, black) = Upright(2000, 1000);
        var page = QuarterTurn.Rotate(upright, 1);   // 縦長 1000×2000 で取り込まれた
        var ocr = new FakeOcr(CardText);

        var a = await PageAnalyzer.AnalyzeAsync(page, ocr, Presets, detectUpright: true, CancellationToken.None);

        Assert.Equal(3, a.QuarterTurns);
        Assert.Equal(5, ocr.Calls.Count);                          // 4 方向＋再 OCR
        Assert.Equal((1586, 1000), ocr.Calls[4]);                  // 回した後（横長 2000×1000）で判定・再サンプル
        Near(black, NumberBox(a), 2.0 / 1586);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        var (page, _) = Upright(400, 252);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PageAnalyzer.AnalyzeAsync(page, new FakeOcr(CardText), Presets, detectUpright: true, cts.Token));
    }

    [Fact]
    public void NeedsCardResample_Conditions()
    {
        var t = PipelineTuning.Default;
        Assert.True(PageAnalyzer.NeedsCardResample(DocumentType.MenkyoshoFront, 2000, 1000, t));
        Assert.True(PageAnalyzer.NeedsCardResample(DocumentType.ZairyuCardFront, 1200, 1000, t));
        Assert.False(PageAnalyzer.NeedsCardResample(DocumentType.ShikakuKakuninsho, 2000, 1000, t));
        Assert.False(PageAnalyzer.NeedsCardResample(DocumentType.Generic, 2000, 1000, t));
        Assert.False(PageAnalyzer.NeedsCardResample(DocumentType.MenkyoshoFront, 1000, 2000, t));
    }
}
