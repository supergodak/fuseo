using Fuseo.App.State;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;

namespace Fuseo.App.Library;

/// <summary>保存する中身の一式（UI スレッドで取り、別スレッドで書く）。</summary>
public sealed record WorkSnapshot(WorkDocument Document, IReadOnlyDictionary<string, PageBitmap> Images);

/// <summary>
/// <see cref="PageState"/> ⇄ <see cref="WorkPage"/> の対応（W5）。
/// - 画像は <see cref="PageState.Original"/>（取り込んだまま・回転 0・切り抜き前）を保存し、切り抜きは四隅（manualQuad）で持つ。座標は <see cref="PageState"/> の snapshot そのまま
///   （現在の回転で回した画像の上）。
/// - Undo 履歴は保存しない。
/// </summary>
public static class WorkMapper
{
    /// <summary>
    /// 一覧の表示名: 最初のページの元ファイル名。複数のファイルから取り込んでいれば「&lt;最初の名前&gt; ほか N 件」
    /// （N = 最初の名前以外の元ファイルの数）。
    /// </summary>
    public static string Title(IReadOnlyList<PageState> pages, string manyFormat)
    {
        if (pages.Count == 0) return "";
        var first = pages[0].SourceName;
        var others = pages.Select(p => p.SourceName).Distinct(StringComparer.Ordinal).Count() - 1;
        return others > 0 ? string.Format(manyFormat, first, others) : first;
    }

    /// <summary>
    /// 現在のページ一式を保存用に写し取る。**UI スレッドで呼ぶ**（PageState の値は不変の snapshot と元画像なので、
    /// 以後の編集と混ざらない。チェック印の集合だけは可変なのでここで複製する）。
    /// </summary>
    public static WorkSnapshot Capture(
        Guid id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt, WorkStatus status, string? lastExportedName,
        IReadOnlyList<PageState> pages)
    {
        var images = new Dictionary<string, PageBitmap>(StringComparer.Ordinal);
        var workPages = new List<WorkPage>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var s = pages[i];
            var imageFile = WorkPaths.PageImage(s.Id);
            images[imageFile] = s.Original;
            workPages.Add(new WorkPage
            {
                Index = i,
                ImageFile = imageFile,
                ThumbnailFile = WorkPaths.Thumbnail(s.Id),
                SourceName = s.SourceName,
                DocumentType = s.Type,
                ManualRotation = s.Rotation,
                Candidates = s.Candidates.ToList(),
                Manual = new() { Rects = [.. s.Manual.Rects], Strokes = [.. s.Manual.Strokes] },
                PixelWidth = s.Original.Width,
                PixelHeight = s.Original.Height,
                FromPdf = s.FromPdf,
                SourcePageIndex = s.SourcePageIndex,
                // QR の生の箱は rawDetections（v0.1 と同じ項目）。OCR の結果は保存しない（§10.15・書類の文字列を残さない）。
                RawDetections = s.Findings.Qr.ToList(),
                RawDetector = s.Findings.Qr.Count > 0 ? DetectorId.QrBarcode : null,
                CheckedRuleIds = s.CheckedRuleIds.OrderBy(r => r, StringComparer.Ordinal).ToList(),
                RawFaces = s.Findings.Faces.ToList(),
                TypeIsManual = s.TypeIsManual,
                ManualQuad = s.Crop,
            });
        }
        var doc = new WorkDocument
        {
            Id = id,
            Title = title,
            CreatedAt = TruncateToMs(createdAt),
            UpdatedAt = TruncateToMs(updatedAt),
            Status = status,
            LastExportedName = lastExportedName,
            Pages = workPages,
        };
        return new WorkSnapshot(doc, images);
    }

    /// <summary>読み込んだ作業からページ状態を作る（検出は走らせない）。</summary>
    public static IReadOnlyList<PageState> Restore(LoadedWork work)
    {
        var result = new List<PageState>(work.Document.Pages.Count);
        for (var i = 0; i < work.Document.Pages.Count; i++)
        {
            var p = work.Document.Pages[i];
            // ページ id はファイル名から取り戻す（同じ id なら次の保存で画像を書き直さない）。違う形なら新しい id。
            var id = WorkPaths.PageIdFromImageFile(p.ImageFile) ?? Guid.NewGuid();
            result.Add(PageState.Restore(
                id, work.Images[i], p.SourceName, p.SourcePageIndex, p.FromPdf, p.DocumentType, p.ManualRotation,
                p.Manual, p.Candidates, QrBoxes(p), p.CheckedRuleIds, p.ManualQuad, p.RawFaces, p.TypeIsManual));
        }
        return result;
    }

    /// <summary>
    /// 保存された QR の箱。v0.1 の rawDetections は rawDetector の箱（v0.1 の検出器は QR だけ。検出しなかった・失敗したページは
    /// rawDetector = null で箱も空）なので、QR 以外の検出器の印が付いていたら使わない。
    /// </summary>
    private static IReadOnlyList<Fuseo.Core.Geometry.NormRect> QrBoxes(WorkPage p) =>
        p.RawDetector is null or DetectorId.QrBarcode ? p.RawDetections : [];

    /// <summary>JSON はミリ秒までなので、メモリ上の値も揃える（往復で値が変わらないように）。</summary>
    public static DateTimeOffset TruncateToMs(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}
