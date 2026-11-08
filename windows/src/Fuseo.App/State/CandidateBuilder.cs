using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>
/// 1 ページの自動検出の材料（W9-3・docs/windows-design.md §10.15）。**不変**。
/// - 「切り抜き後の画像（<see cref="PageState.Cropped"/>）の回転 0」を R0 と呼ぶ。
/// - <see cref="Ocr"/> の座標は <c>QuarterTurn.Rotate(Cropped, OcrTurns)</c> の画像の上（OCR は向きを合わせた画像で読むため）。
///   null = 読んでいない・文字認識が使えない・失敗。**ディスクに保存しない**（書類の文字列を残さない）。
/// - <see cref="Qr"/>・<see cref="Faces"/> は R0 の上の生の箱（padding 前）。
/// </summary>
public sealed record PageFindings(int OcrTurns, IReadOnlyList<OcrItem>? Ocr, IReadOnlyList<NormRect> Qr, IReadOnlyList<NormRect> Faces)
{
    public static readonly PageFindings Empty = new(0, null, [], []);

    /// <summary>QR の箱だけ（文字を読んでいない・顔なし）。</summary>
    public static PageFindings FromQr(IReadOnlyList<NormRect> qr) => new(0, null, qr.ToList(), []);
}

/// <summary>
/// 検出の材料 → 候補（§10.15。v0.1 の CandidateFactory を置き換え）。
/// QR・顔を R0 から OCR の枠へ回し（+OcrTurns）、<see cref="RuleEngine.Candidates"/> で候補を作り、
/// 各候補の箱を <c>rotation − OcrTurns</c> だけ回して「いまの向きの画像」の上に置く。
/// IsOn はルールの defaultOn（最終判断は利用者・書き出しシートで確認）。
/// </summary>
public static class CandidateBuilder
{
    public static IReadOnlyList<MaskCandidate> Build(
        DocumentPreset? preset, PageFindings findings, int rotation, bool cardFramed, bool english = false)
    {
        if (preset is null) return [];
        // 文字を読んでいない（Ocr = null）ときは、固定領域を「いま見えている向き」に当てる
        // （OcrTurns = 0 のまま当てると、利用者が回して正立させたカードで欄の位置が横倒しになる）。
        var frame = findings.Ocr is null ? QuarterTurn.Normalize(rotation) : findings.OcrTurns;
        var image = new Dictionary<DetectorId, IReadOnlyList<NormRect>>
        {
            [DetectorId.QrBarcode] = findings.Qr.Select(b => QuarterTurn.Rotate(b, frame)).ToList(),
            [DetectorId.Face] = findings.Faces.Select(b => QuarterTurn.Rotate(b, frame)).ToList(),
        };
        var candidates = RuleEngine.Candidates(preset, findings.Ocr ?? [], image, cardFramed, english);
        var delta = rotation - frame;
        return candidates.Select(c => QuarterTurn.Rotate(c, delta)).ToList();
    }

    /// <summary>
    /// 候補の表示文言を、その時の表示言語でプリセットのルールから引き直す（保存した作業を別の言語で開いたときも揃うように）。
    /// 検出で付いた注記（「（12********34）」「（位置推定）」「（一部読めず）」）は残す。生の番号は Core が伏せた形しか持たない。
    /// 該当ルールが無い・形が分からないときは候補が持つ文言のまま。保存する値（MaskCandidate）は変えない。
    /// </summary>
    public static (string Label, string Basis) DisplayText(DocumentPreset? preset, MaskCandidate c, bool english)
    {
        if (preset?.Rules.FirstOrDefault(r => r.Id == c.RuleId) is not { } rule) return (c.Label, c.Basis);
        var basis = rule.LocalizedBasis(english);
        var label = rule.LocalizedLabel(english);
        if (c.Label == rule.Label || c.Label == rule.LocalizedLabel(true)) return (label, basis);
        if (Note(c.Label, rule) is not { } note) return (c.Label, basis);
        note = TranslateNote(note, english);
        return (english ? $"{label} ({note})" : $"{label}（{note}）", basis);
    }

    private static readonly (string Ja, string En)[] Notes =
    [
        ("位置推定", "position estimated"),
        ("一部読めず", "partly unreadable"),
    ];

    /// <summary>「label（note）」／「labelEn (note)」の note を取り出す（どちらの言語で保存されていても）。</summary>
    private static string? Note(string text, MaskRule rule)
    {
        foreach (var (prefix, open, close) in new[] { (rule.Label, "（", "）"), (rule.LocalizedLabel(true), " (", ")") })
        {
            if (text.Length > prefix.Length + open.Length + close.Length
                && text.StartsWith(prefix + open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal))
                return text[(prefix.Length + open.Length)..^close.Length];
        }
        return null;
    }

    private static string TranslateNote(string note, bool english)
    {
        foreach (var (ja, en) in Notes)
            if (note == ja || note == en) return english ? en : ja;
        return note; // 伏せた番号（言語に依らない）
    }
}
