using System.IO;
using System.Runtime.InteropServices;

namespace Fuseo.App.Services;

/// <summary>フォルダの中の 1 項目（ファイル列挙の差し替え口が返す）。</summary>
public sealed record FolderEntry(string Path, bool IsDirectory, FileAttributes Attributes);

/// <summary>
/// 取り込むパスの展開結果。<see cref="Files"/> はそのまま <c>IPageSource.LoadAsync</c> に渡す順。
/// <see cref="Unsupported"/> はフォルダの中で飛ばした対応外のファイルの数（隠し・システム・サブフォルダは数えない）。
/// </summary>
public sealed record FolderIntakeResult(IReadOnlyList<string> Files, int Unsupported, bool ContainsFolder);

/// <summary>
/// フォルダ一括処理の入口（W9-4・docs/windows-design.md §10.17）。純粋関数＋ファイル列挙の差し替え口。
/// - フォルダは**直下のファイルだけ**（サブフォルダは見ない）・隠し／システム属性は除く・対応拡張子だけ（大小文字無視）。
/// - 並びは Explorer と同じ論理順（<c>StrCmpLogicalW</c>・ファイル名で比べる）。
/// - フォルダ以外のパス（ファイル）はそのまま残す（対応外なら今どおり取り込み側がエラーにする）。並びは渡された順で、フォルダの位置にその中身が入る。
/// ファイルは読むだけ（列挙と属性だけ）。何も書かない。
/// </summary>
public static class FolderIntake
{
    /// <summary>対応拡張子（「ファイルを開く」のフィルタ OpenFilesFilter と同じ一覧）。</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".pdf"];

    private static readonly HashSet<string> Supported = new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) => Supported.Contains(System.IO.Path.GetExtension(path));

    /// <summary>実際のファイルシステムで展開する。</summary>
    public static FolderIntakeResult Expand(IReadOnlyList<string> paths) => Expand(paths, Directory.Exists, EnumerateFolder, CompareLogical);

    /// <summary>展開する（判定・列挙・並びを差し替えられる）。</summary>
    public static FolderIntakeResult Expand(IReadOnlyList<string> paths, Func<string, bool> isDirectory,
                                            Func<string, IEnumerable<FolderEntry>> enumerate, Comparison<string> compareNames)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = new List<string>();
        var unsupported = 0;
        var containsFolder = false;
        foreach (var path in paths)
        {
            if (!isDirectory(path))
            {
                files.Add(path);
                continue;
            }
            containsFolder = true;
            var (inFolder, skipped) = ListFolder(enumerate(path), compareNames);
            files.AddRange(inFolder);
            unsupported += skipped;
        }
        return new FolderIntakeResult(files, unsupported, containsFolder);
    }

    /// <summary>1 つのフォルダの中身から取り込むファイルを選んで並べる。</summary>
    public static (IReadOnlyList<string> Files, int Unsupported) ListFolder(IEnumerable<FolderEntry> entries, Comparison<string> compareNames)
    {
        var files = new List<string>();
        var unsupported = 0;
        foreach (var e in entries)
        {
            if (e.IsDirectory || (e.Attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;
            if (IsSupported(e.Path)) files.Add(e.Path);
            else unsupported++;
        }
        files.Sort((a, b) =>
        {
            var c = compareNames(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b));
            return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
        });
        return (files, unsupported);
    }

    /// <summary>フォルダ直下の項目（再帰しない・属性で除かない＝除くのは <see cref="ListFolder"/>）。</summary>
    public static IEnumerable<FolderEntry> EnumerateFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options)
            .Select(i => new FolderEntry(i.FullName, i is DirectoryInfo, i.Attributes))
            .ToList();
    }

    /// <summary>Explorer と同じ論理順（数字は数として比べる: file2 &lt; file10）。</summary>
    public static int CompareLogical(string a, string b) => StrCmpLogicalW(a, b);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
