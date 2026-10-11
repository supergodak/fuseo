using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.App.Properties;
using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;

namespace Fuseo.App.Services;

/// <summary>
/// **仮実装（W3 の画面確認用）。統合時に W2 の本実装（IPageSource）へ置き換える。**
/// 画像だけを WIC で読み、Bgra32 の <see cref="PageBitmap"/> にする。PDF・パスワード・EXIF の向き・
/// ページ上限・HEIC の案内は扱わない（すべて W2 の担当）。
/// </summary>
public sealed class TemporaryImageSource : IPageSource
{
    public Task<IReadOnlyList<SourcePage>> LoadAsync(
        IReadOnlyList<string> paths, Func<string, Task<string?>> askPassword, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SourcePage>>(() =>
        {
            var pages = new List<SourcePage>();
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(string.Format(Strings.TempSourcePdfUnsupported, Path.GetFileName(path)));
                using var fs = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                int w = frame.PixelWidth, h = frame.PixelHeight;
                var px = new byte[checked(w * h * 4)];
                frame.CopyPixels(px, w * 4, 0);
                pages.Add(new SourcePage(new PageBitmap(w, h, px), Path.GetFileName(path), 0, false));
            }
            return pages;
        }, ct);
}
