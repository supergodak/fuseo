using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Xunit;
using static Fuseo.Core.Tests.Analysis.AnalysisFixtures;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>
/// Mac PipelineUnitTests（OCR フィクスチャ型）の移植＋Windows 固有の規則（§10.12）。
/// Mac のフィクスチャは左下原点なので中心 Y を 1 − midY に読み替えてある（例: Mac 0.40 → 0.60）。
/// </summary>
public class FieldDetectorTests
{
    // ---- Mac 移植: 行内連結（B 経路） ----

    [Fact]
    public void DigitSequences_ConcatenatesSameLine12Digits()
    {
        var ocr = new[] { Item("1234", 0.10, 0.50), Item("5678", 0.20, 0.50), Item("9012", 0.30, 0.50) };
        var seqs = FieldDetector.DigitSequences(ocr);
        Assert.Contains(seqs, s => s.Digits == "123456789012" && !s.IsReversedVariant);
        Assert.Contains(seqs, s => s.Digits == "901256781234" && s.IsReversedVariant);   // 逆順の保険
    }

    [Fact]
    public void DigitSequences_DoesNotConcatenateDifferentLines()
    {
        // Mac 0.50 / 0.40 / 0.30 → 左上原点 0.50 / 0.60 / 0.70
        var ocr = new[] { Item("1234", 0.10, 0.50), Item("5678", 0.10, 0.60), Item("9012", 0.10, 0.70) };
        var seqs = FieldDetector.DigitSequences(ocr);
        Assert.DoesNotContain(seqs, s => s.Digits.Length == 12);
        Assert.Empty(seqs);   // A 経路も 4 桁 < 最小 6 桁
    }

    [Fact]
    public void DigitSequences_ConcatenatesTiltedLine()
    {
        // 傾き残りで中心 Y が 2% 超ずつずれる 3 群（Mac 0.770 / 0.815 / 0.860 → 0.230 / 0.185 / 0.140）。縦区間は重なる。
        var ocr = new[]
        {
            Item("1234", 0.30, 0.230, h: 0.12), Item("5678", 0.50, 0.185, h: 0.12), Item("9012", 0.70, 0.140, h: 0.12),
        };
        Assert.Contains(FieldDetector.DigitSequences(ocr), s => s.Digits == "123456789012" && !s.IsReversedVariant);
    }

    [Fact]
    public void GroupIntoLines_AdjacentRowsWithTinyOverlapStaySeparate()
    {
        // Mac 0.500 / 0.545（h 0.04・重なりゼロ）→ 0.500 / 0.455
        var ocr = new[] { Item("1111", 0.10, 0.500, h: 0.04), Item("2222", 0.10, 0.455, h: 0.04) };
        Assert.Equal(2, FieldDetector.GroupIntoLines(ocr).Count);
    }

    [Fact]
    public void GroupIntoLines_SplitsByCenterYTolerance_TopLineFirst()
    {
        // Mac A 0.500 / B 0.515 / C 0.400 → 0.500 / 0.485 / 0.600。左上原点なので上（Y 小）の行が先。
        var ocr = new[] { Item("A", 0.10, 0.500), Item("B", 0.20, 0.485), Item("C", 0.10, 0.600) };
        var lines = FieldDetector.GroupIntoLines(ocr);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { "B", "A" }, lines[0].Select(i => i.Text));
        Assert.Equal(new[] { "C" }, lines[1].Select(i => i.Text));
    }

    [Fact]
    public void PhoneNumber_DoesNotProduce12DigitCandidate()
    {
        var ocr = new[] { Item("0570-783-578(24時間受付)", 0.10, 0.50, w: 0.5) };
        Assert.DoesNotContain(FieldDetector.DigitSequences(ocr), s => s.Digits.Length == 12);
        Assert.Empty(FieldDetector.Detect(DetectorId.MyNumber12, ocr));
    }

    // ---- Mac 移植: キーワード近傍（保険証系） ----

    [Fact]
    public void KeywordProximity_InsurerNumberPicksEightDigits()
    {
        var ocr = new[] { Item("保険者番号", 0.10, 0.40, w: 0.2), Item("01234567", 0.35, 0.40, w: 0.2) };
        var fields = FieldDetector.Detect(DetectorId.InsurerNumber, ocr);
        var f = Assert.Single(fields);
        Assert.Equal(DetectorId.InsurerNumber, f.Detector);
        Assert.Equal(ocr[1].Box, f.Box);
        Assert.Equal("01****67", f.MaskedDescription);
    }

    [Fact]
    public void KeywordProximity_KigoBangoExcludesInsurerAndKojin()
    {
        var kojin = new[] { Item("個人番号", 0.10, 0.40, w: 0.2), Item("12345678", 0.35, 0.40, w: 0.2) };
        Assert.Empty(FieldDetector.Detect(DetectorId.KigoBango, kojin));
        var hokensha = new[] { Item("保険者番号", 0.10, 0.40, w: 0.2), Item("01234567", 0.35, 0.40, w: 0.2) };
        Assert.Empty(FieldDetector.Detect(DetectorId.KigoBango, hokensha));
    }

    [Fact]
    public void KeywordProximity_FullwidthDigitsAndKeywordAreNormalized()
    {
        // 数字は ForDigits（全角→半角）、キーワードは ForKeywords で照合する。
        var ocr = new[] { Item("保険者番号", 0.10, 0.40, w: 0.2), Item("０１２３４５６７", 0.35, 0.40, w: 0.2) };
        Assert.Single(FieldDetector.Detect(DetectorId.InsurerNumber, ocr));
    }

    [Fact]
    public void KeywordProximity_DoesNotUseConfusableFix()
    {
        // 「O1234567」は補正すれば 8 桁だが、チェックの無い保険証系には補正を使わない（7 桁のまま＝対象外）。
        var ocr = new[] { Item("保険者番号", 0.10, 0.40, w: 0.2), Item("O1234567", 0.35, 0.40, w: 0.2) };
        Assert.Empty(FieldDetector.Detect(DetectorId.InsurerNumber, ocr));
    }

    // ---- Mac 移植: 在留カード番号 ----

    [Fact]
    public void Zairyu_MatchesStrictFormatOnly()
    {
        var ocr = new[]
        {
            Item("AB12345678CD", 0.76, 0.11, w: 0.22),     // 正規の書式（Mac 0.89 → 0.11）
            Item("No. EF98765432GH", 0.7, 0.20, w: 0.3),   // ラベル込みでも抽出
            Item("AB1234567CD", 0.1, 0.5),                 // 数字 7 桁
            Item("XAB12345678CD", 0.1, 0.6),               // 13 文字
            Item("1234567890AB", 0.1, 0.7),                // 数 10＋英 2
        };
        var fields = FieldDetector.Detect(DetectorId.ZairyuNumber, ocr);
        Assert.Equal(2, fields.Count);
        Assert.Equal("AB********CD", fields[0].MaskedDescription);
        Assert.Equal("EF********GH", fields[1].MaskedDescription);
        Assert.All(fields, f => Assert.Equal(DetectorId.ZairyuNumber, f.Detector));
    }

    [Fact]
    public void Zairyu_FixesConfusablesInMiddleEightOnly()
    {
        var ocr = new[]
        {
            Item("ABI2345678CD", 0.1, 0.1),   // 中央の I → 1
            Item("EF1234567OGH", 0.1, 0.2),   // 中央の O → 0
            Item("ij12345o78kl", 0.1, 0.3),   // 小文字も可（表示は大文字）
            Item("0B12345678CD", 0.1, 0.4),   // 先頭の英字側は直さない → 不一致
            Item("AB12345678C0", 0.1, 0.5),   // 末尾の英字側も直さない → 不一致
        };
        var fields = FieldDetector.Detect(DetectorId.ZairyuNumber, ocr);
        Assert.Equal(new[] { "AB********CD", "EF********GH", "IJ********KL" }, fields.Select(f => f.MaskedDescription));
    }

    // ---- 番号系（チェックデジット・読み違いの補正） ----

    [Fact]
    public void MyNumber_SplitIntoGroups_IsFoundWithMaskedLabel()
    {
        var n = MyNumber();
        var ocr = new[] { Item(n[..4], 0.10, 0.50), Item(n[4..8], 0.20, 0.50), Item(n[8..], 0.30, 0.50) };
        var f = Assert.Single(FieldDetector.Detect(DetectorId.MyNumber12, ocr));
        Assert.Equal(FieldDetector.Masked(n), f.MaskedDescription);
        Assert.Equal(new NormRect(0.10, 0.485, 0.28, 0.03), Round(f.Box));
        Assert.False(f.Partial);
    }

    [Fact]
    public void MyNumber_ConfusableFix_ProducesValidNumberFromOIStyleLine()
    {
        // W9-0 実測の型「OI 23 0956 4004」: 0→O・1→I と読まれた行を補正版で拾う。
        var n = MyNumber();   // 01234567890X
        Assert.StartsWith("01", n);
        var line = Line(0.1, 0.5, 0.1, "OI", n[2..4], n[4..8], n[8..]);
        Assert.Equal($"OI {n[2..4]} {n[4..8]} {n[8..]}", line.Text);
        Assert.DoesNotContain(FieldDetector.DigitSequences([line]), s => s.Digits == n);   // 元の列では成立しない
        var f = Assert.Single(FieldDetector.Detect(DetectorId.MyNumber12, [line]));
        Assert.Equal(FieldDetector.Masked(n), f.MaskedDescription);
        Assert.Equal(line.Box, f.Box);
    }

    [Fact]
    public void MyNumber_ConfusableFix_StillRequiresCheckDigit()
    {
        var n = MyNumber();
        var wrong = (char)('0' + (n[11] - '0' + 1) % 10);
        var line = Line(0.1, 0.5, 0.1, "OI", n[2..4], n[4..8], n[8..11] + wrong);
        Assert.Empty(FieldDetector.Detect(DetectorId.MyNumber12, [line]));
    }

    [Fact]
    public void MyNumber_OriginalAndFixedVariants_AreDeduplicated()
    {
        var n = MyNumber();
        var ocr = new[] { Item(n, 0.1, 0.5, w: 0.4) };   // 補正しても同じ列 → 1 件だけ
        Assert.Single(FieldDetector.Detect(DetectorId.MyNumber12, ocr));
    }

    [Fact]
    public void CreditCard_UsesLuhnAndConfusableFix()
    {
        var n = Luhn();   // 16 桁
        var line = Line(0.1, 0.5, 0.1, n[..4], n[4..8], n[8..12].Replace('1', 'l'), n[12..]);
        var f = Assert.Single(FieldDetector.Detect(DetectorId.CreditCardLuhn, [line]));
        Assert.Equal(FieldDetector.Masked(n), f.MaskedDescription);
    }

    [Fact]
    public void License_Valid12Digits_IsFound()
    {
        var n = License();
        var line = Line(0.1, 0.5, 0.1, "第", n, "号");
        var f = Assert.Single(FieldDetector.Detect(DetectorId.LicenseNumber12, [line]));
        Assert.False(f.Partial);
        Assert.Equal(FieldDetector.Masked(n), f.MaskedDescription);
    }

    // ---- 免許証番号の「第…号」位置の手がかり（Windows 固有・§10.10） ----

    [Fact]
    public void License_MisreadDigit_FallsBackToPositionBetweenDaiAndGo()
    {
        // 赤い網目の桁が「℃」と読まれた型（合成の数字）: 12 桁が成立しない → 第と号の間の単語の外接矩形を Partial で。
        var line = Line(0.1, 0.5, 0.1, "第", "12", "34℃6", "789012", "号");
        Assert.Empty(FieldDetector.NumberFields([line], [12], Checkdigits.IsValidLicenseNumber,
            DetectorId.LicenseNumber12, PipelineTuning.Default));
        var f = Assert.Single(FieldDetector.Detect(DetectorId.LicenseNumber12, [line]));
        Assert.True(f.Partial);
        Assert.Null(f.MaskedDescription);
        var w = line.Words;
        Assert.Equal(Round(new NormRect(w[1].Box.X, w[1].Box.Y, w[3].Box.Right - w[1].Box.X, w[1].Box.H)), Round(f.Box));
    }

    [Fact]
    public void License_PositionFallback_RequiresNineAsciiDigitsAndLength10To14()
    {
        // 数字 8 文字（「第 1234 ℃ 5678 号」）→ 出さない
        Assert.Empty(FieldDetector.Detect(DetectorId.LicenseNumber12, [Line(0.1, 0.5, 0.1, "第", "1234", "℃", "5678", "号")]));
        // 15 文字 → 出さない
        Assert.Empty(FieldDetector.Detect(DetectorId.LicenseNumber12, [Line(0.1, 0.5, 0.1, "第", "123456", "℃", "12345678", "号")]));
        // 「号」が無い → 出さない
        Assert.Empty(FieldDetector.Detect(DetectorId.LicenseNumber12, [Line(0.1, 0.5, 0.1, "第", "12", "34℃6", "789012")]));
        // 全角数字は ForDigits 後に数える（10 文字・数字 9）→ 出す
        Assert.True(Assert.Single(FieldDetector.Detect(DetectorId.LicenseNumber12,
            [Line(0.1, 0.5, 0.1, "第", "１２３４", "℃", "56789", "号")])).Partial);
    }

    [Fact]
    public void License_PositionFallback_NotUsedWhenValidNumberExists()
    {
        var n = License();
        var ocr = new[]
        {
            Line(0.1, 0.3, 0.1, "第", n, "号"),
            Line(0.1, 0.6, 0.1, "第", "12", "34℃6", "789012", "号"),
        };
        var f = Assert.Single(FieldDetector.Detect(DetectorId.LicenseNumber12, ocr));
        Assert.False(f.Partial);
    }

    [Fact]
    public void License_PositionFallback_IsOnlyForLicenseDetector()
    {
        var line = Line(0.1, 0.5, 0.1, "第", "12", "34℃6", "789012", "号");
        Assert.Empty(FieldDetector.Detect(DetectorId.MyNumber12, [line]));
    }

    // ---- 部分表示・画像ベース ----

    [Theory]
    [InlineData("1234", "****")]
    [InlineData("12345", "12*45")]
    [InlineData("123456789012", "12********12")]
    public void Masked_HidesMiddle(string digits, string expected) => Assert.Equal(expected, FieldDetector.Masked(digits));

    [Theory]
    [InlineData(DetectorId.QrBarcode)]
    [InlineData(DetectorId.Face)]
    public void ImageBasedDetectors_ReturnEmpty(DetectorId id) =>
        Assert.Empty(FieldDetector.Detect(id, [Item("123456789012", 0.1, 0.5)]));

    private static NormRect Round(NormRect r) =>
        new(Math.Round(r.X, 9), Math.Round(r.Y, 9), Math.Round(r.W, 9), Math.Round(r.H, 9));
}
