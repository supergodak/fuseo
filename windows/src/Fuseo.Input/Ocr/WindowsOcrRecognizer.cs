using System.Runtime.InteropServices.WindowsRuntime;
using Fuseo.Core.Analysis;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrWord = Fuseo.Core.Analysis.OcrWord;

namespace Fuseo.Input.Ocr;

/// <summary>
/// Windows 標準の文字認識（<c>Windows.Media.Ocr</c>・日本語・端末内のみ）を <see cref="ITextRecognizer"/> にするアダプタ
/// （docs/windows-design.md §10.2／§10.13）。
///
/// - 日本語の OCR が入っていなければ <see cref="TryCreate"/> が null（英語へのフォールバックはしない）。
/// - 入力は <see cref="PageBitmap"/>（BGRA32・ストレート α）をそのまま <c>SoftwareBitmap</c>（Bgra8・Straight）に複製して渡す。
///   長辺が <c>OcrEngine.MaxImageDimension</c> を超えたら面積平均で縮小してから渡す（座標は渡した画像の寸法で正規化
///   するので、縮小しても結果の座標は元の画像の上でそのまま使える）。
/// - 出力は **1 行（<c>OcrLine</c>）＝ 1 観測**。単語の <c>BoundingRect</c>（画素）を正規化して <see cref="OcrItem.FromWords"/>。
///   <c>TextAngle</c> は使わない（向きは <see cref="PageAnalyzer"/> の正立判定が決める）。confidence は Windows OCR に無い。
/// - **並列**: 同じ <c>OcrEngine</c> を同時に呼ぶと 2 つ目以降が「Another RecognizeAsync operation is already running!」
///   （COMException）で失敗する（W9-2 実測: 27 枚×4 並列で 130 件失敗）。エンジンの作成は約 0.06ms と軽く、
///   呼び出しごとに作れば 4〜8 並列でも失敗 0・結果は直列と完全一致だったので、**呼び出しごとにエンジンを作る**。
/// - 結果はディスクに保存しない（書類の文字列を残さない）。通信は一切しない。
/// </summary>
public sealed class WindowsOcrRecognizer : ITextRecognizer
{
    /// <summary>OCR の言語（日本語のみ・§10.2）。</summary>
    public const string LanguageTag = "ja";

    private WindowsOcrRecognizer() { }

    /// <summary>日本語の OCR が使えれば認識器、入っていなければ null（画面は番号検出・種別判定を止めて QR だけで続ける）。</summary>
    public static WindowsOcrRecognizer? TryCreate() => CreateEngine() is null ? null : new WindowsOcrRecognizer();

    private static OcrEngine? CreateEngine() => OcrEngine.TryCreateFromLanguage(new Language(LanguageTag));

    public async Task<IReadOnlyList<OcrItem>> RecognizeAsync(PageBitmap page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ct.ThrowIfCancellationRequested();
        // 呼び出しごとに作る（同じエンジンの同時呼び出しは失敗する・上の説明）。作成の間に言語が外された場合は黙って空にしない。
        var engine = CreateEngine()
            ?? throw new InvalidOperationException("日本語の文字認識（OCR）が使えません。");

        var image = FitForEngine(page, (int)Math.Min(OcrEngine.MaxImageDimension, int.MaxValue));
        OcrResult result;
        using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                   image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Straight))
        {
            result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        }
        return ToItems(result.Lines.Select(l => l.Words.Select(w =>
            (w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))), image.Width, image.Height);
    }

    /// <summary>長辺が上限を超えるときだけ縮小した画像（収まっていれば元のまま）。</summary>
    internal static PageBitmap FitForEngine(PageBitmap page, int maxDimension) =>
        maxDimension > 0 ? PixelOps.DownscaleToFit(page, maxDimension) : page;

    /// <summary>
    /// 行ごとの単語（画素座標・左上原点）を観測に変換する。箱は渡した画像の寸法で正規化し 0..1 に収める。
    /// 単語が 0 個の行・空文字だけの行は捨てる。
    /// </summary>
    internal static IReadOnlyList<OcrItem> ToItems(
        IEnumerable<IEnumerable<(string Text, double X, double Y, double W, double H)>> lines, int width, int height)
    {
        var items = new List<OcrItem>();
        foreach (var line in lines)
        {
            var words = new List<CoreOcrWord>();
            foreach (var (text, x, y, w, h) in line)
            {
                if (string.IsNullOrEmpty(text)) continue;
                words.Add(new CoreOcrWord(text, Normalize(x, y, w, h, width, height)));
            }
            if (words.Count > 0) items.Add(OcrItem.FromWords(words));
        }
        return items;
    }

    internal static NormRect Normalize(double x, double y, double w, double h, int width, int height)
    {
        var x0 = Math.Clamp(x / width, 0, 1);
        var y0 = Math.Clamp(y / height, 0, 1);
        var x1 = Math.Clamp((x + w) / width, 0, 1);
        var y1 = Math.Clamp((y + h) / height, 0, 1);
        return new NormRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}
