using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>チェックリスト 1 行の種類（docs/windows-design.md §3.5）。</summary>
public enum ChecklistRowKind
{
    /// <summary>fixed ルール: チェックボックス付き（v0.1 は固定領域を候補にしないので、利用者が塗ったら印を付ける）。</summary>
    Check,
    /// <summary>dynamic で detector が qrBarcode 以外: 「手動で塗ってください」の案内行（チェックボックス無し）。</summary>
    ManualGuide,
    /// <summary>dynamic・qrBarcode: 検出結果を候補として出す行（W4 で候補が入る。今は枠だけ）。</summary>
    QrCandidate,
}

public sealed record ChecklistRow(string RuleId, string Label, string Basis, ChecklistRowKind Kind);

public static class Checklist
{
    public static ChecklistRowKind Classify(MaskRule rule) => rule.Kind switch
    {
        RuleKind.Dynamic when rule.Detector == DetectorId.QrBarcode => ChecklistRowKind.QrCandidate,
        RuleKind.Dynamic => ChecklistRowKind.ManualGuide,
        _ => ChecklistRowKind.Check,
    };

    /// <summary>プリセットの rules を JSON の順のまま行にする（label＋basis。英語は W6）。</summary>
    public static IReadOnlyList<ChecklistRow> Build(DocumentPreset preset, bool english = false) =>
        preset.Rules
            .Select(r => new ChecklistRow(r.Id, r.LocalizedLabel(english), r.LocalizedBasis(english), Classify(r)))
            .ToList();
}
