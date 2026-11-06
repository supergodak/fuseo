using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Analysis;

/// <summary>
/// プリセットのルールを 1 ページに当てて候補（padding 適用済み）を作る（Mac 版 RuleEngine の写し＋Windows の条件）。
/// IsOn の初期値は rule.defaultOn。**最終決定は確認 UI の利用者**（絶対条件）。
///
/// Windows 固有（§10.3／§10.12）: 固定領域（region）と位置推定（fallbackRegion）は
/// <c>cardFramed</c>（ページ＝カード 1 枚: 4 隅切り抜き済み、または縦横比がカード）のときだけ候補にする。
/// 机の上で撮っただけの写真では位置が合わないため。
/// </summary>
public static class RuleEngine
{
    /// <param name="ocr">このページ（現在の向き）の OCR 結果。</param>
    /// <param name="imageDetections">画像ベースの検出（QR・顔）の生の箱（padding 前・ページと同じ向きの左上原点）。無い検出子は空扱い。</param>
    /// <param name="cardFramed">ページ＝カード 1 枚か（固定領域・位置推定を出す条件）。</param>
    /// <param name="english">ラベル・根拠を英語にするか（英語が無いルールは日本語のまま）。</param>
    public static IReadOnlyList<MaskCandidate> Candidates(
        DocumentPreset preset, IReadOnlyList<OcrItem> ocr,
        IReadOnlyDictionary<DetectorId, IReadOnlyList<NormRect>> imageDetections,
        bool cardFramed, bool english, PipelineTuning? tuning = null)
    {
        var t = tuning ?? PipelineTuning.Default;
        return Candidates(preset, cardFramed, english, id => id switch
        {
            DetectorId.QrBarcode or DetectorId.Face =>
                imageDetections.TryGetValue(id, out var boxes)
                    ? boxes.Select(b => new DetectedField(id, b, null)).ToList()
                    : [],
            _ => FieldDetector.Detect(id, ocr, t),
        });
    }

    /// <summary>検出子を差し替えられる本体（テストでフェイクの検出結果を渡す・Mac の FakeDetector 相当）。</summary>
    internal static IReadOnlyList<MaskCandidate> Candidates(
        DocumentPreset preset, bool cardFramed, bool english, Func<DetectorId, IReadOnlyList<DetectedField>> detect)
    {
        var output = new List<MaskCandidate>();
        foreach (var rule in preset.Rules)
        {
            var label = rule.LocalizedLabel(english);
            var basis = rule.LocalizedBasis(english);
            switch (rule.Kind)
            {
                case RuleKind.Fixed:
                    if (!cardFramed || rule.Region is null) continue;
                    output.Add(new MaskCandidate(
                        Guid.NewGuid(), rule.Id, label, rule.Region.ToNormRect().Padded(rule.EffectivePadding),
                        CandidateSource.FixedRegion, null, null, rule.DefaultOn, basis));
                    break;

                case RuleKind.Dynamic:
                    if (rule.Detector is not { } detectorId) continue;
                    var fields = detect(detectorId);
                    foreach (var field in fields)
                    {
                        var note = field.Partial
                            ? (english ? "partly unreadable" : "一部読めず")
                            : field.MaskedDescription;
                        output.Add(new MaskCandidate(
                            Guid.NewGuid(), rule.Id, Decorate(label, note, english),
                            field.Box.Padded(rule.EffectivePadding),
                            CandidateSource.Detector, detectorId, null, rule.DefaultOn, basis));
                    }
                    // 検出 0 件のときの位置推定（光の反射等で OCR が読めない写真の保険）。カード 1 枚のページだけ。
                    if (fields.Count == 0 && cardFramed && rule.FallbackRegion is { } fallback)
                    {
                        output.Add(new MaskCandidate(
                            Guid.NewGuid(), rule.Id,
                            Decorate(label, english ? "position estimated" : "位置推定", english),
                            fallback.ToNormRect().Padded(rule.EffectivePadding),
                            CandidateSource.FixedRegion, null, null, rule.DefaultOn, basis));
                    }
                    break;
            }
        }
        return output;
    }

    /// <summary>「label（note）」／英語「label (note)」（Mac と同じ書式）。note が無ければ label のまま。</summary>
    private static string Decorate(string label, string? note, bool english) =>
        note is null ? label : english ? $"{label} ({note})" : $"{label}（{note}）";
}
