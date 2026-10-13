using System.IO;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;

namespace Fuseo.App.Library;

/// <summary>一覧の 1 行。読めない作業（壊れている・版が違う・画像が無い）は <see cref="IsReadable"/> = false（削除だけできる）。</summary>
public sealed record WorkSummary(
    Guid Id, string Title, DateTimeOffset UpdatedAt, WorkStatus Status, int PageCount,
    string? ThumbnailPath, int ThumbnailRotation, bool IsReadable, string? Problem);

/// <summary>読み込んだ作業（document と、ページ順の元画像）。</summary>
public sealed record LoadedWork(WorkDocument Document, IReadOnlyList<PageBitmap> Images);

/// <summary>作業を開けない・保存できない（利用者に見せる理由つき）。</summary>
public sealed class WorkLibraryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 自動保存ライブラリのファイルストア（docs/windows-design.md §3.6・contract.md §6）。
/// 1 作業 = <c>&lt;root&gt;/&lt;id&gt;/</c>（<c>document.json</c>・<c>pages/&lt;pageId&gt;.png</c>・<c>thumbs/&lt;pageId&gt;.jpg</c>）。
/// - ページ画像とサムネイルは**初めて保存するときに 1 回だけ**書く（元画像は不変）。一時ファイル → <see cref="File.Move(string, string)"/> で確定。
/// - document.json は毎回、同じフォルダの一時ファイルに書いてから置き換える（途中で失敗しても既存の document.json は壊れない）。
/// - JSON から消えたページの画像（孤児）は保存の後に消す。残った一時ファイルは一覧を読むときに掃除する。
/// - 読み込みでは imageFile / thumbnailFile が**その作業フォルダ内の相対パス**であることを、形（<see cref="WorkPaths"/>）と
///   完全パスの両方で確かめる。外を指す作業は「読めない作業」。
/// - 操作はすべて 1 本のロックで直列化する（自動保存と削除・一覧が同時に走っても混ざらない）。
/// 保存されるのは黒塗り前の画像。通信はしない・このパソコンの中だけ。
/// </summary>
public sealed class FileWorkLibrary
{
    public const string DocumentFileName = "document.json";
    public const string TempSuffix = ".fuseo-tmp";
    public const int ThumbnailMaxSide = 320;

    private readonly object _gate = new();
    private readonly IPageImageCodec _codec;

    public FileWorkLibrary(string? root = null, IPageImageCodec? codec = null)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot);
        _codec = codec ?? new WicPageImageCodec();
    }

    /// <summary>既定の保存先 <c>%LOCALAPPDATA%\Fuseo\Library</c>。</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fuseo", "Library");

    public string Root { get; }

    /// <summary>同時に書くページ画像の数の上限（1 枚で数十 MB の作業領域を使うため）。</summary>
    public static int MaxWriteParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public string DirectoryOf(Guid id) => Path.Combine(Root, id.ToString("D"));

    // ---- 保存 ----

    /// <summary>
    /// 保存する。<paramref name="images"/> は imageFile → 元画像（まだ書いていないページの分は必須。書き済みのページは無くてよい）。
    /// 失敗したら書きかけの一時ファイルを消して例外を投げる（既存の document.json はそのまま）。
    /// </summary>
    public void Save(WorkDocument doc, IReadOnlyDictionary<string, PageBitmap> images)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(images);
        var json = WorkDocumentJson.SerializeToUtf8(doc); // 検査を兼ねる（壊れた物は書かない）
        lock (_gate)
        {
            var dir = DirectoryOf(doc.Id);
            Directory.CreateDirectory(dir);
            var temps = new System.Collections.Concurrent.ConcurrentBag<string>();
            try
            {
                var jobs = new List<(string Path, PageBitmap Bitmap, bool Thumbnail)>();
                foreach (var page in doc.Pages)
                {
                    var imagePath = Resolve(dir, page.ImageFile);
                    var thumbPath = Resolve(dir, page.ThumbnailFile);
                    var hasImage = File.Exists(imagePath);
                    images.TryGetValue(page.ImageFile, out var bmp);
                    if (!hasImage && bmp is null)
                        throw new WorkLibraryException($"ページ {page.Index + 1} の画像がありません");
                    if (bmp is not null && (bmp.Width != page.PixelWidth || bmp.Height != page.PixelHeight))
                        throw new WorkLibraryException($"ページ {page.Index + 1} の画像の寸法が合いません");
                    if (!hasImage) jobs.Add((imagePath, bmp!, false));
                    if (!File.Exists(thumbPath) && bmp is not null) jobs.Add((thumbPath, bmp, true));
                }
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = MaxWriteParallelism }, job =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.Path)!);
                    WriteAtomically(job.Path, temps, s =>
                    {
                        if (job.Thumbnail) _codec.WriteThumbnailJpeg(job.Bitmap, ThumbnailMaxSide, s);
                        else _codec.WritePng(job.Bitmap, s);
                    }, overwrite: false);
                });
                WriteAtomically(Path.Combine(dir, DocumentFileName), temps, s => s.Write(json), overwrite: true);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                DeleteQuietly(temps);
                throw e.InnerExceptions[0] is WorkLibraryException w ? w : new WorkLibraryException(e.InnerExceptions[0].Message, e);
            }
            catch
            {
                DeleteQuietly(temps);
                throw;
            }
            PruneOrphans(dir, doc);
        }
    }

    private static void WriteAtomically(string finalPath, System.Collections.Concurrent.ConcurrentBag<string> temps,
                                        Action<FileStream> write, bool overwrite)
    {
        var tmp = $"{finalPath}.{Guid.NewGuid():N}{TempSuffix}";
        temps.Add(tmp);
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            write(fs);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, finalPath, overwrite);
    }

    /// <summary>document.json が参照しないページ画像・サムネイル（と一時ファイル）を消す。失敗しても保存は成功扱い。</summary>
    private static void PruneOrphans(string dir, WorkDocument doc)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.Pages)
        {
            keep.Add(Resolve(dir, p.ImageFile));
            keep.Add(Resolve(dir, p.ThumbnailFile));
        }
        foreach (var sub in new[] { "pages", "thumbs" })
        {
            var subDir = Path.Combine(dir, sub);
            if (!Directory.Exists(subDir)) continue;
            foreach (var f in SafeFiles(subDir))
                if (!keep.Contains(Path.GetFullPath(f))) DeleteQuietly([f]);
        }
    }

    // ---- 一覧 ----

    /// <summary>作業の一覧（更新日時の新しい順）。前回の失敗で残った一時ファイルもここで掃除する。</summary>
    public IReadOnlyList<WorkSummary> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return [];
            var result = new List<WorkSummary>();
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(dir), "D", out var id)) continue; // 自分の物でないフォルダには触らない
                CleanTemporaries(dir);
                result.Add(Summarize(id, dir));
            }
            return result.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id).ToList();
        }
    }

    private WorkSummary Summarize(Guid id, string dir)
    {
        try
        {
            var doc = ReadDocument(dir);
            if (doc.Id != id) throw new WorkLibraryException("フォルダ名と id が合いません");
            foreach (var p in doc.Pages)
                if (!File.Exists(Resolve(dir, p.ImageFile))) throw new WorkLibraryException($"ページ {p.Index + 1} の画像がありません");
            string? thumb = null;
            var rotation = 0;
            if (doc.Pages.Count > 0)
            {
                var t = Resolve(dir, doc.Pages[0].ThumbnailFile);
                if (File.Exists(t)) thumb = t;
                rotation = doc.Pages[0].ManualRotation;
            }
            return new WorkSummary(id, doc.Title, doc.UpdatedAt, doc.Status, doc.Pages.Count, thumb, rotation, true, null);
        }
        catch (Exception e) when (e is WorkLibraryException or WorkDocumentFormatException or IOException or UnauthorizedAccessException)
        {
            DateTimeOffset updated;
            try { updated = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { updated = DateTimeOffset.MinValue; }
            return new WorkSummary(id, "", updated, WorkStatus.InProgress, 0, null, 0, false, e.Message);
        }
    }

    // ---- 読み込み ----

    /// <summary>作業を読み込む（元画像をすべてデコードする）。だめなら <see cref="WorkLibraryException"/>。</summary>
    public LoadedWork Load(Guid id)
    {
        lock (_gate)
        {
            var dir = DirectoryOf(id);
            try
            {
                var doc = ReadDocument(dir);
                if (doc.Id != id) throw new WorkLibraryException("フォルダ名と id が合いません");
                var images = new PageBitmap[doc.Pages.Count];
                Parallel.For(0, doc.Pages.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxWriteParallelism }, i =>
                {
                    var p = doc.Pages[i];
                    var path = Resolve(dir, p.ImageFile);
                    if (!File.Exists(path)) throw new WorkLibraryException($"ページ {p.Index + 1} の画像がありません");
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var bmp = _codec.ReadPng(fs);
                    if (bmp.Width != p.PixelWidth || bmp.Height != p.PixelHeight)
                        throw new WorkLibraryException($"ページ {p.Index + 1} の画像の寸法が合いません");
                    images[i] = bmp;
                });
                return new LoadedWork(doc, images);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                throw e.InnerExceptions[0] as WorkLibraryException ?? new WorkLibraryException(e.InnerExceptions[0].Message, e);
            }
            catch (Exception e) when (e is WorkDocumentFormatException or IOException or UnauthorizedAccessException
                                           or InvalidDataException or NotSupportedException or FileFormatException or ArgumentException
                                           or System.Runtime.InteropServices.ExternalException)
            {
                throw new WorkLibraryException(e.Message, e);
            }
        }
    }

    private static WorkDocument ReadDocument(string dir)
    {
        var path = Path.Combine(dir, DocumentFileName);
        if (!File.Exists(path)) throw new WorkLibraryException("document.json がありません");
        var info = new FileInfo(path);
        if (info.Length > 64L * 1024 * 1024) throw new WorkLibraryException("document.json が大きすぎます");
        var doc = WorkDocumentJson.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.Pages)
        {
            Resolve(dir, p.ImageFile);
            Resolve(dir, p.ThumbnailFile);
        }
        return doc;
    }

    // ---- 削除 ----

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var dir = DirectoryOf(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>すべての作業を消す（id 名のフォルダだけ。それ以外の物には触らない）。</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root))
                if (Guid.TryParseExact(Path.GetFileName(dir), "D", out _)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- パス ----

    /// <summary>
    /// 作業フォルダ内の相対パスを完全パスにする。形（<see cref="WorkPaths.IsSafeRelativePath"/>）と、
    /// 解決後に作業フォルダの中であることの両方を確かめる。外を指したら <see cref="WorkLibraryException"/>。
    /// </summary>
    public static string Resolve(string dir, string relative)
    {
        if (!WorkPaths.IsSafeRelativePath(relative))
            throw new WorkLibraryException("作業フォルダの外を指すファイル名があります");
        var baseDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            throw new WorkLibraryException("作業フォルダの外を指すファイル名があります");
        return full;
    }

    private static void CleanTemporaries(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*" + TempSuffix, SearchOption.AllDirectories)) DeleteQuietly([f]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
