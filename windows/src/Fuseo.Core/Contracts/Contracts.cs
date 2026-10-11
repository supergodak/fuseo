using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.Core.Contracts;

/// <summary>取り込んだ 1 ページ（基準ビットマップ＋元ファイル名・ページ番号）。</summary>
public sealed record SourcePage(PageBitmap Bitmap, string SourceName, int PageIndex, bool FromPdf);

/// <summary>
/// ファイル → ページ群（WIC デコード・PDF ラスタライズ・回転の焼き込み・上限判定）。実装は W2。
/// 契約（§4.4）: PDF は白下地→描画、/Rotate 後の pt サイズから px = pt×300/72・長辺 4000 でクランプ、
/// パスワードはメモリ内のみ、PDF を含む取り込みは合計 50 ページまで（取り込み前に数えて拒否）。
/// </summary>
/// <summary>パスワードの問い合わせ。IsRetry=true は直前の入力が違っていた（UI が「パスワードが違います」を出す）。</summary>
public sealed record PasswordRequest(string FileName, bool IsRetry);

public interface IPageSource
{
    const int MaxPdfPagesPerImport = 50;
    const double RasterDpi = 300;
    const int MaxLongSidePx = 4000;

    Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct);
}

/// <summary>動的検出（v0.1 は QR／バーコードのみ・W4）。</summary>
public interface IDetector
{
    DetectorId Id { get; }
    IReadOnlyList<NormRect> Detect(PageBitmap page);
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
}

/// <summary>焼き込み（W1・Fable）。新しいバッファに #000000FF で塗る。α合成・注釈・レイヤーは禁止。</summary>
public interface IMaskRasterizer
{
    BurnedPage BurnIn(PageBitmap page, ConfirmedMasks masks);
}

public enum ExportFormat { Pdf, Jpeg, Png }

/// <summary>PDF に埋め込む画像の圧縮。既定は JPEG（写真・スキャンで十分小さく、文字も品質 0.95 で読める）。</summary>
public enum PdfImageEncoding { Jpeg, Flate }

public sealed record ExportOptions(
    ExportFormat Format, double JpegQuality = 0.95, PdfImageEncoding PdfImages = PdfImageEncoding.Jpeg);

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
