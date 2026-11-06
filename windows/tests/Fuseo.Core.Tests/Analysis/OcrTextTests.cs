using Fuseo.Core.Analysis;
using Xunit;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>§10.12 の文字列の整形（Windows 固有）。キーワード用と数字用を分ける理由（℃ の罠）もここで固定する。</summary>
public class OcrTextTests
{
    [Theory]
    [InlineData(new[] { "保", "険", "者", "番", "号" }, "保険者番号")]   // 日本語は 1 文字ずつ単語になる → 詰める
    [InlineData(new[] { "01", "23", "4567" }, "01 23 4567")]           // 両側 ASCII → 空白（数字の群を残す）
    [InlineData(new[] { "第", "12", "号" }, "第12号")]                   // 片側が日本語 → 詰める
    [InlineData(new[] { "No.", "1234" }, "No. 1234")]
    [InlineData(new[] { "12", "℃", "34" }, "12℃34")]                   // ℃ は非 ASCII
    [InlineData(new[] { "AB", "", "CD" }, "AB CD")]                     // 空の単語は無視
    public void JoinWords_SpacesOnlyBetweenAsciiEdges(string[] words, string expected) =>
        Assert.Equal(expected, OcrText.JoinWords(words));

    [Fact]
    public void ForKeywords_AppliesNfkcAndStripsAllWhitespace()
    {
        Assert.Equal("保険者番号", OcrText.ForKeywords("保 険　者\t番号"));
        Assert.Equal("AB12", OcrText.ForKeywords("ＡＢ１２"));
        Assert.Equal("在留カード", OcrText.ForKeywords("在留ｶｰﾄﾞ"));   // 半角カナも揃う
    }

    [Fact]
    public void ForDigits_ConvertsFullwidthAlnumAndSpaceOnly()
    {
        Assert.Equal("12ABab 3", OcrText.ForDigits("１２ＡＢａｂ　3"));
        Assert.Equal("第12号", OcrText.ForDigits("第１２号"));   // 漢字はそのまま
        Assert.Equal("ｶｰﾄﾞ", OcrText.ForDigits("ｶｰﾄﾞ"));         // 英数以外は変えない
    }

    [Fact]
    public void ForDigits_KeepsCelsiusAsOneChar_UnlikeNfkc()
    {
        // §10.10 の実物: 赤い網目の桁が「℃」と読まれた。NFKC だと「°C」の 2 文字になり文字数が狂う。
        const string misread = "1234℃6789012";
        Assert.Equal(misread, OcrText.ForDigits(misread));
        Assert.Equal(12, OcrText.ForDigits(misread).Length);
        Assert.Equal(13, OcrText.ForKeywords(misread).Length);   // 罠の確認（キーワード用は数字に使わない）
    }

    [Theory]
    [InlineData("OI 23 4567 8901", "01 23 4567 8901")]
    [InlineData("ABI2345678CD 5", "ABI2345678CD 5")]   // 英字の混じる単語は直さない
    [InlineData("No. I 23", "No. 1 23")]               // 数字の行なら単独の I も直す（チェックデジットが誤採用を防ぐ）
    [InlineData("|2 3o", "12 30")]
    [InlineData("Oslo 12", "Oslo 12")]
    [InlineData("OIl", "OIl")]                         // 数字を含まない行は触らない
    [InlineData("O I  l", "O I  l")]
    public void FixDigitConfusables_OnlyDigitLikeWordsInLinesWithDigits(string input, string expected) =>
        Assert.Equal(expected, OcrText.FixDigitConfusables(input));

    [Fact]
    public void FixDigitConfusables_KeepsSpacing()
    {
        Assert.Equal("01  23", OcrText.FixDigitConfusables("OI  23"));
    }
}
