using System.Runtime.InteropServices.WindowsRuntime;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Fuseo.Input.Detection;

/// <summary>
/// 顔の検出（<c>Windows.Media.FaceAnalysis.FaceDetector</c>・端末内のみ・プリセットの face ルールは既定 OFF・§10.3／§10.13）。
///
/// 返す箱は**左上原点の正規化座標・padding 前**（顔の箱そのまま。広げるのは呼び出し側のルールの padding）。
/// - <c>FaceDetector.IsSupported</c> が false の環境では常に空。
/// - 長辺 <see cref="MaxLongSide"/>px に面積平均で縮小 → 輝度（Gray8）に変換して渡す（正規化座標なので縮小の影響は受けない）。
/// - <see cref="IDetector"/> は同期 API なので内部で待つ（呼び出し元は既に Task.Run の中・UI スレッドからは呼ばない）。
/// - 検出器は呼び出しごとに作る（ページ並列で使われる。同時呼び出しの安全性が文書化されていないため・作成は初回約 50ms、以降ほぼ 0）。
/// 通信は一切しない。
/// </summary>
public sealed class WindowsFaceDetector : IDetector
{
    /// <summary>検出に渡す画像の長辺の上限（px）。</summary>
    public const int MaxLongSide = 1600;

    public DetectorId Id => DetectorId.Face;

    /// <summary>この環境で顔の検出が使えるか（false なら <see cref="Detect"/> は常に空）。</summary>
    public static bool IsSupported => FaceDetector.IsSupported;

    public IReadOnlyList<NormRect> Detect(PageBitmap page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!FaceDetector.IsSupported) return [];
        return DetectAsync(page).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<NormRect>> DetectAsync(PageBitmap page)
    {
        var small = PixelOps.DownscaleToFit(page, MaxLongSide);
        var gray = ToGray8(small);
        var detector = await FaceDetector.CreateAsync().AsTask().ConfigureAwait(false);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(), BitmapPixelFormat.Gray8, small.Width, small.Height);
        var faces = await detector.DetectFacesAsync(bitmap).AsTask().ConfigureAwait(false);
        var boxes = new List<NormRect>(faces.Count);
        foreach (var f in faces)
        {
            var b = f.FaceBox;
            if (Normalize(b.X, b.Y, b.Width, b.Height, small.Width, small.Height) is { } r) boxes.Add(r);
        }
        return boxes;
    }

    /// <summary>BGRA → 輝度 1 バイト（BT.601 の重み・四捨五入）。α は見ない（取り込み済みのページは不透明）。</summary>
    internal static byte[] ToGray8(PageBitmap src)
    {
        var s = src.Pixels;
        var g = new byte[src.Width * src.Height];
        Parallel.For(0, src.Height, y =>
        {
            var row = y * src.Width;
            for (var x = 0; x < src.Width; x++)
            {
                var i = (row + x) * 4;
                g[row + x] = (byte)((114 * s[i] + 587 * s[i + 1] + 299 * s[i + 2] + 500) / 1000);
            }
        });
        return g;
    }

    /// <summary>画素の箱 → 正規化（0..1 に収める）。面積 0 なら null。</summary>
    internal static NormRect? Normalize(uint x, uint y, uint w, uint h, int width, int height)
    {
        var x0 = Math.Clamp((double)x / width, 0, 1);
        var y0 = Math.Clamp((double)y / height, 0, 1);
        var x1 = Math.Clamp((double)(x + (double)w) / width, 0, 1);
        var y1 = Math.Clamp((double)(y + (double)h) / height, 0, 1);
        return x1 > x0 && y1 > y0 ? new NormRect(x0, y0, x1 - x0, y1 - y0) : null;
    }
}
