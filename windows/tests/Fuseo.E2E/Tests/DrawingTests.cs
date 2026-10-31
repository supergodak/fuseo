using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>塗る: 道具の切替（ボタン・V/R/B）・四角・ブラシ 3 種・選択（クリック・移動）・選択の削除（ボタン・Delete・Backspace）。</summary>
public class DrawingTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void RectTool_PaintsExactlyTheDraggedArea() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        Assert.True(app.Button("tool.rect").IsSelected(), "既定の道具が四角でない");
        var r = new Box(0.40, 0.30, 0.25, 0.30);
        app.DrawRect(r);
        app.WaitMaskCount(1);
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects.Count == 1, "四角 1 つ");
        var saved = doc.Pages[0].Manual.Rects[0];
        Assert.InRange(saved.X, r.X - 0.006, r.X + 0.006);
        Assert.InRange(saved.Y, r.Y - 0.006, r.Y + 0.006);
        Assert.InRange(saved.W, r.W - 0.01, r.W + 0.01);
        Assert.InRange(saved.H, r.H - 0.01, r.H + 0.01);

        var outPng = s.OutPath("rect.png");
        app.Export("png", outPng);
        OutputCheck.AssertClean(outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        Assert.Equal((1200, 800), (bmp.Width, bmp.Height));
        OutputCheck.AssertPainted(bmp, r, "ドラッグした四角");
        OutputCheck.AssertCardIntact(bmp, [r]);
    });

    [Fact]
    public void ToolButtonsAndKeys_SwitchTools_AndKeysPaintWithTheChosenTool() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        app.SelectTool("tool.brush");
        app.SelectTool("tool.select");
        app.SelectTool("tool.rect");

        app.Key(VKey.B);
        Wait.Until(() => app.Button("tool.brush").IsSelected(), "B でブラシ");
        app.DragOnImage(0.2, 0.2, 0.5, 0.2);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Strokes.Count == 1 && d.Pages[0].Manual.Rects.Count == 0, "ブラシの線 1 本");

        app.Key(VKey.V);
        Wait.Until(() => app.Button("tool.select").IsSelected(), "V で選択");
        app.DragOnImage(0.2, 0.6, 0.4, 0.8); // 選択の道具のドラッグは何も足さない
        app.Key(VKey.R);
        Wait.Until(() => app.Button("tool.rect").IsSelected(), "R で四角");
        app.DragOnImage(0.6, 0.6, 0.8, 0.8);
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects.Count == 1, "四角 1 つ");
        Assert.Single(doc.Pages[0].Manual.Strokes);
        app.WaitMaskCount(2);
    });

    [Fact]
    public void Brush_ThreeSizes_PaintThinMediumThickLines() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("white.png"), Synth.Solid(1200, 800, 255, 255, 255));
        var app = s.Launch();
        app.Import(1, png);
        app.SelectTool("tool.brush");
        Assert.True(app.Button("brush.medium").IsSelected(), "既定の太さが「中」でない");
        var rows = new[] { ("brush.thin", 0.25), ("brush.medium", 0.5), ("brush.thick", 0.75) };
        foreach (var (id, v) in rows)
        {
            app.SelectTool(id);
            app.DragOnImage(0.2, v, 0.8, v);
        }
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Strokes.Count == 3, "線 3 本");
        Assert.Equal([0.015, 0.03, 0.06], doc.Pages[0].Manual.Strokes.Select(x => x.Width));

        var outPng = s.OutPath("brush.png");
        app.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        var heights = rows.Select(r => OutputCheck.DarkRunHeight(bmp, 0.5, r.Item2)).ToArray();
        Output.WriteLine($"太さ（px）: {string.Join(", ", heights)}");
        // 短辺 800px × 比 = 12 / 24 / 48px
        Assert.InRange(heights[0], 9, 17);
        Assert.InRange(heights[1], 19, 31);
        Assert.InRange(heights[2], 40, 60);
        // 線の外は白のまま
        OutputCheck.AssertNotPainted(bmp, new Box(0.05, 0.05, 0.1, 0.9), "線の左の余白");
    });

    [Fact]
    public void SelectTool_ClickSelects_DeleteByButtonDeleteKeyAndBackspace() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var a = new Box(0.10, 0.50, 0.15, 0.15);
        var b = new Box(0.40, 0.50, 0.15, 0.15);
        var c = new Box(0.70, 0.50, 0.15, 0.15);
        app.DrawRect(a);
        app.DrawRect(b);
        app.DrawRect(c);
        app.WaitMaskCount(3);
        app.SelectTool("tool.select");
        var delete = app.Button("main.deleteSelection");
        Assert.False(delete.IsEnabled(), "何も選んでいないのに削除できる");

        app.ClickOnImage(a.X + a.W / 2, a.Y + a.H / 2);
        Wait.Until(delete.IsEnabled, "クリックで選ばれる");
        delete.Click();
        app.WaitMaskCount(2);

        app.ClickOnImage(b.X + b.W / 2, b.Y + b.H / 2);
        Wait.Until(delete.IsEnabled, "クリックで選ばれる");
        app.Key(VKey.Delete);
        app.WaitMaskCount(1);

        app.ClickOnImage(c.X + c.W / 2, c.Y + c.H / 2);
        Wait.Until(delete.IsEnabled, "クリックで選ばれる");
        app.Key(VKey.Back);
        app.WaitMaskCount(0);
        Wait.Until(() => !delete.IsEnabled(), "選択が消える");
        Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects.Count == 0, "四角 0");
    });

    [Fact]
    public void SelectTool_DragMovesRect_AndUndoMovesItBack() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var d0 = new Box(0.20, 0.20, 0.20, 0.20);
        app.DrawRect(d0);
        app.SelectTool("tool.select");
        app.DragOnImage(0.30, 0.30, 0.50, 0.40); // 中を掴んで右へ 0.2・下へ 0.1
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects is [{ X: > 0.3 }], "四角が動く");
        var moved = doc.Pages[0].Manual.Rects[0];
        Assert.InRange(moved.X, 0.39, 0.41);
        Assert.InRange(moved.Y, 0.29, 0.31);
        Assert.InRange(moved.W, 0.19, 0.21);
        app.WaitMaskCount(1);

        // 何も無い所をクリックすると選択が外れる
        app.ClickOnImage(0.9, 0.9);
        Wait.Until(() => !app.Button("main.deleteSelection").IsEnabled(), "選択が外れる");

        app.Key(VKey.Z, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects is [{ X: < 0.25 }], "元に戻すで元の位置");
        var outPng = s.OutPath("moved.png");
        app.Key(VKey.Y, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects is [{ X: > 0.3 }], "やり直しで動いた位置");
        app.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        OutputCheck.AssertPainted(bmp, new Box(moved.X, moved.Y, moved.W, moved.H), "動かした先");
        OutputCheck.AssertCardIntact(bmp, [new Box(moved.X, moved.Y, moved.W, moved.H)], what: "動かす前の位置を含む塗っていない所");
    });
}
