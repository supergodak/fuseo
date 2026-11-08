using System.Globalization;
using Fuseo.App.Properties;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>チェックリスト 1 行の種類（docs/windows-design.md §3.5・§10.19）。</summary>
public enum ChecklistRowKind
{
    /// <summary>fixed ルール: チェックボックス付き。切り抜いた（カード 1 枚の）ページでは位置で候補になる。</summary>
    Check,
    /// <summary>dynamic・画像で探す検出子（QR・顔）: 「自動で探します」。</summary>
    ImageDetector,
    /// <summary>dynamic・文字認識で探す検出子（番号系）で fallbackRegion なし: 文字認識の有無で文言が変わる。</summary>
    TextDetector,
    /// <summary>dynamic で fallbackRegion を持つ欄: 固定領域と同じく「位置で候補」の文言（切り抜きの有無で変わる）。</summary>
    PositionFallback,
}

/// <summary>チェックリストの 1 行。<see cref="Note"/> は行の下に出す案内（無ければ null）。</summary>
public sealed record ChecklistRow(string RuleId, string Label, string Basis, ChecklistRowKind Kind, string? Note = null);

public static class Checklist
{
    public static ChecklistRowKind Classify(MaskRule rule) => rule.Kind switch
    {
        RuleKind.Dynamic when rule.Detector is DetectorId.QrBarcode or DetectorId.Face => ChecklistRowKind.ImageDetector,
        RuleKind.Dynamic when rule.FallbackRegion is not null => ChecklistRowKind.PositionFallback,
        RuleKind.Dynamic => ChecklistRowKind.TextDetector,
        _ => ChecklistRowKind.Check,
    };

    /// <summary>
    /// 行の案内文（§10.19-1）。QR・顔＝自動で探す／番号系＝文字認識があれば「自動で探します（見つからなければ手で）」・無ければ「手動で」／
    /// 固定領域と fallbackRegion の欄＝カード 1 枚なら「位置で候補にしています」・でなければ「切り抜くと位置で候補になります」。
    /// </summary>
    public static string NoteFor(ChecklistRowKind kind, bool ocrAvailable, bool cardFramed) =>
        NoteFor(kind, ocrAvailable, cardFramed, Strings.Culture);

    public static string NoteFor(ChecklistRowKind kind, bool ocrAvailable, bool cardFramed, CultureInfo? culture) => Strings.Get(kind switch
    {
        ChecklistRowKind.ImageDetector => nameof(Strings.ChecklistAutoDetect),
        ChecklistRowKind.TextDetector => ocrAvailable ? nameof(Strings.ChecklistTextDetect) : nameof(Strings.ChecklistManualGuide),
        // 位置推定を持つ番号の欄（マイナ裏の個人番号・在留カード番号）は、文字認識があれば先に文字で探し、見つからなければ位置で候補にする。
        ChecklistRowKind.PositionFallback when ocrAvailable =>
            cardFramed ? nameof(Strings.ChecklistTextThenPosition) : nameof(Strings.ChecklistTextThenCrop),
        _ => cardFramed ? nameof(Strings.ChecklistPositioned) : nameof(Strings.ChecklistCropForPosition),
    }, culture);

    /// <summary>プリセットの rules を JSON の順のまま行にする（label＋basis＋案内文）。</summary>
    public static IReadOnlyList<ChecklistRow> Build(DocumentPreset preset, bool english = false, bool ocrAvailable = false, bool cardFramed = false) =>
        preset.Rules
            .Select(r =>
            {
                var kind = Classify(r);
                return new ChecklistRow(r.Id, r.LocalizedLabel(english), r.LocalizedBasis(english), kind, NoteFor(kind, ocrAvailable, cardFramed));
            })
            .ToList();
}
