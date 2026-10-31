using System.Runtime.CompilerServices;
using Fuseo.E2E.Driver;
using Xunit.Abstractions;

namespace Fuseo.E2E;

/// <summary>
/// E2E テストの土台。1 テスト = 1 つの <see cref="Session"/>（専用の作業フォルダ・データフォルダ・起動したアプリ）。
/// 失敗したら開いているアプリのウィンドウを撮ってから投げ直し、最後に必ずアプリを終わらせる。
/// </summary>
public abstract class E2ETest(ITestOutputHelper output)
{
    static E2ETest()
    {
        // UIA の矩形と SendInput の座標を物理ピクセルでそろえる（VM は 200%）。
        Native.SetProcessDpiAwarenessContext(Native.PerMonitorAwareV2);
    }

    protected ITestOutputHelper Output { get; } = output;

    /// <summary>
    /// 1 テストの上限。超えたら見張りがアプリを強制終了し（止まっている UIA の呼び出し・ダイアログ待ちがそこで例外になって抜ける）、
    /// テストを時間切れで落とす。さらに外側で dotnet test の --blame-hang-timeout と run-e2e.ps1 の全体の上限が掛かる。
    /// </summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    internal void Run(Action<Session> body, [CallerMemberName] string name = "")
    {
        using var session = new Session(name);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var watchdog = new Timer(_ => session.Abort(), null, TestTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            body(session);
            if (session.Aborted) throw new TimeoutException($"{TestTimeout.TotalMinutes:0} 分を超えたのでアプリを強制終了した");
            IsolationGuard.Verify();
        }
        catch (Exception e)
        {
            var shot = session.Aborted ? null : session.CaptureScreens();
            Output.WriteLine($"失敗: {e.GetType().Name}: {e.Message}");
            if (session.Aborted) Output.WriteLine($"{TestTimeout.TotalMinutes:0} 分の上限で強制終了した（止まっていた操作の例外: {e.GetType().Name}）");
            if (shot is not null) Output.WriteLine($"画面: {shot}");
            if (session.Aborted && e is not TimeoutException)
                throw new TimeoutException($"{TestTimeout.TotalMinutes:0} 分を超えたのでアプリを強制終了した", e);
            throw;
        }
        finally
        {
            Output.WriteLine($"{name}: {sw.Elapsed.TotalSeconds:0.0} 秒");
        }
    }
}

/// <summary>1 テスト分の作業フォルダと、起動したアプリ。</summary>
internal sealed class Session : IDisposable
{
    private readonly List<FuseoApp> _apps = [];

    public Session(string name)
    {
        Name = name;
        Dir = Path.Combine(E2EPaths.Run, name);
        E2EPaths.EnsureLocal(Dir);
        Directory.CreateDirectory(In);
        Directory.CreateDirectory(Out);
    }

    public string Name { get; }
    public string Dir { get; }
    public string In => Path.Combine(Dir, "in");
    public string Out => Path.Combine(Dir, "out");

    /// <summary>このテストの既定のデータフォルダ（FUSEO_DATA_DIR）。再起動のテストは同じものを渡す。</summary>
    public string Data => Path.Combine(Dir, "data");

    public string InPath(string file) => Path.Combine(In, file);
    public string OutPath(string file) => Path.Combine(Out, file);

    public FuseoApp Launch(string? dataDir = null)
    {
        if (Aborted) throw new TimeoutException("時間切れのあとは起動しない");
        var app = FuseoApp.Launch(dataDir ?? Data);
        lock (_apps) _apps.Add(app);
        return app;
    }

    public string? CaptureScreens()
    {
        // ファイルのダイアログは初期フォルダ（ダウンロード等＝Mac の共有）の中身を映すので、撮る前に必ず閉じる。
        foreach (var app in _apps.Where(a => !a.HasExited)) FileDialogs.CancelAny(app);
        var windows = new List<IntPtr>();
        foreach (var app in _apps.Where(a => !a.HasExited))
        {
            try { windows.AddRange(app.Windows().Select(w => new IntPtr(w.Current.NativeWindowHandle))); }
            catch (Exception e) when (e is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException) { }
        }
        return windows.Count == 0 ? null : Screenshot.CaptureWindows($"{DateTime.Now:HHmmss}-{Name}", windows.Distinct());
    }

    public bool Aborted { get; private set; }

    /// <summary>時間切れ（見張りのスレッドから呼ぶ）: 押したままの入力を離し、アプリを強制終了する。</summary>
    public void Abort()
    {
        Aborted = true;
        try
        {
            Mouse.Up(MouseButton.Left);
            Mouse.Up(MouseButton.Right);
            Mouse.Up(MouseButton.Middle);
            foreach (var k in new[] { VKey.Control, VKey.Shift, VKey.Space }) Keyboard.Up(k);
        }
        catch (InvalidOperationException) { }
        List<FuseoApp> apps;
        lock (_apps) apps = [.. _apps];
        foreach (var app in apps) app.Kill();
    }

    public void Dispose()
    {
        foreach (var app in _apps) app.Dispose();
    }
}
