using System.Text.Json;
using System.Text.Json.Serialization;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Library;

// W5「自動保存ライブラリ」のデータモデル（docs/windows-design.md §3.6・スキーマ fuseo-win/1）。
// 項目名は Mac 版の WorkDocument（Sources/MaskingCore/Library/WorkDocument.swift）に合わせ、Windows で足す項目
// （origin・coordinateFrame・fromPdf・sourcePageIndex・rawDetections・rawDetector・checkedRuleIds）を加える。
// 保存されるのは**黒塗り前の画像**を含むデータ。このパソコンの中だけ・通信なし。
// ここは純粋なモデルと JSON だけ（ファイル I/O は Fuseo.App の FileWorkLibrary）。

/// <summary>作業の状態（JSON は camelCase: inProgress / exported）。</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<WorkStatus>))]
public enum WorkStatus
{
    /// <summary>編集中（まだ書き出していない、または書き出した後に編集した）。</summary>
    InProgress,
    /// <summary>書き出し済み（その後に編集したら InProgress に戻すのはアプリ層の責務）。</summary>
    Exported,
}

/// <summary>1 件の作業（= 開いていたページ一式）。<c>&lt;root&gt;/&lt;id&gt;/document.json</c> に保存する。</summary>
public sealed record WorkDocument
{
    /// <summary>保存形式の版。読み込み側は違う版を拒否する（「読めない作業」扱い）。</summary>
    public const string CurrentSchemaVersion = "fuseo-win/1";

    /// <summary>座標の原点（Mac 版は左下原点なので、将来 Mac 側で読むときの判別子として明示する）。</summary>
    public const string OriginTopLeft = "topLeft";

    [JsonRequired] public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonRequired] public string Origin { get; init; } = OriginTopLeft;
    [JsonRequired] public Guid Id { get; init; }
    /// <summary>一覧の表示名（最初のページの元ファイル名。複数ファイル由来なら「… ほか N 件」）。</summary>
    [JsonRequired] public string Title { get; init; } = "";
    [JsonRequired] public DateTimeOffset CreatedAt { get; init; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; init; }
    [JsonRequired] public WorkStatus Status { get; init; }
    /// <summary>直近に書き出したファイル名（拡張子込み・パスは持たない）。未書き出しは null。</summary>
    public string? LastExportedName { get; init; }
    [JsonRequired] public IReadOnlyList<WorkPage> Pages { get; init; } = [];
}

/// <summary>
/// 1 ページ分の作業状態。画像は JSON に埋めず、同じフォルダに PNG（元画像）と JPEG（サムネイル）で置く。
/// - <see cref="ImageFile"/> は**取り込んだままの元画像**（回転 0）。<see cref="PixelWidth"/>/<see cref="PixelHeight"/> はその寸法。
/// - 候補・手動マスクの座標は <see cref="ManualRotation"/> で回した画像の上の正規化座標・左上原点
///   （<see cref="CoordinateFrame"/> = "rotatedImage"）。
/// - <see cref="RawDetections"/> は検出の生の箱（padding 前・回転 0 = 元画像基準）。
/// </summary>
public sealed record WorkPage
{
    /// <summary>マスク座標の基準（<see cref="ManualRotation"/> で回した画像）。</summary>
    public const string CoordinateFrameRotatedImage = "rotatedImage";

    [JsonRequired] public int Index { get; init; }
    /// <summary>作業フォルダからの相対パス（'/' 区切り）。</summary>
    [JsonRequired] public string ImageFile { get; init; } = "";
    [JsonRequired] public string ThumbnailFile { get; init; } = "";
    /// <summary>取り込み元のファイル名（パスは持たない）。</summary>
    [JsonRequired] public string SourceName { get; init; } = "";
    [JsonRequired] public DocumentType DocumentType { get; init; }
    /// <summary>手動回転（時計回り 90° の回数・0..3。Mac 版と同じ単位）。</summary>
    [JsonRequired] public int ManualRotation { get; init; }
    [JsonRequired] public string CoordinateFrame { get; init; } = CoordinateFrameRotatedImage;
    [JsonRequired] public IReadOnlyList<MaskCandidate> Candidates { get; init; } = [];
    [JsonRequired] public ManualMask Manual { get; init; } = new();
    [JsonRequired] public int PixelWidth { get; init; }
    [JsonRequired] public int PixelHeight { get; init; }
    [JsonRequired] public bool FromPdf { get; init; }
    /// <summary>元ファイル内のページ番号（0 始まり。画像は 0）。</summary>
    [JsonRequired] public int SourcePageIndex { get; init; }
    [JsonRequired] public IReadOnlyList<NormRect> RawDetections { get; init; } = [];
    public DetectorId? RawDetector { get; init; }
    /// <summary>チェックリストで「塗った」と印を付けたルール id（順序は保存時に整列）。</summary>
    [JsonRequired] public IReadOnlyList<string> CheckedRuleIds { get; init; } = [];

    // ---- Mac 互換用（Windows では使わない・常に空で持つ） ----
    public IReadOnlyList<JsonElement> Ocr { get; init; } = [];
    public JsonElement? ManualQuad { get; init; }
    public JsonElement? AnalysisOptions { get; init; }
}

/// <summary>document.json の中身が受け付けられない（壊れている・版が違う・値が範囲外）。</summary>
public sealed class WorkDocumentFormatException(string message, Exception? inner = null) : Exception(message, inner);
