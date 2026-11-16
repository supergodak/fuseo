using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.App.Services;

/// <summary>
/// 透かし（0.4.0）を描く。配置は Core の <see cref="WatermarkLayout"/>（Mac 版と同じ計算）、描くのは WPF。
/// 焼き込み済みのページの上に**画素として**重ねる（<see cref="BurnedPage.WithLayer"/>）ので、文字としては残らない。
/// 見本（書き出しシートの小さな画像）と書き出しで同じ <see cref="RenderLayer"/> を使う。
/// </summary>
public static class WatermarkPainter
{
    /// <summary>Mac 版のヒラギノ角ゴ W6 に近い太さ。</summary>
    private static readonly Typeface Face = new(new FontFamily(WpfTextMetrics.GothicFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    /// <summary>文字の色（濃さは不透明度で付ける。Mac 版と同じ 20% の灰）。</summary>
    private static readonly Color Ink = Color.FromRgb(51, 51, 51);

    /// <summary>透かしの層（BGRA・プリマルチプライ・<paramref name="width"/>×<paramref name="height"/>）。文言が空なら null。</summary>
    public static byte[]? RenderLayer(Watermark watermark, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        var text = watermark.TrimmedText;
        if (text.Length == 0 || width <= 0 || height <= 0) return null;
        return TextPainter.RunSta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                Draw(dc, watermark, text, width, height);
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new byte[width * height * 4];
            rtb.CopyPixels(px, width * 4, 0);
            return px;
        });
    }

    /// <summary>焼き込み済みのページに透かしを重ねたもの（文言が空ならそのまま）。</summary>
    public static BurnedPage Apply(BurnedPage page, Watermark? watermark)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (watermark is null) return page;
        var layer = RenderLayer(watermark, page.Bitmap.Width, page.Bitmap.Height);
        return layer is null ? page : page.WithLayer(layer);
    }

    /// <summary>見本: ページを長辺 <paramref name="maxSide"/> px に縮めて透かしを重ねた画像（表示専用）。</summary>
    public static BitmapSource? Preview(BurnedPage page, Watermark watermark, int maxSide = 360)
    {
        ArgumentNullException.ThrowIfNull(page);
        var src = page.Bitmap;
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
        var small = Resample.Downscale(src, scale, 1);
        var layer = RenderLayer(watermark, small.Width, small.Height);
        if (layer is null) return null;
        var outBmp = small.Clone();
        var o = outBmp.Pixels;
        for (var i = 0; i < o.Length; i += 4)
        {
            int ta = layer[i + 3];
            if (ta == 0) continue;
            var inv = 1 - ta / 255.0;
            for (var c = 0; c < 3; c++) o[i + c] = (byte)Math.Clamp(Math.Round(layer[i + c] + o[i + c] * inv), 0, 255);
        }
        var bs = BitmapConvert.ToBitmapSource(outBmp);
        if (bs.CanFreeze) bs.Freeze();
        return bs;
    }

    private static void Draw(DrawingContext dc, Watermark wm, string text, int width, int height)
    {
        // 1px あたりの幅を測ってから大きさを決める（Mac 版と同じ手順）
        var unit = Format(text, 100);
        var size = WatermarkLayout.FontSize(wm.Layout, width, height, unit.WidthIncludingTrailingWhitespace / 100);
        var ft = Format(text, size);
        var w = ft.WidthIncludingTrailingWhitespace;
        var h = ft.Height;
        dc.PushOpacity(wm.ClampedOpacity);
        foreach (var p in WatermarkLayout.Placements(wm.Layout, width, height, w, h))
        {
            dc.PushTransform(new TranslateTransform(p.CenterX, p.CenterY));
            dc.PushTransform(new RotateTransform(-p.RiseDegrees));   // WPF の正の角度は時計回り
            dc.DrawText(ft, new Point(-w / 2, -h / 2));
            dc.Pop();
            dc.Pop();
        }
        dc.Pop();
    }

    private static FormattedText Format(string text, double size)
    {
        var brush = new SolidColorBrush(Ink);
        brush.Freeze();
        return new FormattedText(text, Japanese, FlowDirection.LeftToRight, Face, size, brush, null, TextFormattingMode.Ideal, 1.0);
    }
}
