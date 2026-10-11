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

public class TemporaryImageSourceTests
{
    [Fact]
    public async Task ReadsPng_AsBgra32()
    {
        using var dir = new TempDir();
        var path = dir.File("t.png");
        var px = new byte[3 * 2 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 20; px[i + 2] = 30; px[i + 3] = 255; }
        var src = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, px, 12);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using (var fs = File.Create(path)) enc.Save(fs);

        var pages = await new TemporaryImageSource().LoadAsync([path], _ => Task.FromResult<string?>(null), CancellationToken.None);
        var page = Assert.Single(pages);
        Assert.Equal((3, 2), (page.Bitmap.Width, page.Bitmap.Height));
        Assert.Equal((10, 20, 30, 255), (page.Bitmap.GetPixel(2, 1).B, page.Bitmap.GetPixel(2, 1).G, page.Bitmap.GetPixel(2, 1).R, page.Bitmap.GetPixel(2, 1).A));
        Assert.Equal("t.png", page.SourceName);
    }

    [Fact]
    public async Task Pdf_IsRejectedWithMessage()
    {
        var e = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new TemporaryImageSource().LoadAsync(["x.pdf"], _ => Task.FromResult<string?>(null), CancellationToken.None));
        Assert.Contains("x.pdf", e.Message);
    }
}
