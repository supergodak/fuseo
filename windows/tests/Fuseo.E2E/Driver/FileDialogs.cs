using System.Windows.Automation;

namespace Fuseo.E2E.Driver;

/// <summary>
/// Windows 標準の「開く」「名前を付けて保存」ダイアログ（IFileDialog・#32770）。ファイル名の欄に**完全パス**を入れて決定する
/// （ダイアログの初期フォルダは「ドキュメント」等＝Mac の共有なので、そこへは移動もしない・何も作らない）。
/// ファイル名の欄: 開く = ComboBox/Edit の AutomationId 1148、保存 = Edit 1001。決定ボタン = 1、キャンセル = 2。
/// </summary>
internal static class FileDialogs
{
    public static AutomationElement WaitDialog(FuseoApp app, string title) =>
        Wait.For(() => app.Windows().FirstOrDefault(w => Safe(() => w.Current.Name) == title && Safe(() => w.Current.ClassName) == "#32770"),
                 $"ファイルのダイアログ「{title}」が開く", TimeSpan.FromSeconds(20));

    /// <summary>「開く」で files（同じフォルダ）を選んで決定する。</summary>
    public static void Open(FuseoApp app, string title, IReadOnlyList<string> files)
    {
        var dlg = WaitDialog(app, title);
        var dir = Path.GetDirectoryName(files[0])!;
        if (files.Any(f => !string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("同じフォルダのファイルだけ選べる", nameof(files));
        if (files.Count == 1)
        {
            EnterFileName(dlg, files[0]);
        }
        else
        {
            // 複数: まずフォルダへ移動してから（ファイル名の欄が空に戻る）、"a" "b" の形で名前を並べる。
            EnterFileName(dlg, dir);
            Wait.Until(() => FileNameEdit(dlg).Value() is not { Length: > 0 } v || !v.Equals(dir, StringComparison.OrdinalIgnoreCase),
                       "フォルダへ移動する");
            EnterFileName(dlg, string.Join(" ", files.Select(f => $"\"{Path.GetFileName(f)}\"")));
        }
        Wait.Until(() => !Alive(dlg), $"「{title}」が閉じる");
    }

    /// <summary>「名前を付けて保存」で path（完全パス）を入れて決定する。</summary>
    public static void Save(FuseoApp app, string title, string path)
    {
        var dlg = WaitDialog(app, title);
        EnterFileName(dlg, path);
        Wait.Until(() => !Alive(dlg), $"「{title}」が閉じる");
    }

    /// <summary>
    /// ファイル名の欄に本物のキー入力で打って Enter（ValuePattern の SetValue はダイアログに伝わらないことがあり、
    /// 一覧で選ばれていた別のファイルが開かれた＝実測。人と同じ操作にする）。打った値が入ったことを確かめてから決定する。
    /// </summary>
    private static void EnterFileName(AutomationElement dlg, string text)
    {
        FuseoApp.ActivateWindow(dlg);
        var edit = FileNameEdit(dlg);
        Mouse.Click(edit.Center());
        Wait.Until(() => edit.Current.HasKeyboardFocus, "ファイル名の欄にフォーカス");
        Keyboard.Press(VKey.A, VKey.Control);
        Keyboard.Press(VKey.Delete);
        Keyboard.Type(text);
        Wait.Until(() => edit.Value() == text, "ファイル名の欄に打った値が入る");
        Keyboard.Press(VKey.Enter);
    }

    /// <summary>開いているファイルのダイアログをすべて取り消す（失敗時のスクリーンショットの前に呼ぶ）。</summary>
    public static void CancelAny(FuseoApp app)
    {
        for (var round = 0; round < 3; round++)
        {
            List<AutomationElement> dialogs;
            try
            {
                dialogs = app.Windows().Where(w => Safe(() => w.Current.ClassName) == "#32770" &&
                    w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                     .Cast<AutomationElement>().Any(e => Safe(() => e.Current.AutomationId) is "1148" or "1001")).ToList();
            }
            catch (ElementNotAvailableException) { continue; }
            if (dialogs.Count == 0) return;
            foreach (var d in dialogs)
            {
                try { ButtonById(d, "2")?.Invoke(); } catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
            }
            Thread.Sleep(300);
        }
    }

    public static void Cancel(FuseoApp app, string title)
    {
        var dlg = WaitDialog(app, title);
        Wait.For(() => ButtonById(dlg, "2"), "キャンセルボタン").Invoke();
        Wait.Until(() => !Alive(dlg), $"「{title}」が閉じる");
    }

    /// <summary>ダイアログ直下のボタン（一覧の項目にも同じ番号の AutomationId があるので種類で絞る）。</summary>
    public static AutomationElement? ButtonById(AutomationElement dlg, string id) =>
        dlg.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));

    private static AutomationElement FileNameEdit(AutomationElement dlg) =>
        Wait.For(() =>
        {
            var edits = dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().ToList();
            return edits.FirstOrDefault(e => Safe(() => e.Current.AutomationId) == "1148")
                   ?? edits.FirstOrDefault(e => Safe(() => e.Current.AutomationId) == "1001");
        }, "ファイル名の欄");

    private static bool Alive(AutomationElement e)
    {
        try { return e.Current.BoundingRectangle.Width > 0; }
        catch (ElementNotAvailableException) { return false; }
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (ElementNotAvailableException) { return default; }
    }
}
