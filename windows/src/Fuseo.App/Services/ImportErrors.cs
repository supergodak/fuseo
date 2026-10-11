using System.IO;
using Fuseo.App.Properties;
using Fuseo.Input;

namespace Fuseo.App.Services;

/// <summary>
/// 取り込みの例外 → 利用者向けの文言。W2 の例外（<see cref="PageImportException"/> 系）は
/// ファイル名・件数を持つので優先して使う（英語化は W6 で例外の値から組み立て直す）。
/// </summary>
public static class ImportErrors
{
    public static string Describe(Exception e) => e switch
    {
        PageLimitExceededException { FileName: null } p => PageLimit(p.PageCount, p.Limit),
        PageImportException => e.Message,
        NotSupportedException => string.Format(Strings.ImportErrorNotSupported, e.Message),
        FileNotFoundException or DirectoryNotFoundException => string.Format(Strings.ImportErrorNotFound, e.Message),
        UnauthorizedAccessException => string.Format(Strings.ImportErrorUnreadable, e.Message),
        IOException or FormatException or ArgumentException => string.Format(Strings.ImportErrorUnreadable, e.Message),
        _ => string.Format(Strings.ImportErrorGeneric, e.Message),
    };

    /// <summary>上限超過の文言（W2 が件数を伝えてきたとき用の枠）。</summary>
    public static string PageLimit(int requested, int limit) =>
        string.Format(Strings.ImportErrorPageLimit, requested, limit);
}
