using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.Core.Analysis;
using Fuseo.Core.Imaging;
using ZXing;
using ZXing.Common;

namespace Fuseo.E2E;

/// <summary>
/// 文字認識（W9-3）の E2E 用の合成カード（W9-5・docs/windows-design.md §10.19-2）。テスト実行時に WPF（STA スレッド・
/// <see cref="FormattedText"/>＋<see cref="RenderTargetBitmap"/>・Yu Gothic UI か Meiryo）で日本語の見本の文字を描く。
/// **すべて作り物**: 氏名は「見本」、番号は見るからに合成の本体（01234567890 等）に検査用数字だけ実行時に付ける。
/// ファイルに書くのは描いた画像だけ（VM ローカルのテスト用フォルダ）。実物の書類は使わない。
/// </summary>
internal static class SynthCards
{
    /// <summary>ID-1（85.60×53.98mm）を約 508dpi で描いた寸法。カードの縦横比なので「カード 1 枚」と判定される。</summary>
    public const int Width = 1712, Height = 1080;

    public sealed record Text(string Key, string Value, double X, double Y, double Size, bool Bold = false);

    /// <summary>描いた画像と、各文字列のインクの外接矩形（正規化・左上原点）。</summary>
    public sealed record Card(PageBitmap Bitmap, IReadOnlyDictionary<string, Box> Ink);

    /// <summary>位置の目印（黒い四角・正規化）。向きの確認に使う（文字と重ならない右下寄り）。</summary>
    public static readonly Box Badge = new(0.80, 0.72, 0.12, 0.18);

    // ---- 番号（見るからに合成の本体・検査用数字だけ実行時に決める） ----

    /// <summary>個人番号: 本体 01234567890 ＋ 検査用数字。Printed は 4 桁ずつ空けた表記。</summary>
    public static (string Digits, string Printed) MyNumber()
    {
        var n = MakeValid(c => "01234567890" + c, Checkdigits.IsValidMyNumber);
        return (n, $"{n[..4]} {n[4..8]} {n[8..]}");
    }

    /// <summary>免許証番号: 本体 1234567890 ＋ 検査用数字 ＋ 0。</summary>
    public static string LicenseNumber() => MakeValid(c => "1234567890" + c + "0", Checkdigits.IsValidLicenseNumber);

    /// <summary>画面に出してよい伏せた形（先頭 2＋*8＋末尾 2・Core の FieldDetector と同じ）。</summary>
    public static string Masked(string digits) => digits[..2] + new string('*', digits.Length - 4) + digits[^2..];

    private static string MakeValid(Func<char, string> build, Func<string, bool> validate)
    {
        for (var c = '0'; c <= '9'; c++)
            if (validate(build(c))) return build(c);
        throw new InvalidOperationException("チェックデジットが見つからない本体（テストの作りの誤り）");
    }

    // ---- カードの中身 ----

    /// <summary>マイナンバーカード（裏）ふう: 個人番号・拾得の文言・右下に QR。</summary>
    public static Card MyNumberBack(string printed)
    {
        var card = Render(
        [
            new("label", "個人番号", 80, 70, 56, Bold: true),
            new("number", printed, 80, 170, 110, Bold: true),
            new("name", "氏名　見本　花子", 80, 380, 56),
            new("found", "このカードを拾得された方は", 80, 600, 48),
            new("call", "お近くの警察署またはコールセンターへ", 80, 690, 48),
        ]);
        DrawQr(card.Bitmap, 1250, 560, 10);
        return card;
    }

    /// <summary>運転免許証（表）ふう＋右下の黒い目印（<see cref="Badge"/>）。</summary>
    public static Card LicenseFront(string number)
    {
        var card = Render(
        [
            new("title", "運転免許証", 80, 50, 64, Bold: true),
            new("name", "氏名　見本　太郎", 80, 180, 56),
            new("issue", "交付　令和元年　1月　1日", 80, 290, 48),
            new("number", $"第 {number} 号", 80, 420, 64),
            new("cond", "免許の条件等　眼鏡等", 80, 560, 48),
            new("pref", "見本県公安委員会", 80, 880, 56),
        ]);
        Synth.FillNorm(card.Bitmap, Badge.X, Badge.Y, Badge.W, Badge.H, 0, 0, 0);
        return card;
    }

    /// <summary>マイナンバーカード（表）ふう（性別・臓器提供の欄は固定領域＝カード 1 枚に切り抜いたときだけ候補になる）。</summary>
    public static Card MyNumberFront() => Render(
    [
        new("title", "個人番号カード", 80, 60, 64, Bold: true),
        new("name", "氏名　見本　太郎", 80, 200, 60),
        new("addr", "住所　見本県見本市見本町一丁目2番3号", 80, 320, 50),
        new("birth", "昭和60年 1月 1日生", 80, 450, 48),
        new("gender", "性別 男", 900, 450, 48),
        new("valid", "2036年 1月 1日まで有効", 80, 580, 48),
        new("organ", "臓器提供意思", 80, 760, 40),
    ]);

    // ---- 描画 ----

    public static Card Render(IReadOnlyList<Text> texts, int width = Width, int height = Height) => RunSta(() =>
    {
        var family = PickFont();
        var visual = new DrawingVisual();
        var ink = new Dictionary<string, Box>();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 244, 248)), null, new Rect(0, 0, width, height));
            foreach (var t in texts)
            {
                var typeface = new Typeface(new FontFamily(family), FontStyles.Normal,
                                            t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(t.Value, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                                           typeface, t.Size, Brushes.Black, 1.0);
                var origin = new Point(t.X, t.Y);
                dc.DrawText(ft, origin);
                var r = ft.BuildGeometry(origin).Bounds;
                ink[t.Key] = new Box(r.X / width, r.Y / height, r.Width / width, r.Height / height);
            }
        }
        return new Card(ToPage(visual, width, height), ink);
    });

    /// <summary>
    /// 机ふうの背景（灰色のまだら）に、カードを小さく置いた写真ふうの画像（切り抜いていない＝カード 1 枚ではない）。
    /// <paramref name="place"/> はカードを置く位置（正規化）。
    /// </summary>
    public static PageBitmap OnDesk(PageBitmap card, int width, int height, Box place) => RunSta(() =>
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(118, 112, 106)), null, new Rect(0, 0, width, height));
            var rnd = new Random(7);
            for (var i = 0; i < 400; i++)
            {
                var g = (byte)rnd.Next(100, 140);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(g, (byte)(g - 6), (byte)(g - 12))), null,
                                 new Rect(rnd.Next(width), rnd.Next(height), rnd.Next(20, 120), rnd.Next(4, 16)));
            }
            var src = BitmapSource.Create(card.Width, card.Height, 96, 96, PixelFormats.Bgra32, null, card.Pixels, card.Stride);
            dc.DrawImage(src, new Rect(place.X * width, place.Y * height, place.W * width, place.H * height));
        }
        return ToPage(visual, width, height);
    });

    /// <summary>180° 回した複製（逆さに撮った写真の代わり）。</summary>
    public static PageBitmap Rotate180(PageBitmap b)
    {
        var o = new PageBitmap(b.Width, b.Height);
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var p = b.GetPixel(x, y);
                o.SetPixel(b.Width - 1 - x, b.Height - 1 - y, p.B, p.G, p.R, p.A);
            }
        return o;
    }

    /// <summary>180° 回した画像の上での矩形。</summary>
    public static Box Rotate180(Box r) => new(1 - r.Right, 1 - r.Bottom, r.W, r.H);

    private static void DrawQr(PageBitmap bmp, int x0, int y0, int scale)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        BitMatrix m = new MultiFormatWriter().encode("FUSEO-E2E-CARD-SAMPLE", BarcodeFormat.QR_CODE, 0, 0, hints);
        for (var my = 0; my < m.Height; my++)
            for (var mx = 0; mx < m.Width; mx++)
                if (m[mx, my])
                    for (var yy = 0; yy < scale; yy++)
                        for (var xx = 0; xx < scale; xx++) bmp.SetPixel(x0 + mx * scale + xx, y0 + my * scale + yy, 0, 0, 0, 255);
    }

    private static PageBitmap ToPage(Visual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[width * height * 4];
        rtb.CopyPixels(px, width * 4, 0); // 背景が不透明なのでプリマルチプライ＝ストレート
        return new PageBitmap(width, height, px);
    }

    /// <summary>Yu Gothic UI が無ければ Meiryo（どちらも Windows 標準の日本語フォント）。</summary>
    private static string PickFont()
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Yu Gothic UI") ? "Yu Gothic UI" : "Meiryo";
    }

    private static T RunSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return result;
    }
}
