using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using static Fuseo.E2E.Driver.Native;

namespace Fuseo.E2E.Driver;

/// <summary>失敗したときの画面（アプリのウィンドウの矩形だけ）を PNG で残す。保存先は VM のローカル（E2EPaths.Screenshots）。</summary>
internal static class Screenshot
{
    /// <summary>
    /// ウィンドウの見えている枠だけを撮る（GetWindowRect／UIA の矩形は影の分だけ外へはみ出し、後ろのデスクトップ
    /// ＝Mac と共有のフォルダのファイル名が写るので使わない）。
    /// </summary>
    public static string? CaptureWindows(string name, IEnumerable<IntPtr> windows) =>
        CaptureWindows(name, windows.Select(VisibleFrame).Where(r => !r.IsEmpty));

    private static Rect VisibleFrame(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0
            ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : Rect.Empty;

    private static string? CaptureWindows(string name, IEnumerable<Rect> rects)
    {
        try
        {
            Directory.CreateDirectory(E2EPaths.Screenshots);
            string? first = null;
            var i = 0;
            foreach (var r in rects.Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0))
            {
                var path = Path.Combine(E2EPaths.Screenshots, $"{Sanitize(name)}-{i++}.png");
                Capture(r, path);
                first ??= path;
            }
            return first;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Console.WriteLine($"スクリーンショットを撮れなかった: {e.Message}");
            return null;
        }
    }

    private static void Capture(Rect r, string path)
    {
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
