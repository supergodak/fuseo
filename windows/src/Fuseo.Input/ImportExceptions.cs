namespace Fuseo.Input;

/// <summary>
/// 取り込みの失敗（UI が日本語／英語の文言を組み立てられるよう、必要な値を持たせる）。
/// <see cref="Exception.Message"/> は既定の日本語文言（ログ用ではない。ログには出さない方針）。
/// **パスワードは絶対に含めない**（メッセージ・内部例外・プロパティのいずれにも）。
/// </summary>
public abstract class PageImportException : Exception
{
    /// <summary>原因のファイル名（パスではなくファイル名部分）。合計上限超過のように特定のファイルに帰さない場合は null。</summary>
    public string? FileName { get; }

    protected PageImportException(string? fileName, string message, Exception? inner = null)
        : base(message, inner) => FileName = fileName;
}

/// <summary>
/// ページ数の上限超過（**ラスタライズ前**に数えて拒否する・§4.4）。
/// 1 ファイル単独で上限を超えた場合は <see cref="PageImportException.FileName"/> にそのファイル名、
/// 合計で超えた場合は null。<see cref="PageCount"/> は数えたページ数（画像 1 枚＝1 ページを含む合計）。
/// </summary>
public sealed class PageLimitExceededException : PageImportException
{
    public int PageCount { get; }
    public int Limit { get; }

    public PageLimitExceededException(int pageCount, int limit, string? fileName)
        : base(fileName, fileName is null
            ? $"選んだファイルは合計 {pageCount} ページあります。PDF を含む取り込みは一度に {limit} ページまでです。"
            : $"「{fileName}」は {pageCount} ページあります。一度に取り込めるのは {limit} ページまでです。")
    {
        PageCount = pageCount;
        Limit = limit;
    }
}

/// <summary>
/// パスワード入力が取り消された（askPassword が null を返した）。**取り込み全体を中止**する。
/// 利用者の操作なので UI はエラー表示しない想定（<see cref="OperationCanceledException"/> として扱える）。
/// </summary>
public sealed class ImportCancelledException : OperationCanceledException
{
    public string FileName { get; }

    public ImportCancelledException(string fileName)
        : base($"「{fileName}」のパスワード入力が取り消されたため、取り込みを中止しました。") => FileName = fileName;
}

/// <summary>
/// HEIC/HEIF を開けない。多くは Windows に「HEIF 画像拡張機能」（と HEVC ビデオ拡張機能）が入っていないため。
/// ファイル自体が壊れている場合とは区別できないので、文言では両方の可能性に触れる。
/// </summary>
public sealed class HeicNotSupportedException : PageImportException
{
    public HeicNotSupportedException(string fileName, Exception? inner = null)
        : base(fileName,
            $"「{fileName}」（HEIC）を開けませんでした。Microsoft Store の「HEIF 画像拡張機能」が必要です。" +
            "入っている場合はファイルが壊れている可能性があります。iPhone の写真は JPEG に変換してから取り込むこともできます。",
            inner)
    { }
}

/// <summary>壊れている・読めない・ページを描けないファイル。</summary>
public sealed class UnreadableFileException : PageImportException
{
    public UnreadableFileException(string fileName, Exception? inner = null)
        : base(fileName, $"「{fileName}」を読み込めませんでした。ファイルが壊れているか、対応していない形式の可能性があります。", inner)
    { }
}

/// <summary>対応していない拡張子（JPEG / PNG / BMP / TIFF / HEIC / PDF 以外）。</summary>
public sealed class UnsupportedFileTypeException : PageImportException
{
    /// <summary>拡張子（小文字・ドット付き。拡張子が無ければ空文字）。</summary>
    public string Extension { get; }

    public UnsupportedFileTypeException(string fileName, string extension)
        : base(fileName, $"「{fileName}」は対応していない形式です（JPEG / PNG / BMP / TIFF / HEIC / PDF に対応）。")
        => Extension = extension;
}
