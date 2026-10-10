namespace Fuseo.Core.Geometry;

/// <summary>
/// 座標変換の唯一の入口（手変換禁止・docs/windows-design.md §3.3）。
/// - <see cref="ToPixel"/>: 焼き込み用。**外側へ切り上げ**る（マスクが 1px でも欠けないように安全側）。
/// - <see cref="ToView"/>: 表示用。レターボックス（縦横比を保った中央寄せ）を考慮する。
/// - <see cref="FromView"/>: 画面上の点 → 正規化座標（ドラッグ入力）。
/// </summary>
public static class CoordinateSpace
{
    public static PixelRect ToPixel(NormRect r, int width, int height)
    {
        var x0 = (int)Math.Floor(Math.Clamp(r.X, 0, 1) * width);
        var y0 = (int)Math.Floor(Math.Clamp(r.Y, 0, 1) * height);
        var x1 = (int)Math.Ceiling(Math.Clamp(r.Right, 0, 1) * width);
        var y1 = (int)Math.Ceiling(Math.Clamp(r.Bottom, 0, 1) * height);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>画像を表示領域に縦横比を保って収めたときの、画像が占める領域（表示座標）。</summary>
    public static (double X, double Y, double W, double H) FitRect(
        double imageW, double imageH, double viewW, double viewH)
    {
        if (imageW <= 0 || imageH <= 0 || viewW <= 0 || viewH <= 0) return (0, 0, 0, 0);
        var scale = Math.Min(viewW / imageW, viewH / imageH);
        var w = imageW * scale;
        var h = imageH * scale;
        return ((viewW - w) / 2, (viewH - h) / 2, w, h);
    }

    public static (double X, double Y, double W, double H) ToView(
        NormRect r, double imageW, double imageH, double viewW, double viewH)
    {
        var f = FitRect(imageW, imageH, viewW, viewH);
        return (f.X + r.X * f.W, f.Y + r.Y * f.H, r.W * f.W, r.H * f.H);
    }

    public static NormPoint FromView(double vx, double vy, double imageW, double imageH,
                                     double viewW, double viewH)
    {
        var f = FitRect(imageW, imageH, viewW, viewH);
        if (f.W <= 0 || f.H <= 0) return new NormPoint(0, 0);
        return new NormPoint(Math.Clamp((vx - f.X) / f.W, 0, 1), Math.Clamp((vy - f.Y) / f.H, 0, 1));
    }
}
