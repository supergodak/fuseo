using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuseo.Core.Model;

namespace Fuseo.App.Configuration;

/// <summary>表示言語の設定値（settings.json では "auto" / "ja" / "en"）。</summary>
public enum LanguageSetting { Auto, Japanese, English }

/// <summary>アプリの設定（W6）。ライブラリとは別ファイル <c>%LOCALAPPDATA%\Fuseo\settings.json</c>。</summary>
public sealed record AppSettings
{
    public LanguageSetting Language { get; init; } = LanguageSetting.Auto;

    /// <summary>透かしの配置と濃さ（0.4.0）。前に書き出したときのものを引き継ぐ。**文言は覚えない**（提出先の名前が入るため）。</summary>
    public WatermarkLayoutKind WatermarkLayout { get; init; } = WatermarkLayoutKind.Tiled;
    public double WatermarkOpacity { get; init; } = Watermark.DefaultOpacity;

    /// <summary>ファイルの大きさの上限（バイト・0.4.0）。null = 上限なし。</summary>
    public long? ExportSizeLimit { get; init; }

    /// <summary>選べる上限（1 MB = 1,000,000 バイト・Mac 版と同じ）。</summary>
    public static IReadOnlyList<long> SizeLimitChoices { get; } = [1_000_000, 2_000_000, 3_000_000, 5_000_000, 10_000_000];

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
        // 0.4.0 で追加（無ければ既定値。古い版が読んでも無視されるだけなので schemaVersion は上げない）
        [JsonPropertyName("watermarkLayout")] public string? WatermarkLayout { get; set; }
        [JsonPropertyName("watermarkOpacity")] public double? WatermarkOpacity { get; set; }
        [JsonPropertyName("exportSizeLimit")] public long? ExportSizeLimit { get; set; }
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return AppSettings.Default;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto is { SchemaVersion: SchemaVersion } && ParseLanguage(dto.Language) is { } lang)
                return new AppSettings
                {
                    Language = lang,
                    WatermarkLayout = dto.WatermarkLayout == "diagonal" ? WatermarkLayoutKind.Diagonal : WatermarkLayoutKind.Tiled,
                    WatermarkOpacity = new Watermark("x", Opacity: dto.WatermarkOpacity ?? Watermark.DefaultOpacity).ClampedOpacity,
                    ExportSizeLimit = dto.ExportSizeLimit is { } l && AppSettings.SizeLimitChoices.Contains(l) ? l : null,
                };
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
        var json = JsonSerializer.Serialize(new Dto
                                            {
                                                SchemaVersion = SchemaVersion, Language = FormatLanguage(settings.Language),
                                                WatermarkLayout = settings.WatermarkLayout == WatermarkLayoutKind.Diagonal ? "diagonal" : "tiled",
                                                WatermarkOpacity = settings.WatermarkOpacity,
                                                ExportSizeLimit = settings.ExportSizeLimit,
                                            },
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
