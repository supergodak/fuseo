using Fuseo.Core.Geometry;

namespace Fuseo.Core.Model;

// 文字入れ（v0.3・docs/windows-design.md §11・仕様の正は docs/form-fill-design.md＝Mac 0.3.0）。
// 位置と大きさ・文字サイズ・行間は**すべて手で決める**（マスや罫線の自動検出はしない）。
// 座標は基準ビットマップの正規化・**左上原点**（§3.3。Mac は左下原点）。文字はラスタに焼き込み、マスクは常に文字の上に塗る。

/// <summary>書体（ゴシック＝游ゴシック・明朝＝游明朝。§11.2）。JSON は "gothic" / "mincho"（Mac と同じ）。</summary>
public enum TextFont { Gothic, Mincho }

/// <summary>横の揃え（行ごと）。JSON は "left" / "center" / "right"。</summary>
public enum TextHAlign { Left, Center, Right }

/// <summary>縦の揃え（行の塊を枠に対して置く）。JSON は "top" / "center" / "bottom"。</summary>
public enum TextVAlign { Top, Center, Bottom }

/// <summary>
/// 文字の色（sRGB・各 0〜1。**1/255 単位に丸める**）。パレットの色か、色選択で選んだ任意の色。値は Mac の TextBox.Color と同じ。
/// </summary>
public readonly record struct TextColor
{
    public double R { get; }
    public double G { get; }
    public double B { get; }

    public TextColor(double r, double g, double b)
    {
        R = Quantize(r);
        G = Quantize(g);
        B = Quantize(b);
    }

    private static double Quantize(double v) =>
        double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero) / 255 : 0;

    /// <summary>0〜255 の整数（描画・色選択の受け渡し用）。</summary>
    public byte R8 => (byte)Math.Round(R * 255, MidpointRounding.AwayFromZero);
    public byte G8 => (byte)Math.Round(G * 255, MidpointRounding.AwayFromZero);
    public byte B8 => (byte)Math.Round(B * 255, MidpointRounding.AwayFromZero);

    public static TextColor FromBytes(byte r, byte g, byte b) => new(r / 255.0, g / 255.0, b / 255.0);

    public static readonly TextColor Black = new(0, 0, 0);
    /// <summary>ボールペンの青。</summary>
    public static readonly TextColor Blue = new(13 / 255.0, 51 / 255.0, 158 / 255.0);
    public static readonly TextColor Red = new(200 / 255.0, 28 / 255.0, 28 / 255.0);
    public static readonly TextColor Green = new(20 / 255.0, 120 / 255.0, 50 / 255.0);
    public static readonly TextColor Brown = new(115 / 255.0, 65 / 255.0, 25 / 255.0);
    public static readonly TextColor Gray = new(110 / 255.0, 110 / 255.0, 110 / 255.0);

    /// <summary>
    /// 右ペインに並べる色（この順・Mac と同じ）。Key は画面の名前（resx の TextColor_&lt;Key&gt;）と UI オートメーション用。
    /// </summary>
    public static readonly IReadOnlyList<(string Key, TextColor Color)> Palette =
    [
        ("Black", Black), ("Blue", Blue), ("Red", Red), ("Green", Green), ("Brown", Brown), ("Gray", Gray),
    ];
}

/// <summary>文字以外の設定（次に作る枠への引き継ぎに使う）。既定は Mac と同じ（10.5pt・行間 1.2・字間 0・ゴシック・黒・左・縦中央・マス目なし）。</summary>
public sealed record TextStyle
{
    public double FontSizePt { get; init; } = 10.5;
    public double LineSpacing { get; init; } = 1.2;
    public double LetterSpacing { get; init; }
    public TextFont Font { get; init; } = TextFont.Gothic;
    public TextColor Color { get; init; } = TextColor.Black;
    public TextHAlign HAlign { get; init; } = TextHAlign.Left;
    public TextVAlign VAlign { get; init; } = TextVAlign.Center;
    public int Cells { get; init; }

    public static readonly TextStyle Default = new();

    /// <summary>範囲外・刻み外の値を丸める（UI と読み込みの両方で使う）。</summary>
    public TextStyle Clamped() => this with
    {
        FontSizePt = TextBox.Snap(FontSizePt, TextBox.FontSizeMin, TextBox.FontSizeMax, TextBox.FontSizeStep),
        LineSpacing = TextBox.Snap(LineSpacing, TextBox.LineSpacingMin, TextBox.LineSpacingMax, TextBox.LineSpacingStep),
        LetterSpacing = TextBox.Snap(LetterSpacing, TextBox.LetterSpacingMin, TextBox.LetterSpacingMax, TextBox.LetterSpacingStep),
        Font = Enum.IsDefined(Font) ? Font : TextFont.Gothic,
        HAlign = Enum.IsDefined(HAlign) ? HAlign : TextHAlign.Left,
        VAlign = Enum.IsDefined(VAlign) ? VAlign : TextVAlign.Center,
        Cells = Math.Clamp(Cells, 0, TextBox.MaxCells),
    };
}

/// <summary>
/// 文字枠 1 つ（Mac の TextBox と同じ項目）。不変。設定を変えるときは <see cref="WithStyle"/>（範囲は丸める）。
/// </summary>
public sealed record TextBox
{
    /// <summary>文字サイズ（pt）の範囲と刻み。</summary>
    public const double FontSizeMin = 4, FontSizeMax = 72, FontSizeStep = 0.5;
    /// <summary>行間（行の送り＝文字サイズ×倍率）の範囲と刻み。</summary>
    public const double LineSpacingMin = 0.8, LineSpacingMax = 3.0, LineSpacingStep = 0.05;
    /// <summary>字間（文字サイズに対する割合。0 = 標準）の範囲と刻み。マス目では使わない。</summary>
    public const double LetterSpacingMin = -0.2, LetterSpacingMax = 2.0, LetterSpacingStep = 0.05;
    /// <summary>マス数の上限（0 = マス目なし）。</summary>
    public const int MaxCells = 40;
    /// <summary>pt → px の換算に使う解像度（書き出し PDF の 300dpi 換算と同じ・§4.3）。</summary>
    public const double PixelsPerInch = 300;

    public TextBox(Guid id, NormRect box, string text = "", TextStyle? style = null)
    {
        Id = id;
        Box = box;
        Text = text ?? "";
        var s = (style ?? TextStyle.Default).Clamped();
        FontSizePt = s.FontSizePt;
        LineSpacing = s.LineSpacing;
        LetterSpacing = s.LetterSpacing;
        Font = s.Font;
        Color = s.Color;
        HAlign = s.HAlign;
        VAlign = s.VAlign;
        Cells = s.Cells;
    }

    public Guid Id { get; init; }
    /// <summary>枠（位置と大きさ）。左上原点の正規化。</summary>
    public NormRect Box { get; init; }
    /// <summary>入力した文字。<c>\n</c> で改行（枠の幅での自動折り返しはしない）。</summary>
    public string Text { get; init; } = "";
    /// <summary>文字サイズ（pt・300dpi 換算。px = pt × 300 / 72）。</summary>
    public double FontSizePt { get; private init; }
    /// <summary>行間の倍率（行の送り = 文字サイズ × LineSpacing）。</summary>
    public double LineSpacing { get; private init; }
    /// <summary>字間（文字と文字の間に足す量 = 文字サイズ × LetterSpacing）。マス目（Cells ≥ 1）では使わない。</summary>
    public double LetterSpacing { get; private init; }
    public TextFont Font { get; private init; }
    public TextColor Color { get; private init; }
    public TextHAlign HAlign { get; private init; }
    public TextVAlign VAlign { get; private init; }
    /// <summary>0 = 通常の文章、N ≥ 1 = マス目（枠の幅を N 等分して 1 マス 1 文字・1 行だけ）。</summary>
    public int Cells { get; private init; }

    public TextStyle Style => new()
    {
        FontSizePt = FontSizePt, LineSpacing = LineSpacing, LetterSpacing = LetterSpacing, Font = Font, Color = Color,
        HAlign = HAlign, VAlign = VAlign, Cells = Cells,
    };

    /// <summary>設定を差し替えた複製（範囲・刻みは <see cref="TextStyle.Clamped"/> で丸める）。</summary>
    public TextBox WithStyle(TextStyle style)
    {
        var s = style.Clamped();
        return this with
        {
            FontSizePt = s.FontSizePt, LineSpacing = s.LineSpacing, LetterSpacing = s.LetterSpacing, Font = s.Font,
            Color = s.Color, HAlign = s.HAlign, VAlign = s.VAlign, Cells = s.Cells,
        };
    }

    /// <summary>文字サイズ（px）。基準ビットマップの画素で描くときの大きさ。</summary>
    public double FontSizePx => FontSizePt * PixelsPerInch / 72;

    /// <summary>
    /// 刻みに丸めて範囲に収める（Mac の TextBox.snap と同じ。四捨五入は 0 から遠い方＝Swift の rounded() と同じ）。
    /// 丸めのあと浮動小数の端数（1.2000000000000002 など）を落とす。非有限は下限。
    /// </summary>
    public static double Snap(double v, double min, double max, double step)
    {
        if (!double.IsFinite(v)) return min;
        var stepped = Math.Round(Math.Round(v / step, MidpointRounding.AwayFromZero) * step * 1e6, MidpointRounding.AwayFromZero) / 1e6;
        return Math.Clamp(stepped, min, max);
    }
}
