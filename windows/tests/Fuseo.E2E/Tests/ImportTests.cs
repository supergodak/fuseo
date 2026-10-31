using System.Windows;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>取り込み: ボタン・Ctrl+O・ドラッグ＆ドロップ・PDF・取り込みエラー（対応外・壊れた・ページ数上限）。</summary>
public class ImportTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void OpenButton_ImportsImage_AndShowsPage() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        Assert.Equal(0, app.PageCount());
        Assert.False(app.Button("main.export").IsEnabled(), "ページが無いのに書き出せる");
        app.Import(1, png);
        Assert.Equal(["1. card.png"], app.PageTitles());
        Assert.Equal(Ja.NoMask, app.MaskCountOf(0));
        Wait.Until(() => app.Status() == Ja.Imported(1), "読み込みの表示");
        Assert.True(app.Image().IsShown());
        Assert.True(app.Button("main.export").IsEnabled());
        // 横長の画像は横長に表示される（縦横比を保つ）。
        var r = app.Image().Bounds();
        Assert.InRange(r.Width / r.Height, 1.45, 1.55);
    });

    [Fact]
    public void CtrlO_Cancel_DoesNothing_ThenMultiSelectImportsAll() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var jpg = Synth.WriteJpeg(s.InPath("b.jpg"), Synth.Card());
        var app = s.Launch();
        app.Key(VKey.O, VKey.Control);
        FileDialogs.Cancel(app, Ja.OpenDialog);
        app.WaitNoDialogs();
        Assert.Equal(0, app.PageCount());
        app.OpenWithCtrlO(png, jpg);
        app.WaitPageCount(2);
        Assert.Equal(["1. a.png", "2. b.jpg"], app.PageTitles());
        Wait.Until(() => app.Status() == Ja.Imported(2), "2 ページの読み込み表示");
    });

    [Fact]
    public void DragAndDrop_FromAnotherProcess_ImportsFiles() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("drop1.png"), Synth.Card());
        var pdf = Synth.WritePdf(s.InPath("drop2.pdf"), [new(1, 0, 0), new(0, 1, 0)]);
        var app = s.Launch();
        var target = app.Main.At(0.5, 0.5); // 空の画面（キャンバスの上）
        var effect = FileDropper.Drop([png, pdf], target);
        Assert.Equal(DragDropEffects.Copy, effect);
        app.WaitPageCount(3);
        Assert.Equal("1. drop1.png", app.PageTitles()[0]);
        Assert.Equal("3. drop2.pdf（2 ページ目）", app.PageTitles()[2]);
    });

    [Fact]
    public void Pdf_MultiPage_ImportsEachPage() => Run(s =>
    {
        var pdf = Synth.WritePdf(s.InPath("three.pdf"), [new(1, 0, 0, Synth.Marker), new(0, 0.6, 0), new(0, 0, 1)]);
        var app = s.Launch();
        app.Import(3, pdf);
        Assert.Equal(["1. three.pdf（1 ページ目）", "2. three.pdf（2 ページ目）", "3. three.pdf（3 ページ目）"], app.PageTitles());
        // 600x400pt のページ → 横長
        var r = app.Image().Bounds();
        Assert.InRange(r.Width / r.Height, 1.45, 1.55);
    });

    [Fact]
    public void UnsupportedFile_ShowsError_AndImportsNothing() => Run(s =>
    {
        var txt = s.InPath("note.txt");
        File.WriteAllText(txt, "not an image");
        var app = s.Launch();
        app.OpenWithButton(txt);
        var box = app.WaitMessageBox(Ja.ImportErrorTitle);
        Assert.Contains("note.txt", box.Message);
        Assert.Contains("対応していない形式", box.Message);
        box.Ok();
        app.WaitNoDialogs();
        Assert.Equal(0, app.PageCount());
    });

    [Fact]
    public void DragAndDrop_UnsupportedFile_ShowsError_AndImportsNothing() => Run(s =>
    {
        var txt = s.InPath("note.txt");
        File.WriteAllText(txt, "not an image");
        var app = s.Launch();
        var sourceBlocked = false;
        FileDropper.Drop([txt], app.Main.At(0.5, 0.5), blocked =>
        {
            sourceBlocked = blocked;
            var box = app.WaitMessageBox(Ja.ImportErrorTitle);
            Assert.Contains("note.txt", box.Message);
            box.Ok();
        });
        app.WaitNoDialogs();
        Assert.Equal(0, app.PageCount());
        Output.WriteLine($"エラーの表示中にドラッグ元が待たされたか: {sourceBlocked}");
    });

    [Fact]
    public void DragAndDrop_EncryptedPdf_AsksPassword_AndImports() => Run(s =>
    {
        var pdf = s.InPath("locked.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted-aes256.pdf"), pdf);
        var app = s.Launch();
        var sourceBlocked = false;
        FileDropper.Drop([pdf], app.Main.At(0.5, 0.5), blocked =>
        {
            sourceBlocked = blocked;
            var dlg = app.WaitWindow(Ja.PasswordWindow);
            FuseoApp.ActivateWindow(dlg);
            var input = dlg.Get("password.input");
            Mouse.Click(input.Center());
            Wait.Until(() => input.Current.HasKeyboardFocus, "パスワード欄にフォーカス");
            Keyboard.Type("fuseo-test-pw");
            dlg.Get("password.ok").Click();
        });
        app.WaitPageCount(1);
        Output.WriteLine($"パスワード入力中にドラッグ元が待たされたか: {sourceBlocked}");
    });

    /// <summary>
    /// 既知の不具合（2026-10-31 E2E で発見・未修正）: 対応外・壊れたファイルをドロップすると、ドロップの処理
    /// （MainWindow.OnDrop → ImportAsync）が最初の await より前に失敗し、Drop の処理の中でエラーのメッセージボックスを出す。
    /// 閉じるまでドラッグ元（エクスプローラー等）の DoDragDrop が戻らない＝ドラッグ元のウィンドウが固まる
    /// （パスワード付き PDF のパスワード入力は await の後なので固まらない＝DragAndDrop_EncryptedPdf_AsksPassword_AndImports で確認）。
    /// 直すかは判断待ち（例: Drop では受け取るだけにして、取り込みは Dispatcher.BeginInvoke で Drop が戻った後に始める）。
    /// </summary>
    [Fact(Skip = "既知の不具合: ドロップした対応外のファイルのエラーを閉じるまでドラッグ元が固まる（COVERAGE.md 参照・判断待ち）")]
    public void DragAndDrop_ErrorDialog_DoesNotBlockTheDragSource() => Run(s =>
    {
        var txt = s.InPath("note.txt");
        File.WriteAllText(txt, "not an image");
        var app = s.Launch();
        var sourceBlocked = true;
        FileDropper.Drop([txt], app.Main.At(0.5, 0.5), blocked =>
        {
            sourceBlocked = blocked;
            app.WaitMessageBox(Ja.ImportErrorTitle).Ok();
        });
        Assert.False(sourceBlocked, "エラーの表示中、ドラッグ元の DoDragDrop が戻らない");
    });

    [Fact]
    public void BrokenFile_ShowsError_AndImportsNothing() => Run(s =>
    {
        var broken = s.InPath("broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var brokenPdf = s.InPath("broken.pdf");
        File.WriteAllText(brokenPdf, "%PDF-1.4\nthis is not a pdf\n");
        var app = s.Launch();
        app.OpenWithButton(broken);
        var box = app.WaitMessageBox(Ja.ImportErrorTitle);
        Assert.Contains("broken.png", box.Message);
        box.Ok();
        app.OpenWithButton(brokenPdf);
        box = app.WaitMessageBox(Ja.ImportErrorTitle);
        Assert.Contains("broken.pdf", box.Message);
        box.Ok();
        app.WaitNoDialogs();
        Assert.Equal(0, app.PageCount());
    });

    [Fact]
    public void PageLimit_PerFileAndTotal_ShowsError_AndImportsNothing() => Run(s =>
    {
        var white = new Synth.PdfPage(1, 1, 1);
        var p51 = Synth.WritePdf(s.InPath("p51.pdf"), Enumerable.Repeat(white, 51).ToList());
        var p50 = Synth.WritePdf(s.InPath("p50.pdf"), Enumerable.Repeat(white, 50).ToList());
        var png = Synth.WritePng(s.InPath("one.png"), Synth.Card(400, 300));
        var app = s.Launch();
        app.OpenWithButton(p51);
        var box = app.WaitMessageBox(Ja.ImportErrorTitle);
        Assert.Contains("「p51.pdf」は 51 ページあります。一度に取り込めるのは 50 ページまでです。", box.Message);
        box.Ok();
        Assert.Equal(0, app.PageCount());
        // 開いている 1 ページと合わせて 51 ページ
        app.Import(1, png);
        app.OpenWithButton(p50);
        box = app.WaitMessageBox(Ja.ImportErrorTitle);
        Assert.Contains("合計 50 ページまで", box.Message);
        Assert.Contains("51 ページ", box.Message);
        box.Ok();
        app.WaitNoDialogs();
        Assert.Equal(1, app.PageCount());
    });
}
