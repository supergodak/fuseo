namespace Fuseo.Core.Geometry;

/// <summary>
/// 書類の四隅（正規化 0..1・**左上原点**）。手動 4 隅切り抜き（W4.5・docs/windows-design.md §9-5）で使う。
/// Mac 版の <c>Quad</c>（左下原点）と項目名は同じで、上下だけが逆。
/// 台形補正（<see cref="Perspective.Warp"/>）は TopLeft を出力の左上、TopRight を右上…に写す。
/// </summary>
public readonly record struct Quad(NormPoint TopLeft, NormPoint TopRight, NormPoint BottomRight, NormPoint BottomLeft)
{
    /// <summary>画像全体（切り抜きなし）。</summary>
    public static readonly Quad FullImage = new(new(0, 0), new(1, 0), new(1, 1), new(0, 1));

    /// <summary>左上から時計回り（TL, TR, BR, BL）。</summary>
    public NormPoint[] Corners => [TopLeft, TopRight, BottomRight, BottomLeft];

    public static Quad FromCorners(IReadOnlyList<NormPoint> c) => new(c[0], c[1], c[2], c[3]);

    /// <summary>全体（切り抜きなし）と同じか（浮動小数の誤差は許す）。</summary>
    public bool IsFullImage
    {
        get
        {
            var a = Corners;
            var b = FullImage.Corners;
            for (var i = 0; i < 4; i++)
                if (Math.Abs(a[i].X - b[i].X) > 1e-9 || Math.Abs(a[i].Y - b[i].Y) > 1e-9) return false;
            return true;
        }
    }

    /// <summary>
    /// 凸四角形で、TL→TR→BR→BL が画面上で時計回りか（自己交差・つぶれ・裏返しを除く）。
    /// UI の「適用」の可否と、保存した値の検査に使う。
    /// </summary>
    public bool IsConvex
    {
        get
        {
            var p = Corners;
            foreach (var q in p)
                if (!double.IsFinite(q.X) || !double.IsFinite(q.Y)) return false;
            for (var i = 0; i < 4; i++)
            {
                NormPoint a = p[i], b = p[(i + 1) % 4], c = p[(i + 2) % 4];
                var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                // 左上原点（y 下向き）で時計回り = 外積が正。1e-9 以下は 3 点が一直線（つぶれ）。
                if (cross <= 1e-9) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 90° 単位で回した画像の上の四隅（<see cref="QuarterTurn"/> と同じ向き・turns は時計回りの回数）。
    /// 点を回したうえで呼び名を付け替える（時計回り 90° なら元の左下が新しい左上）。
    /// 性質: <c>Warp(Rotate(img, t), Rotate(q, t))</c> = <c>Rotate(Warp(img, q), t)</c>（テストで固定）。
    /// </summary>
    public static Quad Rotate(Quad q, int turns)
    {
        for (var i = QuarterTurn.Normalize(turns); i > 0; i--)
            q = new Quad(QuarterTurn.Rotate(q.BottomLeft, 1), QuarterTurn.Rotate(q.TopLeft, 1),
                         QuarterTurn.Rotate(q.TopRight, 1), QuarterTurn.Rotate(q.BottomRight, 1));
        return q;
    }

    /// <summary>各点を 0..1 に収める（ドラッグで外へ出た分）。</summary>
    public Quad Clamped() => new(Clamp(TopLeft), Clamp(TopRight), Clamp(BottomRight), Clamp(BottomLeft));

    private static NormPoint Clamp(NormPoint p) => new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1));
}
