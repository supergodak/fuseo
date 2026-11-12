using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Geometry;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 手動 4 隅切り抜き（W4.5）。切り抜き画面には「切り抜く前の元画像をいまの向きに回したもの」を出し、
/// 返ってきた四隅を回転 0 の向きに戻して <see cref="State.PageState.ApplyCrop"/> に渡す（画像と四隅を同じ回数だけ回す）。
/// 切り抜くと画像が変わるので、手動マスクと文字枠は消え（確認あり・元に戻すで戻せる）、QR／バーコード・顔は新しい画像で検出し直し、
/// 文字はいまの向きで読み直す（種別が自動のままなら判定結果に置き換える・§10.15）。
/// </summary>
public sealed partial class MainViewModel
{
    public RelayCommand CropCommand { get; }

    /// <summary>選択中のページの切り抜き画面を開き、決まったら適用する。</summary>
    public async Task CropSelectedPageAsync()
    {
        if (SelectedPage is not { } page || IsBusy) return;
        var state = page.State;
        var rotation = state.Rotation;
        var request = new CropRequest(QuarterTurn.Rotate(state.Original, rotation),
                                      Quad.Rotate(state.Crop ?? Quad.FullImage, rotation));
        if (_dialogs.AskCrop(request) is not { } chosen) return;
        await ApplyCropAsync(page, Quad.Rotate(chosen, -rotation));
    }

    /// <summary>
    /// 切り抜きを適用する。<paramref name="crop"/> は**元画像（回転 0）の上**の四隅（全体 = 切り抜きなし）。
    /// 変わらないなら何もしない。手で塗ったマスクがあれば消えることを確認してから進む。
    /// </summary>
    public async Task ApplyCropAsync(PageViewModel page, Quad crop)
    {
        if (IsBusy || !crop.IsConvex) return;
        var state = page.State;
        Quad? next = crop.IsFullImage ? null : crop;
        if (next == state.Crop) return;
        EndTextEditing(page);
        // 手で塗ったマスクと文字枠は基準の画像が変わるので消える。どちらかあれば確かめる（元に戻すで戻せる）。
        if (state.HasTextsOrManualMasks && !_dialogs.Confirm(Strings.CropConfirmTitle, Strings.CropConfirmMessage)) return;

        IsBusy = true;
        StatusMessage = Strings.StatusCropping;
        try
        {
            var original = state.Original;
            var rotation = state.Rotation;
            var fromPdf = state.FromPdf;
            var (qr, face, ocr, presets) = (_detector, _faceDetector, _recognizer, _presetList);
            // 新しい画像で QR（R0）・顔（いまの向きで探して R0 に戻す）を探し直し、文字はいまの向き（Rotate(Cropped, Rotation)）で向きの判定なしに読み直す（§10.15）。
            var (cropped, findings, autoType, failed) = await Task.Run(async () =>
            {
                var bmp = next is { } q ? Perspective.Warp(original, q) : original;
                var bad = false;
                IReadOnlyList<NormRect> qrBoxes = [];
                IReadOnlyList<NormRect> faceBoxes = [];
                Fuseo.Core.Analysis.PageAnalysis? analysis = null;
                if (qr is not null)
                {
                    try { qrBoxes = qr.Detect(bmp); }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("QR 検出", e); }
                }
                if (face is not null)
                {
                    // 顔はいまの向き（Rotate(Cropped, Rotation)）で探して R0 に戻す（§10.15・メイン決定）。
                    try { faceBoxes = DetectFacesUpright(face, bmp, rotation); }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("顔の検出", e); }
                }
                if (ocr is not null)
                {
                    try
                    {
                        var oriented = rotation == 0 ? bmp : QuarterTurn.Rotate(bmp, rotation);
                        analysis = await Fuseo.Core.Analysis.PageAnalyzer.AnalyzeAsync(
                            oriented, ocr, presets, detectUpright: false, CancellationToken.None,
                            flatPage: fromPdf).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("文字の読み取り", e); }
                }
                var f = new PageFindings(rotation, analysis?.Ocr, qrBoxes.ToList(), faceBoxes.ToList());
                return (bmp, f, analysis?.Classification.Type, bad);
            });
            // 利用者が選んだ種別はそのまま。自動のままなら判定結果に置き換える（読めなかったら今の種別のまま）。
            var type = state.TypeIsManual || autoType is not { } t ? state.Type : t;
            state.ApplyCrop(next, findings, type, _presets.GetValueOrDefault(type), _english, next is null ? null : cropped);
            page.Refresh();
            if (ReferenceEquals(SelectedPage, page)) Selection = null;
            OnPropertyChanged(nameof(SelectedDocumentType));
            RebuildSidePane();
            AfterEdit();
            StatusMessage = failed ? Strings.StatusCropDetectFailed : next is null ? Strings.StatusCropCleared : Strings.StatusCropped;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            StatusMessage = null;
            _dialogs.ShowError(Strings.CropErrorTitle, Strings.UnexpectedError);
            System.Diagnostics.Debug.WriteLine($"切り抜きに失敗: {e.GetType().Name}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Log(string what, Exception e) =>
        System.Diagnostics.Debug.WriteLine($"切り抜き後の{what}に失敗: {e.GetType().Name}");
}
