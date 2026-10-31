using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace Fuseo.E2E.Driver;

/// <summary>
/// 起動した Fuseo.exe 1 つ（テストごとに新しいプロセス）。設定とライブラリは <c>FUSEO_DATA_DIR</c> でテスト専用のフォルダに隔離する。
/// Dispose で必ずプロセスを終わらせる（閉じられなければ強制終了）。
/// </summary>
internal sealed class FuseoApp : IDisposable
{
    public const string MainTitle = "Fuseo for Windows";

    private readonly Process _process;

    private FuseoApp(Process process, AutomationElement main, string dataDir)
    {
        _process = process;
        Main = main;
        DataDir = dataDir;
    }

    public AutomationElement Main { get; private set; }
    public string DataDir { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;
    public IntPtr Handle => new(Main.Current.NativeWindowHandle);

    public static FuseoApp Launch(string dataDir)
    {
        IsolationGuard.Snapshot(); // 初回だけ: 利用者の %LOCALAPPDATA%\Fuseo の様子を控える（最後まで変わらないことを確かめる）
        E2EPaths.EnsureLocal(dataDir);
        Directory.CreateDirectory(dataDir);
        var info = new ProcessStartInfo(E2EPaths.Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dataDir)!,
        };
        info.Environment[E2EPaths.DataDirVariable] = dataDir;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Fuseo.exe を起動できない");
        try
        {
            var main = Wait.For(() => TopLevelWindows(process.Id).FirstOrDefault(w => w.Current.Name == MainTitle),
                                "メイン画面が開く", TimeSpan.FromSeconds(60)); // 単一ファイルの初回展開は時間がかかる
            var app = new FuseoApp(process, main, dataDir);
            app.PlaceWindow();
            Wait.Until(() => main.Find("main.openFiles").IsShown(), "メイン画面のボタンが出る");
            app.Activate();
            return app;
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>画面の左上に寄せる（既定の位置だと下が画面外に出ることがある）。</summary>
    private void PlaceWindow()
    {
        if (Main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp) &&
            ((WindowPattern)wp).Current.WindowVisualState != WindowVisualState.Normal)
            ((WindowPattern)wp).SetWindowVisualState(WindowVisualState.Normal);
        if (Main.TryGetCurrentPattern(TransformPattern.Pattern, out var tp) && ((TransformPattern)tp).Current.CanMove)
            ((TransformPattern)tp).Move(16, 16);
        Wait.Until(() => Main.Bounds().Left < 64 && Main.Bounds().Top < 64, "メイン画面が左上に来る");
    }

    /// <summary>メイン画面を前面にする（キー入力は前面のウィンドウに届く）。SetForegroundWindow が効かなければタイトルバーをクリックする。</summary>
    public void Activate() => ActivateWindow(Main);

    public static void ActivateWindow(AutomationElement window)
    {
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        if (Native.GetForegroundWindow() == hwnd) return;
        Native.SetForegroundWindow(hwnd);
        try
        {
            Wait.Until(() => Native.GetForegroundWindow() == hwnd, "ウィンドウが前面に来る", TimeSpan.FromSeconds(2));
            return;
        }
        catch (TimeoutException) { }
        var r = window.Bounds();
        Mouse.Click(new Point(r.Left + r.Width * 0.4, r.Top + 12));
        Wait.Until(() => Native.GetForegroundWindow() == hwnd, "ウィンドウが前面に来る（タイトルバーのクリック）");
    }

    /// <summary>
    /// このプロセスのトップレベルウィンドウ（所有されたウィンドウ・メッセージボックス・ファイルダイアログを含む）。
    /// 探すのは Win32 の EnumWindows（プロセス ID で絞る）で、UIA で見るのはそのウィンドウだけ。デスクトップ全体を UIA で
    /// たどると、テストプロセス自身のウィンドウ（ドラッグ元＝DoDragDrop の中で応答しない）にも問い合わせて止まる（実測）。
    /// </summary>
    public static List<AutomationElement> TopLevelWindows(int pid)
    {
        var handles = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && Native.IsWindowVisible(h)) handles.Add(h);
            return true;
        }, IntPtr.Zero);
        var all = new List<AutomationElement>();
        foreach (var h in handles)
        {
            try { all.Add(AutomationElement.FromHandle(h)); }
            catch (ElementNotAvailableException) { }
        }
        return all;
    }

    public List<AutomationElement> Windows() => TopLevelWindows(_process.Id);

    /// <summary>タイトルで別のウィンドウ（書き出し・切り抜き・設定・メッセージ等）が開くのを待つ。</summary>
    public AutomationElement WaitWindow(string title, TimeSpan? timeout = null) =>
        Wait.For(() => Windows().FirstOrDefault(w => SafeName(w) == title && !IsMain(w)), $"「{title}」のウィンドウが開く", timeout);

    public AutomationElement? FindWindow(string title) => Windows().FirstOrDefault(w => SafeName(w) == title && !IsMain(w));

    public void WaitWindowClosed(string title, TimeSpan? timeout = null) =>
        Wait.Until(() => FindWindow(title) is null, $"「{title}」のウィンドウが閉じる", timeout);

    /// <summary>メイン画面以外に開いているウィンドウが無いこと。</summary>
    public void WaitNoDialogs() => Wait.Until(() => Windows().All(IsMain), "ダイアログがすべて閉じる");

    private bool IsMain(AutomationElement w)
    {
        try { return w.Current.NativeWindowHandle == Main.Current.NativeWindowHandle; }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string? SafeName(AutomationElement w)
    {
        try { return w.Current.Name; }
        catch (ElementNotAvailableException) { return null; }
    }

    /// <summary>メッセージボックス（Win32 の #32770）を待つ。title で絞る。</summary>
    public MessageBoxDriver WaitMessageBox(string title, TimeSpan? timeout = null)
    {
        var w = Wait.For(() => Windows().FirstOrDefault(x => SafeName(x) == title && SafeClass(x) == "#32770" && x.Find("65535") is not null),
                         $"メッセージ「{title}」が出る", timeout);
        return new MessageBoxDriver(w);
    }

    public MessageBoxDriver? FindMessageBox() =>
        Windows().FirstOrDefault(x => SafeClass(x) == "#32770" && x.Find("65535") is not null) is { } w ? new MessageBoxDriver(w) : null;

    private static string? SafeClass(AutomationElement w)
    {
        try { return w.Current.ClassName; }
        catch (ElementNotAvailableException) { return null; }
    }

    /// <summary>×ボタンと同じ閉じ方（WM_CLOSE）で閉じて、プロセスが終わるのを待つ。</summary>
    public void CloseAndWaitExit(TimeSpan? timeout = null)
    {
        ((WindowPattern)Main.GetCurrentPattern(WindowPattern.Pattern)).Close();
        if (!_process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds))
            throw new TimeoutException("閉じてもプロセスが終わらない");
    }

    public int ExitCode => _process.ExitCode;

    /// <summary>強制終了（時間切れの見張りから）。</summary>
    public void Kill() => Kill(_process);

    public void Dispose()
    {
        Kill(_process);
        _process.Dispose();
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>Win32 のメッセージボックス（OK=1・キャンセル=2・はい=6・いいえ=7）。</summary>
internal sealed class MessageBoxDriver(AutomationElement window)
{
    public AutomationElement Window { get; } = window;
    public string Title => Window.Current.Name;
    public string Message => Window.Find("65535")?.Text() ?? "";

    /// <summary>OK。ボタンが 1 つだけのメッセージボックスでは OK の ID が 2（IDCANCEL）になる。</summary>
    public void Ok() => Press(FileDialogs.ButtonById(Window, "1") is not null ? "1" : "2");
    public void Cancel() => Press("2");

    private void Press(string id)
    {
        var title = Title;
        var b = Wait.For(() => FileDialogs.ButtonById(Window, id), $"メッセージのボタン {id}");
        b.Invoke();
        Wait.Until(() => !IsAlive() || !Window.IsShown(), $"メッセージ「{title}」が閉じる");
    }

    private bool IsAlive()
    {
        try { _ = Window.Current.Name; return true; }
        catch (ElementNotAvailableException) { return false; }
    }
}
