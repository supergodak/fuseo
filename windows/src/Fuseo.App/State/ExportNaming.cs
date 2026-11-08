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

    /// <summary>「書類ごとに分ける」で付ける名前の末尾（§10.17 ユーザー決定「元名-masked」）。</summary>
    public const string SeparateSuffix = "-masked";

    /// <summary>
    /// 元ファイル名から拡張子を除き、名前に使えない文字を <c>_</c> にしたもの（空なら <c>_</c>）。
    /// </summary>
    public static string SafeStem(string sourceName)
    {
        // 先に使えない文字（: \ / など）を置き換えてから拡張子を外す（"a:b.jpg" をドライブ指定と読まないように）。
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((sourceName ?? "").Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        var safe = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(safe) ? "_" : safe;
    }

    /// <summary>
    /// 「書類ごとに分ける」の書き出し先（§10.17）: <c>&lt;元名&gt;-masked&lt;拡張子&gt;</c>。**既存のファイルとは絶対に重ならない**:
    /// 重なれば <c>-2</c>, <c>-3</c> … を付ける（JPEG/PNG で複数ページなら <c>_1</c>, <c>_2</c> … のどれか 1 つでも重なれば次の番号）。
    /// <paramref name="taken"/>（同じ回で既に使った書き出し先・大小文字無視）とも重ならない。決めた先は <paramref name="taken"/> に足す。
    /// </summary>
    public static IReadOnlyList<string> SeparateTargets(string folder, string sourceName, ExportFormat format, int pageCount,
                                                        Func<string, bool> exists, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(taken);
        var stem = SafeStem(sourceName) + SeparateSuffix;
        var ext = Extension(format);
        for (var n = 1; n <= 10000; n++)
        {
            var name = n == 1 ? stem : $"{stem}-{n}";
            var targets = Targets(Path.Combine(folder, name + ext), format, pageCount);
            if (targets.Any(t => taken.Contains(t) || exists(t))) continue;
            foreach (var t in targets) taken.Add(t);
            return targets;
        }
        throw new IOException("書き出し先の名前を決められませんでした");
    }

    /// <summary>実ファイルでの「既にある」（ファイル・フォルダ・書き出し途中の一時ファイルのどれか）。</summary>
    public static bool PathTaken(string path) =>
        File.Exists(path) || Directory.Exists(path) || File.Exists(path + ExportWriter.TempSuffix);
}
