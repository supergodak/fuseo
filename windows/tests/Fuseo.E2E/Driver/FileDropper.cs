using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fuseo.E2E.Driver;

/// <summary>
/// エクスプローラーからのドロップの再現: テストプロセスに小さなウィンドウを出し、そこで本物のマウスを押すと OLE の
/// DoDragDrop（FileDrop データ）を始める。押したまま SendInput でアプリの上まで動かして離す＝アプリから見ると
/// 別プロセスから OLE でファイルが落ちてくる（エクスプローラーからのドロップと同じ経路）。
/// </summary>
internal static class FileDropper
{
    /// <param name="whileDropping">
    /// ドロップ先が Drop の処理の中でモーダルのダイアログを出すと、OLE の DoDragDrop（ドラッグ元）はそれが閉じるまで戻らない。
    /// その間に行う操作（ダイアログを閉じる等）。DoDragDrop がまだ戻っていなければ true を渡す。
    /// </param>
    public static DragDropEffects Drop(IReadOnlyList<string> files, Point target, Action<bool>? whileDropping = null)
    {
        var ready = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var finished = new ManualResetEventSlim();
        Rect helperRect = Rect.Empty;
        var effect = DragDropEffects.None;
        Dispatcher? dispatcher = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var w = new Window
                {
                    Width = 160, Height = 160, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Topmost = true, ShowInTaskbar = false, Background = Brushes.SteelBlue, Title = "fuseo-e2e-drag-source",
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                w.PreviewMouseLeftButtonDown += (_, _) =>
                {
                    started.Set();
                    var data = new DataObject(DataFormats.FileDrop, files.ToArray());
                    effect = DragDrop.DoDragDrop(w, data, DragDropEffects.Copy);
                    finished.Set();
                };
                w.Loaded += (_, _) =>
                {
                    // 物理ピクセルで右下に置く（アプリのウィンドウ 16,16〜 とは重ねない）。
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    var src = PresentationSource.FromVisual(w)!;
                    var m = src.CompositionTarget!.TransformFromDevice;
                    var screenW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                    var screenH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                    var p = m.Transform(new Point(screenW - 420, screenH - 420));
                    w.Left = p.X;
                    w.Top = p.Y;
                    w.Dispatcher.BeginInvoke(() =>
                    {
                        Native.GetWindowRect(hwnd, out var r);
                        helperRect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                        ready.Set();
                    }, DispatcherPriority.ApplicationIdle);
                };
                w.Show();
                Dispatcher.Run();
            }
            catch (Exception e)
            {
                error = e;
                ready.Set();
                finished.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(10)) || error is not null) throw new InvalidOperationException("ドラッグ元のウィンドウを出せない", error);
            var from = new Point(helperRect.X + helperRect.Width / 2, helperRect.Y + helperRect.Height / 2);
            Mouse.MoveTo(from);
            Thread.Sleep(50);
            Mouse.Down();
            if (!started.Wait(TimeSpan.FromSeconds(5))) { Mouse.Up(); throw new InvalidOperationException("ドラッグが始まらない"); }
            Thread.Sleep(100);
            // 少しずつ動かす（OLE はマウスの移動でドロップ先を調べる）。
            const int steps = 40;
            for (var i = 1; i <= steps; i++)
            {
                Mouse.MoveTo(new Point(from.X + (target.X - from.X) * i / steps, from.Y + (target.Y - from.Y) * i / steps));
                Thread.Sleep(15);
            }
            for (var i = 0; i < 6; i++)
            {
                Mouse.MoveTo(new Point(target.X + (i % 2), target.Y));
                Thread.Sleep(40);
            }
            Mouse.Up();
            if (whileDropping is not null)
            {
                var returned = finished.Wait(TimeSpan.FromSeconds(3));
                whileDropping(!returned);
            }
            if (!finished.Wait(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("ドロップが終わらない（ドラッグ元が戻らない）");
            return effect;
        }
        finally
        {
            dispatcher?.InvokeShutdown();
            thread.Join(5000);
        }
    }
}
