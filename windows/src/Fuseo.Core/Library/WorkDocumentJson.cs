using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Library;

/// <summary>
/// document.json（fuseo-win/1）の読み書き。**ゴールデンテストで固定**しているので、ここの変更はスキーマ変更と同義。
/// - 人が読めるインデント付き・UTF-8（日本語はエスケープしない）。項目の順はモデルの宣言順。
/// - 日時は ISO 8601・UTC・ミリ秒まで（例 2026-10-13T01:02:03.004Z）。
/// - 列挙は camelCase の文字列（未知の値・数値は拒否）。
/// - 読み込みは <see cref="Parse"/> だけ。版・原点・値の範囲・相対パスの形まで検査し、だめなら
///   <see cref="WorkDocumentFormatException"/>（呼び出し側はその作業を「読めない作業」として扱う）。
/// </summary>
public static class WorkDocumentJson
{
    /// <summary>1 ページの画素数の上限（取り込みの長辺 4000px を十分に超える値。壊れた JSON で巨大確保しないため）。</summary>
    public const int MaxPixelSide = 20000;

    /// <summary>正規化座標の許容誤差（回転の計算で出る -1e-17 のような値を範囲内とみなす）。</summary>
    private const double Epsilon = 1e-6;

    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        o.Converters.Add(new UtcDateConverter());
        o.Converters.Add(new NormRectConverter());
        o.Converters.Add(new NormPointConverter());
        o.Converters.Add(new QuadConverter());
        o.Converters.Add(new BrushStrokeConverter());
        o.Converters.Add(new ManualMaskConverter());
        o.Converters.Add(new MaskCandidateConverter());
        o.Converters.Add(new TextBoxConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>
    /// JSON 文字列にする（書く前にも <see cref="Validate"/> を通す＝壊れた物を保存しない）。
    /// 版は**中身から決める**（文字枠が 1 つでもあれば <see cref="WorkDocument.SchemaVersionWithTexts"/>、無ければ
    /// <see cref="WorkDocument.CurrentSchemaVersion"/>）。呼び出し側が版を間違えても文字入りを古い版の番号で書かない。
    /// </summary>
    public static string Serialize(WorkDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        Validate(doc);
        return JsonSerializer.Serialize(doc with { SchemaVersion = SchemaVersionFor(doc) }, Options);
    }

    /// <summary>書くときの版（文字枠があるときだけ 1 つ上げる・§11.3）。</summary>
    public static string SchemaVersionFor(WorkDocument doc) =>
        doc.Pages.Any(p => p.Texts.Count > 0) ? WorkDocument.SchemaVersionWithTexts : WorkDocument.CurrentSchemaVersion;

    public static byte[] SerializeToUtf8(WorkDocument doc) => Encoding.UTF8.GetBytes(Serialize(doc));

    public static WorkDocument Parse(string json)
    {
        WorkDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<WorkDocument>(json, Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException
                                       or FormatException or ArgumentException or OverflowException)
        {
            throw new WorkDocumentFormatException($"JSON として読めない: {e.Message}", e);
        }
        if (doc is null) throw new WorkDocumentFormatException("JSON が null");
        Validate(doc);
        return doc;
    }

    public static WorkDocument Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return Parse(new UTF8Encoding(false, true).GetString(utf8));
        }
        catch (DecoderFallbackException e)
        {
            throw new WorkDocumentFormatException("UTF-8 として読めない", e);
        }
    }

    /// <summary>中身の検査。版・原点・座標系・範囲・相対パスの形・必須値の null を確かめる。</summary>
    public static void Validate(WorkDocument doc)
    {
        Require(doc is not null, "document が null");
        Require(doc!.SchemaVersion is WorkDocument.CurrentSchemaVersion or WorkDocument.SchemaVersionWithTexts,
                $"版が違う: {doc.SchemaVersion}");
        Require(doc.Origin == WorkDocument.OriginTopLeft, $"原点が違う: {doc.Origin}");
        Require(doc.Id != Guid.Empty, "id が空");
        Require(doc.Title is not null, "title が null");
        Require(Enum.IsDefined(doc.Status), "status が不正");
        Require(doc.Pages is not null, "pages が null");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < doc.Pages!.Count; i++)
        {
            var p = doc.Pages[i];
            Require(p is not null, $"pages[{i}] が null");
            ValidatePage(p!, i);
            Require(files.Add(p!.ImageFile), $"pages[{i}].imageFile が重複");
            Require(files.Add(p.ThumbnailFile), $"pages[{i}].thumbnailFile が重複");
        }
    }

    private static void ValidatePage(WorkPage p, int i)
    {
        var at = $"pages[{i}]";
        Require(p.Index == i, $"{at}.index が並びと合わない: {p.Index}");
        Require(WorkPaths.IsSafeRelativePath(p.ImageFile), $"{at}.imageFile が作業フォルダ内の相対パスでない");
        Require(WorkPaths.IsSafeRelativePath(p.ThumbnailFile), $"{at}.thumbnailFile が作業フォルダ内の相対パスでない");
        Require(p.SourceName is not null, $"{at}.sourceName が null");
        Require(Enum.IsDefined(p.DocumentType), $"{at}.documentType が不正");
        Require(p.ManualRotation is >= 0 and <= 3, $"{at}.manualRotation が範囲外: {p.ManualRotation}");
        Require(p.CoordinateFrame == WorkPage.CoordinateFrameRotatedImage, $"{at}.coordinateFrame が違う: {p.CoordinateFrame}");
        Require(p.PixelWidth is > 0 and <= MaxPixelSide && p.PixelHeight is > 0 and <= MaxPixelSide,
                $"{at} の画素寸法が範囲外: {p.PixelWidth}x{p.PixelHeight}");
        Require(p.SourcePageIndex >= 0, $"{at}.sourcePageIndex が負");
        Require(p.RawDetector is null || Enum.IsDefined(p.RawDetector.Value), $"{at}.rawDetector が不正");

        Require(p.Candidates is not null, $"{at}.candidates が null");
        var ids = new HashSet<Guid>();
        foreach (var c in p.Candidates!)
        {
            Require(c is not null, $"{at}.candidates に null");
            Require(c!.Id != Guid.Empty && ids.Add(c.Id), $"{at}.candidates の id が空か重複");
            Require(c.RuleId is not null && c.Label is not null && c.Basis is not null, $"{at}.candidates の文字列が null");
            Require(IsValidRect(c.Box), $"{at}.candidates の box が範囲外");
            Require(Enum.IsDefined(c.Source), $"{at}.candidates の source が不正");
            Require(c.Confidence is null || (float.IsFinite(c.Confidence.Value) && c.Confidence is >= 0 and <= 1),
                    $"{at}.candidates の confidence が範囲外");
        }

        Require(p.Manual is not null && p.Manual.Rects is not null && p.Manual.Strokes is not null, $"{at}.manual が null");
        foreach (var r in p.Manual!.Rects) Require(IsValidRect(r), $"{at}.manual.rects が範囲外");
        foreach (var s in p.Manual.Strokes)
        {
            Require(s is not null && s.Points is not null && s.Points.Count > 0, $"{at}.manual.strokes が空");
            Require(double.IsFinite(s!.Width) && s.Width > 0 && s.Width <= 1, $"{at}.manual.strokes の width が範囲外");
            foreach (var pt in s.Points) Require(double.IsFinite(pt.X) && double.IsFinite(pt.Y), $"{at}.manual.strokes の点が不正");
        }

        Require(p.RawDetections is not null, $"{at}.rawDetections が null");
        foreach (var r in p.RawDetections!) Require(IsValidRect(r), $"{at}.rawDetections が範囲外");
        Require(p.CheckedRuleIds is not null && p.CheckedRuleIds.All(id => id is not null), $"{at}.checkedRuleIds が不正");
        Require(p.RawFaces is not null, $"{at}.rawFaces が null");
        foreach (var r in p.RawFaces!) Require(IsValidRect(r), $"{at}.rawFaces が範囲外");
        Require(p.SourceDocId != Guid.Empty, $"{at}.sourceDocId が空");
        Require(p.Ocr is not null, $"{at}.ocr が null");
        Require(p.Texts is not null, $"{at}.texts が null");
        foreach (var t in p.Texts!)
        {
            Require(t is not null, $"{at}.texts に null");
            Require(t!.Id != Guid.Empty, $"{at}.texts の id が空");
            Require(IsValidRect(t.Box), $"{at}.texts の box が範囲外");
            Require(t.Text is not null, $"{at}.texts の text が null");
        }
        if (p.ManualQuad is { } q)
            Require(q.Corners.All(c => c.X >= -Epsilon && c.X <= 1 + Epsilon && c.Y >= -Epsilon && c.Y <= 1 + Epsilon) && q.IsConvex,
                    $"{at}.manualQuad が範囲外か凸四角形でない");
    }

    private static bool IsValidRect(NormRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.W) && double.IsFinite(r.H)
        && r.W >= 0 && r.H >= 0
        && r.X >= -Epsilon && r.Y >= -Epsilon && r.Right <= 1 + Epsilon && r.Bottom <= 1 + Epsilon;

    private static void Require([DoesNotReturnIf(false)] bool ok, string message)
    {
        if (!ok) throw new WorkDocumentFormatException(message);
    }

    // ---- 変換器（Core の型にはスキーマ用の属性を付けず、ここで形を固定する） ----

    /// <summary>ISO 8601・UTC・ミリ秒（書き）。読みはオフセット付きの ISO 8601 を受けて UTC にする。</summary>
    private sealed class UtcDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            if (s is null || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v))
                throw new JsonException("日時が ISO 8601 でない");
            return v.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    /// <summary>{"x","y","w","h"}（Mac の NormRect と同じ名前。左上原点）。</summary>
    private sealed class NormRectConverter : JsonConverter<NormRect>
    {
        public override NormRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            double? x = null, y = null, w = null, h = null;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "x": x = reader.GetDouble(); break;
                    case "y": y = reader.GetDouble(); break;
                    case "w": w = reader.GetDouble(); break;
                    case "h": h = reader.GetDouble(); break;
                    default: reader.Skip(); break;
                }
            }
            if (x is null || y is null || w is null || h is null) throw new JsonException("矩形に x/y/w/h が揃っていない");
            return new NormRect(x.Value, y.Value, w.Value, h.Value);
        }

        public override void Write(Utf8JsonWriter writer, NormRect value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("w", value.W);
            writer.WriteNumber("h", value.H);
            writer.WriteEndObject();
        }
    }

    /// <summary>[x, y]（Mac の CGPoint の Codable 表現と同じ）。</summary>
    private sealed class NormPointConverter : JsonConverter<NormPoint>
    {
        public override NormPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("点は [x, y]");
            reader.Read();
            var x = reader.GetDouble();
            reader.Read();
            var y = reader.GetDouble();
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("点は [x, y]");
            return new NormPoint(x, y);
        }

        public override void Write(Utf8JsonWriter writer, NormPoint value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteEndArray();
        }
    }

    /// <summary>{"topLeft": [x,y], "topRight", "bottomRight", "bottomLeft"}（Mac の Quad と同じ名前。左上原点）。</summary>
    private sealed class QuadConverter : JsonConverter<Quad>
    {
        private static readonly string[] Names = ["topLeft", "topRight", "bottomRight", "bottomLeft"];

        public override Quad Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var pts = new NormPoint?[4];
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                var k = Array.IndexOf(Names, name);
                if (k >= 0) pts[k] = JsonSerializer.Deserialize<NormPoint>(ref reader, options);
                else reader.Skip();
            }
            if (pts.Any(p => p is null)) throw new JsonException("四隅が揃っていない");
            return new Quad(pts[0]!.Value, pts[1]!.Value, pts[2]!.Value, pts[3]!.Value);
        }

        public override void Write(Utf8JsonWriter writer, Quad value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            var c = value.Corners;
            for (var k = 0; k < 4; k++)
            {
                writer.WritePropertyName(Names[k]);
                JsonSerializer.Serialize(writer, c[k], options);
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>{"points": [[x,y],…], "width": w}。</summary>
    private sealed class BrushStrokeConverter : JsonConverter<BrushStroke>
    {
        public override BrushStroke Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<NormPoint>? points = null;
            double? width = null;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "points": points = JsonSerializer.Deserialize<List<NormPoint>>(ref reader, options); break;
                    case "width": width = reader.GetDouble(); break;
                    default: reader.Skip(); break;
                }
            }
            if (points is null || width is null) throw new JsonException("ストロークに points/width が無い");
            return new BrushStroke(points, width.Value);
        }

        public override void Write(Utf8JsonWriter writer, BrushStroke value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("points");
            JsonSerializer.Serialize(writer, value.Points, options);
            writer.WriteNumber("width", value.Width);
            writer.WriteEndObject();
        }
    }

    /// <summary>{"rects": […], "strokes": […]}（Mac の ManualMask と同じ名前）。</summary>
    private sealed class ManualMaskConverter : JsonConverter<ManualMask>
    {
        public override ManualMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<NormRect>? rects = null;
            List<BrushStroke>? strokes = null;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "rects": rects = JsonSerializer.Deserialize<List<NormRect>>(ref reader, options); break;
                    case "strokes": strokes = JsonSerializer.Deserialize<List<BrushStroke>>(ref reader, options); break;
                    default: reader.Skip(); break;
                }
            }
            if (rects is null || strokes is null) throw new JsonException("manual に rects/strokes が無い");
            return new ManualMask { Rects = rects, Strokes = strokes };
        }

        public override void Write(Utf8JsonWriter writer, ManualMask value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("rects");
            JsonSerializer.Serialize(writer, value.Rects, options);
            writer.WritePropertyName("strokes");
            JsonSerializer.Serialize(writer, value.Strokes, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Mac の MaskCandidate と同じ形: id・ruleID・label・box・source{kind, detector}・confidence・isOn・basis。
    /// id は保存・復元で保持する。
    /// </summary>
    private sealed class MaskCandidateConverter : JsonConverter<MaskCandidate>
    {
        public override MaskCandidate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Guid? id = null;
            string? ruleId = null, label = null, basis = null;
            NormRect? box = null;
            (CandidateSource Source, DetectorId? Detector)? source = null;
            float? confidence = null;
            var hasConfidence = false;
            bool? isOn = null;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "id": id = reader.GetGuid(); break;
                    case "ruleID": ruleId = reader.GetString(); break;
                    case "label": label = reader.GetString(); break;
                    case "basis": basis = reader.GetString(); break;
                    case "box": box = JsonSerializer.Deserialize<NormRect>(ref reader, options); break;
                    case "source": source = ReadSource(ref reader, options); break;
                    case "confidence":
                        hasConfidence = true;
                        confidence = reader.TokenType == JsonTokenType.Null ? null : reader.GetSingle();
                        break;
                    case "isOn": isOn = reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            if (id is null || ruleId is null || label is null || basis is null || box is null || source is null
                || isOn is null || !hasConfidence)
                throw new JsonException("候補の項目が揃っていない");
            return new MaskCandidate(id.Value, ruleId, label, box.Value, source.Value.Source, source.Value.Detector,
                                     confidence, isOn.Value, basis);
        }

        private static (CandidateSource, DetectorId?) ReadSource(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            string? kind = null;
            DetectorId? detector = null;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "kind": kind = reader.GetString(); break;
                    case "detector": detector = JsonSerializer.Deserialize<DetectorId>(ref reader, options); break;
                    default: reader.Skip(); break;
                }
            }
            return kind switch
            {
                "fixedRegion" when detector is null => (CandidateSource.FixedRegion, null),
                "detector" when detector is not null => (CandidateSource.Detector, detector),
                _ => throw new JsonException($"source が不正: {kind}"),
            };
        }

        public override void Write(Utf8JsonWriter writer, MaskCandidate value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteString("ruleID", value.RuleId);
            writer.WriteString("label", value.Label);
            writer.WritePropertyName("box");
            JsonSerializer.Serialize(writer, value.Box, options);
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            if (value.Source == CandidateSource.Detector && value.Detector is { } d)
            {
                writer.WriteString("kind", "detector");
                writer.WritePropertyName("detector");
                JsonSerializer.Serialize(writer, d, options);
            }
            else if (value.Source == CandidateSource.FixedRegion && value.Detector is null)
            {
                writer.WriteString("kind", "fixedRegion");
            }
            else
            {
                throw new JsonException("候補の source と detector が食い違っている");
            }
            writer.WriteEndObject();
            if (value.Confidence is { } c) writer.WriteNumber("confidence", c);
            else writer.WriteNull("confidence");
            writer.WriteBoolean("isOn", value.IsOn);
            writer.WriteString("basis", value.Basis);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// 文字枠（Mac の TextBox と同じ項目名）: id・box・text・fontSizePt・lineSpacing・letterSpacing・font・color{r,g,b}・hAlign・vAlign・cells。
    /// 読み込みは**欠けた設定を既定値で読む**（§11.3・Mac で試用中に踏んだ事故の対策）: 必須は id と box だけ。
    /// 知らない書体・揃えの名前は既定値、色は {r,g,b} のほか名前（"blue" / "black"・Mac の試用版の形）も読む。範囲外の値は丸める。
    /// </summary>
    private sealed class TextBoxConverter : JsonConverter<TextBox>
    {
        public override TextBox Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Guid? id = null;
            NormRect? box = null;
            string? text = null;
            var d = TextStyle.Default;
            var style = d;
            BeginObject(ref reader);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "id": id = reader.GetGuid(); break;
                    case "box": box = JsonSerializer.Deserialize<NormRect>(ref reader, options); break;
                    case "text": text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                    case "fontSizePt": style = style with { FontSizePt = NumberOr(ref reader, d.FontSizePt) }; break;
                    case "lineSpacing": style = style with { LineSpacing = NumberOr(ref reader, d.LineSpacing) }; break;
                    case "letterSpacing": style = style with { LetterSpacing = NumberOr(ref reader, d.LetterSpacing) }; break;
                    case "cells": style = style with { Cells = reader.TokenType == JsonTokenType.Null ? d.Cells : reader.GetInt32() }; break;
                    case "font": style = style with { Font = NameOr(ref reader, d.Font) }; break;
                    case "hAlign": style = style with { HAlign = NameOr(ref reader, d.HAlign) }; break;
                    case "vAlign": style = style with { VAlign = NameOr(ref reader, d.VAlign) }; break;
                    case "color": style = style with { Color = ReadColor(ref reader, d.Color) }; break;
                    default: reader.Skip(); break;
                }
            }
            if (id is null || box is null) throw new JsonException("文字枠に id/box が無い");
            return new TextBox(id.Value, box.Value, text ?? "", style);
        }

        private static double NumberOr(ref Utf8JsonReader reader, double fallback) =>
            reader.TokenType == JsonTokenType.Null ? fallback : reader.GetDouble();

        /// <summary>camelCase の名前 → 列挙。知らない名前・文字列以外は既定値（Mac の try? と同じ）。</summary>
        private static T NameOr<T>(ref Utf8JsonReader reader, T fallback) where T : struct, Enum
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                return fallback;
            }
            var s = reader.GetString();
            foreach (var v in Enum.GetValues<T>())
                if (string.Equals(JsonNamingPolicy.CamelCase.ConvertName(v.ToString()), s, StringComparison.Ordinal)) return v;
            return fallback;
        }

        private static TextColor ReadColor(ref Utf8JsonReader reader, TextColor fallback)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString() == "blue" ? TextColor.Blue : TextColor.Black;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return fallback;
            }
            double? r = null, g = null, b = null;
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "r": r = reader.GetDouble(); break;
                    case "g": g = reader.GetDouble(); break;
                    case "b": b = reader.GetDouble(); break;
                    default: reader.Skip(); break;
                }
            }
            return r is null || g is null || b is null ? fallback : new TextColor(r.Value, g.Value, b.Value);
        }

        public override void Write(Utf8JsonWriter writer, TextBox value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WritePropertyName("box");
            JsonSerializer.Serialize(writer, value.Box, options);
            writer.WriteString("text", value.Text);
            writer.WriteNumber("fontSizePt", value.FontSizePt);
            writer.WriteNumber("lineSpacing", value.LineSpacing);
            writer.WriteNumber("letterSpacing", value.LetterSpacing);
            writer.WriteString("font", JsonNamingPolicy.CamelCase.ConvertName(value.Font.ToString()));
            writer.WritePropertyName("color");
            writer.WriteStartObject();
            writer.WriteNumber("r", value.Color.R);
            writer.WriteNumber("g", value.Color.G);
            writer.WriteNumber("b", value.Color.B);
            writer.WriteEndObject();
            writer.WriteString("hAlign", JsonNamingPolicy.CamelCase.ConvertName(value.HAlign.ToString()));
            writer.WriteString("vAlign", JsonNamingPolicy.CamelCase.ConvertName(value.VAlign.ToString()));
            writer.WriteNumber("cells", value.Cells);
            writer.WriteEndObject();
        }
    }

    private static void BeginObject(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("オブジェクトが必要");
    }

    /// <summary>次のプロパティ名を読み、値の先頭トークンに reader を進める（値の読み取りは呼び出し側）。オブジェクトの終わりなら false。</summary>
    private static bool NextProperty(ref Utf8JsonReader reader, out string name)
    {
        if (!reader.Read()) throw new JsonException("JSON が途中で終わっている");
        if (reader.TokenType == JsonTokenType.EndObject) { name = ""; return false; }
        if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("プロパティ名が必要");
        name = reader.GetString()!;
        if (!reader.Read()) throw new JsonException("JSON が途中で終わっている");
        return true;
    }
}
