using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;

namespace Fuseo.App.Services;

/// <summary>
/// 書体の寸法（§11.1・§11.2）を WPF で測る。ゴシック＝游ゴシック、明朝＝游明朝（Regular）。
/// 見つからない環境では WPF の代替（既定の合成フォント）に任せて落ちない（ascent/descent は書体ファミリの値から取る）。
/// 送り幅は <see cref="FormattedText"/>（描画と同じ物）で測る。px 単位（96dpi＝1px）。
/// </summary>
public sealed class WpfTextMetrics : ITextMetrics
{
    public const string GothicFamily = "Yu Gothic";
    public const string MinchoFamily = "Yu Mincho";

    public static readonly WpfTextMetrics Instance = new();

    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    private readonly Typeface _gothic;
    private readonly Typeface _mincho;
    private readonly (double Ascent, double Descent) _gothicRatio;
    private readonly (double Ascent, double Descent) _minchoRatio;

    /// <param name="gothicFamily">テスト用（存在しない書体名で落ちないことの確認）。既定は游ゴシック。</param>
    /// <param name="minchoFamily">テスト用。既定は游明朝。</param>
    public WpfTextMetrics(string? gothicFamily = null, string? minchoFamily = null)
    {
        _gothic = MakeTypeface(gothicFamily ?? GothicFamily);
        _mincho = MakeTypeface(minchoFamily ?? MinchoFamily);
        _gothicRatio = Ratio(_gothic);
        _minchoRatio = Ratio(_mincho);
    }

    private static Typeface MakeTypeface(string family) =>
        new(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>(ascent, descent) / 文字サイズ。書体が見つかればその値、無ければ代替（合成フォント）の書体ファミリの値。</summary>
    private static (double, double) Ratio(Typeface typeface)
    {
        if (typeface.TryGetGlyphTypeface(out var g) && g.Height > 0)
            return (g.Baseline, Math.Max(0, g.Height - g.Baseline));
        var f = typeface.FontFamily;
        var baseline = f.Baseline > 0 ? f.Baseline : 0.88;
        var line = f.LineSpacing > baseline ? f.LineSpacing : baseline + 0.12;
        return (baseline, line - baseline);
    }

    /// <summary>その書体が実在するか（無ければ代替で描く）。</summary>
    public bool IsInstalled(TextFont font) => TypefaceFor(font).TryGetGlyphTypeface(out _);

    public Typeface TypefaceFor(TextFont font) => font == TextFont.Mincho ? _mincho : _gothic;

    public double Ascent(TextFont font, double sizePx) => (font == TextFont.Mincho ? _minchoRatio : _gothicRatio).Ascent * sizePx;
    public double Descent(TextFont font, double sizePx) => (font == TextFont.Mincho ? _minchoRatio : _gothicRatio).Descent * sizePx;

    public double Advance(string text, TextFont font, double sizePx) =>
        string.IsNullOrEmpty(text) || !(sizePx > 0) ? 0 : Format(text, font, sizePx, Brushes.Black).WidthIncludingTrailingWhitespace;

    /// <summary>1 行の文字（改行を含まない）を描くための <see cref="FormattedText"/>。</summary>
    internal FormattedText Format(string text, TextFont font, double sizePx, Brush brush) =>
        new(text, Japanese, FlowDirection.LeftToRight, TypefaceFor(font), sizePx, brush, null, TextFormattingMode.Ideal, 1.0);
}

/// <summary>
/// 文字枠を描く（§11.1）。**画面のキャンバスと焼き込みで同じ <see cref="Draw"/> を使う**（見た目＝出力）。
/// 配置は Core の <see cref="TextLayout"/>（行の位置・揃え・行間・字間・マス目）で決め、ここはその位置に文字を置くだけ。
/// 座標は基準ビットマップの px・左上原点。ベースラインに合わせて「ベースライン − FormattedText.Baseline」の位置に描く。
/// 焼き込みは <see cref="Compose"/>（基準ビットマップの複製に文字を描いた画像）→ <c>MaskRasterizer.BurnIn</c>（マスクは常にその上）。
/// </summary>
public static class TextPainter
{
    /// <summary>
    /// 文字枠を描く。<paramref name="dc"/> のユーザー空間は基準ビットマップの px（imageW × imageH）であること
    /// （画面はキャンバスの大きさ = 画像の画素数・焼き込みは同じ大きさの DrawingVisual）。空の枠は描かない。
    /// </summary>
    public static void Draw(DrawingContext dc, IEnumerable<TextBox> texts, double imageW, double imageH, WpfTextMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var m = metrics ?? WpfTextMetrics.Instance;
        foreach (var box in texts)
        {
            if (box.Text.Length == 0) continue;
            var layout = TextLayout.Layout(box, imageW, imageH, m);
            var brush = new SolidColorBrush(Color.FromRgb(box.Color.R8, box.Color.G8, box.Color.B8));
            brush.Freeze();
            var size = box.FontSizePx;
            foreach (var line in layout.Lines)
            {
                if (line.Text.Length == 0) continue;
                if (layout.Kern == 0)
                {
                    var ft = m.Format(line.Text, box.Font, size, brush);
                    dc.DrawText(ft, new Point(line.X, line.BaselineY - ft.Baseline));
                    continue;
                }
                // 字間あり: 文字ごとに置く（位置は TextLayout.ElementOffsets＝幅の計算と同じ）。
                foreach (var (el, dx) in TextLayout.ElementOffsets(line.Text, box.Font, size, layout.Kern, m))
                {
                    var ft = m.Format(el, box.Font, size, brush);
                    dc.DrawText(ft, new Point(line.X + dx, line.BaselineY - ft.Baseline));
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="bitmap"/> の複製に文字を描いた画像を返す（焼き込みの前段・§11.1）。描く文字が無ければ <paramref name="bitmap"/> をそのまま返す。
    /// <paramref name="baseW"/>/<paramref name="baseH"/> は文字枠の基準の画像の大きさ（縮小画像に描くときは縮尺を掛ける）。
    /// WPF の描画（RenderTargetBitmap）は STA が要るので、呼び出しが STA でなければ専用の STA スレッドで描く（別スレッドからも呼べる）。
    /// 文字の外側の画素は元のまま（文字の層を元の画素の上に重ねるだけ）。
    /// </summary>
    public static PageBitmap Compose(PageBitmap bitmap, IReadOnlyList<TextBox> texts, double baseW, double baseH,
                                     WpfTextMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(texts);
        if (!texts.Any(t => t.Text.Length > 0) || !(baseW > 0) || !(baseH > 0)) return bitmap;
        var layer = RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(bitmap.Width / baseW, bitmap.Height / baseH));
                Draw(dc, texts, baseW, baseH, metrics);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new byte[bitmap.Width * bitmap.Height * 4];
            rtb.CopyPixels(px, bitmap.Width * 4, 0);
            return px;
        });
        return Over(bitmap, layer);
    }

    /// <summary>
    /// 文字の層（BGRA・プリマルチプライ）を元の画素（BGRA・プリマルチプライ無し）の上に重ねた新しい画像。層が透明な画素は元のバイトのまま。
    /// </summary>
    internal static PageBitmap Over(PageBitmap bottom, byte[] premultipliedTop)
    {
        var outBmp = bottom.Clone();
        var o = outBmp.Pixels;
        for (var i = 0; i < o.Length; i += 4)
        {
            int ta = premultipliedTop[i + 3];
            if (ta == 0) continue;
            double ba = o[i + 3] / 255.0, inv = 1 - ta / 255.0;
            var outA = ta / 255.0 + ba * inv;
            for (var c = 0; c < 3; c++)
            {
                var premul = premultipliedTop[i + c] / 255.0 + o[i + c] / 255.0 * ba * inv;
                o[i + c] = (byte)Math.Clamp(Math.Round(outA > 0 ? premul / outA * 255 : 0), 0, 255);
            }
            o[i + 3] = (byte)Math.Clamp(Math.Round(outA * 255), 0, 255);
        }
        return outBmp;
    }

    /// <summary>STA で実行する（すでに STA ならその場で）。専用スレッドは終わりに Dispatcher を閉じる（隠しウィンドウを残さない）。</summary>
    internal static T RunSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return body();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true, Name = "Fuseo text render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
