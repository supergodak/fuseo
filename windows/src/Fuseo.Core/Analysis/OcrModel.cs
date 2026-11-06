using Fuseo.Core.Geometry;

namespace Fuseo.Core.Analysis;

/// <summary>OCR の 1 単語（左上原点・正規化）。Text は認識したままの文字列（正規化しない）。</summary>
public sealed record OcrWord(string Text, NormRect Box);

/// <summary>
/// OCR の 1 行 = 1 観測（Mac の VNRecognizedTextObservation に合わせる・docs/windows-design.md §10.2）。
/// <see cref="Text"/> は <see cref="OcrText.JoinWords"/> で作った**生の文字列**（NFKC を掛けない）。
/// Box は単語の外接矩形の和。<see cref="Words"/> は免許証番号の位置の手がかり（第…号）に使う。
/// </summary>
public sealed record OcrItem(string Text, NormRect Box, IReadOnlyList<OcrWord> Words)
{
    /// <summary>単語から行を作る（Text は <see cref="OcrText.JoinWords"/>・Box は外接矩形の和）。単語 0 個は不可。</summary>
    public static OcrItem FromWords(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0) throw new ArgumentException("単語が 0 個", nameof(words));
        var box = words[0].Box;
        for (var i = 1; i < words.Count; i++) box = BoxMath.Union(box, words[i].Box);
        return new OcrItem(OcrText.JoinWords(words.Select(w => w.Text)), box, words);
    }
}

/// <summary>正規化矩形の和（NormRect の公開 API を増やさないため Analysis 内に置く）。</summary>
internal static class BoxMath
{
    public static NormRect Union(NormRect a, NormRect b)
    {
        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        var x1 = Math.Max(a.Right, b.Right);
        var y1 = Math.Max(a.Bottom, b.Bottom);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }
}
