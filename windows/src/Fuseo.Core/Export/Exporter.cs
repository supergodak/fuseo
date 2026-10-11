using Fuseo.Core.Contracts;

namespace Fuseo.Core.Export;

/// <summary><see cref="IExporter"/> の実装。形式ごとに自前のエンコーダへ振り分けるだけ。</summary>
public sealed class Exporter : IExporter
{
    public void Export(IReadOnlyList<BurnedPage> pages, ExportOptions options, Func<int, Stream> openDestination)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openDestination);
        if (pages.Count == 0) throw new ArgumentException("ページがありません", nameof(pages));

        switch (options.Format)
        {
            case ExportFormat.Pdf:
                RasterPdfWriter.Write(pages, options.PdfImages, options.JpegQuality, openDestination(0));
                break;
            case ExportFormat.Jpeg:
            case ExportFormat.Png:
                for (var i = 0; i < pages.Count; i++)
                {
                    var rgb = RgbImage.FromBgraOnWhite(pages[i].Bitmap);
                    var bytes = options.Format == ExportFormat.Jpeg
                        ? JpegEncoder.Encode(rgb, options.JpegQuality)
                        : PngEncoder.Encode(rgb);
                    var s = openDestination(i);
                    s.Write(bytes);
                    s.Flush();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Format, "未知の形式");
        }
    }
}
