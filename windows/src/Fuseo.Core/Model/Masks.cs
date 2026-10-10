using Fuseo.Core.Geometry;

namespace Fuseo.Core.Model;

/// <summary>ブラシの一筆。点列は正規化座標、Width は画像短辺に対する比。</summary>
public sealed record BrushStroke(IReadOnlyList<NormPoint> Points, double Width);

/// <summary>手動マスク（矩形とブラシ）。</summary>
public sealed record ManualMask
{
    public List<NormRect> Rects { get; init; } = [];
    public List<BrushStroke> Strokes { get; init; } = [];
    public bool IsEmpty => Rects.Count == 0 && Strokes.Count == 0;
}

public enum CandidateSource { FixedRegion, Detector }

/// <summary>
/// 自動検出の候補。Box は padding 適用済み。**IsOn の最終決定はユーザー**（確認 UI 必須・絶対条件）。
/// </summary>
public sealed record MaskCandidate(
    Guid Id, string RuleId, string Label, NormRect Box,
    CandidateSource Source, DetectorId? Detector, float? Confidence, bool IsOn, string Basis);
