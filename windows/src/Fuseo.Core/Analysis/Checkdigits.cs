namespace Fuseo.Core.Analysis;

/// <summary>公開仕様のチェックデジット検証（Mac 版 Checkdigits.swift の写し）。OCR の数字列の誤検出を排除する要。</summary>
public static class Checkdigits
{
    /// <summary>
    /// マイナンバー（12 桁）。総務省令の検査用数字: 本体 11 桁を右から P1..P11、
    /// Qn = n+1 (1≤n≤6) / n−5 (7≤n≤11)。rem = Σ(Pn×Qn) mod 11、検査用数字 = rem ≤ 1 ? 0 : 11 − rem。
    /// </summary>
    public static bool IsValidMyNumber(string digits)
    {
        if (digits.Length != 12 || !digits.All(OcrText.IsAsciiDigit)) return false;
        var sum = 0;
        for (var n = 1; n <= 11; n++)
        {
            var p = digits[11 - n] - '0';   // 右から n 番目（本体は 0..10）
            var q = n <= 6 ? n + 1 : n - 5;
            sum += p * q;
        }
        var rem = sum % 11;
        var expected = rem <= 1 ? 0 : 11 - rem;
        return digits[11] - '0' == expected;
    }

    /// <summary>
    /// 運転免許証番号（12 桁）。11 桁目がチェックデジット（モジュラス 11・ウェイト 2〜7 巡回）:
    /// 先頭 10 桁を右から重み 2,3,4,5,6,7,2,3,… で乗じ、チェック = (11 − Σ mod 11) mod 11。
    /// 10 になる番号は照合不能＝不一致。12 桁目は再交付回数のため検証しない。
    /// </summary>
    public static bool IsValidLicenseNumber(string digits)
    {
        if (digits.Length != 12 || !digits.All(OcrText.IsAsciiDigit)) return false;
        var sum = 0;
        var weight = 2;
        for (var i = 9; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 7 ? 2 : weight + 1;
        }
        var expected = (11 - sum % 11) % 11;
        if (expected > 9) return false;
        return digits[10] - '0' == expected;
    }

    /// <summary>クレジットカード等の Luhn（13〜19 桁）。</summary>
    public static bool IsValidLuhn(string digits)
    {
        if (digits.Length < 13 || digits.Length > 19 || !digits.All(OcrText.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
