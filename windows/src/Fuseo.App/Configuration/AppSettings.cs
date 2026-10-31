using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuseo.App.Configuration;

/// <summary>表示言語の設定値（settings.json では "auto" / "ja" / "en"）。</summary>
public enum LanguageSetting { Auto, Japanese, English }

/// <summary>アプリの設定（W6）。ライブラリとは別ファイル <c>%LOCALAPPDATA%\Fuseo\settings.json</c>。</summary>
public sealed record AppSettings
{
    public LanguageSetting Language { get; init; } = LanguageSetting.Auto;

    public static AppSettings Default { get; } = new();
}

/// <summary>起動時の表示言語を決める（切り替えは再起動後に反映＝起動時に 1 回だけ呼ぶ）。</summary>
public static class UiLanguage
{
    /// <summary>
    /// - 日本語 → ja-JP。
    /// - English → OS の UI 言語が英語ならそのカルチャ（日付の書式を地域に合わせる）、それ以外は en-US。
    /// - 自動 → OS の UI 言語が日本語なら日本語、それ以外は English と同じ。
    /// </summary>
    public static CultureInfo Resolve(LanguageSetting setting, CultureInfo osUiCulture)
    {
        var japanese = setting switch
        {
            LanguageSetting.Japanese => true,
            LanguageSetting.English => false,
            _ => osUiCulture.TwoLetterISOLanguageName == "ja",
        };
        if (japanese) return CultureInfo.GetCultureInfo("ja-JP");
        return osUiCulture.TwoLetterISOLanguageName == "en" && !osUiCulture.IsNeutralCulture
            ? osUiCulture
            : CultureInfo.GetCultureInfo("en-US");
    }
}

/// <summary>
/// settings.json の読み書き。壊れている・読めないときは既定値で起動し、既定値で上書きする。
/// 保存は一時ファイル → File.Move（上書き）で原子的に置き換える。
/// </summary>
public sealed class SettingsStore(string? path = null)
{
    public const string FileName = "settings.json";
    private const int SchemaVersion = 1;

    /// <summary>既定の保存先 <c>%LOCALAPPDATA%\Fuseo\settings.json</c>（<see cref="AppDataLocation"/>・FUSEO_DATA_DIR があればその下）。</summary>
    public static string DefaultPath => Path.Combine(AppDataLocation.Root, FileName);

    public string FilePath { get; } = Path.GetFullPath(path ?? DefaultPath);

    private sealed class Dto
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return AppSettings.Default;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto is { SchemaVersion: SchemaVersion } && ParseLanguage(dto.Language) is { } lang)
                return new AppSettings { Language = lang };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 下で既定値に戻す
        }
        // 壊れている（または知らない形式）: 既定値で上書きする。書けなくても起動は続ける。
        TrySave(AppSettings.Default);
        return AppSettings.Default;
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Dto { SchemaVersion = SchemaVersion, Language = FormatLanguage(settings.Language) },
                                            new JsonSerializerOptions { WriteIndented = true });
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    private void TrySave(AppSettings settings)
    {
        try { Save(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static LanguageSetting? ParseLanguage(string? value) => value switch
    {
        "auto" => LanguageSetting.Auto,
        "ja" => LanguageSetting.Japanese,
        "en" => LanguageSetting.English,
        _ => null,
    };

    private static string FormatLanguage(LanguageSetting value) => value switch
    {
        LanguageSetting.Japanese => "ja",
        LanguageSetting.English => "en",
        _ => "auto",
    };
}
