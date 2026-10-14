using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using Fuseo.App.Configuration;
using Fuseo.App.Library;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>日本語の文字（ひらがな・カタカナ・漢字・全角記号の一部）。</summary>
internal static class Japanese
{
    private static readonly Regex Chars = new(@"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uFF01-\uFF5E「」、。]", RegexOptions.Compiled);
    public static bool Contains(string s) => Chars.IsMatch(s);
}

/// <summary>W6: resx（ja/en）の対応・英語 UI の文言。</summary>
public class LocalizationTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly ResourceManager Manager = new("Fuseo.App.Properties.Strings", typeof(Strings).Assembly);

    private static Dictionary<string, string> Neutral() => Read(Manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!);

    /// <summary>en のサテライトそのもの（親へのフォールバックを含めない）。</summary>
    private static Dictionary<string, string> English() =>
        Read(Manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)
             ?? throw new InvalidOperationException("英語のサテライト（en\\Fuseo.resources.dll）が見つからない"));

    private static Dictionary<string, string> Read(ResourceSet set)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in set) d[(string)e.Key] = (string)e.Value!;
        return d;
    }

    [Fact]
    public void JaAndEn_HaveSameKeys_AndEnglishIsNotEmpty()
    {
        var ja = Neutral();
        var en = English();
        Assert.Equal(ja.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
        foreach (var (k, v) in en) Assert.False(string.IsNullOrWhiteSpace(v), k);
    }

    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static List<int> Indexes(string s) =>
        Placeholder.Matches(s).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToList();

    [Fact]
    public void Placeholders_MatchBetweenJaAndEn()
    {
        var ja = Neutral();
        var en = English();
        foreach (var (k, v) in ja) Assert.True(Indexes(v).SequenceEqual(Indexes(en[k])), $"{k}: ja={v} / en={en[k]}");
    }

    [Fact]
    public void EnglishStrings_HaveNoJapanese_ExceptLanguageAndCompanyNames()
    {
        var allowed = new HashSet<string> { nameof(Strings.SettingsLanguageJapanese), nameof(Strings.AboutCopyright) };
        foreach (var (k, v) in English())
            if (!allowed.Contains(k)) Assert.False(Japanese.Contains(v), $"{k}: {v}");
    }

    [Fact]
    public void Get_WithCulture_PicksLanguage_AndNullMeansJapanese()
    {
        Assert.Equal("Export…", Strings.Get(nameof(Strings.ExportButton), En));
        Assert.Equal("書き出し…", Strings.Get(nameof(Strings.ExportButton), Ja));
        Assert.Equal("書き出し…", Strings.Get(nameof(Strings.ExportButton), null));
        Assert.True(Strings.IsEnglishCulture(En));
        Assert.False(Strings.IsEnglishCulture(Ja));
        Assert.False(Strings.IsEnglishCulture(null));
    }

    // ---- 取り込みエラー（例外の値から組み立てる） ----

    private sealed class FutureImportException(string? name) : PageImportException(name, "将来の日本語メッセージ");

    public static TheoryData<Exception> ImportExceptions() =>
    [
        new PageLimitExceededException(62, 50, null),
        new PageLimitExceededException(80, 50, "big.pdf"),
        new HeicNotSupportedException("photo.heic"),
        new UnreadableFileException("broken.png"),
        new UnsupportedFileTypeException("a.gif", ".gif"),
        new FutureImportException("future.pdf"),
        new FutureImportException(null),
        new NotSupportedException("x"),
        new FileNotFoundException("x"),
        new UnauthorizedAccessException("x"),
        new IOException("x"),
        new InvalidOperationException("x"),
    ];

    [Theory]
    [MemberData(nameof(ImportExceptions))]
    public void ImportErrors_English_HasNoJapanese(Exception e)
    {
        var text = ImportErrors.Describe(e, En);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(Japanese.Contains(text), text);
    }

    [Fact]
    public void ImportErrors_English_UsesValues()
    {
        var limit = ImportErrors.Describe(new PageLimitExceededException(80, 50, "big.pdf"), En);
        Assert.Contains("big.pdf", limit);
        Assert.Contains("80", limit);
        Assert.Contains("50", limit);
        Assert.Contains("62", ImportErrors.Describe(new PageLimitExceededException(62, 50, null), En));
        Assert.Contains("photo.heic", ImportErrors.Describe(new HeicNotSupportedException("photo.heic"), En));
        Assert.Contains("a.gif", ImportErrors.Describe(new UnsupportedFileTypeException("a.gif", ".gif"), En));
        Assert.Contains("broken.png", ImportErrors.Describe(new UnreadableFileException("broken.png"), En));
    }

    [Fact]
    public void ImportErrors_Japanese_MatchesInputMessages()
    {
        // 日本語の文言は W2 の例外メッセージと同じ（言語で中身が食い違わない）。
        Exception[] all =
        [
            new PageLimitExceededException(80, 50, "big.pdf"), new HeicNotSupportedException("x.heic"),
            new UnreadableFileException("b.png"), new UnsupportedFileTypeException("a.gif", ".gif"),
        ];
        foreach (var e in all) Assert.Equal(e.Message, ImportErrors.Describe(e, null));
    }

    // ---- 既定のファイル名・日付 ----

    [Fact]
    public void DefaultFileName_FollowsLanguage()
    {
        var day = new DateTime(2026, 10, 11, 9, 0, 0);
        Assert.Equal("Redacted_20261011.pdf", ExportNaming.DefaultFileName(day, ExportFormat.Pdf, En));
        Assert.Equal("伏せ済み_20261011.png", ExportNaming.DefaultFileName(day, ExportFormat.Png, Ja));
        Assert.Equal("伏せ済み_20261011.jpg", ExportNaming.DefaultFileName(day, ExportFormat.Jpeg, null));
    }

    [Fact]
    public void LibraryUpdated_UsesUiCulture()
    {
        var local = new DateTimeOffset(new DateTime(2026, 10, 9, 14, 5, 0, DateTimeKind.Local));
        Assert.Equal("更新 2026/10/09 14:05", LibraryItemViewModel.FormatUpdated(local, Ja));
        Assert.Equal("Updated " + local.DateTime.ToString("g", En), LibraryItemViewModel.FormatUpdated(local, En));
        Assert.StartsWith("Updated 10/9/2026 2:05", LibraryItemViewModel.FormatUpdated(local, En));
        Assert.Equal("", LibraryItemViewModel.FormatUpdated(DateTimeOffset.MinValue, En));
    }

    // ---- プリセット由来の文言 ----

    [Fact]
    public void EveryPreset_HasEnglishTexts()
    {
        var presets = PresetStore.LoadAll();
        Assert.Equal(Enum.GetValues<DocumentType>().Length, presets.Count);
        foreach (var p in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayNameEn), $"{p.DocumentType} displayNameEn");
            Assert.NotEqual(p.DisplayName, p.Name(true));
            Assert.Equal(p.Warnings.Count, p.LocalizedWarnings(true).Count);
            if (p.Warnings.Count > 0) Assert.NotEqual(p.Warnings, p.LocalizedWarnings(true));
            foreach (var row in Checklist.Build(p, english: true))
            {
                var rule = p.Rules.Single(r => r.Id == row.RuleId);
                Assert.False(string.IsNullOrWhiteSpace(rule.LabelEn), $"{rule.Id} labelEn");
                Assert.False(string.IsNullOrWhiteSpace(rule.BasisEn), $"{rule.Id} basisEn");
                Assert.Equal(rule.LabelEn, row.Label);
                Assert.Equal(rule.BasisEn, row.Basis);
            }
        }
    }

    private static readonly NormRect RawQr = new(0.4, 0.4, 0.2, 0.2);

    private static MainViewModel Vm(bool english, FakeDialogs? dialogs = null) =>
        new(new FakePageSource(), new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), dialogs ?? new FakeDialogs(),
            () => new DateTime(2026, 10, 14), new FakeDetector(RawQr), english: english);

    [Fact]
    public async Task MainViewModel_English_UsesPresetEnglishEverywhere()
    {
        var presets = PresetStore.LoadAll().ToDictionary(p => p.DocumentType);
        var vm = Vm(english: true);
        Assert.True(vm.IsEnglish);
        Assert.All(vm.DocumentTypes, o => Assert.Equal(presets[o.Type].Name(true), o.DisplayName));

        await vm.ImportAsync(["back.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        var back = presets[DocumentType.MyNumberCardBack];
        Assert.Equal(back.Rules.Select(r => r.LocalizedLabel(true)), vm.Checklist.Select(c => c.Label));
        Assert.All(back.Rules, r => Assert.Equal(r.LabelEn, r.LocalizedLabel(true)));
        Assert.Equal(back.LocalizedWarnings(true), vm.BannerWarnings);
        var qrRule = back.Rules.Single(r => r.Detector == DetectorId.QrBarcode);
        var cand = Assert.Single(vm.CandidateItems);
        Assert.Equal(qrRule.LabelEn, cand.Label);
        Assert.Equal(qrRule.BasisEn, cand.Basis);
        Assert.Equal(qrRule.LabelEn, vm.SelectedPage!.State.Candidates.Single().Label);

        var export = vm.CreateExportViewModel();
        Assert.Equal(back.LocalizedWarnings(true), export.Warnings);
    }

    [Fact]
    public async Task MainViewModel_Japanese_KeepsPresetJapanese()
    {
        var vm = Vm(english: false);
        await vm.ImportAsync(["back.png"]);
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        var back = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardBack);
        Assert.Equal(back.DisplayName, vm.SelectedDocumentType!.DisplayName);
        Assert.Equal(back.Warnings, vm.BannerWarnings);
        Assert.Equal(back.Rules.Single(r => r.Detector == DetectorId.QrBarcode).Label, vm.CandidateItems.Single().Label);
    }

    [Fact]
    public void CandidateLabel_FollowsCurrentLanguage_EvenIfSavedInOther()
    {
        // 日本語で保存した候補を英語 UI で開いても、表示は英語（保存値は変えない）。
        var back = PresetStore.LoadAll().Single(p => p.DocumentType == DocumentType.MyNumberCardBack);
        var rule = back.Rules.Single(r => r.Detector == DetectorId.QrBarcode);
        var saved = CandidateFactory.Build(back, DetectorId.QrBarcode, [RawQr], 0, english: false).Single();
        Assert.Equal(rule.Label, saved.Label);
        var vmItem = new CandidateViewModel(saved, (_, _) => { }, (rule.LocalizedLabel(true), rule.LocalizedBasis(true)));
        Assert.Equal(rule.LabelEn, vmItem.Label);
        Assert.Equal(rule.Label, saved.Label);
    }
}

/// <summary>W6: 表示言語の決め方・settings.json。</summary>
public class SettingsTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Theory]
    [InlineData(LanguageSetting.Auto, "ja-JP", "ja-JP")]
    [InlineData(LanguageSetting.Auto, "en-US", "en-US")]
    [InlineData(LanguageSetting.Auto, "en-GB", "en-GB")]
    [InlineData(LanguageSetting.Auto, "fr-FR", "en-US")]
    [InlineData(LanguageSetting.Auto, "zh-CN", "en-US")]
    [InlineData(LanguageSetting.Japanese, "en-US", "ja-JP")]
    [InlineData(LanguageSetting.English, "ja-JP", "en-US")]
    [InlineData(LanguageSetting.English, "en-AU", "en-AU")]
    public void Resolve_PicksCulture(LanguageSetting setting, string os, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(setting, C(os)).Name);

    [Fact]
    public void Store_Missing_ReturnsDefault_WithoutWriting()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        Assert.Equal(LanguageSetting.Auto, store.Load().Language);
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData(LanguageSetting.Auto, "auto")]
    [InlineData(LanguageSetting.Japanese, "ja")]
    [InlineData(LanguageSetting.English, "en")]
    public void Store_RoundTrips_Atomically(LanguageSetting value, string json)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { Language = value });
        Assert.Equal(value, new SettingsStore(path).Load().Language);
        Assert.Contains($"\"language\": \"{json}\"", File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("{\"schemaVersion\":1,\"language\":\"de\"}")]
    [InlineData("{\"schemaVersion\":99,\"language\":\"en\"}")]
    [InlineData("[1,2,3]")]
    public void Store_Corrupt_FallsBackToDefault_AndOverwrites(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, content);
        var store = new SettingsStore(path);
        Assert.Equal(LanguageSetting.Auto, store.Load().Language);
        Assert.Contains("\"language\": \"auto\"", File.ReadAllText(path));
        Assert.Equal(LanguageSetting.Auto, store.Load().Language);
    }

    [Fact]
    public void DefaultPath_IsUnderLocalAppDataFuseo()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Fuseo", "settings.json"), SettingsStore.DefaultPath);
        Assert.Equal(Path.Combine(local, "Fuseo", "Library"), FileWorkLibrary.DefaultRoot);
    }
}

/// <summary>W6: 設定ウィンドウ・バージョン情報の ViewModel 側。</summary>
public class SettingsViewModelTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel Vm(SettingsStore? store, FileWorkLibrary? lib) =>
        new(_source, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs, () => new DateTime(2026, 10, 14),
            library: lib, settings: store);

    [Fact]
    public void ChangingLanguage_SavesImmediately()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var vm = Vm(store, null).CreateSettingsViewModel();
        Assert.Equal(LanguageSetting.Auto, vm.SelectedLanguage.Value);
        Assert.True(vm.CanChangeLanguage);
        vm.SelectedLanguage = vm.LanguageOptions.Single(o => o.Value == LanguageSetting.English);
        Assert.Equal(LanguageSetting.English, store.Load().Language);
        // 開き直すと保存した値が選ばれている
        Assert.Equal(LanguageSetting.English, Vm(store, null).CreateSettingsViewModel().SelectedLanguage.Value);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void OpenFolder_CreatesAndOpensLibraryRoot()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "Library");
        var vm = Vm(null, new FileWorkLibrary(root)).CreateSettingsViewModel();
        Assert.Equal(root, vm.StoragePath);
        Assert.False(vm.CanChangeLanguage);
        vm.OpenFolderCommand.Execute(null);
        Assert.True(Directory.Exists(root));
        Assert.Equal([root], _dialogs.OpenedFolders);
    }

    [Fact]
    public void OpenFolder_WithoutLibrary_IsDisabled()
    {
        var vm = Vm(null, null).CreateSettingsViewModel();
        Assert.Null(vm.StoragePath);
        Assert.False(vm.OpenFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteAll_UsesLibraryDeleteAll_AndIsBlockedWhileDocumentOpen()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var main = Vm(null, lib);
        await main.ImportAsync(["a.png"]);
        await main.FlushSaveAsync();
        await main.NewDocumentAsync();
        Assert.Single(main.LibraryItems);

        await main.ImportAsync(["b.png"]);
        var vm = main.CreateSettingsViewModel();
        Assert.Same(main.DeleteAllWorksCommand, vm.DeleteAllCommand);
        Assert.False(vm.DeleteAllCommand.CanExecute(null));
        Assert.Equal(Strings.SettingsDeleteAllBlocked, vm.DeleteAllStatus);
        vm.Detach();

        await main.NewDocumentAsync();
        vm = main.CreateSettingsViewModel();
        Assert.Equal(2, main.LibraryItems.Count);
        Assert.True(vm.DeleteAllCommand.CanExecute(null));
        Assert.Equal(string.Format(Strings.SettingsWorkCount, 2), vm.DeleteAllStatus);

        await main.DeleteAllWorksAsync();
        Assert.Single(_dialogs.Confirms);
        Assert.Empty(main.LibraryItems);
        Assert.Empty(lib.List());
        Assert.Equal(Strings.SettingsNoWorks, vm.DeleteAllStatus);
        vm.Detach();
    }

    [Fact]
    public void Commands_OpenSettingsAndAbout()
    {
        SettingsViewModel? shown = null;
        _dialogs.OnShowSettings = s => shown = s;
        var main = Vm(null, null);
        main.OpenSettingsCommand.Execute(null);
        Assert.NotNull(shown);
        main.OpenAboutCommand.Execute(null);
        Assert.Equal(1, _dialogs.AboutShown);
    }

    [Fact]
    public void AboutInfo_HasVersionAndLicenses()
    {
        Assert.Equal("0.1.0", AboutInfo.Version);
        Assert.StartsWith("MIT License", AboutInfo.LicenseText);
        Assert.Contains("ATI Inc.", AboutInfo.LicenseText);
        var notices = AboutInfo.ThirdPartyNotices;
        Assert.Contains("PDFium", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZXing", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://fuseo.ati-mirai.co.jp/", AboutInfo.WebsiteAddress);
        Assert.Equal("https://github.com/supergodak/fuseo", AboutInfo.SourceCodeAddress);
    }

    [Fact]
    public void FolderLauncher_RejectsUrlsAndMissingFolders()
    {
        using var dir = new TempDir();
        Assert.True(FolderLauncher.IsAllowed(dir.Path));
        Assert.False(FolderLauncher.IsAllowed("https://fuseo.ati-mirai.co.jp/"));
        Assert.False(FolderLauncher.IsAllowed("file://C:/Windows"));
        Assert.False(FolderLauncher.IsAllowed(@"\\server\share"));
        Assert.False(FolderLauncher.IsAllowed("relative\\path"));
        Assert.False(FolderLauncher.IsAllowed(Path.Combine(dir.Path, "missing")));
        Assert.Throws<ArgumentException>(() => FolderLauncher.Open("https://example.com/"));
    }
}
