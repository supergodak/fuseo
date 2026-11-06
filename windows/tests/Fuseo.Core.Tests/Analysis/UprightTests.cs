using Fuseo.Core.Analysis;
using Fuseo.Core.Model;
using Xunit;
using static Fuseo.Core.Tests.Analysis.AnalysisFixtures;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>正立判定（§10.3・Windows 固有）とカードの縦横比判定。</summary>
public class UprightTests
{
    [Fact]
    public void Score_CountsMeaningfulCharsPlusWeightedKeywordScore()
    {
        var presets = new[] { Preset(DocumentType.MenkyoshoFront, [("運転免許証", 6)]) };
        // 有意: 運転免許証(5)＋AB12(4)＋ひらがな(2)＝11。空白・記号（-・℃・。・「」）は数えない。
        var ocr = new[] { Item("運転免許証", 0.1, 0.1), Item("AB-12 ℃。「あい」", 0.1, 0.2) };
        Assert.Equal(71.0, Upright.Score(ocr, presets));
        Assert.Equal(11.0, Upright.Score(ocr, presets, PipelineTuning.Default with { UprightKeywordWeight = 0 }));
    }

    [Fact]
    public void Score_EmptyOcrIsZero() => Assert.Equal(0.0, Upright.Score([], PresetStore.LoadAll()));

    [Theory]
    [InlineData(new[] { 10.0, 30.0, 5.0, 4.0 }, 1)]    // 3 倍 → 90°
    [InlineData(new[] { 10.0, 4.0, 15.0, 4.0 }, 2)]    // ちょうど 1.5 倍 → 180°
    [InlineData(new[] { 10.0, 14.0, 5.0, 4.0 }, 0)]    // 僅差 → 回さない
    [InlineData(new[] { 40.0, 10.0, 5.0, 4.0 }, 0)]    // 正立が 1 位
    [InlineData(new[] { 0.0, 0.0, 0.0, 0.0 }, 0)]      // 文字なし
    [InlineData(new[] { 0.0, 0.0, 0.0, 3.0 }, 3)]      // 他が 0 なら 1 位を採る
    [InlineData(new[] { 5.0, 20.0, 20.0, 1.0 }, 0)]    // 同点の 1 位 → 回さない
    public void Choose_RotatesOnlyWhenWinnerIsClear(double[] scores, int expected) =>
        Assert.Equal(expected, Upright.Choose(scores));

    [Theory]
    [InlineData(856, 540, true)]
    [InlineData(540, 856, true)]     // 縦向きでも長辺/短辺で見る
    [InlineData(1000, 600, false)]   // 1.6667/1.5858 = 1.051 → 5% の外
    [InlineData(1000, 1000, false)]
    [InlineData(2480, 3508, false)]  // A4
    [InlineData(0, 100, false)]
    public void IsCardAspect_WithinFivePercent(int w, int h, bool expected)
        => Assert.Equal(expected, CardFrame.IsCardAspect(w, h));

    [Fact]
    public void IsCardAspect_Boundaries()
    {
        var a = 85.60 / 53.98;
        Assert.True(CardFrame.IsCardAspect(10000, (int)Math.Round(10000 / (a * 1.04))));
        Assert.True(CardFrame.IsCardAspect(10000, (int)Math.Round(10000 / (a * 0.96))));
        Assert.False(CardFrame.IsCardAspect(10000, (int)Math.Round(10000 / (a * 1.06))));
        Assert.False(CardFrame.IsCardAspect(10000, (int)Math.Round(10000 / (a * 0.94))));
    }
}
