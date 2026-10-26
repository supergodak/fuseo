using System.IO;
using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;

namespace Fuseo.Input;

/// <summary>
/// <see cref="IPageSource"/> の実装（W2・契約は docs/windows-design.md §4.4）。
///
/// 流れ（Mac の PDFIntake.run と同じ 2 段構え）:
/// 1. 分類・PDF の解錠（パスワードを聞く）・ページ数の確定。**ここで上限を判定し、超えたら 1 ページも描かずに拒否**。
/// 2. 1 ページずつデコード／ラスタライズ（重い処理は Task.Run・ページごとに取り消しを確認）。
///
/// askPassword は呼び出し元のコンテキスト（UI スレッド）で呼ぶ（ダイアログを出せるように）。
/// パスワードは PDFium に渡す瞬間だけ使い、保持・ログ出力・例外への埋め込みをしない。
/// </summary>
public sealed class PageSource : IPageSource
{
    /// <summary>画像（PDF 以外）の長辺上限。超える画像は縮小する（メモリ保護・PDF と同じ値）。</summary>
    public const int MaxImageLongSidePx = IPageSource.MaxLongSidePx;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> HeicExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };
    private static readonly HashSet<string> TiffExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };

    private readonly int _maxPagesPerImport;
    private int _rasterizedPages;

    public PageSource() : this(IPageSource.MaxPdfPagesPerImport) { }

    /// <summary>テスト用（上限を小さくして検証する）。</summary>
    internal PageSource(int maxPagesPerImport) => _maxPagesPerImport = maxPagesPerImport;

    /// <summary>テスト用: これまでにデコード／ラスタライズしたページ数（「描く前に拒否」の確認）。</summary>
    internal int RasterizedPageCount => Volatile.Read(ref _rasterizedPages);

    /// <summary>取り込める拡張子か（UI のファイル選択フィルタ・ドロップ判定用）。</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(ext) || HeicExtensions.Contains(ext);
    }

    private enum Kind { Pdf, Tiff, Heic, Image }

    private sealed class Entry(string path, string name, Kind kind)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public Kind Kind { get; } = kind;
        public PdfiumDocument? Pdf { get; set; }
    }

    public async Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct,
        int pagesAlreadyOpen = 0)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegative(pagesAlreadyOpen);
        ArgumentNullException.ThrowIfNull(askPassword);
        var entries = new List<Entry>(paths.Count);
        try
        {
            // ---- 1) 分類・解錠・ページ数（描画はしない） ----
            var total = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var entry = new Entry(path, name, Classify(path, name));
                entries.Add(entry);

                int count;
                switch (entry.Kind)
                {
                    case Kind.Pdf:
                        entry.Pdf = await OpenPdfAsync(path, name, askPassword, ct);
                        count = entry.Pdf.PageCount;
                        if (count <= 0) throw new UnreadableFileException(name);
                        break;
                    case Kind.Tiff:
                        count = await Task.Run(() => CountTiffFrames(path, name), ct);
                        break;
                    default:
                        count = 1;
                        break;
                }
                if (count > 1 && count > _maxPagesPerImport)
                    throw new PageLimitExceededException(count, _maxPagesPerImport, name);
                total += count;
            }
            // 合計上限（黙って切り捨てない）。開いているページも数える。画像だけの取り込みも対象
            // （Mac は画像だけなら無制限だが、Windows は W8 の実測でメモリ不足になりうるため制限する）。
            if (pagesAlreadyOpen + total > _maxPagesPerImport)
                throw new PageLimitExceededException(pagesAlreadyOpen + total, _maxPagesPerImport, null, pagesAlreadyOpen);

            // ---- 2) 1 ページずつ描く ----
            var result = new List<SourcePage>(total);
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.Kind == Kind.Pdf)
                {
                    var doc = e.Pdf!;
                    for (var i = 0; i < doc.PageCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var index = i;
                        var bmp = await Task.Run(() => RenderPdfPage(doc, index, e.Name), ct);
                        Interlocked.Increment(ref _rasterizedPages);
                        result.Add(new SourcePage(bmp, e.Name, index, FromPdf: true));
                    }
                    // 描き終えた文書はすぐ閉じる（メモリ上のコピーを解放）。
                    doc.Dispose();
                    e.Pdf = null;
                }
                else
                {
                    var bitmaps = await Task.Run(() => DecodeImage(e, ct), ct);
                    for (var i = 0; i < bitmaps.Count; i++)
                    {
                        Interlocked.Increment(ref _rasterizedPages);
                        result.Add(new SourcePage(bitmaps[i], e.Name, i, FromPdf: false));
                    }
                }
            }
            return result;
        }
        finally
        {
            foreach (var e in entries) e.Pdf?.Dispose();
        }
    }

    private static Kind Classify(string path, string name)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return Kind.Pdf;
        if (TiffExtensions.Contains(ext)) return Kind.Tiff;
        if (HeicExtensions.Contains(ext)) return Kind.Heic;
        if (ImageExtensions.Contains(ext)) return Kind.Image;
        throw new UnsupportedFileTypeException(name, ext.ToLowerInvariant());
    }

    private static byte[] ReadFile(string path, string name)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            throw new UnreadableFileException(name, e);
        }
    }

    /// <summary>
    /// PDF を開く。パスワードが要る（または違う）間は askPassword を回数制限なしで呼び続け、null なら全体を中止。
    /// </summary>
    private static async Task<PdfiumDocument> OpenPdfAsync(
        string path, string name, Func<PasswordRequest, Task<string?>> askPassword, CancellationToken ct)
    {
        var bytes = await Task.Run(() => ReadFile(path, name), ct);
        string? password = null;
        var asked = false; // 一度聞いた後の失敗＝入力違い
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pw = password;
            var (doc, error) = await Task.Run(() =>
            {
                var d = PdfiumDocument.TryOpen(bytes, pw, out var err);
                return (d, err);
            }, ct);
            pw = null;
            if (doc is not null) return doc;
            // パスワード以外の失敗（壊れている等）。内部例外にもパスワードを含めない。
            if (error != PdfiumDocument.ErrPassword) throw new UnreadableFileException(name);

            password = await askPassword(new PasswordRequest(name, IsRetry: asked));
            asked = true;
            if (password is null) throw new ImportCancelledException(name);
        }
    }

    private static int CountTiffFrames(string path, string name)
    {
        var bytes = ReadFile(path, name);
        try { return WicImageDecoder.CountFrames(bytes); }
        catch (Exception e) when (IsDecodeFailure(e)) { throw new UnreadableFileException(name, e); }
    }

    private static PageBitmap RenderPdfPage(PdfiumDocument doc, int index, string name)
    {
        try { return doc.RenderPage(index); }
        catch (Exception e) when (e is InvalidDataException or InsufficientMemoryException or ArgumentOutOfRangeException)
        {
            throw new UnreadableFileException(name, e);
        }
    }

    private static List<PageBitmap> DecodeImage(Entry e, CancellationToken ct)
    {
        var bytes = ReadFile(e.Path, e.Name);
        try
        {
            return WicImageDecoder.Decode(bytes, allFrames: e.Kind == Kind.Tiff, MaxImageLongSidePx, ct);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            // HEIC はコーデック（HEIF 画像拡張機能）が無いと必ずここに来る。壊れたファイルとは区別できない。
            if (e.Kind == Kind.Heic) throw new HeicNotSupportedException(e.Name, ex);
            throw new UnreadableFileException(e.Name, ex);
        }
    }

    private static bool IsDecodeFailure(Exception e) =>
        e is NotSupportedException or FileFormatException or InvalidDataException or ArgumentException
          or InvalidOperationException or OverflowException or IOException
          or System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException;
}
