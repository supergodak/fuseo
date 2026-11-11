using System.Text;
using System.Windows.Automation;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>
/// 文字入れ（v0.3・docs/windows-design.md §11・W10-3）: 文字の道具で枠を作って打つ（Shift+Enter で改行・Enter で確定・Esc で取り消し）→
/// 一覧・書き出しの「文字 N 箇所」／右ペインのパネル（サイズ・行間・字間・色・書体・揃え・マス数・mm）→ 新しい書類 → 一覧から開き直して同じ／
/// PDF に書き出して読み戻す（文字の上のマスクは黒・マスクの外に文字のインク・テキストもフォントも無い）／回転で文字が消える確認。
/// 入力はすべて実行時に作る白い合成ページ（実物の書類は使わない）。
/// </summary>
public class TextTests(ITestOutputHelper output) : E2ETest(output)
{
    /// <summary>白い横長のページ（A5 横を 200dpi 程度で描いた大きさ）。</summary>
    private static string WhitePage(Session s, string name = "form.png") =>
        Synth.WritePng(s.InPath(name), Synth.Solid(1654, 1169, 255, 255, 255));

    private static bool EditorShown(FuseoApp app) => app.Main.Find("text.editor").IsShown();

    private static List<string> Rows(FuseoApp app) =>
        app.Main.FindAll("text.row").Where(r => r.IsShown()).Select(r => r.Text()).ToList();

    /// <summary>文字の道具で枠をドラッグして作り、打って Enter で確定する。</summary>
    private static void TypeBox(FuseoApp app, Box frame, string text)
    {
        app.DragOnImage(frame.X, frame.Y, frame.Right, frame.Bottom);
        Wait.Until(() => EditorShown(app), "入力欄が出る");
        Keyboard.Type(text);
        Keyboard.Press(VKey.Enter);
        Wait.Until(() => !EditorShown(app), "Enter で入力欄が閉じる");
    }

    /// <summary>右ペインの数値の欄に打って Enter（その場で反映）。表示が <paramref name="shown"/> になるまで待つ。</summary>
    private static void SetNumber(FuseoApp app, string id, string value, string shown)
    {
        var box = app.Main.Get(id);
        box.Click();
        Keyboard.Press(VKey.A, VKey.Control);
        Keyboard.Type(value);
        Keyboard.Press(VKey.Enter);
        Wait.Until(() => box.Value() == shown, $"{id} が {shown}");
    }

    [Fact]
    public void TextTool_TypeWithShiftEnterNewline_EnterCommits_EscCancels_ListedAndCountedInExport() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, WhitePage(s));
        app.Key(VKey.T);
        Wait.Until(() => app.Button("tool.text").IsSelected(), "T で文字の道具");
        Wait.Until(() => app.Main.Find("text.panel.target").IsShown(), "文字のパネルが出る");   // Border は UIA の木に出ないので中の文字で見る
        Assert.Equal("次に作る枠の設定", app.Main.Get("text.panel.target").Text());

        app.DragOnImage(0.1, 0.2, 0.5, 0.32);
        Wait.Until(() => EditorShown(app), "入力欄が出る");
        Keyboard.Type("山田 太郎");
        Keyboard.Press(VKey.Enter, VKey.Shift);   // 改行（確定しない）
        Assert.True(EditorShown(app), "Shift+Enter で確定してしまった");
        Keyboard.Type("二行目");
        Keyboard.Press(VKey.Enter);
        Wait.Until(() => !EditorShown(app), "Enter で入力欄が閉じる");
        Wait.Until(() => Rows(app).SequenceEqual(["文字 1: 山田 太郎（2 行）"]), "一覧に 1 行");
        Assert.Equal("選択中の枠", app.Main.Get("text.panel.target").Text());
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Texts.Count == 1, "文字 1 つの保存");
        Assert.Equal("山田 太郎\n二行目", doc.Pages[0].Texts[0].Text);
        Assert.Equal(WorkDocument.SchemaVersionWithTexts, doc.SchemaVersion);
        app.WaitMaskCount(0);   // 文字はマスクに数えない

        // Esc は取り消し（新しい枠ごと消える）
        app.DragOnImage(0.1, 0.6, 0.4, 0.7);
        Wait.Until(() => EditorShown(app), "2 つ目の入力欄");
        Keyboard.Type("取り消す");
        Keyboard.Press(VKey.Escape);
        Wait.Until(() => !EditorShown(app), "Esc で入力欄が閉じる");
        Wait.Until(() => Rows(app).Count == 1, "取り消した枠は残らない");

        // 枠をクリックして選ぶ（右ペインの一覧はパネルの下でスクロールの外にあることがある）→ Ctrl+D で複製 → Delete で消す
        app.ClickOnImage(0.3, 0.26);
        Wait.Until(() => app.Main.Get("text.panel.target").Text() == "選択中の枠", "枠のクリックで選ぶ");
        app.Key(VKey.D, VKey.Control);
        Wait.Until(() => Rows(app).Count == 2, "Ctrl+D で複製");
        app.Key(VKey.Delete);
        Wait.Until(() => Rows(app).Count == 1, "Delete で消える");

        var w = app.OpenExport();
        Assert.Equal(Ja.TextSummary(1), w.Get("export.textSummary").Text());
        Assert.False(w.Find("export.textOverflow").IsShown(), "はみ出していないのに注意が出る");
        w.Get("export.cancel").Click();
        app.WaitWindowClosed(Ja.ExportWindow);
    });

    [Fact]
    public void Panel_SizeSpacingColorCellsAndMillimeters_SurviveReopenFromLibrary() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, WhitePage(s));
        app.SelectTool("tool.text");
        TypeBox(app, new Box(0.1, 0.2, 0.4, 0.08), "123");
        Wait.Until(() => app.Main.Get("text.panel.target").Text() == "選択中の枠", "作った枠が選ばれている");

        SetNumber(app, "text.size", "14", "14");
        SetNumber(app, "text.lineSpacing", "1.5", "1.5");
        SetNumber(app, "text.letterSpacing", "0.25", "0.25");
        app.Main.Get("text.font.mincho").Click();
        app.Main.Get("text.hAlign.right").Click();
        app.Main.Get("text.vAlign.top").Click();
        app.Main.Get("text.color.blue").Click();
        SetNumber(app, "text.frame.x", "20", "20.0");
        SetNumber(app, "text.frame.y", "15", "15.0");
        SetNumber(app, "text.frame.width", "60", "60.0");
        SetNumber(app, "text.frame.height", "8", "8.0");
        SetNumber(app, "text.cells", "12", "12");
        Wait.Until(() => !app.Main.Get("text.letterSpacing").IsEnabled(), "マス目では字間を使わない（欄が無効）");

        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Texts.Count == 1 && d.Pages[0].Texts[0].Cells == 12, "設定の保存");
        var t = doc.Pages[0].Texts[0];
        Assert.Equal((14.0, 1.5, 0.25), (t.FontSizePt, t.LineSpacing, t.LetterSpacing));
        Assert.Equal((TextFont.Mincho, TextHAlign.Right, TextVAlign.Top), (t.Font, t.HAlign, t.VAlign));
        Assert.Equal(TextColor.Blue, t.Color);

        app.NewDocument();
        app.WaitLibraryCount(1);
        app.LibraryItems()[0].Get("library.item.open").Click();
        app.WaitPageCount(1);
        Wait.Until(() => Rows(app).SequenceEqual(["文字 1: 123"]), "開き直すと文字が戻る");
        app.ClickOnImage(0.357, 0.19);   // mm で置いた枠（X 20・Y 15・幅 60・高さ 8 mm）の中
        Wait.Until(() => app.Main.Get("text.panel.target").Text() == "選択中の枠", "枠のクリックで選ぶ");

        Assert.Equal("14", app.Main.Get("text.size").Value());
        Assert.Equal("1.5", app.Main.Get("text.lineSpacing").Value());
        Assert.Equal("0.25", app.Main.Get("text.letterSpacing").Value());
        Assert.Equal("12", app.Main.Get("text.cells").Value());
        Assert.True(app.Main.Get("text.font.mincho").IsSelected(), "明朝が戻らない");
        Assert.True(app.Main.Get("text.hAlign.right").IsSelected(), "右揃えが戻らない");
        Assert.True(app.Main.Get("text.vAlign.top").IsSelected(), "上揃えが戻らない");
        Assert.Equal(("20.0", "15.0", "60.0", "8.0"),
                     (app.Main.Get("text.frame.x").Value(), app.Main.Get("text.frame.y").Value(),
                      app.Main.Get("text.frame.width").Value(), app.Main.Get("text.frame.height").Value()));
        var reopened = Autosave.All(s.Data).Single().Pages[0].Texts.Single();
        Assert.Equal(t, reopened);   // 位置・文字・設定がすべて同じ（開いただけでは変えない）
    });

    [Fact]
    public void ExportPdf_MaskOverTextIsBlack_TextInkOutside_NoTextOrFontInPdf() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, WhitePage(s));
        app.SelectTool("tool.text");
        SetNumber(app, "text.size", "36", "36");   // 何も選んでいないので「次に作る枠の設定」
        var frame = new Box(0.1, 0.3, 0.8, 0.2);
        TypeBox(app, frame, "■■■■■■■■");
        var mask = new Box(0.4, 0.25, 0.2, 0.3);
        app.SelectTool("tool.rect");
        app.DrawRect(mask);
        app.WaitMaskCount(1);

        var w = app.OpenExport();
        Assert.Equal(Ja.TextSummary(1), w.Get("export.textSummary").Text());
        var outPdf = s.OutPath("typed.pdf");
        app.SaveExport(w, outPdf);
        Wait.Until(() => app.Status() == Ja.ExportedOne("typed.pdf"), "書き出し完了の表示");

        OutputCheck.AssertClean(outPdf);   // /Font・Tj・/Annots・/Metadata が無い
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(outPdf));
        Assert.DoesNotContain("\nBT\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nBT ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Yu Gothic", text, StringComparison.Ordinal);
        var page = OutputCheck.LoadSingle(outPdf);
        OutputCheck.AssertPainted(page, mask, "文字の上のマスク");
        Assert.True(OutputCheck.DarkFraction(page, new Box(0.1, 0.3, 0.28, 0.2)) > 0.05, "マスクの左に文字のインクが無い");
        Assert.True(OutputCheck.DarkFraction(page, new Box(0.62, 0.3, 0.28, 0.2)) > 0.05, "マスクの右に文字のインクが無い");
        Assert.True(OutputCheck.DarkFraction(page, new Box(0.05, 0.6, 0.9, 0.35)) < 0.001, "枠の外に文字のインクがある");
    });

    [Fact]
    public void Rotate_WithText_AsksFirst_CancelKeeps_OkRemovesText_UndoRestores() => Run(s =>
    {
        var app = s.Launch();
        app.Import(1, WhitePage(s));
        app.SelectTool("tool.text");
        TypeBox(app, new Box(0.1, 0.2, 0.4, 0.1), "消える");
        Wait.Until(() => Rows(app).Count == 1, "一覧に 1 行");

        app.Button("main.rotateRight").Click();
        var box = app.WaitMessageBox(Ja.RotateTextsConfirmTitle);
        Assert.Contains("入力した文字は消えます", box.Message);
        box.Cancel();
        app.WaitNoDialogs();
        var doc = Autosave.WaitLatest(s.Data, d => d.Pages[0].Texts.Count == 1, "文字 1 つ");
        Assert.Equal(0, doc.Pages[0].ManualRotation);   // 取り消したら回さない

        app.Button("main.rotateRight").Click();
        app.WaitMessageBox(Ja.RotateTextsConfirmTitle).Ok();
        app.WaitNoDialogs();
        var rotated = Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1 && d.Pages[0].Texts.Count == 0, "回して文字が消える");
        Wait.Until(() => Rows(app).Count == 0, "一覧から消える");
        Assert.Equal(WorkDocument.CurrentSchemaVersion, rotated.SchemaVersion);   // 文字が無くなれば版は元のまま

        app.Key(VKey.Z, VKey.Control);
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 0 && d.Pages[0].Texts.Count == 1, "元に戻すで文字と向きが戻る");
        Wait.Until(() => Rows(app).Count == 1, "一覧に戻る");
    });
}
