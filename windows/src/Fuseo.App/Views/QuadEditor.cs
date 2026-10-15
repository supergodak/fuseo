using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Fuseo.Core.Geometry;

namespace Fuseo.App.Views;

/// <summary>
/// 切り抜き画面（W4.5）の四隅つまみ。画像要素と同じ大きさで重ね、画像は縦横比を保って中央に収まっている前提
/// （<see cref="CoordinateSpace.FitRect"/>・<see cref="CoordinateSpace.FromView"/> で変換＝手変換しない）。
/// 枠の外を暗くし、四辺と丸いつまみを描く。凸でない（交差・へこみ）ときは枠を赤にする。
/// </summary>
public sealed class QuadEditor : FrameworkElement
{
    private const double HandleRadius = 8;
    private const double HitRadius = 22; // つまみの当たり判定（指でも掴める大きさ）

    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(21, 101, 192)));
    private static readonly Brush Invalid = Freeze(new SolidColorBrush(Color.FromRgb(198, 40, 40)));
    private static readonly Pen HandleRing = Freeze(new Pen(Brushes.White, 2));

    private readonly NormPoint[] _corners = Quad.FullImage.Corners;
    private int _dragging = -1;

    public double ImagePixelWidth { get; set; }
    public double ImagePixelHeight { get; set; }

    /// <summary>四隅が変わった（ドラッグ・全体に戻す）。</summary>
    public event EventHandler? QuadChanged;

    public Quad Quad
    {
        get => Quad.FromCorners(_corners);
        set
        {
            value.Corners.CopyTo(_corners, 0);
            InvalidateVisual();
            QuadChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private (double X, double Y, double W, double H) Fit =>
        CoordinateSpace.FitRect(ImagePixelWidth, ImagePixelHeight, ActualWidth, ActualHeight);

    private Point ToView(NormPoint p)
    {
        var f = Fit;
        return new Point(f.X + p.X * f.W, f.Y + p.Y * f.H);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 透明な下地（つまみの外でもマウスを受ける）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var f = Fit;
        if (f.W <= 0) return;
        var pts = _corners.Select(ToView).ToArray();
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(pts[0], true, true);
            g.PolyLineTo(pts.Skip(1).ToArray(), true, false);
        }
        outline.Freeze();
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(f.X, f.Y, f.W, f.H)), outline);
        dc.DrawGeometry(Shade, null, shade);
        var edge = Quad.IsConvex ? Accent : Invalid;
        dc.DrawGeometry(null, new Pen(edge, 2), outline);
        foreach (var p in pts) dc.DrawEllipse(edge, HandleRing, p, HandleRadius, HandleRadius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this);
        var best = -1;
        var bestDist = HitRadius;
        for (var i = 0; i < 4; i++)
        {
            var d = (ToView(_corners[i]) - at).Length;
            if (d <= bestDist) { best = i; bestDist = d; }
        }
        if (best < 0) return;
        _dragging = best;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging < 0) return;
        var at = e.GetPosition(this);
        _corners[_dragging] = CoordinateSpace.FromView(at.X, at.Y, ImagePixelWidth, ImagePixelHeight, ActualWidth, ActualHeight);
        InvalidateVisual();
        QuadChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _dragging = -1;

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
