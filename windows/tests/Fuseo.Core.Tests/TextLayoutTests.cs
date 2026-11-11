using System.Text.Json;
using Fuseo.Core.Geometry;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.Core.Tests;

/// <summary>
/// 決め打ちの書体の寸法（§11.1: Core のテストはフェイクで行う）。ascent = 0.8×サイズ・descent = 0.2×サイズ、
/// 送り幅は ASCII が 0.5×サイズ・それ以外（全角）が 1.0×サイズ。明朝は ascent だけ 0.85 にして書体の違いを確かめられるようにする。
/// </summary>
internal sealed class FakeTextMetrics : ITextMetrics
{
    public double Ascent(TextFont font, double sizePx) => (font == TextFont.Mincho ? 0.85 : 0.8) * sizePx;
    public double Descent(TextFont font, double sizePx) => 0.2 * sizePx;

    public double Advance(string text, TextFont font, double sizePx) =>
        text.Sum(c => c < 0x80 ? 0.5 : 1.0) * sizePx;
}

/// <summary>
/// 文字入れ W10-1（docs/windows-design.md §11・Mac の TextBoxTests の配置・保存の項目を移したもの）。
/// ①配置（揃え・行間・字間・マス目・はみ出し・枠に合わせる）②設定の丸め・色 ③保存形式（文字が無ければ版はそのまま・
/// あれば 1 つ上げる・往復・欠けた設定の読み込み）。座標は左上原点。
/// </summary>
public class TextLayoutTests
{
    /// <summary>A4 を 300dpi で描いた大きさ（PDF 取り込みと同じ）。</summary>
    private const double W = 2480, H = 3508;

    private static readonly FakeTextMetrics M = new();

    private static TextBox Box(string text, NormRect r, TextStyle? style = null) => new(Guid.NewGuid(), r, text, style);

    private static PxRect Frame(NormRect r) => TextLayout.FramePx(r, W, H);

    // ---- ① 配置 ----

    [Fact]
    public void Layout_LeftTop_FirstBaselineIsTopPlusAscent()
    {
        var b = Box("見本", new NormRect(0.1, 0.5, 0.4, 0.05), new TextStyle { HAlign = TextHAlign.Left, VAlign = TextVAlign.Top });
        var frame = Frame(b.Box);
        var l = TextLayout.Layout(b, W, H, M);
        Assert.Equal(frame.X, l.Lines[0].X, 6);
        Assert.Equal(frame.Y + 0.8 * b.FontSizePx, l.Lines[0].BaselineY, 6);   // 左上原点: 上端から ascent 下がった所
        Assert.Equal(2 * b.FontSizePx, l.Lines[0].Width, 6);
    }

    [Fact]
    public void Layout_FontSize_IsPtAt300dpi()
    {
        var b = Box("a", new NormRect(0, 0, 1, 1), new TextStyle { FontSizePt = 12 });
        Assert.Equal(50, b.FontSizePx, 9);   // 12pt × 300 / 72
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void Layout_LineSpacing_SetsDistanceBetweenBaselines(double ls)
    {
        var b = Box("一行目\n二行目\n三行目", new NormRect(0.1, 0.3, 0.5, 0.2), new TextStyle { FontSizePt = 12, LineSpacing = ls });
        var l = TextLayout.Layout(b, W, H, M);
        Assert.Equal(3, l.Lines.Count);
        var expected = 12 * 300.0 / 72 * ls;
        Assert.Equal(expected, l.Lines[1].BaselineY - l.Lines[0].BaselineY, 6);   // 下の行ほど y が大きい（左上原点）
        Assert.Equal(expected, l.Lines[2].BaselineY - l.Lines[1].BaselineY, 6);
    }

    [Fact]
    public void Layout_HAlign_CenterAndRight_PerLine()
    {
        var r = new NormRect(0.2, 0.4, 0.5, 0.05);
        var frame = Frame(r);
        var c = TextLayout.Layout(Box("中央", r, new TextStyle { HAlign = TextHAlign.Center }), W, H, M).Lines[0];
        Assert.Equal(frame.MidX, c.X + c.Width / 2, 6);
        var rt = TextLayout.Layout(Box("右\n右右右", r, new TextStyle { HAlign = TextHAlign.Right }), W, H, M);
        Assert.All(rt.Lines, line => Assert.Equal(frame.Right, line.X + line.Width, 6));
    }

    [Fact]
    public void Layout_VAlignCenter_CentersTheBlock()
    {
        var r = new NormRect(0.1, 0.4, 0.6, 0.1);
        var frame = Frame(r);
        var l = TextLayout.Layout(Box("上\n下", r, new TextStyle { VAlign = TextVAlign.Center, LineSpacing = 1.6 }), W, H, M);
        Assert.Equal(frame.MidY, l.InkBounds.MidY, 6);
    }

    [Fact]
    public void Layout_VAlignBottom_PutsBlockBottomOnFrameBottom()
    {
        var r = new NormRect(0.1, 0.4, 0.6, 0.1);
        var frame = Frame(r);
        var l = TextLayout.Layout(Box("上\n下", r, new TextStyle { VAlign = TextVAlign.Bottom }), W, H, M);
        Assert.Equal(frame.Bottom, l.InkBounds.Bottom, 6);
        Assert.True(l.Lines[0].BaselineY < l.Lines[1].BaselineY);
    }

    [Fact]
    public void Layout_Cells_EachCharacterAtCellCenter_AndOverflowWhenTooMany()
    {
        var r = new NormRect(0.1, 0.6, 0.6, 0.02);
        var frame = Frame(r);
        var style = new TextStyle { Cells = 12, FontSizePt = 10 };
        var l = TextLayout.Layout(Box("123456789012", r, style), W, H, M);
        Assert.Equal(12, l.Lines.Count);
        var cellW = frame.W / 12;
        for (var i = 0; i < l.Lines.Count; i++)
            Assert.Equal(frame.X + cellW * (i + 0.5), l.Lines[i].X + l.Lines[i].Width / 2, 6);
        Assert.False(l.Overflows);
        var over = TextLayout.Layout(Box("1234567890123", r, style), W, H, M);
        Assert.True(over.Overflows, "マス数を超えたらはみ出し");
        Assert.Equal(12, over.Lines.Count);   // マス数を超えた文字は描かない
    }

    [Fact]
    public void Layout_Cells_UseOnlyFirstLine_AndIgnoreLetterSpacing()
    {
        var r = new NormRect(0.05, 0.2, 0.9, 0.1);
        var plain = TextLayout.Layout(Box("1234\n5678", r, new TextStyle { Cells = 4, FontSizePt = 20 }), W, H, M);
        var spaced = TextLayout.Layout(Box("1234\n5678", r, new TextStyle { Cells = 4, FontSizePt = 20, LetterSpacing = 1.5 }), W, H, M);
        Assert.Equal(["1", "2", "3", "4"], plain.Lines.Select(l => l.Text));
        Assert.Equal(plain.Lines, spaced.Lines);   // マス目では字間を使わない（位置が同じ）
        Assert.Equal(0, spaced.Kern);
    }

    [Fact]
    public void Layout_Cells_CountGraphemeClusters()
    {
        // 結合文字（か＋濁点）・サロゲートペアは 1 文字＝1 マス（Mac の Character と同じ単位）。
        var l = TextLayout.Layout(Box("が𠮷A", new NormRect(0, 0, 0.5, 0.05), new TextStyle { Cells = 3 }), W, H, M);
        Assert.Equal(["が", "𠮷", "A"], l.Lines.Select(x => x.Text));
        Assert.False(l.Overflows);
    }

    [Fact]
    public void Layout_Overflow_WhenWiderOrTallerThanFrame()
    {
        Assert.True(TextLayout.Layout(Box("とても長い文字列です", new NormRect(0.1, 0.5, 0.05, 0.01)), W, H, M).Overflows);
        var wide = new NormRect(0.1, 0.5, 0.8, 0.05);
        Assert.False(TextLayout.Layout(Box("短い", wide, new TextStyle { FontSizePt = 10 }), W, H, M).Overflows);
        Assert.True(TextLayout.Layout(Box("1\n2\n3\n4\n5", wide, new TextStyle { FontSizePt = 10 }), W, H, M).Overflows,
                    "行が多くて高さを超えたらはみ出し");
    }

    [Fact]
    public void Layout_EmptyText_HasNoInk_AndDoesNotCountAsOverflow()
    {
        var r = new NormRect(0.1, 0.1, 0.0001, 0.0001);
        var b = Box("", r, new TextStyle { FontSizePt = 72 });
        var l = TextLayout.Layout(b, W, H, M);
        Assert.Equal(0, l.InkBounds.W);
        Assert.False(TextLayout.Overflows(b, W, H, M), "空の枠は描かないのではみ出しに数えない");
    }

    [Fact]
    public void Layout_CrLf_IsOneLineBreak()
    {
        var l = TextLayout.Layout(Box("一\r\n二\r三", new NormRect(0, 0, 1, 0.5)), W, H, M);
        Assert.Equal(["一", "二", "三"], l.Lines.Select(x => x.Text));
    }

    [Fact]
    public void FittingFontSize_FillsAbout90PercentOfHeight()
    {
        foreach (var (text, ls) in new[] { ("一行", 1.2), ("一行目\n二行目", 1.5) })
        {
            var b = Box(text, new NormRect(0.1, 0.5, 0.8, 0.03), new TextStyle { LineSpacing = ls });
            var size = TextLayout.FittingFontSizePt(b, W, H, M);
            b = b.WithStyle(b.Style with { FontSizePt = size });
            var frameH = Frame(b.Box).H;
            var l = TextLayout.Layout(b, W, H, M);
            Assert.True(l.InkBounds.H <= frameH, $"{l.InkBounds.H} <= {frameH}");
            Assert.True(l.InkBounds.H > frameH * 0.85, $"{l.InkBounds.H} > {frameH * 0.85}");
            Assert.Equal(0, size % 0.5, 9);
        }
    }

    [Fact]
    public void FittingFontSize_CellsCountAsOneLine_AndClampsToRange()
    {
        var cells = Box("1\n2\n3", new NormRect(0.1, 0.5, 0.8, 0.03), new TextStyle { Cells = 3 });
        var oneLine = Box("123", new NormRect(0.1, 0.5, 0.8, 0.03));
        Assert.Equal(TextLayout.FittingFontSizePt(oneLine, W, H, M), TextLayout.FittingFontSizePt(cells, W, H, M));
        Assert.Equal(TextBox.FontSizeMax, TextLayout.FittingFontSizePt(Box("a", new NormRect(0, 0, 1, 1)), W, H, M));
        Assert.Equal(TextBox.FontSizeMin, TextLayout.FittingFontSizePt(Box("a", new NormRect(0, 0, 1, 0.0001)), W, H, M));
    }

    // ---- 字間 ----

    [Fact]
    public void LetterSpacing_WidensLineByGapsOnly_AndAlignsWithoutTrailingGap()
    {
        var r = new NormRect(0.1, 0.4, 0.8, 0.05);
        var frame = Frame(r);
        var plain = TextLayout.Layout(Box("ABCDE", r, new TextStyle { FontSizePt = 12 }), W, H, M).Lines[0];
        var spaced = TextLayout.Layout(Box("ABCDE", r, new TextStyle { FontSizePt = 12, LetterSpacing = 0.5 }), W, H, M);
        var kern = 12 * 300.0 / 72 * 0.5;
        Assert.Equal(kern, spaced.Kern, 9);
        Assert.Equal(kern * 4, spaced.Lines[0].Width - plain.Width, 6);   // 5 文字の間は 4 か所（最後の文字の後ろは数えない）
        var right = TextLayout.Layout(Box("ABCDE", r, new TextStyle { FontSizePt = 12, LetterSpacing = 0.5, HAlign = TextHAlign.Right }),
                                      W, H, M).Lines[0];
        Assert.Equal(frame.Right, right.X + right.Width, 6);
        var tight = TextLayout.Layout(Box("ABCDE", r, new TextStyle { FontSizePt = 12, LetterSpacing = -0.1 }), W, H, M).Lines[0];
        Assert.True(tight.Width < plain.Width);
    }

    [Fact]
    public void LetterSpacing_OverflowUsesSpacedWidth()
    {
        var r = new NormRect(0.1, 0.4, 0.06, 0.05);   // 148.8px: 2 文字 100px は入り、字間 2.0 で 200px は出る
        Assert.False(TextLayout.Layout(Box("見本", r, new TextStyle { FontSizePt = 12 }), W, H, M).Overflows);
        Assert.True(TextLayout.Layout(Box("見本", r, new TextStyle { FontSizePt = 12, LetterSpacing = 2.0 }), W, H, M).Overflows);
    }

    [Fact]
    public void ElementOffsets_MatchLineWidth()
    {
        const double size = 50, kern = 10;
        var offsets = TextLayout.ElementOffsets("AB見", TextFont.Gothic, size, kern, M);
        Assert.Equal([0.0, 35.0, 70.0], offsets.Select(o => o.X));   // A=25+10, B=25+10
        var width = TextLayout.LineWidth("AB見", TextFont.Gothic, size, kern, M);
        Assert.Equal(offsets[^1].X + M.Advance("見", TextFont.Gothic, size), width, 9);   // 最後の文字の右端 = 幅
        Assert.Equal(0, TextLayout.LineWidth("", TextFont.Gothic, size, kern, M));
    }

    // ---- ② 設定の丸め・色 ----

    [Fact]
    public void Style_Clamped_SnapsToRangeAndStep()
    {
        var b = Box("", new NormRect(0, 0, 0.1, 0.1));
        b = b.WithStyle(new TextStyle { FontSizePt = 200, LineSpacing = 1.234, Cells = 99 });
        Assert.Equal(72, b.FontSizePt);
        Assert.Equal(1.25, b.LineSpacing, 9);
        Assert.Equal(TextBox.MaxCells, b.Cells);
        b = b.WithStyle(new TextStyle { FontSizePt = 10.3, LineSpacing = 0.1, LetterSpacing = 9 });
        Assert.Equal(10.5, b.FontSizePt);
        Assert.Equal(0.8, b.LineSpacing, 9);
        Assert.Equal(2.0, b.LetterSpacing, 9);
        b = b.WithStyle(new TextStyle { LetterSpacing = 0.333, Cells = -3 });
        Assert.Equal(0.35, b.LetterSpacing, 9);
        Assert.Equal(0, b.Cells);
        // 0.5 刻みの中間は 0 から遠い方へ（Swift の rounded() と同じ。銀行丸めにしない）
        Assert.Equal(10.5, TextBox.Snap(10.25, 4, 72, 0.5));
        Assert.Equal(1.2, TextBox.Snap(1.2000000000000002, 0.8, 3, 0.05));   // 端数を落とす
        Assert.Equal(4, TextBox.Snap(double.NaN, 4, 72, 0.5));
    }

    [Fact]
    public void Constructor_ClampsStyle()
    {
        var b = new TextBox(Guid.NewGuid(), new NormRect(0, 0, 1, 1), "x", new TextStyle { FontSizePt = 1, LineSpacing = 9 });
        Assert.Equal(TextBox.FontSizeMin, b.FontSizePt);
        Assert.Equal(TextBox.LineSpacingMax, b.LineSpacing);
        Assert.Equal(TextStyle.Default, new TextBox(Guid.NewGuid(), new NormRect(0, 0, 1, 1)).Style);
    }

    [Fact]
    public void Color_PaletteMatchesMac_AndCustomIsQuantized()
    {
        Assert.Equal(6, TextColor.Palette.Count);
        Assert.Equal(["Black", "Blue", "Red", "Green", "Brown", "Gray"], TextColor.Palette.Select(p => p.Key));
        Assert.Equal(TextColor.Black, TextColor.Palette[0].Color);
        Assert.Equal((13, 51, 158), Rgb(TextColor.Blue));
        Assert.Equal((200, 28, 28), Rgb(TextColor.Red));
        Assert.Equal((20, 120, 50), Rgb(TextColor.Green));
        Assert.Equal((115, 65, 25), Rgb(TextColor.Brown));
        Assert.Equal((110, 110, 110), Rgb(TextColor.Gray));
        var custom = new TextColor(0.5, 0.123456, 2);
        Assert.Equal(1, custom.B);   // 範囲外は丸める
        Assert.Equal(Math.Round(0.123456 * 255, MidpointRounding.AwayFromZero), custom.G * 255, 9);   // 1/255 単位
        Assert.Equal(custom, TextColor.FromBytes(custom.R8, custom.G8, custom.B8));
        Assert.Equal(0, new TextColor(double.NaN, 0, 0).R);
    }

    private static (int, int, int) Rgb(TextColor c) => (c.R8, c.G8, c.B8);

    // ---- ③ 保存形式 ----

    private static WorkDocument DocWithTexts(params TextBox[] texts) =>
        WorkDocumentJsonTests.Sample() with { Pages = [WorkDocumentJsonTests.Sample().Pages[0] with { Texts = texts }] };

    private static TextBox FullText() => new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001"), new NormRect(0.125, 0.25, 0.5, 0.0625), "見本\n二行目",
        new TextStyle
        {
            FontSizePt = 11.5, LineSpacing = 1.45, LetterSpacing = 0.25, Font = TextFont.Mincho, Color = TextColor.Blue,
            HAlign = TextHAlign.Right, VAlign = TextVAlign.Bottom, Cells = 0,
        });

    [Fact]
    public void Schema_WithoutTexts_StaysV1_AndHasNoTextsKey()
    {
        var json = WorkDocumentJson.Serialize(WorkDocumentJsonTests.Sample());
        Assert.Contains("\"schemaVersion\": \"fuseo-win/1\"", json);
        Assert.DoesNotContain("\"texts\"", json);
    }

    [Fact]
    public void Schema_WithTexts_IsV2_AndRoundTrips()
    {
        var doc = DocWithTexts(FullText(), new TextBox(Guid.NewGuid(), new NormRect(0, 0, 0.5, 0.1), "123", new TextStyle { Cells = 12 }));
        var json = WorkDocumentJson.Serialize(doc);
        Assert.Contains("\"schemaVersion\": \"fuseo-win/2\"", json);
        var back = WorkDocumentJson.Parse(json);
        Assert.Equal(WorkDocument.SchemaVersionWithTexts, back.SchemaVersion);
        Assert.Equal(doc.Pages[0].Texts, back.Pages[0].Texts);
        Assert.Equal(json, WorkDocumentJson.Serialize(back));
    }

    [Fact]
    public void Schema_VersionComesFromContent_NotFromCaller()
    {
        // 呼び出し側が v1 のまま文字を足しても v2 で書く／文字を消せば v1 に戻る（0.2.x でまた開ける）
        var withTexts = DocWithTexts(FullText()) with { SchemaVersion = WorkDocument.CurrentSchemaVersion };
        Assert.Contains("\"schemaVersion\": \"fuseo-win/2\"", WorkDocumentJson.Serialize(withTexts));
        var cleared = withTexts with { SchemaVersion = WorkDocument.SchemaVersionWithTexts, Pages = [withTexts.Pages[0] with { Texts = [] }] };
        Assert.Contains("\"schemaVersion\": \"fuseo-win/1\"", WorkDocumentJson.Serialize(cleared));
    }

    /// <summary>文字枠の JSON の形（Mac の TextBox と同じ項目名・box は左上原点）を固定する。</summary>
    private const string GoldenTexts = """
      "texts": [
        {
          "id": "dddddddd-0000-0000-0000-000000000001",
          "box": {
            "x": 0.125,
            "y": 0.25,
            "w": 0.5,
            "h": 0.0625
          },
          "text": "見本\n二行目",
          "fontSizePt": 11.5,
          "lineSpacing": 1.45,
          "letterSpacing": 0.25,
          "font": "mincho",
          "color": {
            "r": 0.050980392156862744,
            "g": 0.2,
            "b": 0.6196078431372549
          },
          "hAlign": "right",
          "vAlign": "bottom",
          "cells": 0
        }
      ]
""";

    [Fact]
    public void Golden_TextBoxShape()
    {
        var json = WorkDocumentJson.Serialize(DocWithTexts(FullText())).Replace("\r\n", "\n");
        Assert.Contains(GoldenTexts.Replace("\r\n", "\n").TrimEnd('\n'), json);
        // 文字枠以外の部分は文字の無い作業と同じ（版を除く）
        var without = WorkDocumentJson.Serialize(WorkDocumentJsonTests.Sample()).Replace("\r\n", "\n");
        var stripped = json.Replace(",\n" + GoldenTexts.Replace("\r\n", "\n").TrimEnd('\n'), "").Replace("fuseo-win/2", "fuseo-win/1");
        Assert.Equal(without, stripped);
    }

    /// <summary>Mac の試用版（0.3.0 build 73）の形（字間なし・色が名前）も、最小限のキーだけでも読める（欠けた設定は既定値）。</summary>
    [Fact]
    public void Decode_TolerantOfMissingSettings_NamedColors_AndUnknownNames()
    {
        const string trial = """
            {"box":{"h":0.02,"w":0.5,"x":0.1,"y":0.2},"cells":12,"color":"blue","font":"mincho","fontSizePt":11,
             "hAlign":"left","id":"11111111-2222-3333-4444-555555555555","lineSpacing":1.4,"text":"A","vAlign":"center"}
            """;
        var t = ParseText(trial);
        Assert.Equal(0, t.LetterSpacing);
        Assert.Equal(TextColor.Blue, t.Color);
        Assert.Equal(12, t.Cells);
        Assert.Equal(TextFont.Mincho, t.Font);
        Assert.Equal(TextColor.Black, ParseText(trial.Replace("\"blue\"", "\"black\"")).Color);
        var minimal = ParseText("""{"box":{"h":0.1,"w":0.1,"x":0,"y":0},"id":"11111111-2222-3333-4444-555555555555"}""");
        Assert.Equal(TextStyle.Default, minimal.Style);
        Assert.Equal("", minimal.Text);
        // 知らない書体・揃えの名前は既定値、範囲外の値は丸める
        var odd = ParseText("""{"box":{"h":0.1,"w":0.1,"x":0,"y":0},"id":"11111111-2222-3333-4444-555555555555","font":"brush","hAlign":3,"fontSizePt":500,"cells":99,"future":[1]}""");
        Assert.Equal(TextFont.Gothic, odd.Font);
        Assert.Equal(TextHAlign.Left, odd.HAlign);
        Assert.Equal(TextBox.FontSizeMax, odd.FontSizePt);
        Assert.Equal(TextBox.MaxCells, odd.Cells);
    }

    [Theory]
    [InlineData("""{"box":{"h":0.1,"w":0.1,"x":0,"y":0}}""")]                                                           // id が無い
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555"}""")]                                                    // box が無い
    [InlineData("""{"box":{"h":0.1,"w":0.9,"x":0.5,"y":0},"id":"11111111-2222-3333-4444-555555555555"}""")]              // 1 を超える枠
    [InlineData("""{"box":{"h":0.1,"w":0.1,"x":0,"y":0},"id":"00000000-0000-0000-0000-000000000000"}""")]                // 空の id
    [InlineData("""{"box":{"h":0.1,"w":0.1,"x":0,"y":0},"id":"11111111-2222-3333-4444-555555555555","fontSizePt":"big"}""")]
    public void Decode_RejectsBrokenTextBox(string textJson) =>
        Assert.Throws<WorkDocumentFormatException>(() => ParseText(textJson));

    [Fact]
    public void Parse_AcceptsV2WithoutTexts_AndRejectsUnknownVersion()
    {
        var json = WorkDocumentJson.Serialize(WorkDocumentJsonTests.Sample());
        Assert.Empty(WorkDocumentJson.Parse(json.Replace("fuseo-win/1", "fuseo-win/2")).Pages[0].Texts);
        Assert.Throws<WorkDocumentFormatException>(() => WorkDocumentJson.Parse(json.Replace("fuseo-win/1", "fuseo-win/3")));
        Assert.Empty(WorkDocumentJson.Parse(json).Pages[0].Texts);   // 0.2.x の作業は文字なしとして読める
    }

    [Fact]
    public void Texts_KeepCoordinatesAsTopLeft()
    {
        var json = WorkDocumentJson.Serialize(DocWithTexts(FullText()));
        using var d = JsonDocument.Parse(json);
        Assert.Equal("topLeft", d.RootElement.GetProperty("origin").GetString());
        var box = d.RootElement.GetProperty("pages")[0].GetProperty("texts")[0].GetProperty("box");
        Assert.Equal(0.25, box.GetProperty("y").GetDouble());   // 上からの位置をそのまま書く（反転しない）
    }

    /// <summary>1 つの文字枠の JSON を、サンプルの作業のページに入れて読む。</summary>
    private static TextBox ParseText(string textJson)
    {
        var json = WorkDocumentJson.Serialize(WorkDocumentJsonTests.Sample())
            .Replace("\"analysisOptions\": null", "\"analysisOptions\": null, \"texts\": [" + textJson + "]")
            .Replace("fuseo-win/1", "fuseo-win/2");
        return Assert.Single(WorkDocumentJson.Parse(json).Pages[0].Texts);
    }
}
