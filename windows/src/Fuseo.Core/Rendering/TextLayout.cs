using System.Globalization;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.Core.Rendering;

/// <summary>
/// 書体の寸法（§11.1）。Core は書体の実装を知らない（WPF の GlyphTypeface／FormattedText は App 側・テストは決め打ちのフェイク）。
/// 単位はすべて基準ビットマップの px。
/// </summary>
public interface ITextMetrics
{
    /// <summary>ベースラインから上の高さ（px）。</summary>
    double Ascent(TextFont font, double sizePx);

    /// <summary>ベースラインから下の深さ（px・正の値）。</summary>
    double Descent(TextFont font, double sizePx);

    /// <summary>文字列の送り幅（px・字間なし）。</summary>
    double Advance(string text, TextFont font, double sizePx);
}

/// <summary>px 単位の実数矩形（左上原点）。</summary>
public readonly record struct PxRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;
}

/// <summary>
/// 1 行（またはマス目の 1 文字）の配置結果。<see cref="X"/> は書き始めの x、<see cref="BaselineY"/> はベースラインの y（px・**左上原点**）。
/// <see cref="Width"/> は見た目の幅（字間込み・最後の文字の後ろの字間は除く）。
/// </summary>
public readonly record struct PlacedLine(string Text, double X, double BaselineY, double Width);

/// <summary>配置の結果。<see cref="InkBounds"/> は描く文字の外接（ascent〜descent・px）。空文字なら枠の左端・縦の中央の幅 0。</summary>
public sealed record TextBlockLayout(IReadOnlyList<PlacedLine> Lines, PxRect InkBounds, bool Overflows, double Kern);

/// <summary>
/// 文字枠の配置の純計算（docs/form-fill-design.md §4 の Windows 版・§11.1）。**画面表示と焼き込みで同じ結果**を使う
/// （App の TextPainter はこの結果の位置に描くだけ）。座標は基準ビットマップの px・左上原点
/// （Mac の左下原点から写すときの反転はここで済ませてある: ベースライン = 塊の上端 + ascent）。
///
/// 規則（手で決める方針・Mac と同じ）:
/// - 打った改行だけが改行。枠の幅で自動折り返しはしない。
/// - 行の送り = 文字サイズ × 行間。行の塊の高さ = (行数 − 1) × 送り + (ascent + descent)。
/// - 縦の揃えは塊を枠に対して置く。横の揃えは行ごと。
/// - 字間（kern = 文字サイズ × LetterSpacing）は各文字の後ろに足す。幅・揃え・はみ出しは最後の文字の後ろの字間を除いた見た目で測る。
/// - マス目（Cells = N）: 枠の幅を N 等分し、i 文字目をマス i の中心に置く（1 行目だけ・N 文字まで・字間は使わない）。
/// - はみ出しても切り取らない（<see cref="TextBlockLayout.Overflows"/> で画面に知らせる）。
/// </summary>
public static class TextLayout
{
    /// <summary>枠の px 矩形（連続値・丸めない）。変換は <see cref="CoordinateSpace.ToView"/>（表示領域 = 画像そのもの）を通す。</summary>
    public static PxRect FramePx(NormRect box, double imageW, double imageH)
    {
        var v = CoordinateSpace.ToView(box, imageW, imageH, imageW, imageH);
        return new PxRect(v.X, v.Y, v.W, v.H);
    }

    /// <summary>文字（書記素クラスタ）に分ける（Mac の String の Character と同じ単位）。</summary>
    public static IReadOnlyList<string> Elements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>改行で行に分ける（\r\n・\r も改行として扱う。空文字は 1 行）。</summary>
    public static string[] SplitLines(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// 字間ありの 1 行の、文字ごとの書き始め（行頭からの x・px）。字間が 0 のときは使わない（行をまとめて描く）。
    /// 幅は <see cref="LineWidth"/> と一致する（最後の文字の右端 = 幅）。
    /// </summary>
    public static IReadOnlyList<(string Element, double X)> ElementOffsets(string line, TextFont font, double sizePx, double kern,
                                                                          ITextMetrics metrics)
    {
        var result = new List<(string, double)>();
        var x = 0.0;
        foreach (var el in Elements(line))
        {
            result.Add((el, x));
            x += metrics.Advance(el, font, sizePx) + kern;
        }
        return result;
    }

    /// <summary>
    /// 1 行の見た目の幅（px）。字間が 0 なら行の送り幅そのもの。字間があれば文字ごとの送り幅の和＋字間×(文字数−1)
    /// （最後の文字の後ろの字間は数えない＝揃え・はみ出しを見た目で判定する）。
    /// </summary>
    public static double LineWidth(string line, TextFont font, double sizePx, double kern, ITextMetrics metrics)
    {
        if (line.Length == 0) return 0;
        if (kern == 0) return metrics.Advance(line, font, sizePx);
        var els = Elements(line);
        var sum = 0.0;
        foreach (var el in els) sum += metrics.Advance(el, font, sizePx);
        return Math.Max(0, sum + kern * (els.Count - 1));
    }

    /// <summary>文字枠を画像の大きさに対して配置する（描かない）。</summary>
    public static TextBlockLayout Layout(TextBox box, double imageW, double imageH, ITextMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(metrics);
        var frame = FramePx(box.Box, imageW, imageH);
        var size = box.FontSizePx;
        var ascent = metrics.Ascent(box.Font, size);
        var descent = metrics.Descent(box.Font, size);
        var advance = size * box.LineSpacing;

        if (box.Cells > 0)
        {
            var first = SplitLines(box.Text)[0];
            var chars = Elements(first);
            var cellW = frame.W / box.Cells;
            var baseline = FirstBaselineY(frame, 1, advance, ascent, descent, box.VAlign);
            var placed = new List<PlacedLine>();
            for (var i = 0; i < Math.Min(chars.Count, box.Cells); i++)
            {
                var w = metrics.Advance(chars[i], box.Font, size);
                var x = frame.X + cellW * (i + 0.5) - w / 2;
                placed.Add(new PlacedLine(chars[i], x, baseline, w));
            }
            var cellOverflow = chars.Count > box.Cells || ascent + descent > frame.H + 0.5;
            return new TextBlockLayout(placed, Ink(placed, ascent, descent, frame), cellOverflow, 0);
        }

        var kern = size * box.LetterSpacing;
        var texts = SplitLines(box.Text);
        var n = Math.Max(1, texts.Length);
        var top = FirstBaselineY(frame, n, advance, ascent, descent, box.VAlign);
        var lines = new List<PlacedLine>(texts.Length);
        var maxW = 0.0;
        for (var i = 0; i < texts.Length; i++)
        {
            var s = texts[i];
            var w = LineWidth(s, box.Font, size, kern, metrics);
            maxW = Math.Max(maxW, w);
            var x = box.HAlign switch
            {
                TextHAlign.Center => frame.MidX - w / 2,
                TextHAlign.Right => frame.Right - w,
                _ => frame.X,
            };
            lines.Add(new PlacedLine(s, x, top + i * advance, w));
        }
        var blockH = (n - 1) * advance + ascent + descent;
        var overflow = maxW > frame.W + 0.5 || blockH > frame.H + 0.5;
        return new TextBlockLayout(lines, Ink(lines, ascent, descent, frame), overflow, kern);
    }

    /// <summary>はみ出しているか（画面の赤い破線と書き出しの確認に使う）。空の枠は描かないので false。</summary>
    public static bool Overflows(TextBox box, double imageW, double imageH, ITextMetrics metrics) =>
        box.Text.Length > 0 && Layout(box, imageW, imageH, metrics).Overflows;

    /// <summary>1 行目のベースラインの y（px・左上原点）= 塊の上端 + ascent。</summary>
    private static double FirstBaselineY(PxRect frame, int n, double advance, double ascent, double descent, TextVAlign vAlign)
    {
        var blockH = (n - 1) * advance + ascent + descent;
        var blockTop = vAlign switch
        {
            TextVAlign.Top => frame.Y,
            TextVAlign.Bottom => frame.Bottom - blockH,
            _ => frame.MidY - blockH / 2,
        };
        return blockTop + ascent;
    }

    private static PxRect Ink(IReadOnlyList<PlacedLine> lines, double ascent, double descent, PxRect frame)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var any = false;
        foreach (var l in lines)
        {
            if (!(l.Width > 0)) continue;
            any = true;
            x0 = Math.Min(x0, l.X);
            x1 = Math.Max(x1, l.X + l.Width);
            y0 = Math.Min(y0, l.BaselineY - ascent);
            y1 = Math.Max(y1, l.BaselineY + descent);
        }
        return any ? new PxRect(x0, y0, x1 - x0, y1 - y0) : new PxRect(frame.X, frame.MidY, 0, 0);
    }

    /// <summary>
    /// 「枠の高さに合わせる」: 行の塊の高さが枠の高さの 90% になる文字サイズ（pt・0.5 刻み・4〜72pt に丸め）。1 回だけ計算する（自動で追従しない）。
    /// </summary>
    public static double FittingFontSizePt(TextBox box, double imageW, double imageH, ITextMetrics metrics)
    {
        var frameH = FramePx(box.Box, imageW, imageH).H;
        var n = box.Cells > 0 ? 1 : Math.Max(1, SplitLines(box.Text).Length);
        const double probe = 100;
        var heightRatio = (metrics.Ascent(box.Font, probe) + metrics.Descent(box.Font, probe)) / probe;
        var perPx = (n - 1) * box.LineSpacing + heightRatio;   // 塊の高さ / 文字サイズ
        if (!(perPx > 0) || !(frameH > 0)) return box.FontSizePt;
        var sizePx = frameH * 0.9 / perPx;
        return TextBox.Snap(sizePx * 72 / TextBox.PixelsPerInch, TextBox.FontSizeMin, TextBox.FontSizeMax, TextBox.FontSizeStep);
    }
}
