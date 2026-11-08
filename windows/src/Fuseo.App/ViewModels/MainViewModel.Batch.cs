using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Model;
using Fuseo.Input;

namespace Fuseo.App.ViewModels;

/// <summary>
/// フォルダ一括処理（W9-4・docs/windows-design.md §10.17）の画面側: 「フォルダを開く」と「1 枚ずつ／一覧」の切り替え。
/// 一覧は確認のための表示だけ（無確認で保存する経路は作らない。書き出しは今の書き出しシートを必ず通る）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>一覧のサムネイルの長辺（px）。</summary>
    public const int GridThumbnailMaxSide = 240;

    /// <summary>一覧のサムネイルを同時に作る数。</summary>
    private const int GridThumbnailParallelism = 2;

    private readonly SemaphoreSlim _gridThumbGate = new(GridThumbnailParallelism);
    private readonly List<Task> _gridThumbTasks = [];
    private bool _isGridView;

    public ObservableCollection<GridCellViewModel> GridCells { get; } = [];

    /// <summary>「一覧」を表示しているか（ページが無ければ常に false）。</summary>
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            var v = value && HasPages;
            if (!SetProperty(ref _isGridView, v)) return;
            OnPropertyChanged(nameof(IsSingleView));
            RebuildGrid();
        }
    }

    public bool IsSingleView { get => !IsGridView; set { if (value) IsGridView = false; } }

    /// <summary>
    /// 一覧のセルを選んだ（クリック）= そのページを選んで「1 枚ずつ」に戻る。読むと常に null。
    /// </summary>
    public GridCellViewModel? SelectedGridCell
    {
        get => null;
        set
        {
            if (value is not null) OpenGridCell(value);
            OnPropertyChanged();
        }
    }

    /// <summary>一覧のセルのページを選んで「1 枚ずつ」に戻る。</summary>
    public void OpenGridCell(GridCellViewModel cell)
    {
        if (!Pages.Contains(cell.Page)) return;
        SelectedPage = cell.Page;
        IsGridView = false;
    }

    private async Task OpenFolderAsync()
    {
        if (IsBusy) return;
        var folder = _dialogs.AskFolder(Strings.OpenFolderTitle);
        if (!string.IsNullOrEmpty(folder)) await ImportAsync([folder]);
    }

    /// <summary>
    /// 一覧のセルを作り直す（一覧の表示中だけ）。セルの文字は今のページ状態から作り、サムネイルは中身が変わったページだけ別スレッドで作り直す。
    /// </summary>
    private void RebuildGrid()
    {
        if (!IsGridView)
        {
            if (GridCells.Count > 0) GridCells.Clear();
            return;
        }
        var positions = DocumentPositions();
        GridCells.Clear();
        foreach (var p in Pages)
        {
            var (index, count) = positions[p];
            var preset = _presets.GetValueOrDefault(p.State.Type);
            var typeName = preset?.Name(_english) ?? p.State.Type.ToString();
            var warnings = preset?.LocalizedWarnings(_english) ?? [];
            GridCells.Add(new GridCellViewModel(p, string.Format(Strings.GridCellTitle, p.State.SourceName, index, count),
                                                typeName, warnings));
            EnsureGridThumbnail(p);
        }
    }

    /// <summary>ページごとの「書類の中で何枚目か／書類のページ数」（書類 = SourceDocId・順は画面の順）。</summary>
    private Dictionary<PageViewModel, (int Index, int Count)> DocumentPositions()
    {
        var counts = Pages.GroupBy(p => p.State.SourceDocId).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<Guid, int>();
        var result = new Dictionary<PageViewModel, (int, int)>();
        foreach (var p in Pages)
        {
            var id = p.State.SourceDocId;
            var k = seen.GetValueOrDefault(id) + 1;
            seen[id] = k;
            result[p] = (k, counts[id]);
        }
        return result;
    }

    /// <summary>
    /// 一覧のサムネイル（いまの向き・切り抜き後の画像を縮小し、確定マスク＝ON の候補＋手動を黒で重ねたもの）を、
    /// 中身が変わっていれば別スレッドで作り直す。**表示専用**（書き出しは書き出しシートの BurnIn → IExporter だけ）。
    /// </summary>
    private void EnsureGridThumbnail(PageViewModel page)
    {
        var snapshot = page.State.Snapshot;
        if (ReferenceEquals(page.GridThumbnailKey, snapshot)) return;
        page.GridThumbnailKey = snapshot;
        var state = page.State;
        var masks = PageState.ConfirmedMasksOf(snapshot);
        var (rasterizer, gate) = (_rasterizer, _gridThumbGate);
        var task = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var small = PixelOps.DownscaleToFit(state.BaseFor(snapshot.Crop, snapshot.Rotation), GridThumbnailMaxSide);
                // 縮小した画像に黒を重ねる（マスクは正規化座標なので縮小後に塗ってよい）。
                return BitmapConvert.ToBitmapSource(rasterizer.BurnIn(small, masks).Bitmap);
            }
            finally
            {
                gate.Release();
            }
        });
        _gridThumbTasks.Add(Apply());
        _gridThumbTasks.RemoveAll(t => t.IsCompleted);

        async Task Apply()
        {
            try
            {
                var bmp = await task;
                // 作っている間にまた編集されたら捨てる（新しい方が後から届く）。
                if (ReferenceEquals(page.GridThumbnailKey, snapshot)) page.GridThumbnail = bmp;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"一覧のサムネイルを作れなかった: {e.GetType().Name}");
                if (ReferenceEquals(page.GridThumbnailKey, snapshot)) page.GridThumbnailKey = null;
            }
        }
    }

    /// <summary>作りかけの一覧のサムネイルを待つ（テスト用）。</summary>
    public Task WhenGridThumbnailsReadyAsync() => Task.WhenAll(_gridThumbTasks.ToList());
}

/// <summary>一覧（W9-4）の 1 セル。</summary>
public sealed class GridCellViewModel(PageViewModel page, string title, string typeName, IReadOnlyList<string> warnings)
{
    public PageViewModel Page { get; } = page;

    /// <summary>「ファイル名（p/n）」（p = 書類の中で何枚目か・n = 書類のページ数）。</summary>
    public string Title { get; } = title;

    public string TypeName { get; } = typeName;
    public int MaskCount { get; } = page.State.MaskCount;
    public bool HasNoMask => MaskCount == 0;
    public string MaskCountText => MaskCount == 0 ? Strings.PageNoMask : string.Format(Strings.PageMaskCount, MaskCount);
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningsText => string.Join("\n", Warnings);
}
