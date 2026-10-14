using System.Globalization;
using System.IO;
using Fuseo.App.Properties;
using Fuseo.Input;

namespace Fuseo.App.Services;

/// <summary>
/// 取り込みの例外 → 利用者向けの文言。W2 の例外（<see cref="PageImportException"/> 系）は
/// **例外の値**（ファイル名・ページ数・上限）から表示言語の文言を組み立てる（例外の Message は日本語固定なので画面に出さない）。
/// </summary>
public static class ImportErrors
{
    public static string Describe(Exception e) => Describe(e, Strings.Culture);

    public static string Describe(Exception e, CultureInfo? culture)
    {
        string S(string key) => Strings.Get(key, culture);
        return e switch
        {
            PageLimitExceededException { FileName: null } p => PageLimit(p.PageCount, p.Limit, culture),
            PageLimitExceededException p => string.Format(S(nameof(Strings.ImportErrorPageLimitFile)), p.FileName, p.PageCount, p.Limit),
            HeicNotSupportedException h => string.Format(S(nameof(Strings.ImportErrorHeic)), h.FileName),
            UnreadableFileException u => string.Format(S(nameof(Strings.ImportErrorUnreadableFile)), u.FileName),
            UnsupportedFileTypeException u => string.Format(S(nameof(Strings.ImportErrorUnsupportedFile)), u.FileName),
            // 将来足された PageImportException の派生: 値だけで汎用の文言にする（Message は使わない）。
            PageImportException { FileName: { } name } => string.Format(S(nameof(Strings.ImportErrorFileGeneric)), name),
            PageImportException => string.Format(S(nameof(Strings.ImportErrorGeneric)), ""),
            NotSupportedException => string.Format(S(nameof(Strings.ImportErrorNotSupported)), e.Message),
            FileNotFoundException or DirectoryNotFoundException => string.Format(S(nameof(Strings.ImportErrorNotFound)), e.Message),
            UnauthorizedAccessException => string.Format(S(nameof(Strings.ImportErrorUnreadable)), e.Message),
            IOException or FormatException or ArgumentException => string.Format(S(nameof(Strings.ImportErrorUnreadable)), e.Message),
            _ => string.Format(S(nameof(Strings.ImportErrorGeneric)), e.Message),
        };
    }

    /// <summary>合計での上限超過の文言。</summary>
    public static string PageLimit(int requested, int limit) => PageLimit(requested, limit, Strings.Culture);

    private static string PageLimit(int requested, int limit, CultureInfo? culture) =>
        string.Format(Strings.Get(nameof(Strings.ImportErrorPageLimit), culture), requested, limit);
}
