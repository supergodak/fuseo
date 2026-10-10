using System.Text.Json.Serialization;
using Fuseo.Core.Geometry;

namespace Fuseo.Core.Model;

/// <summary>
/// プリセット（バンドル JSON・schema v1）。Mac 版 docs/core-design.md §2.2 と同じスキーマを読む。
/// v0.1 の Windows 版は種別ピッカー・注意書き・「普通伏せる欄」チェックリストに使い、
/// 固定領域（region / fallbackRegion）は候補にしない（切り抜きが無いので位置が合わない・§3.5）。
/// </summary>
public sealed record DocumentPreset
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("documentType")] public DocumentType DocumentType { get; init; }
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("displayNameEn")] public string? DisplayNameEn { get; init; }
    [JsonPropertyName("enhance")] public bool Enhance { get; init; }
    [JsonPropertyName("classification")] public ClassificationSpec Classification { get; init; } = new();
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonPropertyName("warningsEn")] public IReadOnlyList<string>? WarningsEn { get; init; }
    [JsonPropertyName("rules")] public IReadOnlyList<MaskRule> Rules { get; init; } = [];

    public string Name(bool english) => english && DisplayNameEn is { Length: > 0 } en ? en : DisplayName;
    public IReadOnlyList<string> LocalizedWarnings(bool english) =>
        english && WarningsEn is { Count: > 0 } en ? en : Warnings;
}

public sealed record ClassificationSpec
{
    [JsonPropertyName("keywords")] public IReadOnlyList<WeightedKeyword> Keywords { get; init; } = [];
}

public sealed record WeightedKeyword(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("weight")] int Weight);

public sealed record MaskRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("labelEn")] public string? LabelEn { get; init; }
    [JsonPropertyName("kind")] public RuleKind Kind { get; init; }
    [JsonPropertyName("detector")] public DetectorId? Detector { get; init; }
    [JsonPropertyName("region")] public TopOriginRegion? Region { get; init; }
    [JsonPropertyName("fallbackRegion")] public TopOriginRegion? FallbackRegion { get; init; }
    [JsonPropertyName("defaultOn")] public bool DefaultOn { get; init; }
    [JsonPropertyName("padding")] public double? Padding { get; init; }
    [JsonPropertyName("basis")] public string Basis { get; init; } = "";
    [JsonPropertyName("basisEn")] public string? BasisEn { get; init; }

    /// <summary>マスク矩形の外周余白（正規化・既定 0.01。Mac 版と同じ）。</summary>
    public double EffectivePadding => Padding ?? 0.01;

    public string LocalizedLabel(bool english) => english && LabelEn is { Length: > 0 } en ? en : Label;
    public string LocalizedBasis(bool english) => english && BasisEn is { Length: > 0 } en ? en : Basis;
}

/// <summary>
/// JSON 上の固定領域 {x, yTop, w, h}（左上原点・正規化）。Windows 版の内部座標も左上原点なので
/// **変換なしで** <see cref="NormRect"/> になる（Mac 版はここで上下反転していた）。
/// </summary>
public sealed record TopOriginRegion(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("yTop")] double YTop,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H)
{
    public NormRect ToNormRect() => new(X, YTop, W, H);
}
