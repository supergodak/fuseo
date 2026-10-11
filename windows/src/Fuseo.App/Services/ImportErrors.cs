using System.IO;
using Fuseo.App.Properties;

namespace Fuseo.App.Services;

/// <summary>
/// 取り込みの例外 → 利用者向けの文言。
/// W2（IPageSource の本実装）が上限超過・読めないファイルをどの例外型で伝えるかは契約に未定義のため、
/// 今は型で大まかに分けて例外の Message を添える（統合時に W2 の例外型に合わせて分岐を足す）。
/// </summary>
public static class ImportErrors
{
    public static string Describe(Exception e) => e switch
    {
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
