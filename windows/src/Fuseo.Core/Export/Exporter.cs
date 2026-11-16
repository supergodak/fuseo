using Fuseo.Core.Contracts;
using Fuseo.Core.Imaging;

namespace Fuseo.Core.Export;

/// <summary><see cref="IExporter"/> の実装。形式ごとに自前のエンコーダへ振り分ける。</summary>
/// <remarks>
/// 0.4.0: 大きさの上限（<see cref="ExportOptions.MaxBytes"/>）があれば、メモリ上で作って収まるまで段を下げてから書く
/// （収まらなければ何も書かずに <see cref="SizeLimitUnreachableException"/>）。PDF のパスワードは <see cref="RasterPdfWriter"/> が暗号化する。
/// 段と下限は Mac 版（Sources/MaskingCore/Render/Exporter.swift）と同じ。
/// </remarks>
public sealed class Exporter : IExporter
{
    /// <summary>縮小の下限: 短辺がこれより小さくなる段は使わない（番号や氏名が読めなくなるため）。</summary>
    public const int MinShortSidePx = 600;

    /// <summary>上から順に試す（先に画質、次に解像度を下げる）。</summary>
    public static readonly IReadOnlyList<(double Scale, double Quality)> SizeSteps =
    [
        (1.0, 0.85), (1.0, 0.75), (1.0, 0.65), (0.85, 0.7), (0.85, 0.6), (0.7, 0.65), (0.7, 0.55), (0.6, 0.55), (0.5, 0.5),
    ];

    public void Export(IReadOnlyList<BurnedPage> pages, ExportOptions options, Func<int, Stream> openDestination)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openDestination);
        if (pages.Count == 0) throw new ArgumentException("ページがありません", nameof(pages));
        if (options.MaxBytes is { } limit && limit <= 0) throw new ArgumentOutOfRangeException(nameof(options), "上限が 0 以下");
        if (options.EffectivePdfPassword is { } pw && !ExportOptions.IsValidPdfPassword(pw))
            throw new ArgumentException("パスワードは半角の英数字と記号（32 文字まで）", nameof(options));

        switch (options.Format)
        {
            case ExportFormat.Pdf:
            {
                var password = options.EffectivePdfPassword;
                if (options.MaxBytes is not { } max)
                {
                    RasterPdfWriter.Write(pages, options.PdfImages, options.JpegQuality, openDestination(0), password: password);
                    break;
                }
                var bytes = Fit(max, step =>
                {
                    using var ms = new MemoryStream();
                    RasterPdfWriter.Write(pages, PdfImageEncoding.Jpeg, step.Quality, ms, step.Scale, password);
                    return ms.ToArray();
                });
                Write(openDestination(0), bytes);
                break;
            }
            case ExportFormat.Jpeg:
            case ExportFormat.Png:
                // 先に全ページを作る（上限に収まらないページが 1 つでもあれば、どのファイルも書かない）
                var encoded = new List<byte[]>(pages.Count);
                foreach (var page in pages)
                {
                    if (options.Format == ExportFormat.Jpeg && options.MaxBytes is { } maxJ)
                        encoded.Add(Fit(maxJ, step => JpegEncoder.Encode(
                            RgbImage.FromBgraOnWhite(Resample.Downscale(page.Bitmap, step.Scale, MinShortSidePx)), step.Quality)));
                    else
                    {
                        var rgb = RgbImage.FromBgraOnWhite(page.Bitmap);
                        encoded.Add(options.Format == ExportFormat.Jpeg ? JpegEncoder.Encode(rgb, options.JpegQuality) : PngEncoder.Encode(rgb));
                    }
                }
                for (var i = 0; i < encoded.Count; i++) Write(openDestination(i), encoded[i]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Format, "未知の形式");
        }
    }

    private static void Write(Stream s, byte[] bytes)
    {
        s.Write(bytes);
        s.Flush();
    }

    /// <summary>段を上から試し、最初に <paramref name="limit"/> 以下になったものを返す。</summary>
    internal static byte[] Fit(long limit, Func<(double Scale, double Quality), byte[]> make)
    {
        long smallest = long.MaxValue;
        foreach (var step in SizeSteps)
        {
            var bytes = make(step);
            if (bytes.LongLength <= limit) return bytes;
            smallest = Math.Min(smallest, bytes.LongLength);
        }
        throw new SizeLimitUnreachableException(limit, smallest);
    }
}
