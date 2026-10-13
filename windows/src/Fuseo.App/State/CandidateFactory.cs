using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>
/// 自動検出の生の箱 → 候補（W4）。Mac と同じく、**その種別のプリセットに該当検出器の dynamic ルールがあるときだけ**候補を出す。
/// - 生の箱は padding 前・回転 0（元画像基準）。現在の回転まで <see cref="QuarterTurn"/> で回してから padding を掛ける。
/// - IsOn はルールの defaultOn（最終判断は利用者・書き出しシートで確認）。
/// - label・basis はチェックリストと同じ言語選択（<see cref="MaskRule.LocalizedLabel"/>／<see cref="MaskRule.LocalizedBasis"/>）。
/// </summary>
public static class CandidateFactory
{
    public static IReadOnlyList<MaskCandidate> Build(
        DocumentPreset? preset, DetectorId detector, IReadOnlyList<NormRect> rawBoxes, int rotation, bool english = false)
    {
        if (preset is null || rawBoxes.Count == 0) return [];
        var rule = preset.Rules.FirstOrDefault(r => r.Kind == RuleKind.Dynamic && r.Detector == detector);
        if (rule is null) return [];
        var label = rule.LocalizedLabel(english);
        var basis = rule.LocalizedBasis(english);
        return rawBoxes
            .Select(box => new MaskCandidate(
                Guid.NewGuid(), rule.Id, label,
                QuarterTurn.Rotate(box, rotation).Padded(rule.Padding ?? 0.01),
                CandidateSource.Detector, detector, null, IsOn: rule.DefaultOn, basis))
            .ToList();
    }
}
