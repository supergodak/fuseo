using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>元に戻す／やり直し（ボタン・Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z）と回転（ボタン・Ctrl+L / Ctrl+R）。</summary>
public class EditTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void UndoRedo_ButtonsAndAllShortcuts() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var undo = app.Button("main.undo");
        var redo = app.Button("main.redo");
        Assert.False(undo.IsEnabled());
        Assert.False(redo.IsEnabled());
        app.DrawRect(new Box(0.3, 0.3, 0.2, 0.2));
        app.WaitMaskCount(1);

        undo.Click();
        app.WaitMaskCount(0);
        Wait.Until(redo.IsEnabled, "やり直せる");
        redo.Click();
        app.WaitMaskCount(1);
        Wait.Until(() => !redo.IsEnabled(), "やり直す物が無い");

        app.Key(VKey.Z, VKey.Control);
        app.WaitMaskCount(0);
        app.Key(VKey.Y, VKey.Control);
        app.WaitMaskCount(1);
        app.Key(VKey.Z, VKey.Control);
        app.WaitMaskCount(0);
        app.Key(VKey.Z, VKey.Control, VKey.Shift);
        app.WaitMaskCount(1);

        // 回転も元に戻せる
        app.Button("main.rotateRight").Click();
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "回転 1");
        app.Key(VKey.Z, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 0 && d.Pages[0].Manual.Rects.Count == 1, "回転が戻り四角は残る");
        app.WaitMaskCount(1);
    });

    [Fact]
    public void Rotate_ButtonsAndShortcuts_RotateImageAndMasksTogether() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var m = new Box(0.5, 0.6, 0.2, 0.2);
        app.DrawRect(m);
        double Aspect() { var r = app.Image().Bounds(); return r.Width / r.Height; }

        app.Button("main.rotateRight").Click();
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "右に回転");
        Wait.Until(() => Aspect() < 0.7, "縦長に表示される");
        app.Button("main.rotateLeft").Click();
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 0, "左に回転で戻る");
        Wait.Until(() => Aspect() > 1.4, "横長に戻る");
        app.Key(VKey.R, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "Ctrl+R");
        app.Key(VKey.R, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 2, "Ctrl+R 2 回目");
        app.Key(VKey.L, VKey.Control);
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "Ctrl+L");
        // マスクも同じだけ回っている（回した画像の上の座標）: (x,y,w,h) → (1-y-h, x, h, w)
        var rotated = new Box(1 - m.Bottom, m.X, m.H, m.W);
        var saved = doc.Pages[0].Manual.Rects.Single();
        Assert.InRange(saved.X, rotated.X - 0.01, rotated.X + 0.01);
        Assert.InRange(saved.Y, rotated.Y - 0.01, rotated.Y + 0.01);

        var outPng = s.OutPath("rotated.png");
        app.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        Assert.Equal((800, 1200), (bmp.Width, bmp.Height));
        var badge = new Box(1 - (Synth.Badge.Y + Synth.Badge.H), Synth.Badge.X, Synth.Badge.H, Synth.Badge.W);
        OutputCheck.AssertPainted(bmp, badge, "右上に来た目印の四角");
        OutputCheck.AssertPainted(bmp, rotated, "一緒に回った四角");
        OutputCheck.AssertCardIntact(bmp, [rotated], OutputCheck.Unrotate(1));
    });
}

/// <summary>切り抜き: 四隅のドラッグ・適用・全体を使う・キャンセル・不正な四角形・手で塗った所が消える確認。</summary>
public class CropTests(ITestOutputHelper output) : E2ETest(output)
{
    /// <summary>灰色の地の中央（0.25〜0.75）に、左半分が青・右半分が緑の領域。</summary>
    private static string CropSource(Session s)
    {
        var bmp = Synth.Solid(1200, 800, 128, 128, 128);
        Synth.FillNorm(bmp, 0.25, 0.25, 0.25, 0.5, 0, 0, 255);
        Synth.FillNorm(bmp, 0.50, 0.25, 0.25, 0.5, 0, 200, 0);
        return Synth.WritePng(s.InPath("crop.png"), bmp);
    }

    private static AutomationElement OpenCrop(FuseoApp app)
    {
        app.Button("main.crop").Click();
        var w = app.WaitWindow(Ja.CropWindow);
        Wait.Until(() => w.Find("crop.image").IsShown(), "切り抜きの画像が出る");
        return w;
    }

    private static void DragCorner(AutomationElement crop, (double U, double V) from, (double U, double V) to)
    {
        var img = crop.Get("crop.image");
        Mouse.Drag(img.At(from.U, from.V), img.At(to.U, to.V));
    }

    private static void DragToInnerRegion(AutomationElement crop)
    {
        DragCorner(crop, (0, 0), (0.25, 0.25));
        DragCorner(crop, (1, 0), (0.75, 0.25));
        DragCorner(crop, (1, 1), (0.75, 0.75));
        DragCorner(crop, (0, 1), (0.25, 0.75));
    }

    [Fact]
    public void DragFourCorners_Apply_ExportsOnlyTheCroppedArea() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, CropSource(s));
        var crop = OpenCrop(app);
        DragToInnerRegion(crop);
        crop.Get("crop.apply").Click();
        app.WaitWindowClosed(Ja.CropWindow);
        Wait.Until(() => app.Status().StartsWith("切り抜きました", StringComparison.Ordinal), "切り抜きの表示");
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualQuad is not null, "切り抜きが保存される");
        var q = doc.Pages[0].ManualQuad!.Value;
        Assert.InRange(q.TopLeft.X, 0.24, 0.26);
        Assert.InRange(q.BottomRight.Y, 0.74, 0.76);
        Wait.Until(() => { var r = app.Image().Bounds(); return r.Width / r.Height is > 1.4 and < 1.6; }, "切り抜いた画像を表示");

        var outPng = s.OutPath("cropped.png");
        app.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        Output.WriteLine($"切り抜き後 {bmp.Width}x{bmp.Height}");
        Assert.InRange(bmp.Width, 560, 640);
        Assert.InRange(bmp.Height, 370, 430);
        var left = OutputCheck.Mean(bmp, new Box(0.05, 0.05, 0.4, 0.9));
        var right = OutputCheck.Mean(bmp, new Box(0.55, 0.05, 0.4, 0.9));
        Assert.True(left.B > 230 && left.R < 25 && left.G < 25, $"左が青でない {left}");
        Assert.True(right.G > 180 && right.R < 25 && right.B < 25, $"右が緑でない {right}");
        // 四隅のすぐ内側にも灰色（切り抜いた外）が残っていない
        foreach (var corner in new[] { new Box(0.01, 0.01, 0.04, 0.04), new Box(0.95, 0.95, 0.04, 0.04) })
        {
            var c = OutputCheck.Mean(bmp, corner);
            Assert.False(Math.Abs(c.R - 128) < 20 && Math.Abs(c.G - 128) < 20 && Math.Abs(c.B - 128) < 20, $"隅に外側の灰色が残る {c}");
        }
    });

    [Fact]
    public void InvalidQuad_DisablesApply_UseWholeRestores_CancelKeepsPage() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, CropSource(s));
        var crop = OpenCrop(app);
        var apply = crop.Get("crop.apply");
        Assert.True(apply.IsEnabled());
        Assert.False(crop.Find("crop.invalid").IsShown());
        DragCorner(crop, (0, 0), (0.9, 0.9)); // 左上を右下の向こうへ＝へこんだ四角形
        Wait.Until(() => !apply.IsEnabled() && crop.Find("crop.invalid").IsShown(), "適用できない・警告が出る");
        crop.Get("crop.full").Click();
        Wait.Until(() => apply.IsEnabled() && !crop.Find("crop.invalid").IsShown(), "全体に戻って適用できる");
        DragCorner(crop, (1, 0), (0.6, 0.4));
        crop.Get("crop.cancel").Click();
        app.WaitWindowClosed(Ja.CropWindow);
        var doc = Autosave.WaitLatest(s.Data, _ => true, "取り込みの保存");
        Assert.Null(doc.Pages[0].ManualQuad);
        var r = app.Image().Bounds();
        Assert.InRange(r.Width / r.Height, 1.45, 1.55);
    });

    [Fact]
    public void Crop_WithHandPaintedMask_AsksFirst_CancelKeeps_OkClears_UndoRestores() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, CropSource(s));
        app.DrawRect(new Box(0.3, 0.3, 0.2, 0.2));
        app.WaitMaskCount(1);

        var crop = OpenCrop(app);
        DragCorner(crop, (0, 0), (0.1, 0.1));
        crop.Get("crop.apply").Click();
        var box = app.WaitMessageBox(Ja.CropConfirmTitle);
        Assert.Contains("手で塗った箇所は消えます", box.Message);
        box.Cancel();
        app.WaitNoDialogs();
        app.WaitMaskCount(1);
        Assert.Null(Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects.Count == 1, "四角 1").Pages[0].ManualQuad);

        crop = OpenCrop(app);
        DragCorner(crop, (0, 0), (0.1, 0.1));
        crop.Get("crop.apply").Click();
        app.WaitMessageBox(Ja.CropConfirmTitle).Ok();
        app.WaitMaskCount(0);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualQuad is not null && d.Pages[0].Manual.Rects.Count == 0, "切り抜いて四角が消える");

        app.Key(VKey.Z, VKey.Control);
        app.WaitMaskCount(1);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualQuad is null && d.Pages[0].Manual.Rects.Count == 1, "元に戻すで切り抜く前に戻る");
    });
}

/// <summary>表示: 拡大・縮小・全体表示・Ctrl+ホイール、パン（右／中ボタンのドラッグ・Space+ドラッグ・ホイール・Shift+ホイール）。</summary>
public class ViewTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void Zoom_ButtonsCtrlWheelAndFit_AndPaintingStillLandsOnTheImage() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var w0 = app.Image().Bounds().Width;
        void Expect(double ratio, string what) =>
            Wait.Until(() => Math.Abs(app.Image().Bounds().Width / w0 - ratio) < 0.015, $"{what}（幅 ×{ratio}）");

        app.Button("main.zoomOut").Click();
        Expect(0.8, "縮小");
        app.Button("main.zoomOut").Click();
        Expect(0.64, "もう一度縮小");
        app.Button("main.zoomIn").Click();
        Expect(0.8, "拡大");
        var center = app.Image().Center();
        app.Activate();
        Keyboard.Down(VKey.Control);
        try { Mouse.Wheel(center, 120); }
        finally { Keyboard.Up(VKey.Control); }
        Expect(0.88, "Ctrl+ホイールで拡大");
        Keyboard.Down(VKey.Control);
        try { Mouse.Wheel(center, -120); }
        finally { Keyboard.Up(VKey.Control); }
        Expect(0.8, "Ctrl+ホイールで縮小");
        app.Button("main.zoomFit").Click();
        Expect(1.0, "全体表示");

        // 縮小した状態で塗っても、画像の上の同じ位置に入る
        app.Button("main.zoomOut").Click();
        app.Button("main.zoomOut").Click();
        Expect(0.64, "縮小");
        app.DrawRect(new Box(0.3, 0.3, 0.3, 0.3));
        var r = Autosave.WaitLatest(s.Data, d => d.Pages[0].Manual.Rects.Count == 1, "四角 1").Pages[0].Manual.Rects[0];
        Assert.InRange(r.X, 0.29, 0.31);
        Assert.InRange(r.W, 0.29, 0.31);
    });

    [Fact]
    public void Pan_RightMiddleSpaceDragAndWheel_MoveTheViewWithoutPainting() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        app.Button("main.zoomOut").Click();
        app.Button("main.zoomOut").Click();
        System.Windows.Rect Now() => app.Image().Bounds();
        void ExpectMoved(System.Windows.Rect before, double dx, double dy, string what) =>
            Wait.Until(() => Math.Abs(Now().X - before.X - dx) <= 4 && Math.Abs(Now().Y - before.Y - dy) <= 4, $"{what}（{dx},{dy} 動く）");
        Wait.Until(() => Now().Width > 0, "画像");
        Thread.Sleep(200); // 縮小の反映（描画）を待つ
        var r0 = Now();

        var c = app.Image().Center();
        Mouse.Drag(c, new System.Windows.Point(c.X + 200, c.Y + 100), MouseButton.Right);
        ExpectMoved(r0, 200, 100, "右ボタンのドラッグ");
        var r1 = Now();
        c = app.Image().Center();
        Mouse.Drag(c, new System.Windows.Point(c.X - 200, c.Y - 100), MouseButton.Middle);
        ExpectMoved(r1, -200, -100, "中ボタンのドラッグ");

        // Space+左ドラッグ: キャンバスにフォーカスを置いてから（ボタンにフォーカスがあると Space がボタンを押す）
        app.Key(VKey.V);
        Wait.Until(() => app.Button("tool.select").IsSelected(), "選択の道具");
        app.SelectTool("tool.rect"); // 四角の道具のまま Space ドラッグしても塗らないことを確かめる
        Mouse.Click(new System.Windows.Point(app.Image().Bounds().Left - 30, app.Image().Center().Y)); // 画像の外の灰色
        var r2 = Now();
        Keyboard.Down(VKey.Space);
        try
        {
            c = app.Image().Center();
            Mouse.Drag(c, new System.Windows.Point(c.X + 150, c.Y + 50));
        }
        finally { Keyboard.Up(VKey.Space); }
        ExpectMoved(r2, 150, 50, "Space+ドラッグ");

        var r3 = Now();
        Mouse.Wheel(app.Image().Center(), -120); // 下へスクロール = 画像が上へ 60DIP（200% で 120px）
        ExpectMoved(r3, 0, -120, "ホイール");
        var r4 = Now();
        Keyboard.Down(VKey.Shift);
        try { Mouse.Wheel(app.Image().Center(), -120); }
        finally { Keyboard.Up(VKey.Shift); }
        ExpectMoved(r4, -120, 0, "Shift+ホイール");

        Assert.Equal(Ja.NoMask, app.MaskCountOf(0));
        Assert.Empty(Autosave.Latest(s.Data)!.Pages[0].Manual.Rects);
    });
}
