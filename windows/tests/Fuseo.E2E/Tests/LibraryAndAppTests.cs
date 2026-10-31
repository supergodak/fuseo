using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Fuseo.E2E.Tests;

/// <summary>作業中の書類（自動保存ライブラリ）: 新しい書類（ボタン・Ctrl+N）・開く（ボタン・ダブルクリック）・削除・すべて削除（確認のキャンセル／OK）。</summary>
public class LibraryTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void NewDocument_ButtonAndCtrlN_KeepTheWorkInTheList() => Run(s =>
    {
        var a = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var b = Synth.WritePng(s.InPath("b.png"), Synth.Card());
        var app = s.Launch();
        Assert.False(app.Main.Find("library.panel").IsShown(), "保存した作業が無いのに一覧が出ている");
        app.Import(1, a);
        app.DrawRect(new Box(0.3, 0.3, 0.2, 0.2));
        app.NewDocument();
        app.WaitLibraryCount(1);
        var item = app.LibraryItems()[0];
        Assert.Equal("a.png", item.Get("library.item.title").Text());
        Assert.Equal(Ja.LibraryPages(1), item.Get("library.item.pageCount").Text());
        Assert.Equal(Ja.LibraryInProgress, item.Get("library.item.status").Text());

        app.Import(1, b);
        app.Key(VKey.N, VKey.Control);
        app.WaitPageCount(0);
        app.WaitLibraryCount(2);
        Assert.Equal(2, Autosave.WorkCount(s.Data));
    });

    [Fact]
    public void OpenButtonAndDoubleClick_RestoreMasksAndRotation() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        app.DrawRect(new Box(0.1, 0.1, 0.2, 0.2));
        app.DrawRect(new Box(0.5, 0.5, 0.2, 0.2));
        app.Button("main.rotateRight").Click();
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1 && d.Pages[0].Manual.Rects.Count == 2, "保存");
        app.NewDocument();
        app.WaitLibraryCount(1);

        app.LibraryItems()[0].Get("library.item.open").Click();
        app.WaitPageCount(1);
        app.WaitMaskCount(2);
        Wait.Until(() => app.Status().StartsWith("「card.png」を開きました", StringComparison.Ordinal), "開いた表示");
        var r = app.Image().Bounds();
        Assert.True(r.Height > r.Width, "回転が戻っていない");
        Assert.False(app.Main.Find("library.panel").IsShown());

        app.NewDocument();
        app.WaitLibraryCount(1);
        Mouse.DoubleClick(app.LibraryItems()[0].Get("library.item.title").Center());
        app.WaitPageCount(1);
        app.WaitMaskCount(2);
    });

    [Fact]
    public void DeleteOne_ConfirmCancelKeeps_OkDeletes() => Run(s =>
    {
        var a = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var b = Synth.WritePng(s.InPath("b.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, a);
        app.NewDocument();
        app.Import(1, b);
        app.NewDocument();
        app.WaitLibraryCount(2);

        app.LibraryItems()[0].Get("library.item.delete").Click();
        var box = app.WaitMessageBox(Ja.LibraryDeleteTitle);
        Assert.Contains("元に戻せません", box.Message);
        box.Cancel();
        app.WaitNoDialogs();
        Assert.Equal(2, app.LibraryItems().Count);

        var title = app.LibraryItems()[0].Get("library.item.title").Text();
        app.LibraryItems()[0].Get("library.item.delete").Click();
        app.WaitMessageBox(Ja.LibraryDeleteTitle).Ok();
        app.WaitLibraryCount(1);
        Assert.NotEqual(title, app.LibraryItems()[0].Get("library.item.title").Text());
        Wait.Until(() => Autosave.WorkCount(s.Data) == 1, "フォルダも消える");
    });

    [Fact]
    public void DeleteAll_ConfirmCancelKeeps_OkDeletesEverything() => Run(s =>
    {
        var a = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, a);
        app.NewDocument();
        app.Import(1, a);
        app.NewDocument();
        app.WaitLibraryCount(2);

        app.Button("library.deleteAll").Click();
        app.WaitMessageBox(Ja.LibraryDeleteAllTitle).Cancel();
        app.WaitNoDialogs();
        Assert.Equal(2, app.LibraryItems().Count);

        app.Button("library.deleteAll").Click();
        app.WaitMessageBox(Ja.LibraryDeleteAllTitle).Ok();
        Wait.Until(() => !app.Main.Find("library.panel").IsShown(), "一覧が消える");
        Wait.Until(() => Autosave.WorkCount(s.Data) == 0, "フォルダも消える");
    });
}

/// <summary>設定（保存場所・すべて削除・言語）とバージョン情報（コピー 2 つ・閉じる）。</summary>
public class SettingsAndAboutTests(ITestOutputHelper output) : E2ETest(output)
{
    private static AutomationElement OpenSettings(FuseoApp app, string title = Ja.SettingsWindow)
    {
        app.Button("main.settings").Click();
        return app.WaitWindow(title);
    }

    [Fact]
    public void Settings_ShowsIsolatedStorage_DeleteAllFollowsState_CloseButtonAndEscape() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("a.png"), Synth.Card());
        var app = s.Launch();
        var w = OpenSettings(app);
        // 保存場所はテスト専用のフォルダ（FUSEO_DATA_DIR）＝利用者のデータに触れていない
        Assert.Equal(Autosave.LibraryDir(s.Data), w.Get("settings.storagePath").Value());
        Assert.Equal(Ja.SettingsNoWorks, w.Get("settings.deleteAllStatus").Text());
        Assert.False(w.Get("settings.deleteAll").IsEnabled());
        w.Get("settings.close").Click();
        app.WaitWindowClosed(Ja.SettingsWindow);

        app.Import(1, png);
        w = OpenSettings(app);
        Assert.Equal(Ja.SettingsBlocked, w.Get("settings.deleteAllStatus").Text());
        Assert.False(w.Get("settings.deleteAll").IsEnabled(), "書類を開いているのに削除できる");
        FuseoApp.ActivateWindow(w);
        Keyboard.Press(VKey.Escape);
        app.WaitWindowClosed(Ja.SettingsWindow);

        app.NewDocument();
        app.WaitLibraryCount(1);
        w = OpenSettings(app);
        Assert.Equal(Ja.SettingsWorkCount(1), w.Get("settings.deleteAllStatus").Text());
        w.Get("settings.deleteAll").Click();
        app.WaitMessageBox(Ja.LibraryDeleteAllTitle).Ok();
        Wait.Until(() => w.Get("settings.deleteAllStatus").Text() == Ja.SettingsNoWorks, "削除後の案内");
        w.Get("settings.close").Click();
        app.WaitWindowClosed(Ja.SettingsWindow);
        Wait.Until(() => !app.Main.Find("library.panel").IsShown(), "一覧が消える");
        Assert.Equal(0, Autosave.WorkCount(s.Data));
    });

    [Fact]
    public void Settings_OpenFolder_OpensExplorerOnTheStorageFolder() => Run(s =>
    {
        var app = s.Launch();
        var cabinet = new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass");
        int[] Handles() => AutomationElement.RootElement.FindAll(TreeScope.Children, cabinet).Cast<AutomationElement>()
            .Select(e => e.Current.NativeWindowHandle).ToArray();
        var before = Handles();
        var w = OpenSettings(app);
        w.Get("settings.openFolder").Click();
        var explorer = Wait.For(() => AutomationElement.RootElement.FindAll(TreeScope.Children, cabinet).Cast<AutomationElement>()
            .FirstOrDefault(e => !before.Contains(e.Current.NativeWindowHandle)), "エクスプローラーが開く", TimeSpan.FromSeconds(20));
        try
        {
            Wait.Until(() => explorer.Current.Name.StartsWith("Library", StringComparison.Ordinal), "保存場所のフォルダ（Library）が開く");
            Assert.True(Directory.Exists(Autosave.LibraryDir(s.Data)), "保存場所のフォルダが作られていない");
        }
        finally
        {
            ((WindowPattern)explorer.GetCurrentPattern(WindowPattern.Pattern)).Close();
        }
        w.Get("settings.close").Click();
        app.WaitWindowClosed(Ja.SettingsWindow);
    });

    [Fact]
    public void Settings_Language_English_AppliesAfterRestart_AndBack() => Run(s =>
    {
        var app = s.Launch();
        var w = OpenSettings(app);
        Actions.ChooseComboItem(w, w.Get("settings.language"), Ja.LanguageEnglish);
        Wait.Until(() => File.Exists(Path.Combine(s.Data, "settings.json"))
                         && File.ReadAllText(Path.Combine(s.Data, "settings.json")).Contains("\"language\": \"en\""), "settings.json に保存");
        w.Get("settings.close").Click();
        app.WaitWindowClosed(Ja.SettingsWindow);
        Assert.Equal("書き出し…", app.Button("main.export").Text()); // 反映は再起動後
        app.CloseAndWaitExit();

        var en = s.Launch();
        Assert.Equal("Export…", en.Button("main.export").Text());
        Assert.Equal("Open files…", en.Button("main.openFiles").Text());
        w = OpenSettings(en, "Settings");
        Actions.ChooseComboItem(w, w.Get("settings.language"), "日本語 (Japanese)");
        w.Get("settings.close").Click();
        en.WaitWindowClosed("Settings");
        en.CloseAndWaitExit();

        var ja = s.Launch();
        Assert.Equal("書き出し…", ja.Button("main.export").Text());
    });

    [Fact]
    public void About_ShowsVersion_CopiesBothAddresses_AndCloses() => Run(s =>
    {
        var saved = ClipboardAccess.GetText(); // Parallels は Mac とクリップボードを共有するので最後に戻す
        try
        {
            var app = s.Launch();
            app.Button("main.about").Click();
            var w = app.WaitWindow(Ja.AboutWindow);
            Assert.Equal("バージョン 0.1.0", w.Get("about.version").Text());
            Assert.Contains("MIT", w.Get("about.license").Value());
            w.Get("about.copyWebsite").Click();
            Wait.Until(() => ClipboardAccess.GetText() == "https://fuseo.ati-mirai.co.jp/", "サイトのアドレスがコピーされる");
            Assert.Contains("https://fuseo.ati-mirai.co.jp/", w.Get("about.copied").Text());
            w.Get("about.copySource").Click();
            Wait.Until(() => ClipboardAccess.GetText() == "https://github.com/supergodak/fuseo", "ソースのアドレスがコピーされる");
            w.Get("about.close").Click();
            app.WaitWindowClosed(Ja.AboutWindow);
        }
        finally
        {
            ClipboardAccess.SetText(saved);
        }
    });
}

/// <summary>終了と再開: 閉じるときに保留中の自動保存を確定する・再起動後に一覧から続きを開ける。</summary>
public class LifecycleTests(ITestOutputHelper output) : E2ETest(output)
{
    [Fact]
    public void CloseRightAfterEditing_FlushesThePendingSave() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        Autosave.WaitLatest(s.Data, _ => true, "取り込みの保存");
        app.DrawRect(new Box(0.3, 0.3, 0.2, 0.2));
        app.CloseAndWaitExit(); // デバウンス（1 秒）を待たずに閉じる
        Assert.Equal(0, app.ExitCode);
        Assert.Single(Autosave.Latest(s.Data)!.Pages[0].Manual.Rects);
    });

    [Fact]
    public void Restart_ResumesFromAutosave_AndExportsTheSameResult() => Run(s =>
    {
        var png = Synth.WritePng(s.InPath("card.png"), Synth.Card());
        var app = s.Launch();
        app.Import(1, png);
        var m = new Box(0.5, 0.6, 0.2, 0.2);
        app.DrawRect(m);
        app.Button("main.rotateRight").Click();
        Autosave.WaitLatest(s.Data, d => d.Pages[0].ManualRotation == 1, "保存");
        app.CloseAndWaitExit();

        var again = s.Launch();
        again.WaitLibraryCount(1);
        var item = again.LibraryItems()[0];
        Assert.Equal(Ja.LibraryInProgress, item.Get("library.item.status").Text());
        item.Get("library.item.open").Click();
        again.WaitPageCount(1);
        again.WaitMaskCount(1);
        var outPng = s.OutPath("resumed.png");
        again.Export("png", outPng);
        var bmp = OutputCheck.LoadSingle(outPng);
        Assert.Equal((800, 1200), (bmp.Width, bmp.Height));
        var rotated = new Box(1 - m.Bottom, m.X, m.H, m.W);
        OutputCheck.AssertPainted(bmp, rotated, "再開した作業の四角");
        OutputCheck.AssertCardIntact(bmp, [rotated], OutputCheck.Unrotate(1));

        again.NewDocument();
        again.WaitLibraryCount(1);
        Wait.Until(() => again.LibraryItems()[0].Find("library.item.status")?.Text() == Ja.LibraryExported, "書き出し済みと表示される");
    });
}
