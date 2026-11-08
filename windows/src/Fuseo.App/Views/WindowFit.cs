using System.Windows;

namespace Fuseo.App.Views;

/// <summary>
/// ダイアログの既定の大きさを作業領域（タスクバーを除いた画面）に収める。高い拡大率（例: 1080p で 150% = 高さ約 690）では
/// 書き出しシートの既定の高さ（860）が画面からはみ出し、下端の「保存」「キャンセル」が押せなくなっていた（W9-4 の目視で発見）。
/// 縮めた分は各ウィンドウの中のスクロール領域が吸収する。最小サイズも作業領域を超えないようにする。
/// </summary>
internal static class WindowFit
{
    public static void ToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxHeight = area.Height;
        if (window.Width > area.Width) window.Width = area.Width;
        if (window.Height > area.Height) window.Height = area.Height;
    }
}
