using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>
/// 書き出し: ボタン・Ctrl+E → 形式（PDF / JPEG / PNG）→ プレビュー → 警告 → 保存ダイアログ → 保存／キャンセル。
/// 書いたファイルは形式・メタデータ・テキスト・ページ数・画素まで確かめる。
/// </summary>
public class ExportTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void Pdf_ViaButton_FromPdfWithText_RasterOnlyAndPainted() => Run(s =>
    {
        var pdf = Synth.WritePdf(s.InPath("text.pdf"),
            [new(1, 0, 0, Synth.Marker), new(0, 0.6, 0, Synth.Marker), new(0, 0, 1, Synth.Marker)]);
        var app = s.Launch();
        app.Import(3, pdf);
        var mask = new Box(0.25, 0.08, 0.65, 0.16); // 1 ページ目の文字の行
        app.DrawRect(mask);
        app.WaitMaskCount(1);

        var w = app.OpenExport();
        Assert.True(w.Get("export.format.pdf").IsSelected(), "既定の形式が PDF でない");
        Wait.Until(() => w.Descendants(System.Windows.Automation.ControlType.Image).Any(i => i.IsShown()), "プレビューが出る");
        Assert.StartsWith("マスク 1 箇所（自動 0・手動 1）／ 3 ページ", w.Get("export.summary").Text());
        Assert.True(w.Get("export.zeroMaskWarning").IsShown(), "マスクの無いページがあるのに警告が出ない");
        var outPdf = s.OutPath("out.pdf");
        app.SaveExport(w, outPdf);
        Wait.Until(() => app.Status() == Ja.ExportedOne("out.pdf"), "書き出し完了の表示");
        Autosave.WaitLatest(s.Data, d => d.Status == Fuseo.Core.Library.WorkStatus.Exported && d.LastExportedName == "out.pdf", "書き出し済みとして保存");

        OutputCheck.AssertClean(outPdf);
        var pages = OutputCheck.Load(outPdf);
        Assert.Equal(3, pages.Count);
        OutputCheck.AssertPainted(pages[0], mask, "文字の行");
        OutputCheck.AssertPainted(pages[0], new Box(0.06, 0.09, 0.14, 0.2), "元の黒い四角", inset: 0.01);
        var red = OutputCheck.Mean(pages[0], new Box(0.3, 0.5, 0.6, 0.4));
        Assert.True(red.R > 220 && red.G < 30 && red.B < 30, $"塗っていない所が元の赤でない {red}");
        var green = OutputCheck.Mean(pages[1], new Box(0.3, 0.1, 0.6, 0.15)); // 2 ページ目の文字の行は塗っていない
        Assert.True(OutputCheck.DarkFraction(pages[1], new Box(0.27, 0.12, 0.5, 0.1)) > 0.01, "2 ページ目の文字（画像として）が消えている");
        Assert.True(green.G > 120, $"2 ページ目が緑でない {green}");
    });

    [Fact]
    public void Jpeg_ViaCtrlE_FromJpegWithExif_NoMetadataAndPainted() => Run(s =>
    {
        var jpg = Synth.WriteJpeg(s.InPath("photo.jpg"), Synth.Card());
        var app = s.Launch();
        app.Import(1, jpg);
        var mask = new Box(0.5, 0.5, 0.3, 0.2);
        app.DrawRect(mask);
        var outJpg = s.OutPath("out.jpg");
        app.Export("jpeg", outJpg, viaShortcut: true);
        OutputCheck.AssertClean(outJpg);
        var bmp = OutputCheck.LoadSingle(outJpg);
        Assert.Equal((1200, 800), (bmp.Width, bmp.Height));
        OutputCheck.AssertPainted(bmp, mask, "四角");
        OutputCheck.AssertCardIntact(bmp, [mask]);
    });

    [Fact]
    public void Png_MultiPage_WritesNumberedFiles_AndAsksBeforeOverwriting() => Run(s =>
    {
        var a = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var b = Synth.WritePng(s.InPath("b.png"), Synth.Card(800, 600));
        var app = s.Launch();
        app.Import(2, a, b);
        app.PageItems()[0].Click();
        var mask = new Box(0.3, 0.3, 0.3, 0.3);
        app.DrawRect(mask);
        var chosen = s.OutPath("multi.png");
        app.Export("png", chosen);
        var p1 = s.OutPath("multi_1.png");
        var p2 = s.OutPath("multi_2.png");
        Assert.True(File.Exists(p1) && File.Exists(p2), "ページごとのファイル（_1, _2）が無い");
        Assert.False(File.Exists(chosen), "選んだ名前そのものに書いている");
        OutputCheck.AssertClean(p1);
        OutputCheck.AssertClean(p2);
        OutputCheck.AssertPainted(OutputCheck.LoadSingle(p1), mask, "1 ページ目の四角");
        var second = OutputCheck.LoadSingle(p2);
        Assert.Equal((800, 600), (second.Width, second.Height));
        OutputCheck.AssertCardIntact(second, []);

        // 同じ名前でもう一度 → アプリが _1, _2 の上書きを確かめる。キャンセルなら書かない。
        var stamp = File.GetLastWriteTimeUtc(p1);
        var w = app.OpenExport();
        Actions.ChooseFormat(w, "png");
        w.Get("export.confirmButton").Click();
        FileDialogs.Save(app, Ja.SaveDialog, chosen);
        var box = app.WaitMessageBox(Ja.ExportOverwriteTitle);
        Assert.Contains("multi_1.png", box.Message);
        Assert.Contains("multi_2.png", box.Message);
        box.Cancel();
        Assert.NotNull(app.FindWindow(Ja.ExportWindow)); // シートに戻る
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(p1));
        w.Get("export.confirmButton").Click();
        FileDialogs.Save(app, Ja.SaveDialog, chosen);
        app.WaitMessageBox(Ja.ExportOverwriteTitle).Ok();
        app.WaitWindowClosed(Ja.ExportWindow);
        Wait.Until(() => File.GetLastWriteTimeUtc(p1) != stamp, "上書きされる");
    });

    [Fact]
    public void ZeroMask_WarnsAndRenamesSaveButton_OnlyWhileAPageHasNoMask() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var w = app.OpenExport();
        Assert.True(w.Get("export.zeroMaskWarning").IsShown());
        Assert.Equal(Ja.ExportSaveWithoutMask, w.Get("export.confirmButton").Text());
        Assert.StartsWith("マスク 0 箇所", w.Get("export.summary").Text());
        w.Get("export.cancel").Click();
        app.WaitWindowClosed(Ja.ExportWindow);

        app.DrawRect(new Box(0.4, 0.4, 0.2, 0.2));
        w = app.OpenExport();
        Assert.False(w.Find("export.zeroMaskWarning").IsShown(), "マスクがあるのに警告が出る");
        Assert.Equal(Ja.ExportSave, w.Get("export.confirmButton").Text());
        Actions.ChooseFormat(w, "png");
        app.SaveExport(w, s.OutPath("ok.png"));
        Assert.True(File.Exists(s.OutPath("ok.png")));
    });

    [Fact]
    public void CancelSheet_CancelSaveDialog_AndEscape_WriteNothing() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var w = app.OpenExport();
        w.Get("export.cancel").Click();
        app.WaitWindowClosed(Ja.ExportWindow);

        w = app.OpenExport(viaShortcut: true);
        Actions.ChooseFormat(w, "jpeg");
        w.Get("export.confirmButton").Click();
        FileDialogs.Cancel(app, Ja.SaveDialog);
        Assert.NotNull(app.FindWindow(Ja.ExportWindow)); // 保存ダイアログを取り消すとシートに戻る
        FuseoApp.ActivateWindow(app.WaitWindow(Ja.ExportWindow));
        Keyboard.Press(VKey.Escape);
        app.WaitWindowClosed(Ja.ExportWindow);
        app.WaitNoDialogs();
        Assert.Empty(Directory.GetFiles(s.Out));
        Assert.DoesNotContain("書き出しました", app.Status());
    });
}

/// <summary>パスワード付き PDF（Fuseo.Input.Tests の合成フィクスチャ・パスワードはテスト専用の固定値）。</summary>
public class PasswordTests(ITestOutputHelper output) : E2ETest(output)
{
    private const string TestPassword = "fuseo-test-pw";

    private static string Fixture(Session s)
    {
        var dest = s.InPath("locked.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted-aes256.pdf"), dest);
        return dest;
    }

    private static void TypePassword(FuseoApp app, string password)
    {
        var dlg = app.WaitWindow(Ja.PasswordWindow);
        FuseoApp.ActivateWindow(dlg);
        var input = dlg.Get("password.input");
        Mouse.Click(input.Center());
        Wait.Until(() => input.Current.HasKeyboardFocus, "パスワード欄にフォーカス");
        Keyboard.Type(password);
    }

    [Fact]
    public void WrongPassword_AsksAgain_ThenRightPasswordImports() => Run(s =>
    {
        var pdf = Fixture(s);
        var app = s.Launch();
        app.OpenWithButton(pdf);
        var dlg = app.WaitWindow(Ja.PasswordWindow);
        Assert.Contains("「locked.pdf」はパスワードで保護されています", dlg.Get("password.message").Text());
        TypePassword(app, "wrong-password");
        dlg.Get("password.ok").Click();
        Wait.Until(() => app.FindWindow(Ja.PasswordWindow)?.Find("password.message")?.Text().StartsWith("パスワードが違います", StringComparison.Ordinal) == true,
                   "もう一度聞かれる");
        TypePassword(app, TestPassword);
        Keyboard.Press(VKey.Enter); // OK は既定のボタン
        app.WaitPageCount(1);
        Assert.Equal(["1. locked.pdf（1 ページ目）"], app.PageTitles());
        app.WaitNoDialogs();
    });

    [Fact]
    public void Cancel_ImportsNothing_AndShowsNoError() => Run(s =>
    {
        var pdf = Fixture(s);
        var app = s.Launch();
        app.OpenWithButton(pdf);
        app.WaitWindow(Ja.PasswordWindow).Get("password.cancel").Click();
        app.WaitWindowClosed(Ja.PasswordWindow);
        Wait.Until(() => app.Button("main.openFiles").IsEnabled(), "取り込みが終わる");
        Wait.Never(() => app.FindMessageBox() is not null, "エラーが出る", TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, app.PageCount());
        Assert.Equal("", app.Status());
    });
}
