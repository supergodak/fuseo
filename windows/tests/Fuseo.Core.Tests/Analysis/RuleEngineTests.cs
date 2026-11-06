using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Xunit;
using static Fuseo.Core.Tests.Analysis.AnalysisFixtures;

namespace Fuseo.Core.Tests.Analysis;

/// <summary>
/// Mac RuleEngine のテスト移植（固定・位置推定・ラベル・defaultOn）＋Windows の cardFramed 条件と
/// 「（一部読めず）」ラベル（§10.12）。フェイク検出は内部の検出子差し替え版で渡す（Mac の FakeDetector 相当）。
/// </summary>
public class RuleEngineTests
{
    private static readonly IReadOnlyDictionary<DetectorId, IReadOnlyList<NormRect>> NoImage =
        new Dictionary<DetectorId, IReadOnlyList<NormRect>>();

    private static MaskRule FixedRule(double padding = 0.05) => new()
    {
        Id = "t.fixed", Label = "固定欄", LabelEn = "Fixed field", Kind = RuleKind.Fixed,
        Region = new TopOriginRegion(0.2, 0.3, 0.4, 0.1), DefaultOn = true, Padding = padding,
        Basis = "根拠", BasisEn = "basis",
    };

    private static MaskRule DynamicRule(DetectorId id, bool defaultOn = true, double? padding = null, TopOriginRegion? fallback = null) => new()
    {
        Id = "t.dyn", Label = "個人番号", LabelEn = "My Number", Kind = RuleKind.Dynamic, Detector = id,
        DefaultOn = defaultOn, Padding = padding, FallbackRegion = fallback, Basis = "根拠", BasisEn = "basis",
    };

    private static IReadOnlyList<MaskCandidate> Run(DocumentPreset preset, bool cardFramed, bool english, params DetectedField[] fields) =>
        RuleEngine.Candidates(preset, cardFramed, english, _ => fields);

    // ---- 固定領域（cardFramed のときだけ） ----

    [Fact]
    public void Fixed_AppliesPadding_WhenCardFramed()
    {
        var rule = FixedRule();
        var c = Assert.Single(Run(Preset(DocumentType.Generic, [], rule), cardFramed: true, english: false));
        Assert.Equal(rule.Region!.ToNormRect().Padded(0.05), c.Box);
        Assert.Equal(CandidateSource.FixedRegion, c.Source);
        Assert.Null(c.Detector);
        Assert.True(c.IsOn);
        Assert.Equal("固定欄", c.Label);
        Assert.Equal("根拠", c.Basis);
    }

    [Fact]
    public void Fixed_Absent_WhenNotCardFramed()
    {
        Assert.Empty(Run(Preset(DocumentType.Generic, [], FixedRule()), cardFramed: false, english: false));
    }

    [Fact]
    public void Fixed_English_UsesLabelEnAndBasisEn()
    {
        var c = Assert.Single(Run(Preset(DocumentType.Generic, [], FixedRule()), cardFramed: true, english: true));
        Assert.Equal("Fixed field", c.Label);
        Assert.Equal("basis", c.Basis);
    }

    // ---- 位置推定（検出 0 件 かつ cardFramed） ----

    [Fact]
    public void Fallback_UsedOnlyWhenDetectorFindsNothing_AndCardFramed()
    {
        var fallback = new TopOriginRegion(0.32, 0.08, 0.62, 0.28);
        var rule = DynamicRule(DetectorId.MyNumber12, fallback: fallback);
        var preset = Preset(DocumentType.MyNumberCardBack, [], rule);

        var empty = Assert.Single(Run(preset, cardFramed: true, english: false));
        Assert.Equal("個人番号（位置推定）", empty.Label);
        Assert.Equal(CandidateSource.FixedRegion, empty.Source);
        Assert.Null(empty.Detector);
        Assert.Equal(fallback.ToNormRect().Padded(rule.EffectivePadding), empty.Box);

        Assert.Empty(Run(preset, cardFramed: false, english: false));   // 机の上の写真では位置が合わない

        var field = new DetectedField(DetectorId.MyNumber12, new NormRect(0.4, 0.22, 0.4, 0.08), null);
        var found = Assert.Single(Run(preset, cardFramed: true, english: false, field));
        Assert.Equal(CandidateSource.Detector, found.Source);
        Assert.Equal(DetectorId.MyNumber12, found.Detector);
    }

    [Fact]
    public void Fallback_English_Label()
    {
        var rule = DynamicRule(DetectorId.MyNumber12, fallback: new TopOriginRegion(0.1, 0.1, 0.2, 0.2));
        var c = Assert.Single(Run(Preset(DocumentType.MyNumberCardBack, [], rule), cardFramed: true, english: true));
        Assert.Equal("My Number (position estimated)", c.Label);
    }

    // ---- 動的検出のラベル ----

    [Fact]
    public void Dynamic_LabelIncludesMaskedDescription()
    {
        var field = new DetectedField(DetectorId.MyNumber12, new NormRect(0.1, 0.85, 0.2, 0.05), "01********23");
        var rule = DynamicRule(DetectorId.MyNumber12, defaultOn: false, padding: 0.02);
        var preset = Preset(DocumentType.Generic, [], rule);

        var ja = Assert.Single(Run(preset, cardFramed: false, english: false, field));
        Assert.Equal("個人番号（01********23）", ja.Label);
        Assert.Equal(field.Box.Padded(0.02), ja.Box);
        Assert.False(ja.IsOn);   // defaultOn=false
        Assert.Equal("My Number (01********23)", Assert.Single(Run(preset, false, true, field)).Label);
    }

    [Fact]
    public void Dynamic_PartialLabel_SaysPartlyUnreadable()
    {
        var field = new DetectedField(DetectorId.LicenseNumber12, new NormRect(0.2, 0.1, 0.3, 0.05), null, Partial: true);
        var rule = DynamicRule(DetectorId.LicenseNumber12) with { Label = "免許証番号", LabelEn = "License number" };
        var preset = Preset(DocumentType.MenkyoshoFront, [], rule);
        Assert.Equal("免許証番号（一部読めず）", Assert.Single(Run(preset, false, false, field)).Label);
        Assert.Equal("License number (partly unreadable)", Assert.Single(Run(preset, false, true, field)).Label);
    }

    [Fact]
    public void Dynamic_WithoutDescription_UsesPlainLabel()
    {
        var field = new DetectedField(DetectorId.MyNumber12, new NormRect(0.1, 0.1, 0.2, 0.05), null);
        Assert.Equal("個人番号", Assert.Single(Run(Preset(DocumentType.Generic, [], DynamicRule(DetectorId.MyNumber12)), false, false, field)).Label);
    }

    // ---- 公開入口（OCR と画像検出から） ----

    [Fact]
    public void ImageDetections_AreUsedForQr_WithPadding()
    {
        var rule = DynamicRule(DetectorId.QrBarcode) with { Label = "QR", LabelEn = "QR" };
        var preset = Preset(DocumentType.Generic, [], rule);
        var box = new NormRect(0.7, 0.6, 0.2, 0.3);
        var images = new Dictionary<DetectorId, IReadOnlyList<NormRect>> { [DetectorId.QrBarcode] = [box] };

        var c = Assert.Single(RuleEngine.Candidates(preset, [], images, cardFramed: false, english: false));
        Assert.Equal(box.Padded(0.01), c.Box);
        Assert.Equal(DetectorId.QrBarcode, c.Detector);
        Assert.Equal("QR", c.Label);
        Assert.Empty(RuleEngine.Candidates(preset, [], NoImage, cardFramed: false, english: false));
    }

    [Fact]
    public void RealPreset_MyNumberBack_FindsGeneratedNumber_ElseFallbackOnlyWhenFramed()
    {
        var preset = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardBack);
        var rule = preset.Rules.Single(r => r.Detector == DetectorId.MyNumber12);
        var n = MyNumber();
        var ocr = new[] { Item(n[..4], 0.40, 0.20), Item(n[4..8], 0.50, 0.20), Item(n[8..], 0.60, 0.20) };

        var found = RuleEngine.Candidates(preset, ocr, NoImage, cardFramed: false, english: false);
        var c = Assert.Single(found);
        Assert.Equal($"{rule.Label}（{n[..2]}********{n[^2..]}）", c.Label);
        Assert.Equal(rule.DefaultOn, c.IsOn);
        Assert.DoesNotContain(n, c.Label);   // 生の番号を出さない

        Assert.Empty(RuleEngine.Candidates(preset, [], NoImage, cardFramed: false, english: false));
        var fb = Assert.Single(RuleEngine.Candidates(preset, [], NoImage, cardFramed: true, english: false));
        Assert.EndsWith("（位置推定）", fb.Label);
    }

    [Fact]
    public void RealPreset_MyNumberFront_FixedRegionsOnlyWhenFramed()
    {
        var preset = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardFront);
        Assert.Empty(RuleEngine.Candidates(preset, [], NoImage, cardFramed: false, english: false));
        var framed = RuleEngine.Candidates(preset, [], NoImage, cardFramed: true, english: false);
        Assert.Equal(preset.Rules.Where(r => r.Kind == RuleKind.Fixed).Select(r => r.Id), framed.Select(c => c.RuleId));
    }

    [Fact]
    public void RealPreset_LicenseFront_MisreadNumberGivesPartialCandidate()
    {
        var preset = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MenkyoshoFront);
        var line = Line(0.1, 0.5, 0.05, "第", "12", "34℃6", "789012", "号");
        var c = Assert.Single(RuleEngine.Candidates(preset, [line], NoImage, cardFramed: false, english: false));
        Assert.Equal("menkyo.number", c.RuleId);
        Assert.EndsWith("（一部読めず）", c.Label);
    }
}
