namespace Fuseo.Core.Geometry;

/// <summary>
/// 基準ビットマップ上の正規化矩形（0..1）。**左上原点**（Mac 版の左下原点とは上下が逆）。
/// WIC / WPF / PDFium の描画系がすべて左上原点なので、Windows 版はこれを唯一の座標系にする
/// （docs/windows-design.md §3.3）。変換は <see cref="CoordinateSpace"/> だけを通す。
/// </summary>
public readonly record struct NormRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public bool Intersects(NormRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>外周に余白を足して 0..1 に収める（マスクの安全マージン・既定 0.01）。</summary>
    public NormRect Padded(double padding)
    {
        var x0 = Math.Clamp(X - padding, 0, 1);
        var y0 = Math.Clamp(Y - padding, 0, 1);
        var x1 = Math.Clamp(Right + padding, 0, 1);
        var y1 = Math.Clamp(Bottom + padding, 0, 1);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>2 点から正規化矩形を作る（ドラッグの始点・終点の順は問わない）。</summary>
    public static NormRect FromPoints(NormPoint a, NormPoint b)
    {
        var x0 = Math.Clamp(Math.Min(a.X, b.X), 0, 1);
        var y0 = Math.Clamp(Math.Min(a.Y, b.Y), 0, 1);
        var x1 = Math.Clamp(Math.Max(a.X, b.X), 0, 1);
        var y1 = Math.Clamp(Math.Max(a.Y, b.Y), 0, 1);
        return new NormRect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>正規化座標の点（左上原点）。</summary>
public readonly record struct NormPoint(double X, double Y);

/// <summary>ピクセル座標の整数矩形（左上原点・右端/下端は含まない）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
