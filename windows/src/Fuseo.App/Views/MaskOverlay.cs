using System.Windows;
using System.Windows.Media;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Geometry;
using Fuseo.Core.Rendering;

namespace Fuseo.App.Views;

/// <summary>
/// 編集用のマスク表示（半透明）。**書き出しの見た目の代用にはしない**（書き出しシートは BurnIn の結果を表示する）。
/// 画像要素と同じ大きさで重ね、正規化座標 → 表示座標は必ず <see cref="CoordinateSpace.ToView"/> を通す。
/// ズーム/パンは親コンテナの RenderTransform で掛かるので、ここでは扱わない（線の太さだけ倍率で割る）。
/// 文字入れ（v0.3）の文字は**書き出しと同じ <see cref="TextPainter.Draw"/>** で描き（見た目＝出力）、マスクはその上に重ねる（焼き込み順と同じ）。
/// 枠の線（選択・つまみ・はみ出しの赤い破線・吸い付きの補助線）は編集用の表示で、書き出しには入らない。
/// </summary>
public sealed class MaskOverlay : FrameworkElement
{
    private static readonly Brush ManualFill = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush ManualEdge = Freeze(new SolidColorBrush(Color.FromRgb(230, 81, 0)));
    private static readonly Brush CandidateFill = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush CandidateEdge = Freeze(new SolidColorBrush(Color.FromRgb(21, 101, 192)));
    private static readonly Brush SelectedEdge = Freeze(new SolidColorBrush(Color.FromRgb(255, 214, 0)));
    private static readonly Brush DraftFill = Freeze(new SolidColorBrush(Color.FromArgb(110, 230, 81, 0)));
    private static readonly Brush TextFrameEdge = Freeze(new SolidColorBrush(Color.FromRgb(21, 101, 192)));
    private static readonly Brush TextFrameIdle = Freeze(new SolidColorBrush(Color.FromArgb(170, 21, 101, 192)));
    private static readonly Brush TextOverflowEdge = Freeze(new SolidColorBrush(Color.FromRgb(211, 47, 47)));
    private static readonly Brush TextDraftFill = Freeze(new SolidColorBrush(Color.FromArgb(40, 21, 101, 192)));
    private static readonly Brush GuideBrush = Freeze(new SolidColorBrush(Color.FromRgb(216, 27, 96)));

    /// <summary>文字の寸法（null なら既定）。</summary>
    public WpfTextMetrics? TextMetrics { get; set; }

    /// <summary>選択中・入力中の文字枠。</summary>
    public Guid? SelectedTextId { get; set; }
    public Guid? EditingTextId { get; set; }

    /// <summary>すべての文字枠の線を出すか（選択・文字の道具のとき）。はみ出しの赤い破線と選択中の枠はいつでも出す。</summary>
    public bool ShowTextFrames { get; set; }

    /// <summary>ドラッグ中の新しい文字枠（確定前）。</summary>
    public NormRect? DraftTextRect { get; set; }

    /// <summary>吸い付きの補助線（基準ビットマップの px）。</summary>
    public IReadOnlyList<TextSnap.Guide>? SnapGuides { get; set; }

    /// <summary>つまみの大きさ（画面上の px）。当たり判定もこの大きさ（MainWindow）。</summary>
    public const double HandleScreenSize = 8;

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
        var metrics = TextMetrics ?? WpfTextMetrics.Instance;

        // 文字（焼き込みと同じ描画・同じ座標＝画像の画素。この要素の大きさは画像の画素数）。マスクはこの上。
        var texts = page.Texts;
        if (texts.Count > 0)
        {
            dc.PushTransform(new ScaleTransform(vw / iw, vh / ih));
            TextPainter.Draw(dc, texts, iw, ih, metrics);
            dc.Pop();
        }

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

        // 文字枠の線（編集用）。はみ出しは赤い破線（Mac と同じ）。選択中はつまみ 8 つ。
        foreach (var tb in texts)
        {
            var r = View(tb.Box);
            var overflow = TextLayout.Overflows(tb, iw, ih, metrics);
            var selected = SelectedTextId == tb.Id;
            var editing = EditingTextId == tb.Id;
            if (overflow)
                dc.DrawRectangle(null, new Pen(TextOverflowEdge, t * (selected ? 1.6 : 1.2)) { DashStyle = dash }, r);
            else if (selected || editing)
                dc.DrawRectangle(null, new Pen(TextFrameEdge, t * 1.4), r);
            else if (ShowTextFrames)
                dc.DrawRectangle(null, new Pen(TextFrameIdle, t * 0.8) { DashStyle = new DashStyle([2, 3], 0) }, r);
            if (selected && !editing)
            {
                var hs = HandleScreenSize / Math.Max(Zoom, 0.01);
                var px = TextLayout.FramePx(tb.Box, vw, vh);
                foreach (var h in Enum.GetValues<TextHandle>())
                {
                    var (hx, hy) = TextGeometry.HandlePoint(px, h);
                    dc.DrawRectangle(Brushes.White, new Pen(TextFrameEdge, t), new Rect(hx - hs / 2, hy - hs / 2, hs, hs));
                }
            }
        }
        if (DraftTextRect is { } dt) dc.DrawRectangle(TextDraftFill, new Pen(TextFrameEdge, t) { DashStyle = dash }, View(dt));
        if (SnapGuides is { Count: > 0 } guides)
        {
            var gp = new Pen(GuideBrush, t * 0.8);
            foreach (var g in guides)
            {
                if (g.Axis == TextSnap.Axis.Vertical)
                {
                    var x = g.Position * vw / iw;
                    dc.DrawLine(gp, new Point(x, 0), new Point(x, vh));
                }
                else
                {
                    var y = g.Position * vh / ih;
                    dc.DrawLine(gp, new Point(0, y), new Point(vw, y));
                }
            }
        }

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
