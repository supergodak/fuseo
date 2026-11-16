using Fuseo.Core.Analysis;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.Core.Contracts;

/// <summary>取り込んだ 1 ページ（基準ビットマップ＋元ファイル名・ページ番号）。</summary>
public sealed record SourcePage(PageBitmap Bitmap, string SourceName, int PageIndex, bool FromPdf);

/// <summary>
/// ファイル → ページ群（WIC デコード・PDF ラスタライズ・回転の焼き込み・上限判定）。実装は W2。
/// 契約（§4.4）: PDF は白下地→描画、/Rotate 後の pt サイズから px = pt×300/72・長辺 4000 でクランプ、
/// パスワードはメモリ内のみ、開いているページと合わせて合計 50 ページまで（画像だけの取り込みも含む・取り込み前に数えて拒否）。
/// 画像も長辺 4000 に縮小する（W8: 画像だけなら無制限・長辺 8000 だと 24MP×100 枚で約 10GB を使い落ちうるため）。
/// </summary>
/// <summary>パスワードの問い合わせ。IsRetry=true は直前の入力が違っていた（UI が「パスワードが違います」を出す）。</summary>
public sealed record PasswordRequest(string FileName, bool IsRetry);

public interface IPageSource
{
    const int MaxPdfPagesPerImport = 50;
    const double RasterDpi = 300;
    const int MaxLongSidePx = 4000;

    /// <param name="pagesAlreadyOpen">いま開いているページ数（合計上限の判定に含める）。</param>
    Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct,
        int pagesAlreadyOpen = 0);
}

/// <summary>動的検出（v0.1 は QR／バーコードのみ・W4）。</summary>
public interface IDetector
{
    DetectorId Id { get; }
    IReadOnlyList<NormRect> Detect(PageBitmap page);
}

/// <summary>
/// 文字認識（v0.2・§10.2）。1 行 = 1 観測、座標は左上原点の正規化。Core は OCR の実装を知らない
/// （Windows.Media.Ocr のアダプタは W9-2 で Fuseo.Input に置く・テストはフィクスチャで回す）。
/// 結果はディスクに保存しない（書類の文字列を残さない）。
/// </summary>
public interface ITextRecognizer
{
    Task<IReadOnlyList<OcrItem>> RecognizeAsync(PageBitmap page, CancellationToken ct);
}

/// <summary>確定したマスク一式（ON の候補＋手動）。</summary>
public sealed record ConfirmedMasks(IReadOnlyList<NormRect> Rects, IReadOnlyList<BrushStroke> Strokes)
{
    public static readonly ConfirmedMasks None = new([], []);
}

/// <summary>
/// 焼き込み済みのページ。**<see cref="Rendering.MaskRasterizer"/> だけが作れる**（コンストラクタは internal）。
/// 書き出しはこの型しか受け取らない＝焼き込み前の画像を誤って書き出す経路を型で塞ぐ（§4）。
/// マスクが 0 個のページも、利用者が確認したうえで BurnIn を通してこの型にする。
/// </summary>
public sealed class BurnedPage
{
    public PageBitmap Bitmap { get; }
    internal BurnedPage(PageBitmap bitmap) => Bitmap = bitmap;

    /// <summary>
    /// 焼き込み済みの上に層（BGRA・**プリマルチプライ**・同じ大きさ）を重ねた新しいページ（透かし・0.4.0）。
    /// 重ねるだけなので、塗った所の下の画素が戻ることはない（黒の上の半透明は黒っぽいまま）。元のページは変えない。
    /// </summary>
    public BurnedPage WithLayer(byte[] premultipliedLayer)
    {
        ArgumentNullException.ThrowIfNull(premultipliedLayer);
        if (premultipliedLayer.Length != Bitmap.Pixels.Length) throw new ArgumentException("層の大きさがページと合わない", nameof(premultipliedLayer));
        var outBmp = Bitmap.Clone();
        var o = outBmp.Pixels;
        for (var i = 0; i < o.Length; i += 4)
        {
            int ta = premultipliedLayer[i + 3];
            if (ta == 0) continue;
            double ba = o[i + 3] / 255.0, inv = 1 - ta / 255.0;
            var outA = ta / 255.0 + ba * inv;
            for (var c = 0; c < 3; c++)
            {
                var premul = premultipliedLayer[i + c] / 255.0 + o[i + c] / 255.0 * ba * inv;
                o[i + c] = (byte)Math.Clamp(Math.Round(outA > 0 ? premul / outA * 255 : 0), 0, 255);
            }
            o[i + 3] = (byte)Math.Clamp(Math.Round(outA * 255), 0, 255);
        }
        return new BurnedPage(outBmp);
    }
}

/// <summary>焼き込み（W1・Fable）。新しいバッファに #000000FF で塗る。α合成・注釈・レイヤーは禁止。</summary>
public interface IMaskRasterizer
{
    BurnedPage BurnIn(PageBitmap page, ConfirmedMasks masks);
}

public enum ExportFormat { Pdf, Jpeg, Png }

/// <summary>PDF に埋め込む画像の圧縮。既定は JPEG（写真・スキャンで十分小さく、文字も品質 0.95 で読める）。</summary>
public enum PdfImageEncoding { Jpeg, Flate }

/// <param name="MaxBytes">ファイルの大きさの上限（バイト・0.4.0）。PDF と JPEG で有効（JPEG は 1 ファイルごと）。
/// 収まるまで JPEG の画質と解像度を段階的に下げ、下限まで下げても収まらなければ <see cref="SizeLimitUnreachableException"/>。</param>
/// <param name="PdfPassword">PDF を開くときのパスワード（0.4.0・PDF のみ・空なら無し）。AES-256 で暗号化する。</param>
public sealed record ExportOptions(
    ExportFormat Format, double JpegQuality = 0.95, PdfImageEncoding PdfImages = PdfImageEncoding.Jpeg,
    long? MaxBytes = null, string? PdfPassword = null)
{
    /// <summary>実際にかけるパスワード（PDF で、空でないときだけ）。</summary>
    public string? EffectivePdfPassword => Format == ExportFormat.Pdf && !string.IsNullOrEmpty(PdfPassword) ? PdfPassword : null;

    /// <summary>パスワードの長さの上限（Mac 版と同じ）。</summary>
    public const int PdfPasswordMaxLength = 32;

    /// <summary>
    /// パスワードに使えるか: **半角の英数字と記号（空白を含む ASCII の印字文字）だけ・32 文字まで**（Mac 版と同じ規則）。
    /// macOS のプレビュー（PDFKit）は ASCII 以外のパスワードの PDF を開けない（2026-11-16 実測。この版の AES-256 の PDF でも同じ）。
    /// </summary>
    public static bool IsValidPdfPassword(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length <= PdfPasswordMaxLength && password.All(c => c >= 0x20 && c <= 0x7E);
}

/// <summary>ファイルの大きさの上限に収められない（0.4.0）。<see cref="SmallestBytes"/> は下限まで下げたときの大きさ。</summary>
public sealed class SizeLimitUnreachableException(long limitBytes, long smallestBytes)
    : Exception($"ファイルの大きさを {limitBytes} バイト以内にできません（下限まで下げて {smallestBytes} バイト）")
{
    public long LimitBytes { get; } = limitBytes;
    public long SmallestBytes { get; } = smallestBytes;
}

/// <summary>
/// 書き出し（W1・Fable）。焼き込み済みページだけを受け取る。
/// JPEG/PNG はメタデータ・サムネイルを一切持たない自前エンコーダ、PDF は画像 XObject のみの自前ライタ（§4.2・§4.3）。
/// 出力は常に不透明 RGB（α は白に合成）。透明画素の裏に色が残る類の持ち越しも無くす。
/// <paramref name="openDestination"/> はページ番号（0 始まり）を受け取り書き込み先を返す。
/// PDF は 1 回だけ（0 で）呼ばれ、JPEG/PNG はページごとに呼ばれる。Stream の破棄は呼び出し側。
/// </summary>
public interface IExporter
{
    void Export(IReadOnlyList<BurnedPage> pages, ExportOptions options, Func<int, Stream> openDestination);
}
