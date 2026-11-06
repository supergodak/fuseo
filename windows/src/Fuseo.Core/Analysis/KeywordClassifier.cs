using Fuseo.Core.Model;

namespace Fuseo.Core.Analysis;

/// <summary>種別判定の結果。Ranking は生スコア（「種別を変更」の候補・診断用）。採否は Type/Score だけに効く。</summary>
public sealed record ClassificationResult(
    DocumentType Type, int Score, IReadOnlyList<(DocumentType Type, int Score)> Ranking);

public static class DocumentTypeExtensions
{
    /// <summary>
    /// ID-1 規格（85.60×53.98mm）で寸法が確定しているカードか（Mac の isID1Card と同じ）。
    /// 保険証・資格確認書はカード様式と紙様式が混在するため含めない（安全側）。
    /// </summary>
    public static bool IsId1Card(this DocumentType type) => type is
        DocumentType.MenkyoshoFront or DocumentType.MenkyoshoBack or
        DocumentType.MyNumberCardFront or DocumentType.MyNumberCardBack or DocumentType.ZairyuCardFront;
}

/// <summary>
/// キーワード重み付けの種別判定（Mac 版 KeywordClassifier の写し・プリセットの classification.keywords 駆動）。
/// 照合は OCR 側・キーワード側とも <see cref="OcrText.ForKeywords"/> を通した文字列どうし。全スコア 0 なら generic。
/// **文字の多い画像のゲート**（Mac 2026-10-09 dogfood）: 観測数が <see cref="MaxCardObservations"/> を超えたら
/// カード類は候補にせず、それ以外も異なるキーワード 2 語以上の一致を要する。
/// </summary>
public static class KeywordClassifier
{
    /// <summary>カードとみなせる OCR 観測数の上限（実物カード 13〜32 観測に余裕を持たせた値・Mac と同じ）。</summary>
    public const int MaxCardObservations = 80;

    public static ClassificationResult Classify(IReadOnlyList<OcrItem> ocr, IReadOnlyList<DocumentPreset> presets)
    {
        var textHeavy = ocr.Count > MaxCardObservations;
        // OrderByDescending は安定（同点はプリセットの順のまま）。
        var scored = Score(ocr, presets).OrderByDescending(s => s.Score).ToList();
        var ranking = scored.Select(s => (s.Type, s.Score)).ToList();

        foreach (var c in scored)
        {
            if (c.Score <= 0) continue;
            if (textHeavy && (c.Type.IsId1Card() || c.Hits < 2)) continue;
            return new ClassificationResult(c.Type, c.Score, ranking);
        }
        return new ClassificationResult(DocumentType.Generic, 0, ranking);
    }

    /// <summary>プリセットごとの生スコアと一致語数（プリセットの順）。正立判定でも使う。</summary>
    internal static IReadOnlyList<(DocumentType Type, int Score, int Hits)> Score(
        IReadOnlyList<OcrItem> ocr, IReadOnlyList<DocumentPreset> presets)
    {
        var joined = OcrText.ForKeywords(string.Concat(ocr.Select(i => i.Text)));
        var result = new List<(DocumentType, int, int)>(presets.Count);
        foreach (var preset in presets)
        {
            var score = 0;
            var hits = 0;
            // 正規化後に同じになるキーワード（「在留カード」と半角カナの「在留ｶｰﾄﾞ」など）は 1 語として数え、重みは大きい方
            // （Mac では別の文字列なので片方しか一致しなかった。両方を足すと 1 語で 2 語一致扱いになり文字の多い画像のゲートを抜ける）。
            var keywords = preset.Classification.Keywords
                .GroupBy(kw => OcrText.ForKeywords(kw.Text), StringComparer.Ordinal)
                .Select(g => (Text: g.Key, Weight: g.Max(kw => kw.Weight)));
            foreach (var (text, weight) in keywords)
            {
                if (text.Length == 0 || !joined.Contains(text, StringComparison.Ordinal)) continue;
                score += weight;
                hits++;
            }
            result.Add((preset.DocumentType, score, hits));
        }
        return result;
    }
}
