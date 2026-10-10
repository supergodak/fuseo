using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.Core.Tests;

public class PresetStoreTests
{
    [Fact]
    public void LoadsAllSharedPresets()
    {
        var presets = PresetStore.LoadAll();
        var types = presets.Select(p => p.DocumentType).ToHashSet();
        // Mac/iOS と同じ 9 種別（Sources/MaskingCore/Resources/Presets を共有）
        Assert.Equal(Enum.GetValues<DocumentType>().ToHashSet(), types);
    }

    [Fact]
    public void EveryRuleHasBasis_InBothLanguages()
    {
        foreach (var p in PresetStore.LoadAll())
        foreach (var r in p.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Basis), $"{r.Id} の basis が空");
            Assert.False(string.IsNullOrWhiteSpace(r.BasisEn), $"{r.Id} の basisEn が空");
        }
    }

    [Fact]
    public void MyNumberCardBack_HasWarning()
    {
        var back = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardBack);
        Assert.NotEmpty(back.Warnings);
        Assert.NotEmpty(back.LocalizedWarnings(english: true));
    }

    [Fact]
    public void FixedRegion_IsReadAsTopOrigin_WithoutFlip()
    {
        var front = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardFront);
        var rule = front.Rules.First(r => r.Kind == RuleKind.Fixed);
        var rect = rule.Region!.ToNormRect();
        Assert.Equal(rule.Region.YTop, rect.Y);  // 左上原点どうし＝そのまま
    }

    [Fact]
    public void Validate_RejectsRuleWithoutBasis()
    {
        var bad = new DocumentPreset
        {
            SchemaVersion = 1, DocumentType = DocumentType.Generic,
            Rules = [new MaskRule { Id = "x.y", Kind = RuleKind.Dynamic, Detector = DetectorId.QrBarcode, Basis = "" }],
        };
        Assert.Throws<PresetException>(() => PresetStore.Validate(bad, "test"));
    }

    [Fact]
    public void Validate_RejectsFallbackRegionOnFixedRule()
    {
        var region = new TopOriginRegion(0, 0, 0.1, 0.1);
        var bad = new DocumentPreset
        {
            SchemaVersion = 1, DocumentType = DocumentType.Generic,
            Rules = [new MaskRule { Id = "x.y", Kind = RuleKind.Fixed, Region = region, FallbackRegion = region, Basis = "b" }],
        };
        Assert.Throws<PresetException>(() => PresetStore.Validate(bad, "test"));
    }
}

public class EnumParityTests
{
    /// <summary>Mac 版 Preset.swift の DetectorID と同じ綴りの集合であること（プリセット共有の前提）。</summary>
    [Fact]
    public void DetectorIds_MatchMacRawValues()
    {
        string[] mac = ["myNumber12", "licenseNumber12", "creditCardLuhn", "insurerNumber",
                        "kigoBango", "face", "qrBarcode", "zairyuNumber"];
        var names = Enum.GetValues<Fuseo.Core.Model.DetectorId>()
            .Select(v => System.Text.Json.JsonSerializer.Serialize(v).Trim('"')).ToHashSet();
        Assert.Equal(mac.ToHashSet(), names);
    }
}
