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

    [Fact]
    public void Rows_FollowRuleKinds()
    {
        var back = Checklist.Build(Preset(DocumentType.MyNumberCardBack));
        Assert.Equal(ChecklistRowKind.ManualGuide, back.Single(r => r.RuleId == "mynumB.number").Kind);
        Assert.Equal(ChecklistRowKind.QrCandidate, back.Single(r => r.RuleId == "mynumB.qr").Kind);

        var front = Checklist.Build(Preset(DocumentType.MyNumberCardFront));
        Assert.Equal(ChecklistRowKind.Check, front.Single(r => r.RuleId == "mynumF.gender").Kind);
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
