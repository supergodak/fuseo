using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.Core.Geometry;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 手動 4 隅切り抜き（W4.5）。切り抜き画面には「切り抜く前の元画像をいまの向きに回したもの」を出し、
/// 返ってきた四隅を回転 0 の向きに戻して <see cref="State.PageState.ApplyCrop"/> に渡す（画像と四隅を同じ回数だけ回す）。
/// 切り抜くと画像が変わるので、手動マスクは消え（確認あり・元に戻すで戻せる）、QR／バーコードは新しい画像で検出し直す。
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
        if (state.ManualMaskCount > 0 && !_dialogs.Confirm(Strings.CropConfirmTitle, Strings.CropConfirmMessage)) return;

        IsBusy = true;
        StatusMessage = Strings.StatusCropping;
        try
        {
            var original = state.Original;
            var detector = _detector;
            var (cropped, boxes, failed) = await Task.Run(() =>
            {
                var bmp = next is { } q ? Perspective.Warp(original, q) : original;
                if (detector is null) return (bmp, (IReadOnlyList<NormRect>?)null, false);
                try { return (bmp, detector.Detect(bmp), false); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine($"切り抜き後の QR 検出に失敗: {e.GetType().Name}");
                    return (bmp, null, true);
                }
            });
            state.ApplyCrop(next, boxes is null ? null : detector!.Id, boxes ?? [],
                            _presets.GetValueOrDefault(state.Type), _english, next is null ? null : cropped);
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
}
