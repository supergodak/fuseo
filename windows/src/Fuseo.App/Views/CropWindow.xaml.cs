using System.Windows;
using Fuseo.App.Services;
using Fuseo.Core.Geometry;

namespace Fuseo.App.Views;

/// <summary>4 隅切り抜きの画面（W4.5）。四隅が凸四角形のときだけ「適用」できる。</summary>
public partial class CropWindow : Window
{
    private Quad? _result;

    public CropWindow(CropRequest request)
    {
        InitializeComponent();
        Picture.Source = BitmapConvert.ToBitmapSource(request.Image);
        Editor.ImagePixelWidth = request.Image.Width;
        Editor.ImagePixelHeight = request.Image.Height;
        Editor.QuadChanged += (_, _) => UpdateValidity();
        Editor.Quad = request.Initial;
    }

    /// <summary>モーダルで開き、適用した四隅（<see cref="CropRequest.Image"/> の上）を返す。取り消しなら null。</summary>
    public static Quad? Ask(Window owner, CropRequest request)
    {
        var win = new CropWindow(request) { Owner = owner };
        return win.ShowDialog() == true ? win._result : null;
    }

    private void UpdateValidity()
    {
        var ok = Editor.Quad.IsConvex;
        ApplyButton.IsEnabled = ok;
        InvalidText.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFull(object sender, RoutedEventArgs e) => Editor.Quad = Quad.FullImage;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var q = Editor.Quad.Clamped();
        if (!q.IsConvex) return;
        _result = q;
        DialogResult = true;
    }
}
