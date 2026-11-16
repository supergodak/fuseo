namespace Fuseo.Core.Model;

/// <summary>透かしの配置（Mac 版 <c>Watermark.Layout</c> と同じ 2 種類）。</summary>
public enum WatermarkLayoutKind
{
    /// <summary>全面に斜めに敷き詰める（切り取って消しにくい。既定）。</summary>
    Tiled,
    /// <summary>中央に対角線に沿って 1 つ。</summary>
    Diagonal,
}

/// <summary>
/// 透かし（「〇〇提出用」など・0.4.0）。焼き込み済みのページの上に**画素として**描く（文字として残らない）。
/// 文言・配置・濃さの意味と既定値は Mac 版（Sources/MaskingCore/Render/Watermark.swift）と同じ。
/// </summary>
public sealed record Watermark(string Text, WatermarkLayoutKind Layout = WatermarkLayoutKind.Tiled, double Opacity = Watermark.DefaultOpacity)
{
    /// <summary>既定の濃さ（Fubako の実機で、読みやすさと見えやすさの釣り合いが取れた値）。</summary>
    public const double DefaultOpacity = 0.25;
    public const double MinOpacity = 0.1;
    public const double MaxOpacity = 0.6;

    /// <summary>前後の空白を除いた文言（空なら描かない）。</summary>
    public string TrimmedText => (Text ?? "").Trim();

    public double ClampedOpacity => Math.Clamp(double.IsFinite(Opacity) ? Opacity : DefaultOpacity, MinOpacity, MaxOpacity);
}

/// <summary>
/// 透かしを置く位置の計算（純関数・画面の見本と書き出しで同じものを使う）。
/// 座標は画像の px・**左上原点**。<see cref="Placement.RiseDegrees"/> は見た目の傾き（右上がりが正）で、
/// WPF では <c>RotateTransform(-RiseDegrees)</c> で描く（WPF の正の角度は時計回り）。
/// </summary>
public static class WatermarkLayout
{
    public readonly record struct Placement(double CenterX, double CenterY, double RiseDegrees);

    /// <summary>敷き詰めの傾き（30°）。</summary>
    public const double TiledRiseDegrees = 30;

    /// <summary>
    /// 文字の大きさ（px）。tiled = 短辺の 5.5%、diagonal = 文字列の幅が対角線の 70%（短辺の 18% が上限）。
    /// <paramref name="widthPerPx"/> は 1px の文字の大きさあたりの文字列の幅。
    /// </summary>
    public static double FontSize(WatermarkLayoutKind layout, double width, double height, double widthPerPx)
    {
        var shortSide = Math.Min(width, height);
        if (layout == WatermarkLayoutKind.Tiled) return Math.Max(8, shortSide * 0.055);
        var diagonal = Math.Sqrt(width * width + height * height);
        var fit = widthPerPx > 0 ? diagonal * 0.7 / widthPerPx : shortSide * 0.18;
        return Math.Max(8, Math.Min(fit, shortSide * 0.18));
    }

    /// <summary>置く位置（<paramref name="textW"/>/<paramref name="textH"/> は実際の大きさで測った文字列の幅と高さ）。</summary>
    public static IReadOnlyList<Placement> Placements(WatermarkLayoutKind layout, double width, double height, double textW, double textH)
    {
        if (layout == WatermarkLayoutKind.Diagonal)
            return [new Placement(width / 2, height / 2, Math.Atan2(height, width) * 180 / Math.PI)];
        if (!(textW > 0) || !(textH > 0)) return [];
        // 回転した座標系（u: 文字の向き, v: それに直交）で格子を作り、画像を覆う範囲だけ返す（Mac 版と同じ組み方）。
        var stepU = textW + textH * 2.5;
        var stepV = textH * 4;
        var rad = TiledRiseDegrees * Math.PI / 180;
        var (c, s) = (Math.Cos(rad), Math.Sin(rad));
        var cx = width / 2;
        var cy = height / 2;
        var reach = Math.Sqrt(width * width + height * height) / 2 + textW;
        var rows = (int)Math.Ceiling(reach / stepV);
        var cols = (int)Math.Ceiling(reach / stepU) + 1;
        var margin = textW / 2 + textH;
        var result = new List<Placement>();
        for (var row = -rows; row <= rows; row++)
        {
            var v = row * stepV;
            var shift = (row & 1) == 0 ? 0 : stepU / 2;   // 1 行おきに半分ずらす（レンガ積み）
            for (var col = -cols; col <= cols; col++)
            {
                var u = col * stepU + shift;
                // 見た目で右上がり＝左上原点では y が減る向き
                var x = cx + u * c - v * s;
                var y = cy - (u * s + v * c);
                if (x >= -margin && x <= width + margin && y >= -margin && y <= height + margin)
                    result.Add(new Placement(x, y, TiledRiseDegrees));
            }
        }
        return result;
    }
}
