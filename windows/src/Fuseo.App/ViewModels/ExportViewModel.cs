using System.Collections.ObjectModel;
using System.IO;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Contracts;
using Fuseo.Core.Model;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 書き出しシート（**絶対条件: 保存前の確認 UI を外さない**）。
/// 流れ: 全ページを BurnIn → その結果画像をプレビュー → 注意書きの再掲 → 利用者が「保存」→ 保存ダイアログ →
/// **プレビューしたのと同じ BurnedPage** を IExporter へ渡す（見せた物と書く物を一致させる）。
/// </summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly IReadOnlyList<PageState> _pages;
    private readonly IReadOnlyDictionary<DocumentType, DocumentPreset> _presets;
    private readonly IMaskRasterizer _rasterizer;
    private readonly IExporter _exporter;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _now;

    private IReadOnlyList<BurnedPage>? _burned;
    private ExportFormat _format = ExportFormat.Pdf;
    private bool _isPreparing;
    private bool _isSaving;

    public ExportViewModel(IReadOnlyList<PageState> pages, IReadOnlyDictionary<DocumentType, DocumentPreset> presets,
                           IMaskRasterizer rasterizer, IExporter exporter, IDialogService dialogs, Func<DateTime> now,
                           bool? english = null)
    {
        var useEnglish = english ?? Strings.IsEnglish;
        _pages = pages;
        _presets = presets;
        _rasterizer = rasterizer;
        _exporter = exporter;
        _dialogs = dialogs;
        _now = now;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => CanSave);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty), () => !IsSaving);

        // 種別の注意書き（書き出す全ページの種別の和・重複なし）。バナーを閉じていてもここには必ず出す。
        Warnings = pages.Select(p => p.Type).Distinct()
            .SelectMany(t => _presets.TryGetValue(t, out var pr) ? pr.LocalizedWarnings(useEnglish) : [])
            .Distinct().ToList();
        HasZeroMaskPage = pages.Any(p => p.MaskCount == 0);
        var auto = pages.Sum(p => p.ActiveCandidateCount);
        var manual = pages.Sum(p => p.ManualMaskCount);
        SummaryText = string.Format(Strings.ExportSummary, auto + manual, auto, manual, pages.Count);
        HasPdfSource = pages.Any(p => p.FromPdf);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>閉じる要求（保存完了・キャンセル）。ビューが受けてウィンドウを閉じる。</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<PreviewItem> Previews { get; } = [];
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasZeroMaskPage { get; }
    public string SummaryText { get; }
    public bool HasPdfSource { get; }

    /// <summary>保存できたときの案内（メイン画面のステータスに出す）。未保存なら null。</summary>
    public string? SavedSummary { get; private set; }

    /// <summary>保存できたファイル名（複数ファイルなら 1 つ目・パスは持たない）。自動保存の lastExportedName に入れる。</summary>
    public string? SavedFileName { get; private set; }

    public ExportFormat Format
    {
        get => _format;
        set
        {
            if (!SetProperty(ref _format, value)) return;
            OnPropertyChanged(nameof(IsPdf));
            OnPropertyChanged(nameof(IsJpeg));
            OnPropertyChanged(nameof(IsPng));
            OnPropertyChanged(nameof(ShowPdfNote));
            OnPropertyChanged(nameof(ShowSeparateFilesNote));
        }
    }

    public bool IsPdf { get => Format == ExportFormat.Pdf; set { if (value) Format = ExportFormat.Pdf; } }
    public bool IsJpeg { get => Format == ExportFormat.Jpeg; set { if (value) Format = ExportFormat.Jpeg; } }
    public bool IsPng { get => Format == ExportFormat.Png; set { if (value) Format = ExportFormat.Png; } }

    public bool ShowPdfNote => IsPdf;
    public bool ShowSeparateFilesNote => !IsPdf && _pages.Count > 1;

    public string SaveButtonText => HasZeroMaskPage ? Strings.ExportSaveWithoutMask : Strings.ExportSave;

    public bool IsPreparing
    {
        get => _isPreparing;
        private set { if (SetProperty(ref _isPreparing, value)) RefreshCommands(); }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set { if (SetProperty(ref _isSaving, value)) RefreshCommands(); }
    }

    public bool CanSave => _burned is not null && !IsPreparing && !IsSaving;

    /// <summary>
    /// 全ページを焼き込む（UI を塞がないよう別スレッド）。確定マスクの組み立ては呼び出しスレッドで行い、
    /// 以後ページ状態が変わってもプレビュー・書き出しはこの時点の結果だけを使う。
    /// </summary>
    public async Task PrepareAsync()
    {
        if (_burned is not null || IsPreparing) return;
        IsPreparing = true;
        try
        {
            var work = _pages.Select(p => (p.Base, Masks: p.BuildConfirmedMasks())).ToList();
            var burned = await Task.Run(() => work.Select(w => _rasterizer.BurnIn(w.Base, w.Masks)).ToList());
            _burned = burned;
            Previews.Clear();
            for (var i = 0; i < burned.Count; i++) Previews.Add(new PreviewItem(i + 1, burned[i]));
        }
        catch (Exception e)
        {
            _dialogs.ShowError(Strings.ExportErrorTitle, string.Format(Strings.ExportPrepareFailed, e.Message));
        }
        finally
        {
            IsPreparing = false;
        }
    }

    /// <summary>保存ダイアログ → 書き出し（別スレッド）。プレビューしたのと同じ焼き込み結果を書く。</summary>
    public async Task SaveAsync()
    {
        if (!CanSave || _burned is not { } burned) return;
        var format = Format;
        var chosen = _dialogs.AskSavePath(format, ExportNaming.DefaultFileName(_now(), format));
        if (chosen is null) return;
        var targets = ExportNaming.Targets(chosen, format, burned.Count);
        if (targets.Count > 1)
        {
            // 保存ダイアログが上書き確認するのは選んだ名前だけなので、_1, _2 … の既存ファイルはここで確かめる。
            var existing = targets.Where(File.Exists).Select(t => Path.GetFileName(t)).ToList();
            if (existing.Count > 0 &&
                !_dialogs.Confirm(Strings.ExportOverwriteTitle,
                                  string.Format(Strings.ExportOverwriteMessage, string.Join("\n", existing))))
                return;
        }

        IsSaving = true;
        try
        {
            var options = new ExportOptions(format);
            await Task.Run(() => ExportWriter.Write(_exporter, burned, options, targets));
            SavedSummary = targets.Count == 1
                ? string.Format(Strings.StatusExportedOne, Path.GetFileName(targets[0]))
                : string.Format(Strings.StatusExportedMany, targets.Count, Path.GetFileName(targets[0]));
            SavedFileName = Path.GetFileName(targets[0]);
            IsSaving = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            _dialogs.ShowError(Strings.ExportErrorTitle, string.Format(Strings.ExportFailed, e.Message));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
