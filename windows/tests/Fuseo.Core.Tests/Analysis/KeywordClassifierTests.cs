using Fuseo.Core.Analysis;
using Fuseo.Core.Model;
using Xunit;
using static Fuseo.Core.Tests.Analysis.AnalysisFixtures;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>Mac KeywordClassifier のテスト移植（スコア勝者・全ゼロ→generic・文字の多い画像のゲート）＋照合の正規化。</summary>
public class KeywordClassifierTests
{
    [Fact]
    public void PicksHighestScore()
    {
        var presets = new[]
        {
            Preset(DocumentType.MenkyoshoFront, [("運転免許証", 6)]),
            Preset(DocumentType.MyNumberCardFront, [("個人番号カード", 6)]),
            Preset(DocumentType.Generic, []),
        };
        var result = KeywordClassifier.Classify([Item("運転免許証", 0.1, 0.1, w: 0.3)], presets);
        Assert.Equal(DocumentType.MenkyoshoFront, result.Type);
        Assert.Equal(6, result.Score);
    }

    [Fact]
    public void AllZero_FallsBackToGeneric()
    {
        var presets = new[] { Preset(DocumentType.MenkyoshoFront, [("運転免許証", 6)]), Preset(DocumentType.Generic, []) };
        var result = KeywordClassifier.Classify([Item("該当語なし", 0.1, 0.1, w: 0.3)], presets);
        Assert.Equal(DocumentType.Generic, result.Type);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void TextHeavyPage_SingleKeyword_FallsBackToGeneric()
    {
        var presets = new[] { Preset(DocumentType.MenkyoshoBack, [("備考", 5), ("臓器提供", 2)]), Preset(DocumentType.Generic, []) };
        var ocr = Filler(KeywordClassifier.MaxCardObservations).Append(Item("備考", 0.1, 0.5, w: 0.1)).ToList();
        var result = KeywordClassifier.Classify(ocr, presets);
        Assert.Equal(DocumentType.Generic, result.Type);
        Assert.Equal(0, result.Score);
        Assert.Equal(DocumentType.MenkyoshoBack, result.Ranking[0].Type);   // ranking は生スコアのまま
    }

    [Fact]
    public void TextHeavyPage_RejectsCardTypesEvenWithManyKeywords()
    {
        var presets = new[]
        {
            Preset(DocumentType.MenkyoshoBack, [("備考", 5), ("以下の部分", 3), ("臓器提供", 2)]),
            Preset(DocumentType.Generic, []),
        };
        var ocr = Filler(KeywordClassifier.MaxCardObservations + 1)
            .Append(Item("備考", 0.1, 0.5, w: 0.1)).Append(Item("臓器提供", 0.1, 0.6, w: 0.1)).ToList();
        Assert.Equal(DocumentType.Generic, KeywordClassifier.Classify(ocr, presets).Type);
    }

    [Fact]
    public void TextHeavyPage_PaperDocumentWithTwoKeywords_IsAccepted()
    {
        var presets = new[]
        {
            Preset(DocumentType.JuminhyoMyNumber, [("住民票", 5), ("世帯主", 3), ("続柄", 2)]),
            Preset(DocumentType.MenkyoshoBack, [("備考", 5)]),
            Preset(DocumentType.Generic, []),
        };
        var ocr = Filler(200).Append(Item("住民票", 0.1, 0.1, w: 0.2)).Append(Item("世帯主", 0.1, 0.2, w: 0.2))
            .Append(Item("備考", 0.1, 0.8, w: 0.1)).ToList();
        var result = KeywordClassifier.Classify(ocr, presets);
        Assert.Equal(DocumentType.JuminhyoMyNumber, result.Type);
        Assert.Equal(8, result.Score);
    }

    [Fact]
    public void CardSizedText_SingleKeyword_StillAccepted()
    {
        var presets = new[] { Preset(DocumentType.MyNumberCardBack, [("コールセンター", 5), ("個人番号", 2)]), Preset(DocumentType.Generic, []) };
        var ocr = Filler(17).Append(Item("個人番号", 0.1, 0.5, w: 0.2)).ToList();
        var result = KeywordClassifier.Classify(ocr, presets);
        Assert.Equal(DocumentType.MyNumberCardBack, result.Type);
        Assert.Equal(2, result.Score);
    }

    [Fact]
    public void Matching_UsesForKeywordsOnBothSides()
    {
        // Windows OCR の 1 文字ずつの単語から作った行・全角空白・全角英字・行をまたぐキーワードも一致する。
        var presets = new[] { Preset(DocumentType.ZairyuCardFront, [("在留カード", 6), ("RESIDENCE CARD", 6)]), Preset(DocumentType.Generic, []) };
        var ocr = new[]
        {
            Line(0.1, 0.1, 0.05, "在", "留", "カ", "ー", "ド"),
            Item("ＲＥＳＩＤＥＮＣＥ　ＣＡＲＤ", 0.1, 0.2, w: 0.4),
        };
        var result = KeywordClassifier.Classify(ocr, presets);
        Assert.Equal(DocumentType.ZairyuCardFront, result.Type);
        Assert.Equal(12, result.Score);
    }

    [Fact]
    public void KeywordsEqualAfterNormalization_CountAsOneWord_WithLargerWeight()
    {
        // 「在留カード」と半角カナの「在留ｶｰﾄﾞ」は NFKC で同じ。足し合わせると 1 語が 2 語一致になり文字の多い画像のゲートを抜けてしまう。
        var presets = new[]
        {
            Preset(DocumentType.JuminhyoMyNumber, [("在留カード", 6), ("在留ｶｰﾄﾞ", 4)]),
            Preset(DocumentType.Generic, []),
        };
        var cardSized = KeywordClassifier.Classify([Item("在留カード", 0.1, 0.1, w: 0.3)], presets);
        Assert.Equal(DocumentType.JuminhyoMyNumber, cardSized.Type);
        Assert.Equal(6, cardSized.Score);

        var heavy = Filler(KeywordClassifier.MaxCardObservations).Append(Item("在留カード", 0.1, 0.5, w: 0.1)).ToList();
        Assert.Equal(DocumentType.Generic, KeywordClassifier.Classify(heavy, presets).Type);
    }

    [Fact]
    public void RealPresets_LicenseFrontKeywords_ClassifyAsLicenseFront()
    {
        var ocr = new[] { Line(0.1, 0.1, 0.03, "運", "転", "免", "許", "証"), Line(0.1, 0.9, 0.03, "公", "安", "委", "員", "会") };
        Assert.Equal(DocumentType.MenkyoshoFront, KeywordClassifier.Classify(ocr, PresetStore.LoadAll()).Type);
    }

    [Theory]
    [InlineData(DocumentType.MenkyoshoFront, true)]
    [InlineData(DocumentType.MenkyoshoBack, true)]
    [InlineData(DocumentType.MyNumberCardFront, true)]
    [InlineData(DocumentType.MyNumberCardBack, true)]
    [InlineData(DocumentType.ZairyuCardFront, true)]
    [InlineData(DocumentType.Hokensho, false)]
    [InlineData(DocumentType.ShikakuKakuninsho, false)]
    [InlineData(DocumentType.JuminhyoMyNumber, false)]
    [InlineData(DocumentType.Generic, false)]
    public void IsId1Card_MatchesMac(DocumentType type, bool expected) => Assert.Equal(expected, type.IsId1Card());
}
