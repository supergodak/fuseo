using System.Windows;
using System.Windows.Media;
using Fuseo.App.State;
using Fuseo.Core.Geometry;

namespace Fuseo.App.Views;

/// <summary>
/// 編集用のマスク表示（半透明）。**書き出しの見た目の代用にはしない**（書き出しシートは BurnIn の結果を表示する）。
/// 画像要素と同じ大きさで重ね、正規化座標 → 表示座標は必ず <see cref="CoordinateSpace.ToView"/> を通す。
/// ズーム/パンは親コンテナの RenderTransform で掛かるので、ここでは扱わない（線の太さだけ倍率で割る）。
/// </summary>
public sealed class MaskOverlay : FrameworkElement
{
    private static readonly Brush ManualFill = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush ManualEdge = Freeze(new SolidColorBrush(Color.FromRgb(230, 81, 0)));
    private static readonly Brush CandidateFill = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush CandidateEdge = Freeze(new SolidColorBrush(Color.FromRgb(21, 101, 192)));
    private static readonly Brush SelectedEdge = Freeze(new SolidColorBrush(Color.FromRgb(255, 214, 0)));
    private static readonly Brush DraftFill = Freeze(new SolidColorBrush(Color.FromArgb(110, 230, 81, 0)));

    public PageState? Page { get; set; }
    public MaskSelection? Selection { get; set; }

    /// <summary>ドラッグ中の矩形（確定前）。</summary>
    public NormRect? DraftRect { get; set; }

    /// <summary>ドラッグ中のブラシ（確定前）と太さ（画像短辺比）。</summary>
    public IReadOnlyList<NormPoint>? DraftStroke { get; set; }
    public double DraftStrokeWidth { get; set; }

    /// <summary>移動中の手動矩形（添字と移動後の位置）。</summary>
    public (int Index, NormRect Rect)? MovingRect { get; set; }

    /// <summary>表示倍率（線の太さを画面上で一定に保つため）。</summary>
    public double Zoom { get; set; } = 1;

    protected override void OnRender(DrawingContext dc)
    {
        if (Page is not { } page) return;
        double iw = page.Base.Width, ih = page.Base.Height, vw = ActualWidth, vh = ActualHeight;
        if (vw <= 0 || vh <= 0) return;
        var t = 1.5 / Math.Max(Zoom, 0.01);
        var dash = new DashStyle([4, 3], 0);

        foreach (var c in page.Candidates)
        {
            var r = View(c.Box);
            var selected = Selection is { Kind: SelectionKind.Candidate } s && s.CandidateId == c.Id;
            var pen = new Pen(selected ? SelectedEdge : CandidateEdge, selected ? t * 2 : t);
            if (!c.IsOn) pen.DashStyle = dash;
            dc.DrawRectangle(c.IsOn ? CandidateFill : null, pen, r);
        }

        var rects = page.Manual.Rects;
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = MovingRect is { } m && m.Index == i ? m.Rect : rects[i];
            var selected = Selection is { Kind: SelectionKind.ManualRect } s && s.RectIndex == i;
            dc.DrawRectangle(ManualFill, new Pen(selected ? SelectedEdge : ManualEdge, selected ? t * 2 : t), View(rect));
        }

        foreach (var stroke in page.Manual.Strokes) DrawStroke(dc, stroke.Points, stroke.Width, ManualFill);
        if (DraftStroke is { Count: > 0 } draft) DrawStroke(dc, draft, DraftStrokeWidth, DraftFill);
        if (DraftRect is { } d) dc.DrawRectangle(DraftFill, new Pen(ManualEdge, t), View(d));

        Rect View(NormRect n)
        {
            var v = CoordinateSpace.ToView(n, iw, ih, vw, vh);
            return new Rect(v.X, v.Y, v.W, v.H);
        }

        Point ViewPoint(NormPoint p)
        {
            var v = CoordinateSpace.ToView(new NormRect(p.X, p.Y, 0, 0), iw, ih, vw, vh);
            return new Point(v.X, v.Y);
        }

        void DrawStroke(DrawingContext ctx, IReadOnlyList<NormPoint> pts, double widthRatio, Brush brush)
        {
            // 太さは「表示中の画像の短辺 × 比」。画像の表示領域は CoordinateSpace.FitRect から取る。
            var fit = CoordinateSpace.FitRect(iw, ih, vw, vh);
            var thickness = Math.Max(1, widthRatio * Math.Min(fit.W, fit.H));
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            var first = ViewPoint(pts[0]);
            if (pts.Count == 1)
            {
                // 1 点だけ＝丸（長さ 0 の線は WPF が描かないことがあるので円で描く）。
                ctx.DrawEllipse(brush, null, first, thickness / 2, thickness / 2);
                return;
            }
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(first, false, false);
                for (var i = 1; i < pts.Count; i++) g.LineTo(ViewPoint(pts[i]), true, false);
            }
            geo.Freeze();
            ctx.DrawGeometry(null, pen, geo);
        }
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
