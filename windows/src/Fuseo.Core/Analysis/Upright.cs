using System.Text;
using Fuseo.Core.Model;

namespace Fuseo.Core.Analysis;

/// <summary>
/// 写真の正立判定（Windows 固有・§10.3）。Windows OCR には Mac の upright（文字の四隅）が無いので、
/// 0/90/180/270° で OCR した結果を「有意文字数＋重み×最大キーワードスコア」で比べる。
/// 逆さ・横倒しは文字化けして短くなる（W9-0 実測で正しい向きが 1 位・2 位との比 2.28〜5.87）。
/// </summary>
public static class Upright
{
    /// <summary>
    /// 正立スコア = 有意文字数（ASCII 英数字と U+3000 より上の文字・空白と記号は除く）
    /// ＋ <see cref="PipelineTuning.UprightKeywordWeight"/> × 最大キーワードスコア（生スコア・ゲート無し）。
    /// </summary>
    public static double Score(IReadOnlyList<OcrItem> ocr, IReadOnlyList<DocumentPreset> presets, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        var chars = 0;
        foreach (var item in ocr)
        foreach (var r in item.Text.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(r)) continue;
            if (r.IsAscii || r.Value > 0x3000) chars++;
        }
        var kw = KeywordClassifier.Score(ocr, presets).Select(s => s.Score).DefaultIfEmpty(0).Max();
        return chars + tuning.UprightKeywordWeight * Math.Max(kw, 0);
    }

    /// <summary>
    /// 4 方向（添字 0..3 = 0/90/180/270°）のスコアから向きを選ぶ。1 位が 2 位の
    /// <see cref="PipelineTuning.UprightMinRatio"/> 倍以上のときだけその添字、僅差なら 0（回さない）。
    /// </summary>
    public static int Choose(IReadOnlyList<double> scores, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        if (scores.Count < 2) return 0;
        var best = 0;
        for (var i = 1; i < scores.Count; i++)
            if (scores[i] > scores[best]) best = i;
        if (best == 0) return 0;
        var second = double.MinValue;
        for (var i = 0; i < scores.Count; i++)
            if (i != best && scores[i] > second) second = scores[i];
        return scores[best] >= tuning.UprightMinRatio * second ? best : 0;
    }
}

/// <summary>「ページ＝カード 1 枚」の判定（固定領域・位置推定を候補にする条件の片方・§10.3）。</summary>
public static class CardFrame
{
    /// <summary>長辺/短辺 が <see cref="PipelineTuning.CardAspect"/> から相対 ±<see cref="PipelineTuning.CardAspectTolerance"/> 以内か。</summary>
    public static bool IsCardAspect(int width, int height, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        if (width <= 0 || height <= 0) return false;
        var ratio = (double)Math.Max(width, height) / Math.Min(width, height);
        return Math.Abs(ratio - tuning.CardAspect) <= tuning.CardAspect * tuning.CardAspectTolerance;
    }
}
