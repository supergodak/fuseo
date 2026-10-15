using System.Text.Json;
using Fuseo.Core.Geometry;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>document.json（fuseo-win/1）のスキーマ固定・往復・不正値の扱い（W5）。</summary>
public class WorkDocumentJsonTests
{
    private static readonly Guid DocId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PageId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    internal static WorkDocument Sample() => new()
    {
        Id = DocId,
        Title = "免許証.jpg ほか 1 件",
        CreatedAt = new DateTimeOffset(2026, 10, 13, 1, 2, 3, 4, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 10, 13, 10, 20, 30, 500, TimeSpan.FromHours(9)),
        Status = WorkStatus.Exported,
        LastExportedName = "提出用.pdf",
        Pages =
        [
            new WorkPage
            {
                Index = 0,
                ImageFile = WorkPaths.PageImage(PageId),
                ThumbnailFile = WorkPaths.Thumbnail(PageId),
                SourceName = "免許証.jpg",
                DocumentType = DocumentType.MyNumberCardBack,
                ManualRotation = 1,
                Candidates =
                [
                    new MaskCandidate(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "mynumB.qr", "QRコード",
                                      new NormRect(0.5, 0.25, 0.25, 0.125), CandidateSource.Detector, DetectorId.QrBarcode,
                                      null, true, "番号法"),
                    new MaskCandidate(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "x.fixed", "固定",
                                      new NormRect(0, 0, 1, 0.5), CandidateSource.FixedRegion, null, 0.75f, false, "根拠"),
                ],
                Manual = new ManualMask
                {
                    Rects = [new NormRect(0.125, 0.25, 0.5, 0.0625)],
                    Strokes = [new BrushStroke([new NormPoint(0.1, 0.2), new NormPoint(0.3, 0.4)], 0.03)],
                },
                PixelWidth = 400,
                PixelHeight = 300,
                FromPdf = true,
                SourcePageIndex = 2,
                RawDetections = [new NormRect(0.25, 0.5, 0.125, 0.25)],
                RawDetector = DetectorId.QrBarcode,
                CheckedRuleIds = ["mynumB.name"],
            },
        ],
    };

    private const string Golden = """
{
  "schemaVersion": "fuseo-win/1",
  "origin": "topLeft",
  "id": "11111111-2222-3333-4444-555555555555",
  "title": "免許証.jpg ほか 1 件",
  "createdAt": "2026-10-13T01:02:03.004Z",
  "updatedAt": "2026-10-13T01:20:30.500Z",
  "status": "exported",
  "lastExportedName": "提出用.pdf",
  "pages": [
    {
      "index": 0,
      "imageFile": "pages/aaaaaaaa-0000-0000-0000-000000000001.png",
      "thumbnailFile": "thumbs/aaaaaaaa-0000-0000-0000-000000000001.jpg",
      "sourceName": "免許証.jpg",
      "documentType": "myNumberCardBack",
      "manualRotation": 1,
      "coordinateFrame": "rotatedImage",
      "candidates": [
        {
          "id": "bbbbbbbb-0000-0000-0000-000000000001",
          "ruleID": "mynumB.qr",
          "label": "QRコード",
          "box": {
            "x": 0.5,
            "y": 0.25,
            "w": 0.25,
            "h": 0.125
          },
          "source": {
            "kind": "detector",
            "detector": "qrBarcode"
          },
          "confidence": null,
          "isOn": true,
          "basis": "番号法"
        },
        {
          "id": "bbbbbbbb-0000-0000-0000-000000000002",
          "ruleID": "x.fixed",
          "label": "固定",
          "box": {
            "x": 0,
            "y": 0,
            "w": 1,
            "h": 0.5
          },
          "source": {
            "kind": "fixedRegion"
          },
          "confidence": 0.75,
          "isOn": false,
          "basis": "根拠"
        }
      ],
      "manual": {
        "rects": [
          {
            "x": 0.125,
            "y": 0.25,
            "w": 0.5,
            "h": 0.0625
          }
        ],
        "strokes": [
          {
            "points": [
              [
                0.1,
                0.2
              ],
              [
                0.3,
                0.4
              ]
            ],
            "width": 0.03
          }
        ]
      },
      "pixelWidth": 400,
      "pixelHeight": 300,
      "fromPdf": true,
      "sourcePageIndex": 2,
      "rawDetections": [
        {
          "x": 0.25,
          "y": 0.5,
          "w": 0.125,
          "h": 0.25
        }
      ],
      "rawDetector": "qrBarcode",
      "checkedRuleIds": [
        "mynumB.name"
      ],
      "ocr": [],
      "manualQuad": null,
      "analysisOptions": null
    }
  ]
}
""";

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public void Golden_SchemaIsFixed()
    {
        var json = WorkDocumentJson.Serialize(Sample());
        Assert.Equal(Normalize(Golden), Normalize(json));
    }

    [Fact]
    public void RoundTrip_AllFieldsMatch()
    {
        var doc = Sample();
        var back = WorkDocumentJson.Parse(WorkDocumentJson.Serialize(doc));
        AssertEquivalent(doc, back);
        // 再度書いても同じ文字列（往復で何も落ちない・増えない）
        Assert.Equal(WorkDocumentJson.Serialize(doc), WorkDocumentJson.Serialize(back));
        Assert.Equal(TimeSpan.Zero, back.UpdatedAt.Offset);
    }

    [Fact]
    public void RoundTrip_Utf8Bytes()
    {
        var bytes = WorkDocumentJson.SerializeToUtf8(Sample());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM を付けない");
        AssertEquivalent(Sample(), WorkDocumentJson.Parse(bytes));
    }

    internal static void AssertEquivalent(WorkDocument a, WorkDocument b)
    {
        Assert.Equal(a.SchemaVersion, b.SchemaVersion);
        Assert.Equal(a.Origin, b.Origin);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.CreatedAt, b.CreatedAt);
        Assert.Equal(a.UpdatedAt, b.UpdatedAt);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.LastExportedName, b.LastExportedName);
        Assert.Equal(a.Pages.Count, b.Pages.Count);
        for (var i = 0; i < a.Pages.Count; i++)
        {
            var p = a.Pages[i];
            var q = b.Pages[i];
            Assert.Equal(p.Index, q.Index);
            Assert.Equal(p.ImageFile, q.ImageFile);
            Assert.Equal(p.ThumbnailFile, q.ThumbnailFile);
            Assert.Equal(p.SourceName, q.SourceName);
            Assert.Equal(p.DocumentType, q.DocumentType);
            Assert.Equal(p.ManualRotation, q.ManualRotation);
            Assert.Equal(p.CoordinateFrame, q.CoordinateFrame);
            Assert.Equal(p.Candidates, q.Candidates);
            Assert.Equal(p.Manual.Rects, q.Manual.Rects);
            Assert.Equal(p.Manual.Strokes.Count, q.Manual.Strokes.Count);
            for (var k = 0; k < p.Manual.Strokes.Count; k++)
            {
                Assert.Equal(p.Manual.Strokes[k].Points, q.Manual.Strokes[k].Points);
                Assert.Equal(p.Manual.Strokes[k].Width, q.Manual.Strokes[k].Width);
            }
            Assert.Equal(p.PixelWidth, q.PixelWidth);
            Assert.Equal(p.PixelHeight, q.PixelHeight);
            Assert.Equal(p.FromPdf, q.FromPdf);
            Assert.Equal(p.SourcePageIndex, q.SourcePageIndex);
            Assert.Equal(p.RawDetections, q.RawDetections);
            Assert.Equal(p.RawDetector, q.RawDetector);
            Assert.Equal(p.CheckedRuleIds, q.CheckedRuleIds);
            Assert.Empty(q.Ocr);
            Assert.Null(q.ManualQuad);
            Assert.Null(q.AnalysisOptions);
        }
    }

    // ---- 不正値 ----

    private static string Mutate(string from, string to)
    {
        var json = WorkDocumentJson.Serialize(Sample());
        Assert.Contains(from, json);
        return json.Replace(from, to);
    }

    [Theory]
    [InlineData("\"documentType\": \"myNumberCardBack\"", "\"documentType\": \"passport\"")]          // 未知の種別
    [InlineData("\"documentType\": \"myNumberCardBack\"", "\"documentType\": 6")]                    // 数値の列挙
    [InlineData("\"status\": \"exported\"", "\"status\": \"done\"")]                                  // 未知の状態
    [InlineData("\"detector\": \"qrBarcode\"", "\"detector\": \"faceX\"")]                            // 未知の検出器
    [InlineData("\"manualRotation\": 1", "\"manualRotation\": -1")]                                    // 負の回転
    [InlineData("\"manualRotation\": 1", "\"manualRotation\": 4")]                                     // 範囲外の回転
    [InlineData("\"manualRotation\": 1", "\"manualRotation\": 1.5")]                                   // 小数の回転
    [InlineData("\"schemaVersion\": \"fuseo-win/1\"", "\"schemaVersion\": \"fuseo-win/2\"")]          // 版違い
    [InlineData("\"schemaVersion\": \"fuseo-win/1\"", "\"schemaVersion\": 1")]                         // Mac 形式の版
    [InlineData("\"origin\": \"topLeft\"", "\"origin\": \"bottomLeft\"")]                              // 原点違い
    [InlineData("\"coordinateFrame\": \"rotatedImage\"", "\"coordinateFrame\": \"original\"")]         // 座標系違い
    [InlineData("\"pixelWidth\": 400", "\"pixelWidth\": 0")]
    [InlineData("\"pixelWidth\": 400", "\"pixelWidth\": 100000")]
    [InlineData("\"sourcePageIndex\": 2", "\"sourcePageIndex\": -1")]
    [InlineData("\"index\": 0", "\"index\": 3")]
    [InlineData("\"w\": 0.25,\n            \"h\": 0.125", "\"w\": 0.75,\n            \"h\": 0.125")]  // 1 を超える箱
    [InlineData("\"width\": 0.03", "\"width\": -0.03")]
    [InlineData("\"kind\": \"fixedRegion\"", "\"kind\": \"magic\"")]
    [InlineData("\"createdAt\": \"2026-10-13T01:02:03.004Z\"", "\"createdAt\": \"昨日\"")]
    [InlineData("\"id\": \"11111111-2222-3333-4444-555555555555\"", "\"id\": \"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"imageFile\": \"pages/", "\"imageFile\": \"../")]                                   // フォルダの外
    [InlineData("\"imageFile\": \"pages/", "\"imageFile\": \"C:/")]                                   // ドライブ指定
    [InlineData("\"thumbnailFile\": \"thumbs/", "\"thumbnailFile\": \"/thumbs/")]                     // 絶対パス
    [InlineData("\"pages\": [", "\"pages\": null, \"x\": [")]
    [InlineData("\"manual\": {", "\"manual\": null, \"m\": {")]
    [InlineData("\"title\": \"免許証.jpg ほか 1 件\"", "\"title\": null")]
    public void Parse_RejectsInvalidValues(string from, string to)
    {
        var json = Mutate(from.Replace("\n", Environment.NewLine), to.Replace("\n", Environment.NewLine));
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\": \"fuseo-win/1\"}")]   // 必須項目が無い
    [InlineData("\u0000\u0001garbage")]
    public void Parse_RejectsBrokenJson(string json) =>
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(json));

    [Fact]
    public void Parse_RejectsMissingRequiredPageField()
    {
        var json = Mutate("\"fromPdf\": true,", "");
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(json));
    }

    [Fact]
    public void Parse_RejectsInvalidUtf8() =>
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }));

    [Fact]
    public void Serialize_RejectsInvalidDocument()
    {
        var bad = Sample() with { Pages = [Sample().Pages[0] with { ManualRotation = 5 }] };
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Serialize(bad));
        var badPath = Sample() with { Pages = [Sample().Pages[0] with { ImageFile = "..\\x.png" }] };
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Serialize(badPath));
    }

    [Fact]
    public void Parse_AcceptsOffsetDates_AsUtc()
    {
        var json = Mutate("\"createdAt\": \"2026-10-13T01:02:03.004Z\"", "\"createdAt\": \"2026-10-13T10:02:03+09:00\"");
        var doc = WorkDocumentJson.Parse(json);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 1, 2, 3, TimeSpan.Zero), doc.CreatedAt);
    }

    [Fact]
    public void Parse_IgnoresUnknownProperties()
    {
        var json = Mutate("\"origin\": \"topLeft\",", "\"origin\": \"topLeft\", \"future\": {\"a\": [1, 2]},");
        AssertEquivalent(Sample(), WorkDocumentJson.Parse(json));
    }

    [Fact]
    public void EmptyPages_RoundTrip()
    {
        var doc = Sample() with { Pages = [], LastExportedName = null, Status = WorkStatus.InProgress };
        var back = WorkDocumentJson.Parse(WorkDocumentJson.Serialize(doc));
        AssertEquivalent(doc, back);
        Assert.Contains("\"lastExportedName\": null", WorkDocumentJson.Serialize(doc));
    }

    [Theory]
    [InlineData("pages/a.png", true)]
    [InlineData("thumbs/aaaaaaaa-0000-0000-0000-000000000001.jpg", true)]
    [InlineData("document.json", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../a.png", false)]
    [InlineData("pages/../../a.png", false)]
    [InlineData("pages/./a.png", false)]
    [InlineData("/pages/a.png", false)]
    [InlineData("pages//a.png", false)]
    [InlineData("pages\\a.png", false)]
    [InlineData("..\\a.png", false)]
    [InlineData("C:/a.png", false)]
    [InlineData("C:a.png", false)]
    [InlineData("\\\\server\\share\\a.png", false)]
    [InlineData("pages/a.png:secret", false)]
    [InlineData("pages/CON", false)]
    [InlineData("pages/nul.png", false)]
    [InlineData("pages/a.", false)]
    [InlineData("pages/日本語.png", false)]
    [InlineData("pages/a b.png", false)]
    public void SafeRelativePath(string? path, bool ok) => Assert.Equal(ok, WorkPaths.IsSafeRelativePath(path));

    [Fact]
    public void PageIdFromImageFile_ParsesCanonicalOnly()
    {
        Assert.Equal(PageId, WorkPaths.PageIdFromImageFile(WorkPaths.PageImage(PageId)));
        Assert.Null(WorkPaths.PageIdFromImageFile("pages/000.png"));
        Assert.Null(WorkPaths.PageIdFromImageFile("thumbs/aaaaaaaa-0000-0000-0000-000000000001.png"));
    }

    [Fact]
    public void ManualQuad_RoundTrips_AsMacShapedObject()
    {
        var quad = new Quad(new(0.125, 0.0625), new(0.875, 0.125), new(0.75, 0.9375), new(0.0625, 0.875));
        var doc = Sample() with { Pages = [Sample().Pages[0] with { ManualQuad = quad }] };
        var json = WorkDocumentJson.Serialize(doc);
        using (var d = JsonDocument.Parse(json))
        {
            var q = d.RootElement.GetProperty("pages")[0].GetProperty("manualQuad");
            Assert.Equal([0.125, 0.0625], q.GetProperty("topLeft").EnumerateArray().Select(e => e.GetDouble()).ToArray());
            Assert.Equal(0.9375, q.GetProperty("bottomRight")[1].GetDouble());
        }
        Assert.Equal(quad, WorkDocumentJson.Parse(json).Pages[0].ManualQuad);
    }

    [Theory]
    [InlineData("[1.5, 0.125]")]   // 範囲外
    [InlineData("[0.75, 0.9375]")] // TR と BR が同じ点（つぶれ）
    public void ManualQuad_RejectsOutOfRangeOrNonConvex(string topRight)
    {
        var quad = new Quad(new(0.125, 0.0625), new(0.875, 0.125), new(0.75, 0.9375), new(0.0625, 0.875));
        var json = WorkDocumentJson.Serialize(Sample() with { Pages = [Sample().Pages[0] with { ManualQuad = quad }] });
        var bad = System.Text.RegularExpressions.Regex.Replace(json, "\"topRight\": \\[[^\\]]*\\]", "\"topRight\": " + topRight);
        Assert.NotEqual(json, bad);
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(bad));
        // 交差した四隅は書く前にも止める
        var crossed = new Quad(quad.TopLeft, quad.BottomRight, quad.TopRight, quad.BottomLeft);
        Assert.Throws<WorkDocumentFormatException>(() =>
            WorkDocumentJson.Serialize(Sample() with { Pages = [Sample().Pages[0] with { ManualQuad = crossed }] }));
    }

    [Fact]
    public void Options_AreReadOnly() => Assert.True(WorkDocumentJson.Options.IsReadOnly);

    [Fact]
    public void JsonElementCompat_FieldsStayEmpty()
    {
        using var d = JsonDocument.Parse(WorkDocumentJson.Serialize(Sample()));
        var page = d.RootElement.GetProperty("pages")[0];
        Assert.Equal(JsonValueKind.Array, page.GetProperty("ocr").ValueKind);
        Assert.Equal(0, page.GetProperty("ocr").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("manualQuad").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("analysisOptions").ValueKind);
    }
}
