using System.Text;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Analysis;

/// <summary>
/// OCR から見つけた欄。Box は padding 前・左上原点。MaskedDescription は生の番号を出さない部分表示（無ければ null）。
/// Partial = 番号の一部が読めず位置だけで決めた候補（免許証の「第…号」・ラベルは「（一部読めず）」）。
/// </summary>
public sealed record DetectedField(DetectorId Detector, NormRect Box, string? MaskedDescription, bool Partial = false);

/// <summary>
/// OCR ベースの欄検出（Mac 版 VisionFieldDetector の写し・QR と顔は画像ベースなので対象外＝空を返す）。
///
/// 番号系の規約（Mac・core-design.md §2.3 と同じ）:
/// - **窓スライド禁止**。候補は「必要桁数ちょうど」の 2 形のみ:
///   A) 1 観測内の極大数字連続（空白のみ除去後）
///   B) 同一行の数字のみ観測の連結（x 昇順＋逆順の保険。逆順はチェックデジットが誤採用を防ぐ）
/// - チェックデジット（マイナンバー / 免許証 / Luhn）で確定。seen で重複排除・正順優先。
///
/// Windows 固有（§10.12）: 数字の処理は <see cref="OcrText.ForDigits"/> 後の文字列で行う。
/// 番号系は <see cref="OcrText.FixDigitConfusables"/> 後の候補列も足す（元の列が先）。
/// 在留カードは中央 8 文字だけ読み違いを直す。免許証は 12 桁が成立しないとき「第…号」の位置で部分候補を出す。
/// </summary>
public static class FieldDetector
{
    public static IReadOnlyList<DetectedField> Detect(DetectorId id, IReadOnlyList<OcrItem> ocr, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        switch (id)
        {
            case DetectorId.MyNumber12:
                // 「ちょうど 12 桁」は規約なので設定化しない。
                return NumberFields(ocr, [12], Checkdigits.IsValidMyNumber, id, tuning);
            case DetectorId.LicenseNumber12:
            {
                var fields = NumberFields(ocr, [12], Checkdigits.IsValidLicenseNumber, id, tuning);
                return fields.Count > 0 ? fields : LicenseNumberByPosition(ocr);
            }
            case DetectorId.CreditCardLuhn:
                return NumberFields(ocr, tuning.CreditCardLengths, Checkdigits.IsValidLuhn, id, tuning);
            case DetectorId.InsurerNumber:
                // チェックデジットが無いため近傍キーワード必須（保険証実物は PoC 未測定・暫定・Mac と同じ）。
                return KeywordProximityDigits(ocr, tuning.InsurerNumberLengths, ["保険者番号"], [], id, tuning);
            case DetectorId.KigoBango:
                return KeywordProximityDigits(ocr, tuning.KigoBangoLengths, ["記号", "番号"], ["保険者番号", "個人番号"], id, tuning);
            case DetectorId.ZairyuNumber:
                return ZairyuNumberFields(ocr);
            default:
                return [];   // Face / QrBarcode は画像ベース（RuleEngine が imageDetections から取る）
        }
    }

    // ---- 番号系 ----

    internal sealed record DigitSequence(string Digits, NormRect Box, bool IsReversedVariant);

    internal static IReadOnlyList<DetectedField> NumberFields(
        IReadOnlyList<OcrItem> ocr, IReadOnlyList<int> lengths, Func<string, bool> validate,
        DetectorId detector, PipelineTuning tuning)
    {
        // 元の列 → 読み違いを直した列の順に並べ、正順を先に（安定ソート）。逆順の保険・補正版は seen で排他。
        var sequences = DigitSequences(ocr, tuning)
            .Concat(DigitSequences(ocr, tuning, fixConfusables: true))
            .OrderBy(s => s.IsReversedVariant);
        var fields = new List<DetectedField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seq in sequences)
        {
            if (!lengths.Contains(seq.Digits.Length) || seen.Contains(seq.Digits) || !validate(seq.Digits)) continue;
            seen.Add(seq.Digits);
            fields.Add(new DetectedField(detector, seq.Box, Masked(seq.Digits)));
        }
        return fields;
    }

    /// <summary>
    /// 候補となる数字列（A: 観測内の極大連続 / B: 行内の数字のみ観測の連結＋逆順）。
    /// 文字列は ForDigits 後、<paramref name="fixConfusables"/> なら FixDigitConfusables も通す。
    /// </summary>
    internal static IReadOnlyList<DigitSequence> DigitSequences(
        IReadOnlyList<OcrItem> ocr, PipelineTuning? tuning = null, bool fixConfusables = false)
    {
        tuning ??= PipelineTuning.Default;
        string TextOf(OcrItem item)
        {
            var t = OcrText.ForDigits(item.Text);
            return (fixConfusables ? OcrText.FixDigitConfusables(t) : t).Replace(" ", "", StringComparison.Ordinal);
        }

        var result = new List<DigitSequence>();

        // A) 観測内: 空白のみ除去 → 極大数字連続（ハイフン等は区切りとして残す＝電話番号を分断する）
        foreach (var item in ocr)
            foreach (var run in DigitRuns(TextOf(item)))
                if (run.Length >= tuning.MinDigitRunLength)
                    result.Add(new DigitSequence(run, item.Box, false));

        // B) 行内の数字のみ観測（「1234」「5678」「9012」のような分割）を x 昇順で連結＋逆順の保険
        foreach (var line in GroupIntoLines(ocr, tuning))
        {
            var digitObs = line
                .Where(i => i.Text.Length > 0 && TextOf(i).All(OcrText.IsAsciiDigit))
                .OrderBy(i => i.Box.X)
                .ToList();
            if (digitObs.Count < 2) continue;
            var parts = digitObs.Select(TextOf).ToList();
            var box = digitObs.Skip(1).Aggregate(digitObs[0].Box, (acc, i) => BoxMath.Union(acc, i.Box));
            result.Add(new DigitSequence(string.Concat(parts), box, false));
            parts.Reverse();
            result.Add(new DigitSequence(string.Concat(parts), box, true));
        }
        return result;
    }

    /// <summary>極大の ASCII 数字連続をすべて返す（長さの絞り込みは呼び出し側）。</summary>
    private static IEnumerable<string> DigitRuns(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isDigit = i < text.Length && OcrText.IsAsciiDigit(text[i]);
            if (isDigit && start < 0) start = i;
            else if (!isDigit && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }

    /// <summary>
    /// 行グルーピング: 中心 Y 差が許容（既定 ±2%）未満、**または縦区間の重なりが低い方の高さ×35% 以上**なら同一行。
    /// 比較は「行に最後に追加した観測」＝斜めの行を連鎖的に追える。左上原点なので中心 Y の昇順（上の行から）。
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<OcrItem>> GroupIntoLines(IReadOnlyList<OcrItem> items, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        var lines = new List<List<OcrItem>>();
        foreach (var item in items.OrderBy(i => i.Box.Y + i.Box.H / 2))
        {
            if (lines.Count > 0 && IsSameLine(lines[^1][^1], item, tuning)) lines[^1].Add(item);
            else lines.Add([item]);
        }
        return lines;
    }

    /// <summary>35% = 行間の詰まった隣接行（わずかに触れる）を弾きつつ ±3〜4° の傾きは許す値（Mac と同じ）。</summary>
    private static bool IsSameLine(OcrItem a, OcrItem b, PipelineTuning tuning)
    {
        var ca = a.Box.Y + a.Box.H / 2;
        var cb = b.Box.Y + b.Box.H / 2;
        if (Math.Abs(ca - cb) < tuning.LineGroupingCenterYTolerance) return true;
        var overlap = Math.Min(a.Box.Bottom, b.Box.Bottom) - Math.Max(a.Box.Y, b.Box.Y);
        return overlap >= 0.35 * Math.Min(a.Box.H, b.Box.H);
    }

    // ---- 免許証番号の位置の手がかり（Windows 固有・§10.10） ----

    /// <summary>
    /// 12 桁＋チェックが 1 件も成立しなかったときの保険。「第」を含む単語の後ろから「号」を含む単語の手前までを集め、
    /// ForDigits 後の文字数が 10〜14 かつ ASCII 数字が 9 文字以上なら、それらの単語の外接矩形を Partial で返す。
    /// チェックデジットで 1 文字を埋める案は不採用（未知の 1 桁は必ず解が見つかる＝検証にならない）。
    /// 「号」が見つからない・間の単語が無いときは出さない（安全側）。
    /// </summary>
    internal static IReadOnlyList<DetectedField> LicenseNumberByPosition(IReadOnlyList<OcrItem> ocr)
    {
        var fields = new List<DetectedField>();
        foreach (var item in ocr)
        {
            var words = item.Words;
            for (var i = 0; i < words.Count; i++)
            {
                if (!words[i].Text.Contains('第')) continue;
                var end = -1;
                for (var j = i + 1; j < words.Count; j++)
                    if (words[j].Text.Contains('号')) { end = j; break; }
                if (end < 0) break;   // 以降の「第」にも「号」は無い
                if (end > i + 1)
                {
                    var between = words.Skip(i + 1).Take(end - i - 1).ToList();
                    var text = OcrText.ForDigits(string.Concat(between.Select(w => w.Text)));
                    var digits = text.Count(OcrText.IsAsciiDigit);
                    if (text.Length is >= 10 and <= 14 && digits >= 9)
                    {
                        var box = between.Skip(1).Aggregate(between[0].Box, (acc, w) => BoxMath.Union(acc, w.Box));
                        fields.Add(new DetectedField(DetectorId.LicenseNumber12, box, null, Partial: true));
                    }
                }
                i = end;   // 「号」の後ろから次の「第」を探す
            }
        }
        return fields;
    }

    // ---- 在留カード番号（英 2＋数字 8＋英 2・厳格一致） ----

    /// <summary>
    /// 英数字の極大トークンが「ちょうど 12 文字・英 2＋数 8＋英 2」のときだけ候補にする（チェックデジットの公開規定が無い）。
    /// Windows 固有: 中央 8 文字だけ O/o/I/l/| を数字に直して判定する（英字の部分は直さない）。
    /// トークンの区切りは Mac と同じ（ASCII 英数字以外で切る）ので、「|」が中央に入ったトークンは成立しない。
    /// </summary>
    internal static IReadOnlyList<DetectedField> ZairyuNumberFields(IReadOnlyList<OcrItem> ocr)
    {
        var fields = new List<DetectedField>();
        foreach (var item in ocr)
        {
            var text = OcrText.ForDigits(item.Text).Replace(" ", "", StringComparison.Ordinal);
            var token = new StringBuilder();
            void Flush()
            {
                var t = token.ToString();
                token.Clear();
                if (t.Length != 12) return;
                var head = t[..2];
                var body = OcrText.FixConfusables(t[2..10]);
                var tail = t[10..];
                if (!head.All(char.IsAsciiLetter) || !body.All(OcrText.IsAsciiDigit) || !tail.All(char.IsAsciiLetter)) return;
                fields.Add(new DetectedField(DetectorId.ZairyuNumber, item.Box,
                    $"{head.ToUpperInvariant()}********{tail.ToUpperInvariant()}"));
            }
            foreach (var ch in text)
            {
                if (char.IsAsciiLetterOrDigit(ch)) token.Append(ch);
                else Flush();
            }
            Flush();
        }
        return fields;
    }

    // ---- キーワード近傍の数字列（保険証系・チェックデジット無し・読み違いの補正は使わない） ----

    internal static IReadOnlyList<DetectedField> KeywordProximityDigits(
        IReadOnlyList<OcrItem> ocr, IReadOnlyList<int> lengths, IReadOnlyList<string> keywords,
        IReadOnlyList<string> excludeKeywords, DetectorId detector, PipelineTuning? tuning = null)
    {
        tuning ??= PipelineTuning.Default;
        var fields = new List<DetectedField>();
        foreach (var line in GroupIntoLines(ocr, tuning))
        {
            var lineText = OcrText.ForKeywords(string.Concat(line.Select(i => i.Text)));
            bool Has(string k) => lineText.Contains(OcrText.ForKeywords(k), StringComparison.Ordinal);
            if (!keywords.Any(Has) || excludeKeywords.Any(Has)) continue;
            // 行内の数字連続（観測単位）をマスク対象にする
            foreach (var item in line)
            {
                var stripped = OcrText.ForDigits(item.Text).Replace(" ", "", StringComparison.Ordinal);
                foreach (var run in DigitRuns(stripped))
                    if (lengths.Contains(run.Length))
                        fields.Add(new DetectedField(detector, item.Box, Masked(run)));
            }
        }
        return fields;
    }

    /// <summary>部分マスク表示（生の番号をログ・UI へ出さない）。4 文字以下は「****」、それ以外は先頭 2＋*＋末尾 2。</summary>
    internal static string Masked(string digits) =>
        digits.Length <= 4 ? "****" : digits[..2] + new string('*', digits.Length - 4) + digits[^2..];
}
