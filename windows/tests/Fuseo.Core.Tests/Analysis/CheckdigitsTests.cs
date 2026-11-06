using Fuseo.Core.Analysis;
using Xunit;
using static Fuseo.Core.Tests.Analysis.AnalysisFixtures;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>Mac Checkdigits.swift と同じ 3 関数。番号は実行時に合成する（実在しうる番号を書かない）。</summary>
public class CheckdigitsTests
{
    [Fact]
    public void MyNumber_ExactlyOneCheckDigitIsValid()
    {
        const string body = "01234567890";
        var valid = Enumerable.Range(0, 10).Count(c => Checkdigits.IsValidMyNumber(body + c));
        Assert.Equal(1, valid);
        Assert.True(Checkdigits.IsValidMyNumber(MyNumber(body)));
    }

    [Fact]
    public void MyNumber_RejectsWrongLengthAndNonAscii()
    {
        var n = MyNumber();
        Assert.False(Checkdigits.IsValidMyNumber(n[..11]));
        Assert.False(Checkdigits.IsValidMyNumber(n + "0"));
        Assert.False(Checkdigits.IsValidMyNumber(OcrTextFullwidth(n)));
    }

    [Fact]
    public void License_ChecksEleventhDigitOnly()
    {
        var n = License();
        Assert.True(Checkdigits.IsValidLicenseNumber(n));
        Assert.True(Checkdigits.IsValidLicenseNumber(n[..11] + "7"));   // 12 桁目（再交付回数）は検証しない
        var wrong = (char)('0' + (n[10] - '0' + 1) % 10);
        Assert.False(Checkdigits.IsValidLicenseNumber(n[..10] + wrong + n[11]));
    }

    [Fact]
    public void License_BodyWhoseCheckWouldBeTen_IsNeverValid()
    {
        // チェックが 10 になる本体は 1 桁で表せない＝どの桁でも不一致（合成の本体を探す）。
        var body = Enumerable.Range(0, 1000).Select(i => i.ToString("D10"))
            .First(b => Enumerable.Range(0, 10).All(c => !Checkdigits.IsValidLicenseNumber(b + c + "0")));
        Assert.Equal(10, body.Length);
    }

    [Fact]
    public void Luhn_LengthWindowIs13To19()
    {
        var n16 = Luhn();
        Assert.True(Checkdigits.IsValidLuhn(n16));
        Assert.False(Checkdigits.IsValidLuhn(n16[..15] + (char)('0' + (n16[15] - '0' + 1) % 10)));
        Assert.True(Checkdigits.IsValidLuhn(Luhn("000000000000")));        // 13 桁
        Assert.False(Checkdigits.IsValidLuhn(MakeValid(c => "00000000000" + c, _ => true)));   // 12 桁は対象外
        Assert.False(Checkdigits.IsValidLuhn(new string('0', 20)));
    }

    private static string OcrTextFullwidth(string s) => new(s.Select(c => (char)(c + 0xFEE0)).ToArray());
}
