using System.Collections.ObjectModel;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.App.ViewModels;

public enum EditorTool { Select, Rect, Brush }

public enum BrushSize { Thin, Medium, Thick }

/// <summary>
/// メイン画面の ViewModel。取り込み（IPageSource）・焼き込み（IMaskRasterizer）・書き出し（IExporter）・
/// 自動検出（IDetector・W4 は QR／バーコードのみ）はコンストラクタ注入。キャンバスのマウス操作はビューが座標を正規化（CoordinateSpace.FromView）してから
/// ここのメソッドを呼ぶ＝この型は画面座標を一切知らない。
/// </summary>
public sealed class MainViewModel : ObservableObject
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
    private readonly Dictionary<DocumentType, DocumentPreset> _presets;
    private readonly HashSet<DocumentType> _dismissedBanners = [];
    private readonly Func<DateTime> _now;

    private PageViewModel? _selectedPage;
    private EditorTool _tool = EditorTool.Rect;
    private BrushSize _brushSize = BrushSize.Medium;
    private MaskSelection? _selection;
    private bool _isBusy;
    private string? _statusMessage;

    public MainViewModel(IPageSource source, IMaskRasterizer rasterizer, IExporter exporter,
                         IReadOnlyList<DocumentPreset> presets, IDialogService dialogs, Func<DateTime>? now = null,
                         IDetector? detector = null)
    {
        _detector = detector;
        _source = source;
        _rasterizer = rasterizer;
        _exporter = exporter;
        _dialogs = dialogs;
        _now = now ?? (() => DateTime.Now);
        _presets = presets.ToDictionary(p => p.DocumentType);
        // 並びは DocumentType の定義順（generic が先頭）。
        DocumentTypes = presets.OrderBy(p => (int)p.DocumentType)
            .Select(p => new PresetOption(p.DocumentType, p.DisplayName, p)).ToList();

        OpenFilesCommand = new RelayCommand(() => _ = OpenFilesAsync(), () => !IsBusy);
        RemovePageCommand = new RelayCommand(RemoveSelectedPage, () => SelectedPage is not null && !IsBusy);
        MovePageUpCommand = new RelayCommand(() => MoveSelectedPage(-1), () => CanMove(-1));
        MovePageDownCommand = new RelayCommand(() => MoveSelectedPage(+1), () => CanMove(+1));
        UndoCommand = new RelayCommand(Undo, () => SelectedPage?.State.CanUndo == true);
        RedoCommand = new RelayCommand(Redo, () => SelectedPage?.State.CanRedo == true);
        DeleteSelectionCommand = new RelayCommand(DeleteSelection, () => Selection is not null);
        ExportCommand = new RelayCommand(OpenExport, () => Pages.Count > 0 && !IsBusy);
        DismissBannerCommand = new RelayCommand(DismissBanner);
        RotateLeftCommand = new RelayCommand(() => Rotate(-1), () => SelectedPage is not null && !IsBusy);
        RotateRightCommand = new RelayCommand(() => Rotate(+1), () => SelectedPage is not null && !IsBusy);
        SetToolCommand = new RelayCommand(p => { if (p is EditorTool t) Tool = t; });
        SetBrushSizeCommand = new RelayCommand(p => { if (p is BrushSize s) BrushSize = s; });
    }

    public ObservableCollection<PageViewModel> Pages { get; } = [];
    public IReadOnlyList<PresetOption> DocumentTypes { get; }
    public ObservableCollection<ChecklistItemViewModel> Checklist { get; } = [];
    public ObservableCollection<CandidateViewModel> CandidateItems { get; } = [];

    public RelayCommand OpenFilesCommand { get; }
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

    /// <summary>キャンバスの再描画が要るとき（マスク・選択・ページが変わった）。</summary>
    public event EventHandler? CanvasChanged;

    public bool HasPages => Pages.Count > 0;

    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value)) return;
            _selection = null;
            OnPropertyChanged(nameof(Selection));
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
            if (SelectedPage is not { } p || value is null || p.State.Type == value.Type) return;
            // 種別に合わせて候補（defaultOn）を作り直す。種別ごと 1 回の変更として Undo できる。
            p.State.ChangeType(value.Type, _presets.GetValueOrDefault(value.Type));
            Selection = null;
            OnPropertyChanged();
            RebuildSidePane();
            AfterEdit();
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
            if (value != EditorTool.Select) Selection = null;
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
            DeleteSelectionCommand.RaiseCanExecuteChanged();
            CanvasChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ---- 注意書きバナー（§3.5: キャンバス上部・閉じられる。書き出しシートにも再掲＝2 か所ルール） ----

    public IReadOnlyList<string> BannerWarnings =>
        SelectedPage is { } p && _presets.TryGetValue(p.State.Type, out var preset) ? preset.Warnings : [];

    public bool IsBannerVisible =>
        SelectedPage is { } p && BannerWarnings.Count > 0 && !_dismissedBanners.Contains(p.State.Type);

    private void DismissBanner()
    {
        if (SelectedPage is not { } p) return;
        _dismissedBanners.Add(p.State.Type);
        OnPropertyChanged(nameof(IsBannerVisible));
    }

    public bool HasNoCandidates => CandidateItems.Count == 0;

    // ---- 取り込み ----

    private async Task OpenFilesAsync()
    {
        var paths = _dialogs.AskOpenFiles();
        if (paths is { Count: > 0 }) await ImportAsync(paths);
    }

    /// <summary>ファイル（ドロップ・開く）を取り込んで末尾に足し、最初の新しいページを選ぶ。</summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsBusy) return;
        IsBusy = true;
        StatusMessage = Strings.StatusImporting;
        try
        {
            var loaded = await _source.LoadAsync(paths, _dialogs.AskPasswordAsync, CancellationToken.None);
            // 検出は取り込みの一部として済ませる（後から非同期に届いて編集履歴と競合しないように）。
            var (detections, failed) = await DetectAsync(loaded);
            PageViewModel? first = null;
            for (var i = 0; i < loaded.Count; i++)
            {
                var state = PageState.From(loaded[i]);
                if (_detector is not null && detections[i] is { } boxes)
                    state.InitializeDetections(_detector.Id, boxes, _presets.GetValueOrDefault(state.Type));
                var vm = new PageViewModel(state);
                Pages.Add(vm);
                first ??= vm;
            }
            Renumber();
            if (first is not null) SelectedPage = first;
            StatusMessage = failed > 0 ? string.Format(Strings.StatusDetectFailed, loaded.Count, failed)
                : loaded.Count > 0 ? string.Format(Strings.StatusImported, loaded.Count) : null;
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

    /// <summary>同時に検出するページ数の上限（1 ページで数十 MB の作業領域を使うため）。</summary>
    public const int MaxDetectParallelism = 4;

    /// <summary>
    /// 取り込んだ全ページで検出する（別スレッド・ページ並列・上限付き）。ページごとの失敗はそのページを 0 件扱いにして数える
    /// （検出はベストエフォート。失敗したページはステータスで知らせ、手で塗ってもらう）。
    /// </summary>
    private async Task<(IReadOnlyList<NormRect>?[] Boxes, int Failed)> DetectAsync(IReadOnlyList<SourcePage> pages)
    {
        var boxes = new IReadOnlyList<NormRect>?[pages.Count];
        if (_detector is not { } detector || pages.Count == 0) return (boxes, 0);
        StatusMessage = Strings.StatusDetecting;
        var failed = 0;
        await Task.Run(() => Parallel.For(0, pages.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, MaxDetectParallelism) },
            i =>
            {
                try { boxes[i] = detector.Detect(pages[i].Bitmap); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine($"QR 検出に失敗（{i + 1} ページ目）: {e.GetType().Name}");
                    Interlocked.Increment(ref failed);
                }
            }));
        return (boxes, failed);
    }

    // ---- 回転（画像とマスクを同じ回数だけ回す・Undo できる） ----

    private void Rotate(int turns)
    {
        if (SelectedPage is not { } p) return;
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
    }

    private void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Number = i + 1;
        OnPropertyChanged(nameof(HasPages));
        RefreshCommands();
    }

    // ---- キャンバス操作（座標は正規化済み・左上原点で受け取る） ----

    public void AddRect(NormRect rect)
    {
        if (SelectedPage is not { } p || rect.W < MinRectSize || rect.H < MinRectSize) return;
        p.State.AddRect(rect);
        AfterEdit();
    }

    public void AddStroke(IReadOnlyList<NormPoint> points)
    {
        if (SelectedPage is not { } p || points.Count == 0) return;
        p.State.AddStroke(new BrushStroke(points.ToList(), BrushWidth));
        AfterEdit();
    }

    /// <summary>選択ツールのクリック。何も無い所なら選択解除。</summary>
    public MaskSelection? SelectAt(NormPoint point)
    {
        Selection = SelectedPage is { } p ? MaskHitTest.Hit(p.State, point) : null;
        return Selection;
    }

    /// <summary>選択中の手動矩形の移動を確定する（ドラッグ 1 回 = Undo 1 回）。</summary>
    public void MoveRect(int index, NormRect moved)
    {
        if (SelectedPage is not { } p) return;
        p.State.ReplaceRect(index, moved);
        Selection = MaskSelection.Rect(index);
        AfterEdit();
    }

    /// <summary>選択を消す。手動矩形は削除、候補は OFF にする（候補自体は一覧に残す＝あとで戻せる）。</summary>
    private void DeleteSelection()
    {
        if (SelectedPage is not { } p || Selection is not { } s) return;
        if (s.Kind == SelectionKind.ManualRect) p.State.RemoveRect(s.RectIndex);
        else p.State.SetCandidateOn(s.CandidateId, false);
        Selection = null;
        AfterEdit();
    }

    public void SetCandidateOn(Guid id, bool isOn)
    {
        if (SelectedPage is not { } p) return;
        p.State.SetCandidateOn(id, isOn);
        AfterEdit();
    }

    private void Undo()
    {
        if (SelectedPage is not { } p || !p.State.Undo()) return;
        AfterHistoryMove();
    }

    private void Redo()
    {
        if (SelectedPage is not { } p || !p.State.Redo()) return;
        AfterHistoryMove();
    }

    /// <summary>Undo/Redo の後。種別・回転も戻りうるので右ペインごと作り直す。</summary>
    private void AfterHistoryMove()
    {
        Selection = null;
        OnPropertyChanged(nameof(SelectedDocumentType));
        RebuildSidePane();
        AfterEdit();
    }

    private void AfterEdit()
    {
        SelectedPage?.Refresh();
        RebuildCandidates();
        RefreshCommands();
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 右ペイン ----

    private void RebuildSidePane()
    {
        Checklist.Clear();
        if (SelectedPage is { } p && _presets.TryGetValue(p.State.Type, out var preset))
            foreach (var row in State.Checklist.Build(preset)) Checklist.Add(new ChecklistItemViewModel(row, p.State));
        RebuildCandidates();
        OnPropertyChanged(nameof(BannerWarnings));
        OnPropertyChanged(nameof(IsBannerVisible));
    }

    private void RebuildCandidates()
    {
        CandidateItems.Clear();
        if (SelectedPage is { } p)
            foreach (var c in p.State.Candidates) CandidateItems.Add(new CandidateViewModel(c, SetCandidateOn));
        OnPropertyChanged(nameof(HasNoCandidates));
    }

    private void RefreshCommands()
    {
        OpenFilesCommand.RaiseCanExecuteChanged();
        RemovePageCommand.RaiseCanExecuteChanged();
        MovePageUpCommand.RaiseCanExecuteChanged();
        MovePageDownCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        DeleteSelectionCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        RotateLeftCommand.RaiseCanExecuteChanged();
        RotateRightCommand.RaiseCanExecuteChanged();
    }

    // ---- 書き出し（確認シートを必ず通す） ----

    /// <summary>書き出しシート用の ViewModel を作る（ページ順は現在のレールの順）。</summary>
    public ExportViewModel CreateExportViewModel() =>
        new(Pages.Select(p => p.State).ToList(), _presets, _rasterizer, _exporter, _dialogs, _now);

    private void OpenExport()
    {
        if (Pages.Count == 0) return;
        var vm = CreateExportViewModel();
        _dialogs.ShowExport(vm);
        if (vm.SavedSummary is { } saved) StatusMessage = saved;
    }
}
