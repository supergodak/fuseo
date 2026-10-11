using System.IO;
using System.Runtime.InteropServices;
using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;
using PDFiumCore;

namespace Fuseo.Input;

/// <summary>/Rotate 適用後のページサイズ（pt）→ ラスタの画素寸法（§4.4）。</summary>
public static class PdfPageGeometry
{
    /// <summary>
    /// px = pt × <see cref="IPageSource.RasterDpi"/>/72（四捨五入・最低 1px）。長辺が
    /// <see cref="IPageSource.MaxLongSidePx"/> を超える場合は縦横比を保って長辺をその値に収める。
    /// </summary>
    public static (int Width, int Height) PixelSize(double widthPt, double heightPt)
    {
        if (!(widthPt > 0) || !(heightPt > 0) || double.IsInfinity(widthPt) || double.IsInfinity(heightPt))
            throw new ArgumentOutOfRangeException(nameof(widthPt), "ページサイズが不正");
        var scale = IPageSource.RasterDpi / 72.0;
        var longPt = Math.Max(widthPt, heightPt);
        if (longPt * scale > IPageSource.MaxLongSidePx) scale = IPageSource.MaxLongSidePx / longPt;
        var w = Math.Max(1, (int)Math.Round(widthPt * scale, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(heightPt * scale, MidpointRounding.AwayFromZero));
        return (Math.Min(w, IPageSource.MaxLongSidePx), Math.Min(h, IPageSource.MaxLongSidePx));
    }
}

/// <summary>
/// 開いた PDF 1 冊（PDFium）。PDFium はスレッド安全でないため、すべての呼び出しを <see cref="Gate"/> で直列化する。
/// 文書はメモリ上のコピーから開く（日本語パスの扱いの差・ファイルロックを避ける）。
/// パスワードは開く瞬間にだけ渡し、このクラスには保持しない（解錠済みの文書ハンドルだけを持つ）。
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool _initialized;

    // fpdfview.h の FPDF_ANNOT（注釈を描く）
    private const int RenderFlagAnnot = 0x01;

    // fpdfview.h の FPDF_ERR_*
    public const long ErrPassword = 4;

    // fpdfview.h の FPDFBitmap_BGRx（α 無し 32bit・B,G,R,x の順）。Unknown=0, Gray=1, BGR=2, BGRx=3, BGRA=4。
    private const int BitmapFormatBgrx = 3;

    private IntPtr _buffer;
    private FpdfDocumentT? _doc;

    public int PageCount { get; }

    private PdfiumDocument(IntPtr buffer, FpdfDocumentT doc, int pageCount)
    {
        _buffer = buffer;
        _doc = doc;
        PageCount = pageCount;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        fpdfview.FPDF_InitLibrary();
        _initialized = true;
    }

    private static bool IsNull(FpdfDocumentT? d) => d is null || d.__Instance == IntPtr.Zero;
    private static bool IsNull(FpdfPageT? p) => p is null || p.__Instance == IntPtr.Zero;
    private static bool IsNull(FpdfBitmapT? b) => b is null || b.__Instance == IntPtr.Zero;

    /// <summary>
    /// 開く。失敗なら null と PDFium のエラーコード（<see cref="ErrPassword"/> ならパスワード違い／未入力）。
    /// </summary>
    public static PdfiumDocument? TryOpen(byte[] bytes, string? password, out long error)
    {
        if (bytes.Length == 0) { error = 3; return null; } // FPDF_ERR_FORMAT
        lock (Gate)
        {
            EnsureInitialized();
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            var keep = false;
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var doc = fpdfview.FPDF_LoadMemDocument(buffer, bytes.Length, password);
                if (IsNull(doc))
                {
                    error = (long)fpdfview.FPDF_GetLastError();
                    return null;
                }
                var count = fpdfview.FPDF_GetPageCount(doc);
                error = 0;
                keep = true;
                return new PdfiumDocument(buffer, doc, count);
            }
            finally
            {
                if (!keep) Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// 1 ページを描く（§4.4）: /Rotate 適用後の pt サイズ → px、**白で塗ってから**描画。
    /// 回転引数は 0（PDFium は /Rotate を適用した向きで描く。ここで回すと二重適用になる）。
    /// **注釈も描く**（FPDF_ANNOT）: 入力済みのフォーム欄・押印注釈など、利用者がビューアで見ている内容と一致させる
    /// （描かないと「提出したら記入内容が消えていた」事故になる）。全部を画素に焼くので漏れの経路は増えない。
    /// 非表示フラグ付きの注釈は PDFium が描かない。決定 2026-10-11 Fable（Mac の drawPDFPage は注釈を描かない＝要追随検討）。
    /// 出力は不透明 BGRA。
    /// </summary>
    public PageBitmap RenderPage(int index)
    {
        lock (Gate)
        {
            if (IsNull(_doc)) throw new ObjectDisposedException(nameof(PdfiumDocument));
            var page = fpdfview.FPDF_LoadPage(_doc, index);
            if (IsNull(page)) throw new InvalidDataException($"ページ {index} を読めない");
            try
            {
                // FPDF_GetPageWidthF/HeightF は /Rotate（90/270 なら縦横入れ替え）を反映した表示サイズ（pt）。
                double wPt = fpdfview.FPDF_GetPageWidthF(page);
                double hPt = fpdfview.FPDF_GetPageHeightF(page);
                var (w, h) = PdfPageGeometry.PixelSize(wPt, hPt);

                var bitmap = fpdfview.FPDFBitmapCreateEx(w, h, BitmapFormatBgrx, IntPtr.Zero, 0);
                if (IsNull(bitmap)) throw new InsufficientMemoryException($"ビットマップを確保できない（{w}×{h}）");
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, w, h, 0xFFFFFFFFUL); // 白下地（透明ページ対策）
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, RenderFlagAnnot);

                    if (fpdfview.FPDFBitmapGetFormat(bitmap) != BitmapFormatBgrx) throw new InvalidDataException("描画バッファの形式が想定外");
                    var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    var scan0 = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    if (scan0 == IntPtr.Zero || stride < w * 4) throw new InvalidDataException("描画バッファが不正");
                    var result = new PageBitmap(w, h);
                    var px = result.Pixels;
                    for (var y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(scan0, y * stride), px, y * w * 4, w * 4);
                    for (var i = 3; i < px.Length; i += 4) px[i] = 255; // BGRx の x は未定義 → 不透明に揃える
                    return result;
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (!IsNull(_doc)) fpdfview.FPDF_CloseDocument(_doc);
            _doc = null;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
