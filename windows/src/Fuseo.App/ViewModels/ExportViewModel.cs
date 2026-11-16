using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using System.IO;
using Fuseo.App.Configuration;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Contracts;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 書き出しシート（**絶対条件: 保存前の確認 UI を外さない**）。
/// 流れ: 全ページを（文字入れの文字を描いてから）BurnIn → その結果画像をプレビュー → 注意書きの再掲 → 利用者が「保存」→ 保存ダイアログ →
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
    private bool _separate;
    private bool _isCompleted;

    private readonly WpfTextMetrics _textMetrics;
    private readonly SettingsStore? _settings;

    // 提出用の設定（0.4.0）: 透かし・大きさの上限・PDF のパスワード。文言とパスワードはどこにも保存しない。
    private bool _watermarkEnabled;
    private string _watermarkText = "";
    private WatermarkLayoutKind _watermarkLayout;
    private double _watermarkOpacity;
    private BitmapSource? _watermarkPreview;
    private long? _sizeLimit;
    private bool _passwordEnabled;
    private string _password = "";
    private string _passwordConfirm = "";

    public ExportViewModel(IReadOnlyList<PageState> pages, IReadOnlyDictionary<DocumentType, DocumentPreset> presets,
                           IMaskRasterizer rasterizer, IExporter exporter, IDialogService dialogs, Func<DateTime> now,
                           bool? english = null, WpfTextMetrics? textMetrics = null, SettingsStore? settings = null)
    {
        _settings = settings;
        var saved = settings?.Load() ?? AppSettings.Default;
        _watermarkLayout = saved.WatermarkLayout;
        _watermarkOpacity = saved.WatermarkOpacity;
        _sizeLimit = saved.ExportSizeLimit;
        SizeLimitOptions = [new SizeLimitOption(Strings.ExportSizeLimitNone, null),
            .. AppSettings.SizeLimitChoices.Select(l => new SizeLimitOption(string.Format(Strings.ExportSizeLimitItem, ByteLabel(l)), l))];
        AddDateCommand = new RelayCommand(AddDate);
        _textMetrics = textMetrics ?? WpfTextMetrics.Instance;
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
        // 文字入れ（v0.3）: 書いた量の確認（空の枠は描かないので数えない）と、枠からはみ出している文字（切り取らずにそのまま描く）。
        TextCount = pages.Sum(p => p.TextCount);
        OverflowingTextCount = pages.Sum(p => p.Texts.Count(t => TextLayout.Overflows(t, p.Base.Width, p.Base.Height, _textMetrics)));
        TextSummaryText = TextCount > 0 ? string.Format(Strings.ExportTextSummary, TextCount) : "";
        TextOverflowText = OverflowingTextCount > 0 ? string.Format(Strings.ExportTextOverflow, OverflowingTextCount) : "";
        HasPdfSource = pages.Any(p => p.FromPdf);

        // 書類のまとまり（W9-4）: SourceDocId ごと・画面に初めて出てくる順。書類内のページ順は画面のページ順。
        var order = new List<Guid>();
        var groups = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < pages.Count; i++)
        {
            if (!groups.TryGetValue(pages[i].SourceDocId, out var list))
            {
                groups[pages[i].SourceDocId] = list = [];
                order.Add(pages[i].SourceDocId);
            }
            list.Add(i);
        }
        Documents = order.Select(id => new ExportDocument(pages[groups[id][0]].SourceName, groups[id])).ToList();
        // マスクが 0 のページを含む書類（安全側: 1 ページでも 0 なら挙げる）。
        ZeroMaskDocumentNames = Documents.Where(d => d.PageIndexes.Any(i => pages[i].MaskCount == 0))
            .Select(d => d.Name).ToList();
        ZeroMaskDocumentsText = ZeroMaskDocumentNames.Count > 0
            ? string.Format(Strings.ExportZeroMaskDocuments, string.Join(Strings.ListSeparator, ZeroMaskDocumentNames))
            : "";
        OpenSavedFolderCommand = new RelayCommand(OpenSavedFolder, () => SavedFolder is not null);
    }

    /// <summary>書類（元ファイル）ごとのページ番号（0 始まり・画面の順）。</summary>
    public IReadOnlyList<ExportDocument> Documents { get; }

    /// <summary>書類が 2 つ以上のときだけ「1 つにまとめる／書類ごとに分ける」を出す。</summary>
    public bool HasMultipleDocuments => Documents.Count > 1;

    /// <summary>マスクが 0 のページを含む書類の名前（書き出しシートで列挙する）。</summary>
    public IReadOnlyList<string> ZeroMaskDocumentNames { get; }
    public string ZeroMaskDocumentsText { get; }
    public bool HasZeroMaskDocuments => ZeroMaskDocumentNames.Count > 0;

    /// <summary>「書類ごとに分ける」（書類が 1 つなら常に false＝今どおり 1 ファイル）。</summary>
    public bool IsSeparate
    {
        get => _separate && HasMultipleDocuments;
        set
        {
            if (!HasMultipleDocuments || !SetProperty(ref _separate, value)) return;
            OnPropertyChanged(nameof(IsCombine));
            OnPropertyChanged(nameof(ShowSeparateFilesNote));
            OnPropertyChanged(nameof(ShowSeparateModeNote));
        }
    }

    public bool IsCombine { get => !IsSeparate; set { if (value) IsSeparate = false; } }

    public bool ShowSeparateModeNote => IsSeparate;

    /// <summary>「書類ごとに分ける」で保存し終えた（シートは閉じずに完了を表示する）。</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (!SetProperty(ref _isCompleted, value)) return;
            OnPropertyChanged(nameof(IsNotCompleted));
            RefreshCommands();
        }
    }

    public bool IsNotCompleted => !IsCompleted;

    /// <summary>「書類ごとに分ける」の保存先フォルダ（保存できたときだけ）。</summary>
    public string? SavedFolder { get; private set; }

    public string? CompletedText { get; private set; }

    public RelayCommand OpenSavedFolderCommand { get; }

    private void OpenSavedFolder()
    {
        if (SavedFolder is not { } folder) return;
        try
        {
            _dialogs.OpenFolder(folder);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _dialogs.ShowError(Strings.ExportErrorTitle, string.Format(Strings.SettingsOpenFolderFailed, e.Message));
        }
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

    /// <summary>書き出しで描かれる文字枠の数（文字入れ・v0.3）。マスクの数には入れない。</summary>
    public int TextCount { get; }
    public int OverflowingTextCount { get; }
    public bool HasTexts => TextCount > 0;
    public bool HasOverflowingTexts => OverflowingTextCount > 0;
    /// <summary>「文字 N 箇所」。</summary>
    public string TextSummaryText { get; }
    /// <summary>「枠からはみ出している文字が N 箇所あります（はみ出したまま書き出します）。」</summary>
    public string TextOverflowText { get; }

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
            OnPropertyChanged(nameof(ShowSizeLimit));
            OnPropertyChanged(nameof(ShowSizeLimitNote));
            OnPropertyChanged(nameof(ShowPasswordOptions));
            PasswordChanged();
        }
    }

    public bool IsPdf { get => Format == ExportFormat.Pdf; set { if (value) Format = ExportFormat.Pdf; } }
    public bool IsJpeg { get => Format == ExportFormat.Jpeg; set { if (value) Format = ExportFormat.Jpeg; } }
    public bool IsPng { get => Format == ExportFormat.Png; set { if (value) Format = ExportFormat.Png; } }

    public bool ShowPdfNote => IsPdf;
    public bool ShowSeparateFilesNote => !IsPdf && (IsSeparate ? Documents.Any(d => d.PageIndexes.Count > 1) : _pages.Count > 1);

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

    public bool CanSave => _burned is not null && !IsPreparing && !IsSaving && !IsCompleted
                           && PasswordState is PdfPasswordState.Off or PdfPasswordState.Ok;

    // ---- 提出用の設定（0.4.0） ----

    public RelayCommand AddDateCommand { get; }

    public bool WatermarkEnabled
    {
        get => _watermarkEnabled;
        set { if (SetProperty(ref _watermarkEnabled, value)) WatermarkChanged(); }
    }

    public string WatermarkText
    {
        get => _watermarkText;
        set { if (SetProperty(ref _watermarkText, value ?? "")) WatermarkChanged(); }
    }

    public bool IsWatermarkTiled
    {
        get => _watermarkLayout == WatermarkLayoutKind.Tiled;
        set { if (value) SetLayout(WatermarkLayoutKind.Tiled); }
    }

    public bool IsWatermarkDiagonal
    {
        get => _watermarkLayout == WatermarkLayoutKind.Diagonal;
        set { if (value) SetLayout(WatermarkLayoutKind.Diagonal); }
    }

    public double WatermarkOpacity
    {
        get => _watermarkOpacity;
        set { if (SetProperty(ref _watermarkOpacity, Math.Clamp(value, Watermark.MinOpacity, Watermark.MaxOpacity))) WatermarkChanged(); }
    }

    public double WatermarkMinOpacity => Watermark.MinOpacity;
    public double WatermarkMaxOpacity => Watermark.MaxOpacity;

    /// <summary>書き出しに使う透かし（オンで文言があるときだけ）。</summary>
    public Watermark? ActiveWatermark
    {
        get
        {
            if (!WatermarkEnabled) return null;
            var wm = new Watermark(WatermarkText, _watermarkLayout, _watermarkOpacity);
            return wm.TrimmedText.Length == 0 ? null : wm;
        }
    }

    /// <summary>見本（1 ページ目を縮めて透かしを重ねた画像）。文言が無ければ null。</summary>
    public BitmapSource? WatermarkPreview { get => _watermarkPreview; private set => SetProperty(ref _watermarkPreview, value); }
    public bool HasWatermarkPreview => WatermarkPreview is not null;
    public bool ShowWatermarkEmpty => WatermarkEnabled && ActiveWatermark is null;
    public bool ShowWatermarkOptions => WatermarkEnabled;

    private void SetLayout(WatermarkLayoutKind layout)
    {
        if (_watermarkLayout == layout) return;
        _watermarkLayout = layout;
        OnPropertyChanged(nameof(IsWatermarkTiled));
        OnPropertyChanged(nameof(IsWatermarkDiagonal));
        WatermarkChanged();
    }

    private void WatermarkChanged()
    {
        UpdateWatermarkPreview();
        OnPropertyChanged(nameof(ActiveWatermark));
        OnPropertyChanged(nameof(ShowWatermarkEmpty));
        OnPropertyChanged(nameof(ShowWatermarkOptions));
    }

    private void UpdateWatermarkPreview()
    {
        WatermarkPreview = ActiveWatermark is { } wm && _burned is { Count: > 0 } burned ? WatermarkPainter.Preview(burned[0], wm) : null;
        OnPropertyChanged(nameof(HasWatermarkPreview));
    }

    /// <summary>今日の日付（yyyy/MM/dd）を文言に足す（すでに入っていれば何もしない）。</summary>
    private void AddDate()
    {
        var date = _now().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        if (WatermarkText.Contains(date, StringComparison.Ordinal)) return;
        var b = WatermarkText.Trim();
        WatermarkText = b.Length == 0 ? date : b + " " + date;
    }

    public IReadOnlyList<SizeLimitOption> SizeLimitOptions { get; }

    public SizeLimitOption SelectedSizeLimit
    {
        get => SizeLimitOptions.FirstOrDefault(o => o.Bytes == _sizeLimit) ?? SizeLimitOptions[0];
        set
        {
            if (value is null || value.Bytes == _sizeLimit) return;
            _sizeLimit = value.Bytes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSizeLimitNote));
        }
    }

    /// <summary>PNG は無劣化の形式なので上限を出さない。</summary>
    public bool ShowSizeLimit => !IsPng;
    public bool ShowSizeLimitNote => ShowSizeLimit && _sizeLimit is not null;
    public bool ShowPasswordOptions => IsPdf;
    public bool ShowPasswordFields => IsPdf && PasswordEnabled;

    public bool PasswordEnabled
    {
        get => _passwordEnabled;
        set { if (SetProperty(ref _passwordEnabled, value)) PasswordChanged(); }
    }

    /// <summary>PasswordBox はバインドできないので、画面の側から入れる。</summary>
    public string Password { get => _password; set { _password = value ?? ""; PasswordChanged(); } }
    public string PasswordConfirm { get => _passwordConfirm; set { _passwordConfirm = value ?? ""; PasswordChanged(); } }

    public PdfPasswordState PasswordState
    {
        get
        {
            if (!PasswordEnabled || !IsPdf) return PdfPasswordState.Off;
            if (_password.Length == 0) return PdfPasswordState.Empty;
            if (!ExportOptions.IsValidPdfPassword(_password)) return PdfPasswordState.Invalid;
            return _password == _passwordConfirm ? PdfPasswordState.Ok : PdfPasswordState.Mismatch;
        }
    }

    public string PasswordMessage => PasswordState switch
    {
        PdfPasswordState.Mismatch => Strings.ExportPasswordMismatch,
        PdfPasswordState.Invalid => Strings.ExportPasswordInvalid,
        _ => "",
    };

    public bool HasPasswordMessage => PasswordMessage.Length > 0;

    private void PasswordChanged()
    {
        OnPropertyChanged(nameof(ShowPasswordFields));
        OnPropertyChanged(nameof(PasswordState));
        OnPropertyChanged(nameof(PasswordMessage));
        OnPropertyChanged(nameof(HasPasswordMessage));
        RefreshCommands();
    }

    /// <summary>実際の書き出しの設定（形式＋大きさの上限＋パスワード）。透かしは保存のときに焼き込み結果へ重ねる。</summary>
    public ExportOptions BuildOptions(ExportFormat format) => new(
        format,
        MaxBytes: format == ExportFormat.Png ? null : _sizeLimit,
        PdfPassword: format == ExportFormat.Pdf && PasswordState == PdfPasswordState.Ok ? _password : null);

    /// <summary>保存し終えたら: 配置・濃さ・上限を次回へ（文言とパスワードは持ち越さない）。</summary>
    private void RememberChoices()
    {
        _password = _passwordConfirm = "";
        if (_settings is null) return;
        try
        {
            _settings.Save(_settings.Load() with
            {
                WatermarkLayout = _watermarkLayout, WatermarkOpacity = _watermarkOpacity, ExportSizeLimit = _sizeLimit,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 次回の既定に使うだけなので黙って続ける */ }
    }

    /// <summary>「2 MB」「300 KB」（1 MB = 1,000,000 バイト・Mac 版と同じ）。</summary>
    public static string ByteLabel(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            var mb = bytes / 1_000_000.0;
            return mb >= 10 || mb == Math.Round(mb) ? $"{Math.Round(mb):0} MB" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", mb);
        }
        return $"{Math.Max(1, Math.Round(bytes / 1000.0)):0} KB";
    }

    private string FailureMessage(Exception e) => e is SizeLimitUnreachableException s
        ? string.Format(Strings.ExportSizeLimitFailed, ByteLabel(s.LimitBytes), ByteLabel(s.SmallestBytes))
        : string.Format(Strings.ExportFailed, e.Message);

    /// <summary>透かしを重ねたページ（オフならそのまま）。</summary>
    private static IReadOnlyList<BurnedPage> WithWatermark(IReadOnlyList<BurnedPage> pages, Watermark? wm) =>
        wm is null ? pages : pages.Select(p => WatermarkPainter.Apply(p, wm)).ToList();

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
            // 焼き込み順は 基準の画像 → 文字 → マスク（form-fill-design §2）: 文字を描いた複製を BurnIn に渡し、マスクは常にその上に塗る。
            var work = _pages.Select(p => (p.Base, Texts: p.Texts.ToList(), Masks: p.BuildConfirmedMasks())).ToList();
            var metrics = _textMetrics;
            var burned = await Task.Run(() => work.Select(w =>
                _rasterizer.BurnIn(TextPainter.Compose(w.Base, w.Texts, w.Base.Width, w.Base.Height, metrics), w.Masks)).ToList());
            _burned = burned;
            Previews.Clear();
            for (var i = 0; i < burned.Count; i++) Previews.Add(new PreviewItem(i + 1, burned[i]));
            UpdateWatermarkPreview();
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
        if (IsSeparate)
        {
            await SaveSeparateAsync(burned);
            return;
        }
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
            var options = BuildOptions(format);
            var wm = ActiveWatermark;
            await Task.Run(() => ExportWriter.Write(_exporter, WithWatermark(burned, wm), options, targets));
            RememberChoices();
            SavedSummary = targets.Count == 1
                ? string.Format(Strings.StatusExportedOne, Path.GetFileName(targets[0]))
                : string.Format(Strings.StatusExportedMany, targets.Count, Path.GetFileName(targets[0]));
            SavedFileName = Path.GetFileName(targets[0]);
            IsSaving = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            _dialogs.ShowError(Strings.ExportErrorTitle, FailureMessage(e));
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// 「書類ごとに分ける」（§10.17）: 保存先フォルダを選び、書類ごとに <c>&lt;元名&gt;-masked&lt;拡張子&gt;</c> へ順に書く。
    /// **既存のファイルは上書きしない**（名前を変える＋<see cref="ExportWriter"/> の上書きしないモード）。1 件失敗しても残りは続け、
    /// 最後に失敗した書類名と理由をまとめて表示する。書くのはプレビューしたのと同じ焼き込み結果だけ。
    /// </summary>
    private async Task SaveSeparateAsync(IReadOnlyList<BurnedPage> burned)
    {
        var folder = _dialogs.AskFolder(Strings.ExportFolderDialogTitle);
        if (folder is null) return;
        var format = Format;
        var docs = Documents;
        var options = BuildOptions(format);
        var wm = ActiveWatermark;
        IsSaving = true;
        try
        {
            var (saved, failures) = await Task.Run(() =>
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ok = new List<string>();
                var bad = new List<(string Name, string Reason)>();
                foreach (var d in docs)
                {
                    try
                    {
                        var pages = WithWatermark(d.PageIndexes.Select(i => burned[i]).ToList(), wm);
                        var targets = ExportNaming.SeparateTargets(folder, d.Name, format, pages.Count, ExportNaming.PathTaken, taken);
                        ExportWriter.Write(_exporter, pages, options, targets, overwrite: false);
                        ok.Add(Path.GetFileName(targets[0]));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        bad.Add((d.Name, e is SizeLimitUnreachableException ? FailureMessage(e) : e.Message));
                    }
                }
                return (ok, bad);
            });
            if (failures.Count > 0)
                _dialogs.ShowError(Strings.ExportErrorTitle, string.Format(Strings.ExportSeparateFailed, saved.Count, failures.Count,
                    string.Join("\n", failures.Select(f => string.Format(Strings.ExportSeparateFailedItem, f.Name, f.Reason)))));
            if (saved.Count > 0)
            {
                RememberChoices();
                SavedFolder = folder;
                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                SavedFileName = string.IsNullOrEmpty(folderName) ? folder : folderName;
                SavedSummary = string.Format(Strings.StatusExportedSeparate, saved.Count, SavedFileName);
                CompletedText = string.Format(Strings.ExportSeparateCompleted, saved.Count);
                OnPropertyChanged(nameof(SavedFolder));
                OnPropertyChanged(nameof(CompletedText));
                OpenSavedFolderCommand.RaiseCanExecuteChanged();
                IsCompleted = true;
            }
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
        OnPropertyChanged(nameof(CancelButtonText));
    }

    /// <summary>保存し終えたら「閉じる」。</summary>
    public string CancelButtonText => IsCompleted ? Strings.Close : Strings.Cancel;
}

/// <summary>書き出しの書類 1 つ（元ファイルの名前と、書き出すページの番号＝画面の順・0 始まり）。</summary>
public sealed record ExportDocument(string Name, IReadOnlyList<int> PageIndexes);

/// <summary>ファイルの大きさの上限の選択肢（null = 上限なし）。</summary>
public sealed record SizeLimitOption(string Label, long? Bytes)
{
    public override string ToString() => Label;
}

/// <summary>PDF のパスワードの入力の状態（保存ボタンを押せるのは Off と Ok のときだけ）。</summary>
public enum PdfPasswordState { Off, Empty, Invalid, Mismatch, Ok }
