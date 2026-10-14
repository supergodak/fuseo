using System.Globalization;
using System.IO;
using Fuseo.App.Properties;
using Fuseo.Core.Contracts;

namespace Fuseo.App.State;

/// <summary>書き出しファイル名の決め方。</summary>
public static class ExportNaming
{
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => ".pdf",
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Png => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// 既定のファイル名（仮・要決定）。元ファイル名は含めない（提出先に元の名前が漏れないよう無難な名前にする）。
    /// 例: 伏せ済み_20261011.pdf（英語 UI は Redacted_20261011.pdf）。日付部分は言語によらず yyyyMMdd（不変カルチャ）。
    /// </summary>
    public static string DefaultFileName(DateTime now, ExportFormat format) => DefaultFileName(now, format, Strings.Culture);

    public static string DefaultFileName(DateTime now, ExportFormat format, CultureInfo? culture) =>
        $"{Strings.Get(nameof(Strings.DefaultExportBaseName), culture)}_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Extension(format)}";

    /// <summary>
    /// 実際に書く先の一覧（ページ番号順）。
    /// - PDF: 選んだパス 1 つ（全ページを 1 ファイルに）。
    /// - JPEG/PNG で 1 ページ: 選んだパスそのまま（保存ダイアログの上書き確認と食い違わないように）。
    /// - JPEG/PNG で複数ページ: 基本名に _1, _2 … を付けてページごと（拡張子は選んだものを保つ）。
    /// </summary>
    public static IReadOnlyList<string> Targets(string chosenPath, ExportFormat format, int pageCount)
    {
        if (pageCount <= 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (format == ExportFormat.Pdf || pageCount == 1) return [chosenPath];
        var dir = Path.GetDirectoryName(chosenPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(chosenPath);
        var ext = Path.GetExtension(chosenPath);
        if (string.IsNullOrEmpty(ext)) ext = Extension(format);
        return Enumerable.Range(1, pageCount).Select(i => Path.Combine(dir, $"{baseName}_{i}{ext}")).ToList();
    }
}
