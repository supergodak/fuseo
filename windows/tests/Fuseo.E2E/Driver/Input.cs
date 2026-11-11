using System.Runtime.InteropServices;
using System.Windows;
using static Fuseo.E2E.Driver.Native;

namespace Fuseo.E2E.Driver;

/// <summary>
/// 本物の入力（SendInput）。座標は物理ピクセル（UIA の BoundingRectangle と同じ系）。
/// 1 つの操作の中の小さな間（イベント間 10ms 前後）は、アプリにマウス移動を 1 つずつ処理させるためのもの（状態待ちには使わない）。
/// </summary>
internal static class Mouse
{
    private const int StepPauseMs = 12;

    public static void MoveTo(Point p) => MoveTo((int)Math.Round(p.X), (int)Math.Round(p.Y));

    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        Send(new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK });
    }

    public static Point Position
    {
        get
        {
            GetCursorPos(out var p);
            return new Point(p.X, p.Y);
        }
    }

    public static void Down(MouseButton b = MouseButton.Left) =>
        Send(new MOUSEINPUT { dwFlags = b switch { MouseButton.Right => MOUSEEVENTF_RIGHTDOWN, MouseButton.Middle => MOUSEEVENTF_MIDDLEDOWN, _ => MOUSEEVENTF_LEFTDOWN } });

    public static void Up(MouseButton b = MouseButton.Left) =>
        Send(new MOUSEINPUT { dwFlags = b switch { MouseButton.Right => MOUSEEVENTF_RIGHTUP, MouseButton.Middle => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP } });

    private static DateTime _lastClick = DateTime.MinValue;
    private static Point _lastClickAt;

    /// <summary>
    /// 1 回のクリック。直前のクリックと近い位置で、ダブルクリックの間隔内なら、その間隔が過ぎるまで待ってから押す
    /// （2 回の別々のクリックがダブルクリックと見なされ、一覧の行のダブルクリック＝「開く」が走った＝実測）。
    /// </summary>
    public static void Click(Point p, MouseButton b = MouseButton.Left)
    {
        var interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50);
        var since = DateTime.UtcNow - _lastClick;
        if (since < interval && (p - _lastClickAt).Length < 40) Thread.Sleep(interval - since);
        _lastClick = DateTime.UtcNow;
        _lastClickAt = p;
        MoveTo(p);
        Thread.Sleep(StepPauseMs * 3);
        Down(b);
        Thread.Sleep(StepPauseMs);
        Up(b);
        Thread.Sleep(StepPauseMs);
    }

    public static void DoubleClick(Point p)
    {
        MoveTo(p);
        Thread.Sleep(StepPauseMs * 3);
        Down(); Up();
        Thread.Sleep(30);
        Down(); Up();
        Thread.Sleep(StepPauseMs);
    }

    /// <summary>押したまま from → to へ steps 回に分けて動かして離す。</summary>
    public static void Drag(Point from, Point to, MouseButton b = MouseButton.Left, int steps = 12) =>
        DragPath([from, to], b, steps);

    /// <summary>折れ線に沿って押したまま動かす（ブラシ用）。各区間を steps 回に分ける。</summary>
    public static void DragPath(IReadOnlyList<Point> path, MouseButton b = MouseButton.Left, int steps = 12)
    {
        MoveTo(path[0]);
        Thread.Sleep(StepPauseMs * 3);
        Down(b);
        Thread.Sleep(StepPauseMs * 2);
        for (var s = 1; s < path.Count; s++)
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                MoveTo(new Point(path[s - 1].X + (path[s].X - path[s - 1].X) * t, path[s - 1].Y + (path[s].Y - path[s - 1].Y) * t));
                Thread.Sleep(StepPauseMs);
            }
        Thread.Sleep(StepPauseMs * 2);
        Up(b);
        Thread.Sleep(StepPauseMs * 2);
    }

    /// <summary>ホイール（delta は 120 = 1 ノッチ。正 = 奥へ）。</summary>
    public static void Wheel(Point at, int delta)
    {
        MoveTo(at);
        Thread.Sleep(StepPauseMs * 3);
        Send(new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL });
        Thread.Sleep(StepPauseMs);
    }

    private static void Send(MOUSEINPUT mi)
    {
        var input = new[] { new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } } };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"SendInput（マウス）に失敗: {Marshal.GetLastWin32Error()}（画面がロックされていないか）");
    }
}

internal enum MouseButton { Left, Right, Middle }

/// <summary>仮想キーコード（必要な分だけ）。</summary>
internal enum VKey : ushort
{
    Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Alt = 0x12, Escape = 0x1B, Space = 0x20,
    Delete = 0x2E,
    A = 0x41, B = 0x42, D = 0x44, E = 0x45, L = 0x4C, N = 0x4E, O = 0x4F, R = 0x52, T = 0x54, V = 0x56, Y = 0x59, Z = 0x5A,
}

internal static class Keyboard
{
    public static void Down(VKey k) => Send((ushort)k, 0, IsExtended(k) ? KEYEVENTF_EXTENDEDKEY : 0);
    public static void Up(VKey k) => Send((ushort)k, 0, KEYEVENTF_KEYUP | (IsExtended(k) ? KEYEVENTF_EXTENDEDKEY : 0));

    /// <summary>修飾キーを押したまま key を押して離す（例: Press(VKey.Z, VKey.Control, VKey.Shift)）。</summary>
    public static void Press(VKey key, params VKey[] modifiers)
    {
        foreach (var m in modifiers) { Down(m); Thread.Sleep(10); }
        Down(key);
        Thread.Sleep(10);
        Up(key);
        foreach (var m in modifiers.Reverse()) { Thread.Sleep(10); Up(m); }
        Thread.Sleep(20);
    }

    /// <summary>文字列を Unicode 入力で打つ（キーボード配列・IME に依らない）。</summary>
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            Send(0, ch, KEYEVENTF_UNICODE);
            Send(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            Thread.Sleep(8);
        }
    }

    private static bool IsExtended(VKey k) => k is VKey.Delete;

    private static void Send(ushort vk, ushort scan, uint flags)
    {
        var input = new[] { new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } } };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"SendInput（キー）に失敗: {Marshal.GetLastWin32Error()}（画面がロックされていないか）");
    }
}
