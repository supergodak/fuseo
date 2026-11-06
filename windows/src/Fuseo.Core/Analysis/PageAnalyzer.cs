using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.Core.Analysis;

/// <summary>
/// 1 ページの解析結果（§10.13）。<see cref="Ocr"/> の座標は **<c>QuarterTurn.Rotate(page, QuarterTurns)</c> の画像の上**
/// （左上原点の正規化）。ID-1 の再 OCR をした場合も、正規化座標は水平スケールで変わらないので同じ画像の上で使える。
/// <see cref="Classification"/> は最初の OCR（選んだ向き）で決めた種別（再 OCR では取り直さない）。
/// </summary>
public sealed record PageAnalysis(int QuarterTurns, IReadOnlyList<OcrItem> Ocr, ClassificationResult Classification);

/// <summary>
/// ページ解析の流れ（純 C#・Windows 非依存・docs/windows-design.md §10.3／§10.13）:
/// 1. 正立判定（<c>detectUpright</c> のときだけ・写真用）: 時計回り 0〜3 回で OCR → <see cref="Upright.Score"/> →
///    <see cref="Upright.Choose"/>。選ばれた向きの OCR をそのまま使う（再 OCR しない）。false なら 0 で 1 回だけ。
/// 2. 種別判定（<see cref="KeywordClassifier"/>）。
/// 3. 種別が ID-1 カードで、画像が横長かつ長短比が CardAspect から許容（5%）を超えてずれていたら、
///    **高さそのまま幅だけ** CardAspect に合わせた複製（<see cref="Resample.ToAspect"/>）で再 OCR し、OCR をそれに置き換える。
///    画像そのものは置き換えない（Mac は画像ごと直すが、Windows は解析用の複製だけ）。再 OCR が 0 行なら 1 回目のまま。
/// OCR の失敗（例外）はそのまま呼び出し側へ伝える（黙って空にしない）。結果はディスクに保存しない。
/// </summary>
public static class PageAnalyzer
{
    public static async Task<PageAnalysis> AnalyzeAsync(
        PageBitmap page, ITextRecognizer ocr, IReadOnlyList<DocumentPreset> presets, bool detectUpright,
        CancellationToken ct, PipelineTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(presets);
        tuning ??= PipelineTuning.Default;
        ct.ThrowIfCancellationRequested();

        var turns = 0;
        IReadOnlyList<OcrItem> items;
        if (detectUpright)
        {
            // 4 方向を順に読む（同時に 4 枚の回転画像を持たない＝長辺 4000 の写真でもメモリを抑える）。
            var results = new IReadOnlyList<OcrItem>[4];
            var scores = new double[4];
            for (var t = 0; t < 4; t++)
            {
                ct.ThrowIfCancellationRequested();
                var image = t == 0 ? page : QuarterTurn.Rotate(page, t);
                results[t] = await ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
                scores[t] = Upright.Score(results[t], presets, tuning);
            }
            turns = Upright.Choose(scores, tuning);
            items = results[turns];
        }
        else
        {
            items = await ocr.RecognizeAsync(page, ct).ConfigureAwait(false);
        }

        var classification = KeywordClassifier.Classify(items, presets);

        // 選んだ向きでの寸法（90°/270° は縦横が入れ替わる）。
        var (w, h) = turns % 2 == 0 ? (page.Width, page.Height) : (page.Height, page.Width);
        if (NeedsCardResample(classification.Type, w, h, tuning))
        {
            ct.ThrowIfCancellationRequested();
            var oriented = turns == 0 ? page : QuarterTurn.Rotate(page, turns);
            var resampled = Resample.ToAspect(oriented, tuning.CardAspect);
            var again = await ocr.RecognizeAsync(resampled, ct).ConfigureAwait(false);
            // 再 OCR で何も読めなかったときは 1 回目を残す（種別を決めた文字が読めていたのに候補が全部消えるのを防ぐ・メイン決定）。
            if (again.Count > 0) items = again;
        }
        return new PageAnalysis(turns, items, classification);
    }

    /// <summary>
    /// ID-1 の再 OCR をする条件: 種別が ID-1 カード・横長（幅 &gt; 高さ）・長短比が CardAspect から許容を超えてずれている
    /// （<see cref="CardFrame.IsCardAspect"/> の否定＝境界ちょうどはずれていない扱い）。縦長の画像はしない（§10.13）。
    /// </summary>
    internal static bool NeedsCardResample(DocumentType type, int width, int height, PipelineTuning tuning) =>
        type.IsId1Card() && width > height && !CardFrame.IsCardAspect(width, height, tuning);
}
