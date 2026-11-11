using System.Collections.ObjectModel;
using Fuseo.App.Configuration;
using Fuseo.App.Library;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Analysis;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.App.ViewModels;

/// <summary>編集の道具（Text = 文字入れ・v0.3）。</summary>
public enum EditorTool { Select, Rect, Brush, Text }

public enum BrushSize { Thin, Medium, Thick }

/// <summary>
/// メイン画面の ViewModel。取り込み（IPageSource）・焼き込み（IMaskRasterizer）・書き出し（IExporter）・
/// 自動検出（QR／バーコードの IDetector・顔の IDetector・文字認識 ITextRecognizer。どれも null 可・W9-3）はコンストラクタ注入。キャンバスのマウス操作はビューが座標を正規化（CoordinateSpace.FromView）してから
/// ここのメソッドを呼ぶ＝この型は画面座標を一切知らない。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>ブラシの太さ（画像短辺に対する比）。Mac 版の既定 0.03 を「中」にする。</summary>
    public static double BrushWidthFor(BrushSize size) => size switch
    {
        BrushSize.Thin => 0.015,
        BrushSize.Thick => 0.06,
        _ => 0.03,
    };

    /// <summary>これより小さい矩形はクリックの誤操作とみなして足さない（正規化・各辺）。</summary>
    public const double MinRectSize = 0.002;

    private readonly IPageSource _source;
    private readonly IMaskRasterizer _rasterizer;
    private readonly IExporter _exporter;
    private readonly IDialogService _dialogs;
    private readonly IDetector? _detector;
    private readonly IDetector? _faceDetector;
    private readonly ITextRecognizer? _recognizer;
    private readonly IReadOnlyList<DocumentPreset> _presetList;
    private readonly Dictionary<DocumentType, DocumentPreset> _presets;
    private readonly HashSet<DocumentType> _dismissedBanners = [];
    private readonly Func<DateTime> _now;
    private readonly bool _english;

    private PageViewModel? _selectedPage;
    private EditorTool _tool = EditorTool.Rect;
    private BrushSize _brushSize = BrushSize.Medium;
    private MaskSelection? _selection;
    private bool _isBusy;
    private string? _statusMessage;
    private bool _ocrNoticeDismissed;
    private bool _analysisProgressActive;
    private int _analysisProgressShown;

    public MainViewModel(IPageSource source, IMaskRasterizer rasterizer, IExporter exporter,
                         IReadOnlyList<DocumentPreset> presets, IDialogService dialogs, Func<DateTime>? now = null,
                         IDetector? detector = null, FileWorkLibrary? library = null,
                         Func<TimeSpan, CancellationToken, Task>? saveDelay = null, bool? english = null,
                         SettingsStore? settings = null, ITextRecognizer? recognizer = null, IDetector? faceDetector = null)
    {
        // プリセット由来の文言（種別名・チェックリスト・注意書き・候補）を英語にするか。既定は表示言語（Strings.Culture）に合わせる。
        _english = english ?? Strings.IsEnglish;
        _settings = settings;
        _detector = detector;
        _faceDetector = faceDetector;
        _recognizer = recognizer;
        _presetList = presets;
        _source = source;
        _rasterizer = rasterizer;
        _exporter = exporter;
        _dialogs = dialogs;
        _now = now ?? (() => DateTime.Now);
        _presets = presets.ToDictionary(p => p.DocumentType);
        // 並びは DocumentType の定義順（generic が先頭）。
        DocumentTypes = presets.OrderBy(p => (int)p.DocumentType)
            .Select(p => new PresetOption(p.DocumentType, p.Name(_english), p)).ToList();

        OpenFilesCommand = new RelayCommand(() => _ = OpenFilesAsync(), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(() => _ = OpenFolderAsync(), () => !IsBusy);
        RemovePageCommand = new RelayCommand(RemoveSelectedPage, () => SelectedPage is not null && !IsBusy);
        MovePageUpCommand = new RelayCommand(() => MoveSelectedPage(-1), () => CanMove(-1));
        MovePageDownCommand = new RelayCommand(() => MoveSelectedPage(+1), () => CanMove(+1));
        UndoCommand = new RelayCommand(Undo, () => SelectedPage?.State.CanUndo == true && !IsBusy);
        RedoCommand = new RelayCommand(Redo, () => SelectedPage?.State.CanRedo == true && !IsBusy);
        DeleteSelectionCommand = new RelayCommand(DeleteSelection, () => Selection is not null || SelectedText is not null);
        ExportCommand = new RelayCommand(OpenExport, () => Pages.Count > 0 && !IsBusy);
        DismissBannerCommand = new RelayCommand(DismissBanner);
        RotateLeftCommand = new RelayCommand(() => Rotate(-1), () => SelectedPage is not null && !IsBusy);
        RotateRightCommand = new RelayCommand(() => Rotate(+1), () => SelectedPage is not null && !IsBusy);
        CropCommand = new RelayCommand(() => _ = CropSelectedPageAsync(), () => SelectedPage is not null && !IsBusy);
        SetToolCommand = new RelayCommand(p => { if (p is EditorTool t) Tool = t; });
        SetBrushSizeCommand = new RelayCommand(p => { if (p is BrushSize s) BrushSize = s; });
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsBusy);
        OpenAboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        RereadTextCommand = new RelayCommand(() => _ = RereadTextAsync(),
                                             () => SelectedPage is not null && _recognizer is not null && !IsBusy);
        DismissOcrNoticeCommand = new RelayCommand(() =>
        {
            _ocrNoticeDismissed = true;
            OnPropertyChanged(nameof(IsOcrUnavailableNoticeVisible));
        });
        InitLibrary(library, saveDelay);
    }

    public ObservableCollection<PageViewModel> Pages { get; } = [];
    public IReadOnlyList<PresetOption> DocumentTypes { get; }
    public ObservableCollection<ChecklistItemViewModel> Checklist { get; } = [];
    public ObservableCollection<CandidateViewModel> CandidateItems { get; } = [];

    public RelayCommand OpenFilesCommand { get; }
    /// <summary>「フォルダを開く」（W9-4）: 選んだフォルダの直下の対応ファイルを新しい作業として開く。</summary>
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RemovePageCommand { get; }
    public RelayCommand MovePageUpCommand { get; }
    public RelayCommand MovePageDownCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteSelectionCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand RotateLeftCommand { get; }
    public RelayCommand RotateRightCommand { get; }
    public RelayCommand SetToolCommand { get; }
    public RelayCommand SetBrushSizeCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenAboutCommand { get; }
    /// <summary>「文字を読み直す」（§10.15）: いまの向きで読み直して候補を作り直す。手動マスクはそのまま・1 回の変更として Undo。</summary>
    public RelayCommand RereadTextCommand { get; }
    public RelayCommand DismissOcrNoticeCommand { get; }

    /// <summary>プリセット由来の文言を英語で出しているか。</summary>
    public bool IsEnglish => _english;

    /// <summary>キャンバスの再描画が要るとき（マスク・選択・ページが変わった）。</summary>
    public event EventHandler? CanvasChanged;

    public bool HasPages => Pages.Count > 0;

    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (ReferenceEquals(_selectedPage, value)) return;
            EndTextEditing(_selectedPage);   // 入力中の文字は確定してからページを替える
            if (!SetProperty(ref _selectedPage, value)) return;
            _selection = null;
            _selectedTextId = null;
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(SelectedTextId));
            OnPropertyChanged(nameof(EditingTextId));
            OnTextStateChanged();
            OnPropertyChanged(nameof(SelectedDocumentType));
            RebuildSidePane();
            RefreshCommands();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PresetOption? SelectedDocumentType
    {
        get => SelectedPage is { } p ? DocumentTypes.FirstOrDefault(o => o.Type == p.State.Type) : null;
        set
        {
            if (SelectedPage is not { } p || value is null || p.State.Type == value.Type || IsBusy) return;
            // 種別に合わせて候補（defaultOn）を作り直す。種別ごと 1 回の変更として Undo できる。以後このページは手動扱い。
            // 文字をまだ読んでいない（再開した作業など）で文字認識が使えるなら、先にいまの向きで読む（別スレッド・§10.15）。
            if (p.State.Findings.Ocr is null && _recognizer is not null)
            {
                _ = ChangeTypeWithOcrAsync(p, value.Type);
                return;
            }
            ApplyTypeChange(p, value.Type, null);
        }
    }

    private void ApplyTypeChange(PageViewModel p, DocumentType type, PageFindings? findings)
    {
        p.State.ChangeType(type, _presets.GetValueOrDefault(type), _english, findings);
        p.Refresh();
        if (ReferenceEquals(SelectedPage, p))
        {
            Selection = null;
            OnPropertyChanged(nameof(SelectedDocumentType));
            RebuildSidePane();
        }
        AfterEdit();
    }

    private async Task ChangeTypeWithOcrAsync(PageViewModel p, DocumentType type)
    {
        IsBusy = true;
        StatusMessage = Strings.StatusReadingTextOnce;
        PageFindings? findings = null;
        var failed = false;
        try
        {
            findings = await ReadTextAtCurrentRotationAsync(p.State);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 読めなくても種別は変える（OCR なしで QR・顔・固定領域だけから作り直す）。黙らずステータスで知らせる。
            System.Diagnostics.Debug.WriteLine($"文字の読み取りに失敗: {e.GetType().Name}");
            failed = true;
        }
        finally
        {
            IsBusy = false;
        }
        ApplyTypeChange(p, type, findings);
        StatusMessage = failed ? Strings.StatusRereadFailed : null;
    }

    /// <summary>
    /// いまの向き（<c>Rotate(Cropped, Rotation)</c> = <see cref="PageState.Base"/>）で文字を読み、材料の OCR を差し替えたものを返す
    /// （QR・顔はそのまま）。向きの自動判定はしない。**別スレッドで読む**（UI スレッドで OCR しない）。呼び出し側は IsBusy にしておくこと。
    /// </summary>
    private async Task<(PageFindings Findings, DocumentType Type)> AnalyzeAtCurrentRotationAsync(PageState state)
    {
        var recognizer = _recognizer ?? throw new InvalidOperationException("文字認識が使えない");
        var presets = _presetList;
        var rotation = state.Rotation;
        var analysis = await Task.Run(() => PageAnalyzer.AnalyzeAsync(state.Base, recognizer, presets, detectUpright: false, CancellationToken.None));
        return (state.Findings with { OcrTurns = rotation, Ocr = analysis.Ocr }, analysis.Classification.Type);
    }

    private async Task<PageFindings> ReadTextAtCurrentRotationAsync(PageState state) =>
        (await AnalyzeAtCurrentRotationAsync(state)).Findings;

    private async Task RereadTextAsync()
    {
        if (SelectedPage is not { } p || _recognizer is null || IsBusy) return;
        IsBusy = true;
        StatusMessage = Strings.StatusRereading;
        try
        {
            var state = p.State;
            var (findings, autoType) = await AnalyzeAtCurrentRotationAsync(state);
            var type = state.TypeIsManual ? state.Type : autoType;
            state.ApplyReread(findings, type, _presets.GetValueOrDefault(type), _english);
            p.Refresh();
            if (ReferenceEquals(SelectedPage, p))
            {
                Selection = null;
                OnPropertyChanged(nameof(SelectedDocumentType));
                RebuildSidePane();
            }
            AfterEdit();
            StatusMessage = Strings.StatusReread;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"文字の読み直しに失敗: {e.GetType().Name}");
            StatusMessage = Strings.StatusRereadFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (!SetProperty(ref _tool, value)) return;
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsRectTool));
            OnPropertyChanged(nameof(IsBrushTool));
            OnPropertyChanged(nameof(IsTextTool));
            if (value != EditorTool.Select) Selection = null;
            if (value is EditorTool.Rect or EditorTool.Brush)
            {
                // 塗る道具に替えたら文字の入力を確定して選択を外す（文字枠は選択・文字の道具で触る）。
                CommitTextEditing();
                SelectedTextId = null;
            }
            OnTextStateChanged();
        }
    }

    // ToggleButton/RadioButton 用（true の時だけ反映）。
    public bool IsSelectTool { get => Tool == EditorTool.Select; set { if (value) Tool = EditorTool.Select; } }
    public bool IsRectTool { get => Tool == EditorTool.Rect; set { if (value) Tool = EditorTool.Rect; } }
    public bool IsBrushTool { get => Tool == EditorTool.Brush; set { if (value) Tool = EditorTool.Brush; } }

    public BrushSize BrushSize
    {
        get => _brushSize;
        set
        {
            if (!SetProperty(ref _brushSize, value)) return;
            OnPropertyChanged(nameof(BrushWidth));
            OnPropertyChanged(nameof(IsBrushThin));
            OnPropertyChanged(nameof(IsBrushMedium));
            OnPropertyChanged(nameof(IsBrushThick));
        }
    }

    public bool IsBrushThin { get => BrushSize == BrushSize.Thin; set { if (value) BrushSize = BrushSize.Thin; } }
    public bool IsBrushMedium { get => BrushSize == BrushSize.Medium; set { if (value) BrushSize = BrushSize.Medium; } }
    public bool IsBrushThick { get => BrushSize == BrushSize.Thick; set { if (value) BrushSize = BrushSize.Thick; } }

    /// <summary>ブラシの太さ（画像短辺に対する比 = <see cref="BrushStroke.Width"/> にそのまま入る値）。</summary>
    public double BrushWidth => BrushWidthFor(BrushSize);

    public MaskSelection? Selection
    {
        get => _selection;
        private set
        {
            if (!SetProperty(ref _selection, value)) return;
            if (value is not null && _selectedTextId is not null)
            {
                _selectedTextId = null;
                OnPropertyChanged(nameof(SelectedTextId));
                OnTextStateChanged();
            }
            DeleteSelectionCommand.RaiseCanExecuteChanged();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }

    /// <summary>= !<see cref="IsBusy"/>（XAML の IsEnabled 用）。</summary>
    public bool IsIdle => !IsBusy;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ---- 注意書きバナー（§3.5: キャンバス上部・閉じられる。書き出しシートにも再掲＝2 か所ルール） ----

    public IReadOnlyList<string> BannerWarnings =>
        SelectedPage is { } p && _presets.TryGetValue(p.State.Type, out var preset) ? preset.LocalizedWarnings(_english) : [];

    public bool IsBannerVisible =>
        SelectedPage is { } p && BannerWarnings.Count > 0 && !_dismissedBanners.Contains(p.State.Type);

    private void DismissBanner()
    {
        if (SelectedPage is not { } p) return;
        _dismissedBanners.Add(p.State.Type);
        OnPropertyChanged(nameof(IsBannerVisible));
    }

    public bool HasNoCandidates => CandidateItems.Count == 0;

    // ---- 自動判定・文字認識の案内（§10.15） ----

    /// <summary>種別ピッカーの横の「自動」: 自動判定の種別のまま（利用者が選んでいない・generic 以外）。</summary>
    public bool IsAutoTypeVisible =>
        SelectedPage is { } p && !p.State.TypeIsManual && p.State.Type != DocumentType.Generic;

    /// <summary>日本語の文字認識が無い環境の案内（右ペイン上部・閉じられる・1 つだけ）。</summary>
    public bool IsOcrUnavailableNoticeVisible => _recognizer is null && !_ocrNoticeDismissed;

    /// <summary>
    /// 「切り抜くと、性別などの欄も位置で候補になります」: カードの種別なのにページがカード 1 枚になっていない（切り抜き前・縦横比が違う）、
    /// かつそのプリセットに固定領域か位置推定のルールがあるとき。
    /// </summary>
    public bool IsCropHintVisible =>
        SelectedPage is { } p && p.State.Type.IsId1Card() && !p.State.CardFramed
        && _presets.TryGetValue(p.State.Type, out var preset)
        && preset.Rules.Any(r => r.Region is not null || r.FallbackRegion is not null);

    // ---- 取り込み ----

    private async Task OpenFilesAsync()
    {
        var paths = _dialogs.AskOpenFiles();
        if (paths is { Count: > 0 }) await ImportAsync(paths);
    }

    /// <summary>
    /// ファイル・フォルダ（ドロップ・開く）を取り込む。
    /// - ファイルだけ: 今どおり末尾に足し、最初の新しいページを選ぶ。
    /// - フォルダを含む（W9-4・§10.17）: フォルダは直下の対応ファイルに展開してファイルと合わせ、**新しい作業として開く**
    ///   （いまページがあれば自動保存を確定してから空にする＝「新しい書類」と同じ・確認なし）。対応ファイルが 0 件なら取り込まずに知らせる。
    /// 元のファイルは読むだけ。
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsBusy) return;
        IsBusy = true;
        StatusMessage = Strings.StatusImporting;
        try
        {
            // フォルダの展開（列挙）だけ別スレッドで。ファイルだけの取り込みは今までどおりその場で読み込みへ進む
            // （毎回スレッドを跨ぐと、ファイルだけの取り込みまで開始が遅れる）。
            var intake = paths.Any(System.IO.Directory.Exists)
                ? await Task.Run(() => FolderIntake.Expand(paths))
                : FolderIntake.Expand(paths);
            var notes = new List<string>();
            if (intake.ContainsFolder)
            {
                if (intake.Files.Count == 0)
                {
                    StatusMessage = null;
                    _dialogs.ShowError(Strings.ImportErrorTitle, intake.Unsupported > 0
                        ? string.Format(Strings.FolderNoSupportedFilesSkipped, intake.Unsupported)
                        : Strings.FolderNoSupportedFiles);
                    return;
                }
                if (HasPages)
                {
                    await StartNewWorkAsync();
                    StatusMessage = Strings.StatusImporting;
                    notes.Add(Strings.StatusOpenedAsNewWork);
                }
                if (intake.Unsupported > 0) notes.Add(string.Format(Strings.StatusSkippedUnsupported, intake.Unsupported));
            }
            var loaded = await _source.LoadAsync(intake.Files, _dialogs.AskPasswordAsync, CancellationToken.None, Pages.Count);
            // 検出・文字の読み取りは取り込みの一部として済ませる（後から非同期に届いて編集履歴と競合しないように）。
            var (findings, types, failed) = await AnalyzePagesAsync(loaded);
            var docIds = SourceDocIdsFor(loaded);
            PageViewModel? first = null;
            for (var i = 0; i < loaded.Count; i++)
            {
                var state = PageState.From(loaded[i], docIds[i]);
                // 自動の向き・種別は取り込みの一部（Undo の対象外・回転ボタン／ピッカーで直せる）。
                state.InitializeAnalysis(findings[i], findings[i].Ocr is null ? 0 : findings[i].OcrTurns, types[i],
                                         _presets.GetValueOrDefault(types[i]), _english);
                var vm = new PageViewModel(state);
                Pages.Add(vm);
                first ??= vm;
            }
            Renumber();
            if (first is not null) SelectedPage = first;
            if (loaded.Count > 0) NoteEdited();
            var main = failed > 0 ? string.Format(Strings.StatusDetectFailed, loaded.Count, failed)
                : loaded.Count > 0 ? string.Format(Strings.StatusImported, loaded.Count) : null;
            if (main is not null) notes.Insert(0, main);
            StatusMessage = notes.Count > 0 ? string.Join(" ", notes) : null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null; // パスワード入力の取り消しなど。黙って戻る
        }
        catch (Exception e)
        {
            StatusMessage = null;
            _dialogs.ShowError(Strings.ImportErrorTitle, ImportErrors.Describe(e));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 取り込んだページの書類のまとまり（W9-4）: 元ファイルごとに 1 つ。PageSource は渡した順に、ファイルごとにページ番号 0 から返すので、
    /// ページ番号 0 か元ファイル名が変わったところで新しい書類にする。
    /// </summary>
    public static Guid[] SourceDocIdsFor(IReadOnlyList<SourcePage> pages)
    {
        var ids = new Guid[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            var continues = i > 0 && pages[i].PageIndex != 0
                            && string.Equals(pages[i].SourceName, pages[i - 1].SourceName, StringComparison.Ordinal);
            ids[i] = continues ? ids[i - 1] : Guid.NewGuid();
        }
        return ids;
    }

    /// <summary>同時に検出するページ数の上限（1 ページで数十 MB の作業領域を使うため）。</summary>
    public const int MaxDetectParallelism = 4;

    /// <summary>
    /// 取り込んだ全ページを解析する（§10.15。別スレッド・ページ並列・上限付き）: QR・顔（R0）と、文字認識があれば
    /// <see cref="PageAnalyzer.AnalyzeAsync"/>（写真だけ向きの自動判定・PDF はしない）。
    /// ページごとの失敗はそのページのその検出を 0 件（OCR は null・回転 0・generic）扱いにしてページ数を数える
    /// （検出はベストエフォート。失敗したページはステータスで知らせ、手で塗ってもらう）。
    /// </summary>
    private async Task<(PageFindings[] Findings, DocumentType[] Types, int Failed)> AnalyzePagesAsync(IReadOnlyList<SourcePage> pages)
    {
        var n = pages.Count;
        var findings = new PageFindings[n];
        var types = new DocumentType[n];
        Array.Fill(findings, PageFindings.Empty);
        var (qr, face, ocr, presets) = (_detector, _faceDetector, _recognizer, _presetList);
        if (n == 0 || (qr is null && face is null && ocr is null)) return (findings, types, 0);

        StatusMessage = ocr is not null ? string.Format(Strings.StatusReadingText, 0, n) : Strings.StatusDetecting;
        // 進捗は WPF の UI スレッドにだけ返す（テストの同期コンテキストでは別スレッドで動き、最後の表示と競合するため出さない）。
        var ui = SynchronizationContext.Current as System.Windows.Threading.DispatcherSynchronizationContext;
        var failed = 0;
        var done = 0;
        _analysisProgressActive = true;
        _analysisProgressShown = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, MaxDetectParallelism));
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var page = pages[i];
                var bad = false;
                IReadOnlyList<NormRect> qrBoxes = [];
                IReadOnlyList<NormRect> faceBoxes = [];
                PageAnalysis? analysis = null;
                if (qr is not null)
                {
                    try { qrBoxes = qr.Detect(page.Bitmap); }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("QR 検出", i, e); }
                }
                if (ocr is not null)
                {
                    try
                    {
                        analysis = await PageAnalyzer.AnalyzeAsync(page.Bitmap, ocr, presets, detectUpright: !page.FromPdf,
                                                                   CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("文字の読み取り", i, e); }
                }
                if (face is not null)
                {
                    // 顔は正立させた画像で探し、箱を R0 に戻す（横倒しの写真で見落とさないように）。OCR が無い・失敗なら R0 のまま。
                    try { faceBoxes = DetectFacesUpright(face, page.Bitmap, analysis?.QuarterTurns ?? 0); }
                    catch (Exception e) when (e is not OutOfMemoryException) { bad = true; Log("顔の検出", i, e); }
                }
                findings[i] = new PageFindings(analysis?.QuarterTurns ?? 0, analysis?.Ocr, qrBoxes.ToList(), faceBoxes.ToList());
                types[i] = analysis?.Classification.Type ?? DocumentType.Generic;
                if (bad) Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
            var k = Interlocked.Increment(ref done);
            // 終わった後に遅れて届いた分・順番が前後した分は捨てる。
            if (ocr is not null && ui is not null)
                ui.Post(_ =>
                {
                    if (!_analysisProgressActive || k <= _analysisProgressShown) return;
                    _analysisProgressShown = k;
                    StatusMessage = string.Format(Strings.StatusReadingText, k, n);
                }, null);
        })).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            _analysisProgressActive = false;
        }
        return (findings, types, failed);

        static void Log(string what, int i, Exception e) =>
            System.Diagnostics.Debug.WriteLine($"{what}に失敗（{i + 1} ページ目）: {e.GetType().Name}");
    }

    /// <summary>
    /// 顔を <paramref name="turns"/> だけ時計回りに回した画像（正立の向き）で探し、箱を −turns 回して R0（<paramref name="r0"/> の向き）に戻す。
    /// 同期 API なので**必ず Task.Run の中で呼ぶ**。
    /// </summary>
    internal static IReadOnlyList<NormRect> DetectFacesUpright(IDetector face, Fuseo.Core.Imaging.PageBitmap r0, int turns)
    {
        var t = QuarterTurn.Normalize(turns);
        if (t == 0) return face.Detect(r0);
        return face.Detect(QuarterTurn.Rotate(r0, t)).Select(b => QuarterTurn.Rotate(b, -t)).ToList();
    }

    // ---- 回転（画像とマスクを同じ回数だけ回す・Undo できる） ----

    private void Rotate(int turns)
    {
        if (SelectedPage is not { } p) return;
        EndTextEditing();
        // 文字枠は回せない（横書き）ので回転で消える。消える前に確かめる（元に戻すで戻せる）。手動マスク・候補は一緒に回る。
        if (p.State.Texts.Count > 0 && !_dialogs.Confirm(Strings.RotateTextsConfirmTitle, Strings.RotateTextsConfirmMessage)) return;
        if (turns > 0) p.State.RotateClockwise();
        else p.State.RotateCounterClockwise();
        Selection = null;
        AfterEdit();
    }

    // ---- ページの削除・並べ替え ----

    private void RemoveSelectedPage()
    {
        if (SelectedPage is not { } page) return;
        var index = Pages.IndexOf(page);
        Pages.RemoveAt(index);
        Renumber();
        SelectedPage = Pages.Count == 0 ? null : Pages[Math.Min(index, Pages.Count - 1)];
        NoteEdited();
    }

    private bool CanMove(int delta)
    {
        if (SelectedPage is not { } p || IsBusy) return false;
        var i = Pages.IndexOf(p) + delta;
        return i >= 0 && i < Pages.Count;
    }

    private void MoveSelectedPage(int delta)
    {
        if (!CanMove(delta) || SelectedPage is not { } p) return;
        var i = Pages.IndexOf(p);
        Pages.Move(i, i + delta);
        Renumber();
        RefreshCommands();
        NoteEdited();
    }

    private void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Number = i + 1;
        if (Pages.Count == 0) IsGridView = false;
        RebuildGrid();
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(IsLibraryVisible));
        RefreshCommands();
    }

    // ---- キャンバス操作（座標は正規化済み・左上原点で受け取る） ----

    public void AddRect(NormRect rect)
    {
        if (SelectedPage is not { } p || IsBusy || rect.W < MinRectSize || rect.H < MinRectSize) return;
        p.State.AddRect(rect);
        AfterEdit();
    }

    public void AddStroke(IReadOnlyList<NormPoint> points)
    {
        if (SelectedPage is not { } p || IsBusy || points.Count == 0) return;
        p.State.AddStroke(new BrushStroke(points.ToList(), BrushWidth));
        AfterEdit();
    }

    /// <summary>選択ツールのクリック。何も無い所なら選択解除。</summary>
    public MaskSelection? SelectAt(NormPoint point)
    {
        Selection = SelectedPage is { } p ? MaskHitTest.Hit(p.State, point) : null;
        if (Selection is null && SelectedTextId is not null) SelectText(null);
        return Selection;
    }

    /// <summary>選択中の手動矩形の移動を確定する（ドラッグ 1 回 = Undo 1 回）。</summary>
    public void MoveRect(int index, NormRect moved)
    {
        if (SelectedPage is not { } p || IsBusy) return;
        p.State.ReplaceRect(index, moved);
        Selection = MaskSelection.Rect(index);
        AfterEdit();
    }

    /// <summary>選択を消す。手動矩形は削除、候補は OFF にする（候補自体は一覧に残す＝あとで戻せる）。</summary>
    private void DeleteSelection()
    {
        if (SelectedTextId is not null && SelectedText is not null)
        {
            DeleteSelectedText();
            return;
        }
        if (SelectedPage is not { } p || IsBusy || Selection is not { } s) return;
        if (s.Kind == SelectionKind.ManualRect) p.State.RemoveRect(s.RectIndex);
        else p.State.SetCandidateOn(s.CandidateId, false);
        Selection = null;
        AfterEdit();
    }

    public void SetCandidateOn(Guid id, bool isOn)
    {
        if (SelectedPage is not { } p || IsBusy) return;
        p.State.SetCandidateOn(id, isOn);
        AfterEdit();
    }

    private void Undo()
    {
        if (SelectedPage is not { } p || IsBusy || !p.State.Undo()) return;
        AfterHistoryMove();
    }

    private void Redo()
    {
        if (SelectedPage is not { } p || IsBusy || !p.State.Redo()) return;
        AfterHistoryMove();
    }

    /// <summary>Undo/Redo の後。種別・回転も戻りうるので右ペインごと作り直す。</summary>
    private void AfterHistoryMove()
    {
        Selection = null;
        DropStaleTextSelection();
        OnPropertyChanged(nameof(SelectedDocumentType));
        RebuildSidePane();
        AfterEdit();
    }

    private void AfterEdit()
    {
        SelectedPage?.Refresh();
        DropStaleTextSelection();
        OnPropertyChanged(nameof(EditingTextId));
        OnTextStateChanged();
        RebuildGrid();
        RebuildCandidates();
        RefreshCommands();
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        NoteEdited();
    }

    // ---- 右ペイン ----

    private void RebuildSidePane()
    {
        Checklist.Clear();
        if (SelectedPage is { } p && _presets.TryGetValue(p.State.Type, out var preset))
            foreach (var row in State.Checklist.Build(preset, _english, ocrAvailable: _recognizer is not null, cardFramed: p.State.CardFramed))
                Checklist.Add(new ChecklistItemViewModel(row, p.State, NoteEdited));
        RebuildCandidates();
        OnPropertyChanged(nameof(BannerWarnings));
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsAutoTypeVisible));
        OnPropertyChanged(nameof(IsCropHintVisible));
    }

    private void RebuildCandidates()
    {
        CandidateItems.Clear();
        if (SelectedPage is { } p)
        {
            var preset = _presets.GetValueOrDefault(p.State.Type);
            foreach (var c in p.State.Candidates)
                CandidateItems.Add(new CandidateViewModel(c, SetCandidateOn, CandidateBuilder.DisplayText(preset, c, _english)));
        }
        OnPropertyChanged(nameof(HasNoCandidates));
    }

    private void RefreshCommands()
    {
        OpenSettingsCommand.RaiseCanExecuteChanged();
        OpenFilesCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        RemovePageCommand.RaiseCanExecuteChanged();
        MovePageUpCommand.RaiseCanExecuteChanged();
        MovePageDownCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        DeleteSelectionCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        RotateLeftCommand.RaiseCanExecuteChanged();
        RotateRightCommand.RaiseCanExecuteChanged();
        CropCommand.RaiseCanExecuteChanged();
        RereadTextCommand.RaiseCanExecuteChanged();
        DuplicateTextCommand.RaiseCanExecuteChanged();
        FitTextToFrameCommand.RaiseCanExecuteChanged();
        PickTextColorCommand.RaiseCanExecuteChanged();
        RefreshLibraryCommands();
    }

    // ---- 書き出し（確認シートを必ず通す） ----

    /// <summary>書き出しシート用の ViewModel を作る（ページ順は現在のレールの順）。</summary>
    public ExportViewModel CreateExportViewModel()
    {
        EndTextEditing();
        return new(Pages.Select(p => p.State).ToList(), _presets, _rasterizer, _exporter, _dialogs, _now, _english, TextMetrics);
    }

    private void OpenExport()
    {
        if (Pages.Count == 0) return;
        if (SelectedPage?.State.EditingTextId is not null) CommitTextEditing();
        var vm = CreateExportViewModel();
        _dialogs.ShowExport(vm);
        if (vm.SavedSummary is { } saved) StatusMessage = saved;
        if (vm.SavedFileName is { } name) NoteExported(name);
    }
}
