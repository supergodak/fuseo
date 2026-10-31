using System.Windows;
using System.Windows.Automation;
using Fuseo.E2E.Driver;
using Xunit;

namespace Fuseo.E2E;

/// <summary>メイン画面の操作（利用者がする操作の単位）。押す・打つ・ドラッグはすべて本物の入力（SendInput）で行う。</summary>
internal static class Actions
{
    // ---- 状態の読み取り ----

    public static List<AutomationElement> PageItems(this FuseoApp app) =>
        app.Main.Find("main.pages") is { } list ? list.Children(ControlType.ListItem) : [];

    public static int PageCount(this FuseoApp app) => app.Main.FindAll("page.title").Count;

    public static List<string> PageTitles(this FuseoApp app) => app.Main.FindAll("page.title").Select(e => e.Text()).ToList();

    public static string MaskCountOf(this FuseoApp app, int index) => app.PageItems()[index].Find("page.maskCount")?.Text() ?? "";

    public static void WaitPageCount(this FuseoApp app, int n) => Wait.Until(() => app.PageCount() == n, $"ページが {n} 枚");

    public static void WaitMaskCount(this FuseoApp app, int n, int pageIndex = 0) =>
        Wait.Until(() => app.PageItems().Count > pageIndex && app.MaskCountOf(pageIndex) == Ja.MaskCount(n), $"{pageIndex + 1} ページ目が「{Ja.MaskCount(n)}」");

    public static string Status(this FuseoApp app) => app.Main.Find("main.status")?.Text() ?? "";

    public static AutomationElement Image(this FuseoApp app) =>
        Wait.For(() => app.Main.Find("main.pageImage") is { } e && e.IsShown() ? e : null, "ページの画像が出る");

    public static AutomationElement Button(this FuseoApp app, string id) => app.Main.Get(id);

    // ---- 取り込み ----

    /// <summary>「ファイルを開く…」ボタン → 開くダイアログで files を選ぶ。</summary>
    public static void OpenWithButton(this FuseoApp app, params string[] files)
    {
        app.Button("main.openFiles").Click();
        FileDialogs.Open(app, Ja.OpenDialog, files);
    }

    /// <summary>Ctrl+O → 開くダイアログで files を選ぶ。</summary>
    public static void OpenWithCtrlO(this FuseoApp app, params string[] files)
    {
        app.Key(VKey.O, VKey.Control);
        FileDialogs.Open(app, Ja.OpenDialog, files);
    }

    /// <summary>開いて、ページが expectedTotal 枚になるまで待つ。</summary>
    public static void Import(this FuseoApp app, int expectedTotal, params string[] files)
    {
        app.OpenWithButton(files);
        app.WaitPageCount(expectedTotal);
        Wait.Until(() => app.Button("main.openFiles").IsEnabled(), "取り込みが終わる");
    }

    // ---- キー ----

    public static void Key(this FuseoApp app, VKey key, params VKey[] modifiers)
    {
        app.Activate();
        Keyboard.Press(key, modifiers);
    }

    // ---- キャンバス ----

    public static Point ImagePoint(this FuseoApp app, double u, double v) => app.Image().At(u, v);

    /// <summary>今の道具で (u0,v0)→(u1,v1) をドラッグする（画像の正規化座標）。</summary>
    public static void DragOnImage(this FuseoApp app, double u0, double v0, double u1, double v1, MouseButton button = MouseButton.Left)
    {
        var img = app.Image();
        Mouse.Drag(img.At(u0, v0), img.At(u1, v1), button);
    }

    public static void ClickOnImage(this FuseoApp app, double u, double v) => Mouse.Click(app.ImagePoint(u, v));

    public static void SelectTool(this FuseoApp app, string id)
    {
        var b = app.Button(id);
        b.Click();
        Wait.Until(() => b.IsSelected(), $"{id} が選ばれる");
    }

    /// <summary>矩形の道具で塗る（mask 数が 1 増えるまで待つ）。</summary>
    public static void DrawRect(this FuseoApp app, Box r, int pageIndex = 0)
    {
        var before = app.MaskCountOf(pageIndex);
        app.DragOnImage(r.X, r.Y, r.Right, r.Bottom);
        Wait.Until(() => app.MaskCountOf(pageIndex) != before, "矩形が足される");
    }

    // ---- 書類の種類 ----

    public static void ChooseDocumentType(this FuseoApp app, string displayName) => ChooseComboItem(app.Main, app.Main.Get("main.documentType"), displayName);

    /// <summary>コンボボックスを本物のクリックで開いて項目を選ぶ。</summary>
    public static void ChooseComboItem(AutomationElement window, AutomationElement combo, string name)
    {
        combo.Click();
        var item = Wait.For(() => combo.FindByName(name, ControlType.ListItem) is { } i && i.IsShown() ? i : null, $"「{name}」が一覧に出る");
        // ドロップダウンは開くときに滑って出てくるので、項目の位置が止まってからクリックする（動いている間に押すと隣の項目になる＝実測）。
        Mouse.Click(StableCenter(item));
        Wait.Until(() => ((SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()
                         .FirstOrDefault()?.Current.Name == name, $"「{name}」が選ばれる");
    }

    /// <summary>要素の位置が 2 回続けて同じになるまで待ってから中央を返す（アニメーション中の要素用）。</summary>
    public static Point StableCenter(AutomationElement e)
    {
        var last = e.Bounds();
        Wait.Until(() =>
        {
            Thread.Sleep(80);
            var now = e.Bounds();
            var same = now == last && now.Width > 0;
            last = now;
            return same;
        }, $"{Ui.Describe(e)} の位置が止まる");
        return new Point(last.X + last.Width / 2, last.Y + last.Height / 2);
    }

    // ---- 書き出し ----

    /// <summary>書き出しシートを開いて、焼き込み（プレビュー）が終わるまで待つ。</summary>
    public static AutomationElement OpenExport(this FuseoApp app, bool viaShortcut = false)
    {
        if (viaShortcut) app.Key(VKey.E, VKey.Control);
        else app.Button("main.export").Click();
        var w = app.WaitWindow(Ja.ExportWindow);
        Wait.Until(() => w.Find("export.confirmButton") is { } b && b.IsEnabled(), "プレビューの準備が終わる", TimeSpan.FromSeconds(30));
        return w;
    }

    public static void ChooseFormat(AutomationElement exportWindow, string format)
    {
        var r = exportWindow.Get($"export.format.{format}");
        r.Click();
        Wait.Until(() => r.IsSelected(), $"{format} が選ばれる");
    }

    /// <summary>書き出しシートで「保存…」→ 保存ダイアログで path → 書き出しが終わってシートが閉じるまで。</summary>
    public static void SaveExport(this FuseoApp app, AutomationElement exportWindow, string path)
    {
        exportWindow.Get("export.confirmButton").Click();
        FileDialogs.Save(app, Ja.SaveDialog, path);
        app.WaitWindowClosed(Ja.ExportWindow, TimeSpan.FromSeconds(30));
    }

    /// <summary>形式を選んで書き出す（1 回で済ませる）。</summary>
    public static void Export(this FuseoApp app, string format, string path, bool viaShortcut = false)
    {
        var w = app.OpenExport(viaShortcut);
        ChooseFormat(w, format);
        app.SaveExport(w, path);
        Wait.Until(() => app.Status().Contains("書き出しました", StringComparison.Ordinal), "書き出し完了の表示");
    }

    // ---- 作業中の書類（ライブラリ） ----

    public static List<AutomationElement> LibraryItems(this FuseoApp app) =>
        app.Main.Find("library.list") is { } list && list.IsShown() ? list.Children(ControlType.ListItem) : [];

    public static void WaitLibraryCount(this FuseoApp app, int n) =>
        Wait.Until(() => app.LibraryItems().Count == n, $"作業中の書類が {n} 件");

    public static void NewDocument(this FuseoApp app)
    {
        app.Button("main.newDocument").Click();
        app.WaitPageCount(0);
    }

    // ---- 確かめる ----

    public static void AssertNoDialogs(this FuseoApp app) =>
        Assert.True(app.Windows().Count(w => w.Current.NativeWindowHandle != app.Main.Current.NativeWindowHandle) == 0, "ダイアログが残っている");
}
