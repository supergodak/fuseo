using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.App.Tests;

public class ChecklistTests
{
    private static DocumentPreset Preset(DocumentType t) => PresetStore.LoadAll().Single(p => p.DocumentType == t);

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Rows_FollowRuleKinds()
    {
        var back = Checklist.Build(Preset(DocumentType.MyNumberCardBack));
        Assert.Equal(ChecklistRowKind.PositionFallback, back.Single(r => r.RuleId == "mynumB.number").Kind); // fallbackRegion あり
        Assert.Equal(ChecklistRowKind.ImageDetector, back.Single(r => r.RuleId == "mynumB.qr").Kind);

        var front = Checklist.Build(Preset(DocumentType.MyNumberCardFront));
        Assert.Equal(ChecklistRowKind.Check, front.Single(r => r.RuleId == "mynumF.gender").Kind);
        Assert.Equal(ChecklistRowKind.ImageDetector, front.Single(r => r.RuleId == "mynumF.face").Kind);

        var license = Checklist.Build(Preset(DocumentType.MenkyoshoFront));
        Assert.Equal(ChecklistRowKind.TextDetector, license.Single(r => r.RuleId == "menkyo.number").Kind);
        Assert.Equal(ChecklistRowKind.PositionFallback, Checklist.Build(Preset(DocumentType.ZairyuCardFront)).Single(r => r.RuleId == "zairyu.number").Kind);
        Assert.Equal(ChecklistRowKind.TextDetector, Checklist.Build(Preset(DocumentType.Hokensho)).Single(r => r.RuleId == "hoken.insurer").Kind);
    }

    [Fact]
    public void CheckBox_OnlyOnFixedRules_Unchanged()
    {
        foreach (var preset in PresetStore.LoadAll())
        foreach (var row in Checklist.Build(preset, ocrAvailable: true, cardFramed: true))
        {
            var rule = preset.Rules.Single(r => r.Id == row.RuleId);
            Assert.Equal(rule.Kind == RuleKind.Fixed, row.Kind == ChecklistRowKind.Check);
        }
    }

    [Theory]
    [InlineData(ChecklistRowKind.ImageDetector, false, false, "自動で探します")]
    [InlineData(ChecklistRowKind.ImageDetector, true, true, "自動で探します")]
    [InlineData(ChecklistRowKind.TextDetector, true, false, "自動で探します（見つからなければ手で塗ってください）")]
    [InlineData(ChecklistRowKind.TextDetector, true, true, "自動で探します（見つからなければ手で塗ってください）")]
    [InlineData(ChecklistRowKind.TextDetector, false, true, "手動で塗ってください")]
    [InlineData(ChecklistRowKind.Check, true, true, "位置で候補にしています")]
    [InlineData(ChecklistRowKind.Check, true, false, "切り抜くと位置で候補になります")]
    [InlineData(ChecklistRowKind.Check, false, true, "位置で候補にしています")]
    [InlineData(ChecklistRowKind.PositionFallback, true, true, "自動で探します（見つからなければ位置で候補にします）")]
    [InlineData(ChecklistRowKind.PositionFallback, true, false, "自動で探します（見つからなければ、切り抜くと位置で候補になります）")]
    [InlineData(ChecklistRowKind.PositionFallback, false, true, "位置で候補にしています")]
    [InlineData(ChecklistRowKind.PositionFallback, false, false, "切り抜くと位置で候補になります")]
    public void Note_PerKind_OcrAndFraming_Japanese(ChecklistRowKind kind, bool ocr, bool framed, string expected) =>
        Assert.Equal(expected, Checklist.NoteFor(kind, ocr, framed, null));

    [Fact]
    public void Note_English_DiffersPerCase()
    {
        var notes = new[]
        {
            Checklist.NoteFor(ChecklistRowKind.ImageDetector, true, true, En),
            Checklist.NoteFor(ChecklistRowKind.TextDetector, true, true, En),
            Checklist.NoteFor(ChecklistRowKind.TextDetector, false, true, En),
            Checklist.NoteFor(ChecklistRowKind.Check, true, true, En),
            Checklist.NoteFor(ChecklistRowKind.Check, true, false, En),
        };
        Assert.Equal(notes.Length, notes.Distinct().Count());
        Assert.Equal("Searched for automatically", notes[0]);
        Assert.Equal("Paint this by hand", notes[2]);
        foreach (var n in notes) Assert.DoesNotMatch(@"[\u3040-\u30FF\u4E00-\u9FFF]", n);
    }

    [Fact]
    public void Build_PutsTheNoteOnEveryRow_AndFollowsOcrAndFraming()
    {
        var back = Preset(DocumentType.MyNumberCardBack);
        var framed = Checklist.Build(back, ocrAvailable: true, cardFramed: true);
        Assert.Equal("自動で探します（見つからなければ位置で候補にします）", framed.Single(r => r.RuleId == "mynumB.number").Note);
        Assert.Equal("自動で探します", framed.Single(r => r.RuleId == "mynumB.qr").Note);
        var loose = Checklist.Build(back, ocrAvailable: true, cardFramed: false);
        Assert.Equal("自動で探します（見つからなければ、切り抜くと位置で候補になります）", loose.Single(r => r.RuleId == "mynumB.number").Note);

        var license = Preset(DocumentType.MenkyoshoFront);
        Assert.Equal("自動で探します（見つからなければ手で塗ってください）",
            Checklist.Build(license, ocrAvailable: true).Single(r => r.RuleId == "menkyo.number").Note);
        Assert.Equal("手動で塗ってください", Checklist.Build(license, ocrAvailable: false).Single(r => r.RuleId == "menkyo.number").Note);
        Assert.Equal("自動で探します", Checklist.Build(license, ocrAvailable: false).Single(r => r.RuleId == "menkyo.face").Note);

        foreach (var preset in PresetStore.LoadAll())
        foreach (var row in Checklist.Build(preset, ocrAvailable: true))
            Assert.False(string.IsNullOrWhiteSpace(row.Note), row.RuleId);
    }

    [Fact]
    public void TopNote_SaysCandidatesAreListedBelow()
    {
        Assert.Equal("自動で見つけた箇所は下の「自動検出の候補」に出ます（初期の ON/OFF は欄ごとに違います）。見つからない欄は手で塗ってください。チェックは自分用の目印です。",
            Strings.Get(nameof(Strings.ChecklistNote), null));
        Assert.DoesNotContain("自動では塗られません", Strings.Get(nameof(Strings.ChecklistNote), null));
        Assert.StartsWith("Areas found automatically", Strings.Get(nameof(Strings.ChecklistNote), En));
    }

    [Fact]
    public void EveryRow_HasLabelAndBasis()
    {
        foreach (var preset in PresetStore.LoadAll())
        foreach (var row in Checklist.Build(preset))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label), row.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(row.Basis), row.RuleId);
        }
    }
}

public class StringsTests
{
    private static readonly ResourceManager Manager = new("Fuseo.App.Properties.Strings", typeof(Strings).Assembly);

    [Fact]
    public void EveryProperty_HasJapaneseValue()
    {
        var props = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).ToList();
        Assert.True(props.Count > 50);
        foreach (var p in props) Assert.False(string.IsNullOrEmpty((string?)p.GetValue(null)), p.Name);
    }

    [Fact]
    public void EveryResourceKey_HasProperty()
    {
        var set = Manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;
        var names = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name).ToHashSet();
        foreach (System.Collections.DictionaryEntry e in set) Assert.Contains((string)e.Key, names);
    }
}

public class ImportErrorsTests
{
    [Fact]
    public void Describe_UsesInputExceptionValues()
    {
        Assert.Contains("62", Fuseo.App.Services.ImportErrors.Describe(new Fuseo.Input.PageLimitExceededException(62, 50, null)));
        Assert.Contains("big.pdf", Fuseo.App.Services.ImportErrors.Describe(new Fuseo.Input.PageLimitExceededException(80, 50, "big.pdf")));
        Assert.Contains("x.heic", Fuseo.App.Services.ImportErrors.Describe(new Fuseo.Input.HeicNotSupportedException("x.heic")));
        Assert.Contains("a.gif", Fuseo.App.Services.ImportErrors.Describe(new Fuseo.Input.UnsupportedFileTypeException("a.gif", ".gif")));
    }
}
