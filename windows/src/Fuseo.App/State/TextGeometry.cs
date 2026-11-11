using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;

namespace Fuseo.App.State;

/// <summary>位置と大きさ（mm・ページ左上から）。</summary>
public readonly record struct FrameMM(double X, double Y, double Width, double Height);

/// <summary>文字枠の 8 つのつまみ。</summary>
public enum TextHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// 文字枠の幾何の補助（文字入れ v0.3・Mac の TextGeometry と同じ規則）。WPF 非依存。
/// - mm ⇄ 基準ビットマップの px は 300dpi 換算（<see cref="TextBox.PixelsPerInch"/>・書き出し PDF の紙と同じ）。
///   写真の取り込みでは紙の大きさが推定なので「目安の mm」になる（注記はしない・Mac と同じ）。
/// - 正規化 ⇄ px は <see cref="CoordinateSpace"/> 経由のみ（<see cref="TextLayout.FramePx"/>・<see cref="CoordinateSpace.FromView"/>）。
///   座標は左上原点（mm の y もページの上から測る）。
/// </summary>
public static class TextGeometry
{
    /// <summary>複製（Ctrl+D）でずらす量（mm・右下へ）。</summary>
    public const double DuplicateOffsetMM = 2;

    /// <summary>ドラッグで作る・つまみで変える枠の最小の大きさ（基準ビットマップの px）。</summary>
    public const double MinFramePx = 8;

    public static double PixelsFromMM(double mm) => mm / 25.4 * TextBox.PixelsPerInch;
    public static double MMFromPixels(double px) => px / TextBox.PixelsPerInch * 25.4;

    /// <summary>px の矩形 → 正規化（左上・右下の 2 点を CoordinateSpace.FromView で戻す）。</summary>
    public static NormRect FromPx(PxRect r, double imageW, double imageH)
    {
        var a = CoordinateSpace.FromView(r.X, r.Y, imageW, imageH, imageW, imageH);
        var b = CoordinateSpace.FromView(r.Right, r.Bottom, imageW, imageH, imageW, imageH);
        return NormRect.FromPoints(a, b);
    }

    public static FrameMM ToMM(NormRect box, double imageW, double imageH)
    {
        var px = TextLayout.FramePx(box, imageW, imageH);
        return new FrameMM(MMFromPixels(px.X), MMFromPixels(px.Y), MMFromPixels(px.W), MMFromPixels(px.H));
    }

    /// <summary>mm の枠 → 正規化。ページより大きい幅・高さはページに収め、はみ出す位置は内側へ寄せる。非有限なら null。</summary>
    public static NormRect? FromMM(FrameMM f, double imageW, double imageH)
    {
        if (!(imageW > 0) || !(imageH > 0)) return null;
        if (!double.IsFinite(f.X) || !double.IsFinite(f.Y) || !double.IsFinite(f.Width) || !double.IsFinite(f.Height)) return null;
        var w = Math.Min(Math.Max(1, PixelsFromMM(f.Width)), imageW);
        var h = Math.Min(Math.Max(1, PixelsFromMM(f.Height)), imageH);
        var x = Math.Min(Math.Max(0, PixelsFromMM(f.X)), imageW - w);
        var y = Math.Min(Math.Max(0, PixelsFromMM(f.Y)), imageH - h);
        return FromPx(new PxRect(x, y, w, h), imageW, imageH);
    }

    /// <summary>
    /// クリックだけで作る枠（既定の大きさ）。クリック位置を枠の左端・縦の中央にする（表の欄の書き始めをクリックすれば、その行の高さに文字が来る）。
    /// 大きさは文字サイズから: 高さ = 文字サイズの 2 倍、幅 = 10 文字分（ページの 90% まで）。
    /// </summary>
    public static NormRect? DefaultBox(NormPoint click, TextStyle style, double imageW, double imageH)
    {
        if (!(imageW > 0) || !(imageH > 0)) return null;
        var fontPx = style.Clamped().FontSizePt * TextBox.PixelsPerInch / 72;
        var w = Math.Min(fontPx * 10, imageW * 0.9);
        var h = Math.Min(fontPx * 2, imageH * 0.9);
        var c = TextLayout.FramePx(new NormRect(click.X, click.Y, 0, 0), imageW, imageH);
        var x = Math.Min(Math.Max(0, c.X), imageW - w);
        var y = Math.Min(Math.Max(0, c.Y - h / 2), imageH - h);
        return FromPx(new PxRect(x, y, w, h), imageW, imageH);
    }

    /// <summary>基準ビットマップの px 単位で平行移動する（大きさは変えず、ページの外へは出さない）。dy は画面の下向きが正。</summary>
    public static NormRect Offset(NormRect box, double dxPx, double dyPx, double imageW, double imageH)
    {
        var px = TextLayout.FramePx(box, imageW, imageH);
        var x = Math.Min(Math.Max(0, px.X + dxPx), Math.Max(0, imageW - px.W));
        var y = Math.Min(Math.Max(0, px.Y + dyPx), Math.Max(0, imageH - px.H));
        return FromPx(new PxRect(x, y, px.W, px.H), imageW, imageH);
    }

    /// <summary>つまみの位置（px）。</summary>
    public static (double X, double Y) HandlePoint(PxRect r, TextHandle h) => h switch
    {
        TextHandle.TopLeft => (r.X, r.Y),
        TextHandle.Top => (r.MidX, r.Y),
        TextHandle.TopRight => (r.Right, r.Y),
        TextHandle.Right => (r.Right, r.MidY),
        TextHandle.BottomRight => (r.Right, r.Bottom),
        TextHandle.Bottom => (r.MidX, r.Bottom),
        TextHandle.BottomLeft => (r.X, r.Bottom),
        _ => (r.X, r.MidY),
    };

    /// <summary>点（px）に一番近いつまみ（<paramref name="tolerance"/> px 以内）。無ければ null。</summary>
    public static TextHandle? HandleAt(PxRect r, double x, double y, double tolerance)
    {
        TextHandle? best = null;
        var bestD = double.MaxValue;
        foreach (var h in Enum.GetValues<TextHandle>())
        {
            var (hx, hy) = HandlePoint(r, h);
            var d = Math.Max(Math.Abs(hx - x), Math.Abs(hy - y));
            if (d <= tolerance && d < bestD) { best = h; bestD = d; }
        }
        return best;
    }

    public static bool MovesLeft(TextHandle h) => h is TextHandle.TopLeft or TextHandle.Left or TextHandle.BottomLeft;
    public static bool MovesRight(TextHandle h) => h is TextHandle.TopRight or TextHandle.Right or TextHandle.BottomRight;
    public static bool MovesTop(TextHandle h) => h is TextHandle.TopLeft or TextHandle.Top or TextHandle.TopRight;
    public static bool MovesBottom(TextHandle h) => h is TextHandle.BottomLeft or TextHandle.Bottom or TextHandle.BottomRight;

    /// <summary>
    /// つまみのドラッグ（px）。動かした辺だけを動かし、反対の辺は固定。最小の大きさとページの内側に収める。
    /// </summary>
    public static PxRect Resize(PxRect start, TextHandle h, double dx, double dy, double imageW, double imageH)
    {
        double x0 = start.X, y0 = start.Y, x1 = start.Right, y1 = start.Bottom;
        if (MovesLeft(h)) x0 = Math.Clamp(x0 + dx, 0, x1 - MinFramePx);
        if (MovesRight(h)) x1 = Math.Clamp(x1 + dx, x0 + MinFramePx, imageW);
        if (MovesTop(h)) y0 = Math.Clamp(y0 + dy, 0, y1 - MinFramePx);
        if (MovesBottom(h)) y1 = Math.Clamp(y1 + dy, y0 + MinFramePx, imageH);
        return new PxRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>平行移動（px）。ページの外へは出さない。</summary>
    public static PxRect Move(PxRect start, double dx, double dy, double imageW, double imageH) =>
        new(Math.Clamp(start.X + dx, 0, Math.Max(0, imageW - start.W)), Math.Clamp(start.Y + dy, 0, Math.Max(0, imageH - start.H)),
            start.W, start.H);

    /// <summary>点が枠の中か（正規化）。後から足した枠（手前）を優先して探す。</summary>
    public static TextBox? HitText(IReadOnlyList<TextBox> texts, NormPoint p)
    {
        for (var i = texts.Count - 1; i >= 0; i--)
            if (MaskHitTest.Contains(texts[i].Box, p)) return texts[i];
        return null;
    }
}

/// <summary>
/// 位置合わせの補助（吸い付き・Mac の TextSnap と同じ規則）。手作業を助けるだけで自動配置はしない。
/// 他の文字枠と **左端・右端・行の中心（縦の中央）** が近づいたら揃えて、細い線を出す。座標は基準ビットマップの px（左上原点）。
/// しきい値は呼び出し側が画面の 4px を表示倍率で割って渡す（Mac は表示座標の 4pt）。
/// </summary>
public static class TextSnap
{
    /// <summary>吸い付く距離（画面上の px）。</summary>
    public const double ScreenThreshold = 4;

    public enum Axis { Vertical, Horizontal }

    /// <summary>補助線。Vertical なら x、Horizontal なら y（px）。</summary>
    public readonly record struct Guide(Axis Axis, double Position);

    public readonly record struct Result(PxRect Rect, IReadOnlyList<Guide> Guides);

    /// <summary>移動: 左端どうし・右端どうし（横）と、縦の中央どうし（行の中心）を揃える。</summary>
    public static Result SnapMove(PxRect r, IReadOnlyList<PxRect> others, double threshold)
    {
        var rect = r;
        var guides = new List<Guide>();
        (double D, double Pos)? bestX = null;
        foreach (var o in others)
            foreach (var (mine, theirs) in new[] { (r.X, o.X), (r.Right, o.Right) })
            {
                var d = theirs - mine;
                if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(bestX?.D ?? double.PositiveInfinity)) bestX = (d, theirs);
            }
        if (bestX is { } bx)
        {
            rect = rect with { X = rect.X + bx.D };
            guides.Add(new Guide(Axis.Vertical, bx.Pos));
        }
        (double D, double Pos)? bestY = null;
        foreach (var o in others)
        {
            var d = o.MidY - r.MidY;
            if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(bestY?.D ?? double.PositiveInfinity)) bestY = (d, o.MidY);
        }
        if (bestY is { } by)
        {
            rect = rect with { Y = rect.Y + by.D };
            guides.Add(new Guide(Axis.Horizontal, by.Pos));
        }
        return new Result(rect, guides);
    }

    /// <summary>大きさ変更: 動かしている左端/右端だけを、他の枠の同じ端に揃える。</summary>
    public static Result SnapResize(PxRect r, bool movingLeft, bool movingRight, IReadOnlyList<PxRect> others, double threshold)
    {
        double minX = r.X, maxX = r.Right;
        var guides = new List<Guide>();
        if (movingLeft && others.Count > 0)
        {
            var o = others.Select(x => x.X).MinBy(x => Math.Abs(x - r.X));
            if (Math.Abs(o - r.X) <= threshold && o < maxX - 1) { minX = o; guides.Add(new Guide(Axis.Vertical, o)); }
        }
        if (movingRight && others.Count > 0)
        {
            var o = others.Select(x => x.Right).MinBy(x => Math.Abs(x - r.Right));
            if (Math.Abs(o - r.Right) <= threshold && o > minX + 1) { maxX = o; guides.Add(new Guide(Axis.Vertical, o)); }
        }
        return new Result(new PxRect(minX, r.Y, maxX - minX, r.H), guides);
    }
}
