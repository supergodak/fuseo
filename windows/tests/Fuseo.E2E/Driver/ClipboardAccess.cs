using System.Runtime.InteropServices;
using System.Windows;

namespace Fuseo.E2E.Driver;

/// <summary>クリップボードの読み書き（STA のスレッドで行う）。Parallels は Mac とクリップボードを共有するので、テストの前の中身を戻す。</summary>
internal static class ClipboardAccess
{
    public static string? GetText() => OnSta(() =>
    {
        for (var i = 0; i < 20; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(50); }
        }
        return null;
    });

    public static void SetText(string? text) => OnSta<object?>(() =>
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (text is null) Clipboard.Clear(); else Clipboard.SetText(text);
                return null;
            }
            catch (COMException) { Thread.Sleep(50); }
        }
        return null;
    });

    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("クリップボードを読めない", error);
        return result;
    }
}
