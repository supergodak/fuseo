using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Geometry;

namespace Fuseo.App;

/// <summary>
/// メイン画面。ここはマウス・ズーム・パン・ドロップだけを扱う（編集の中身は <see cref="MainViewModel"/>）。
/// 座標の約束:
/// - 入力点は必ず <c>e.GetPosition(PageImage)</c> → <see cref="CoordinateSpace.FromView"/> で正規化（手で割り算しない）。
/// - PageImage は Stretch=Fill・画像の画素数どおりの大きさ（縦横比そのまま）。ズーム/パンは PageHost の
///   RenderTransform だけで掛け、GetPosition が変換を自動で戻すので座標計算には混ぜない。
/// </summary>
public partial class MainWindow : Window
{
    private enum DragMode { None, Rect, Brush, Move, Pan }

    private const double MaxZoom = 16;

    private MainViewModel? _vm;
    private PageViewModel? _shownPage;
    private Fuseo.Core.Imaging.PageBitmap? _shownBase;
    private bool _userZoomed;

    private DragMode _drag;
    private NormPoint _dragStart;
    private readonly List<NormPoint> _brushPoints = [];
    private int _moveIndex;
    private NormRect _moveOriginal;
    private Point _panStart;
    private Matrix _panMatrixStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CanvasChanged -= OnCanvasChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.CanvasChanged += OnCanvasChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        OnCanvasChanged(this, EventArgs.Empty);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Tool)) CancelDrag();
    }

    // ---- 表示の更新 ----

    private void OnCanvasChanged(object? sender, EventArgs e)
    {
        var page = _vm?.SelectedPage;
        // ページが変わったとき、または回転（Undo/Redo を含む）で基準画像が変わったときは画像を差し替えて全体表示に戻す。
        if (!ReferenceEquals(page, _shownPage) || !ReferenceEquals(page?.State.Base, _shownBase))
        {
            CancelDrag();
            _shownPage = page;
            _shownBase = page?.State.Base;
            if (page is null)
            {
                PageImage.Source = null;
            }
            else
            {
                // 表示専用の BitmapSource（書き出しには使わない）。大きさは画素数どおり＝縦横比を保つ。
                PageImage.Source = BitmapConvert.ToBitmapSource(page.State.Base);
                PageImage.Width = page.State.Base.Width;
                PageImage.Height = page.State.Base.Height;
                Overlay.Width = page.State.Base.Width;
                Overlay.Height = page.State.Base.Height;
            }
            _userZoomed = false;
            FitToViewport();
        }
        Overlay.Page = page?.State;
        Overlay.Selection = _vm?.Selection;
        Overlay.InvalidateVisual();
    }

    // ---- ズーム・パン（PageHost の RenderTransform だけ） ----

    private void FitToViewport()
    {
        if (_shownPage is null) { ViewTransform.Matrix = Matrix.Identity; return; }
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw <= 0 || vh <= 0) return; // レイアウト前。SizeChanged でやり直す
        double iw = _shownPage.State.Base.Width, ih = _shownPage.State.Base.Height;
        var s = Math.Min(vw / iw, vh / ih) * 0.96;
        ViewTransform.Matrix = new Matrix(s, 0, 0, s, (vw - iw * s) / 2, (vh - ih * s) / 2);
        SyncZoom();
    }

    private double FitScale()
    {
        if (_shownPage is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return 1;
        return Math.Min(Viewport.ActualWidth / _shownPage.State.Base.Width,
                        Viewport.ActualHeight / _shownPage.State.Base.Height) * 0.96;
    }

    private void ZoomAt(double factor, Point center)
    {
        if (_shownPage is null) return;
        var m = ViewTransform.Matrix;
        var current = m.M11;
        var target = Math.Clamp(current * factor, FitScale() * 0.25, MaxZoom);
        if (current <= 0) return;
        var f = target / current;
        m.ScaleAt(f, f, center.X, center.Y);
        ViewTransform.Matrix = m;
        _userZoomed = true;
        SyncZoom();
    }

    private void SyncZoom()
    {
        Overlay.Zoom = ViewTransform.Matrix.M11;
        Overlay.InvalidateVisual();
    }

    private Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAt(1.25, ViewportCenter);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAt(0.8, ViewportCenter);
    private void OnZoomFit(object sender, RoutedEventArgs e) { _userZoomed = false; FitToViewport(); }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_userZoomed) FitToViewport();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_shownPage is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomAt(Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(Viewport));
        }
        else
        {
            var m = ViewTransform.Matrix;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) m.Translate(e.Delta / 2.0, 0);
            else m.Translate(0, e.Delta / 2.0);
            ViewTransform.Matrix = m;
            _userZoomed = true;
        }
        e.Handled = true;
    }

    // ---- マウス（編集） ----

    /// <summary>マウス位置 → 基準ビットマップの正規化座標（左上原点）。CoordinateSpace 経由のみ。</summary>
    private NormPoint NormAt(MouseEventArgs e)
    {
        var pos = e.GetPosition(PageImage);
        var bmp = _shownPage!.State.Base;
        return CoordinateSpace.FromView(pos.X, pos.Y, bmp.Width, bmp.Height, PageImage.ActualWidth, PageImage.ActualHeight);
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        if (_vm is null || _shownPage is null || _drag != DragMode.None) return;
        if (Keyboard.IsKeyDown(Key.Space)) { BeginPan(e); return; }

        var p = NormAt(e);
        switch (_vm.Tool)
        {
            case EditorTool.Rect:
                _drag = DragMode.Rect;
                _dragStart = p;
                Overlay.DraftRect = NormRect.FromPoints(p, p);
                break;
            case EditorTool.Brush:
                _drag = DragMode.Brush;
                _brushPoints.Clear();
                _brushPoints.Add(p);
                Overlay.DraftStroke = _brushPoints;
                Overlay.DraftStrokeWidth = _vm.BrushWidth;
                break;
            case EditorTool.Select:
                if (_vm.SelectAt(p) is { Kind: SelectionKind.ManualRect } sel)
                {
                    _drag = DragMode.Move;
                    _dragStart = p;
                    _moveIndex = sel.RectIndex;
                    _moveOriginal = _shownPage.State.Manual.Rects[sel.RectIndex];
                }
                break;
        }
        if (_drag != DragMode.None) Viewport.CaptureMouse();
        Overlay.InvalidateVisual();
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None || _shownPage is null) return;
        if (_drag == DragMode.Pan)
        {
            var now = e.GetPosition(Viewport);
            var m = _panMatrixStart;
            m.Translate(now.X - _panStart.X, now.Y - _panStart.Y);
            ViewTransform.Matrix = m;
            return;
        }
        var p = NormAt(e);
        switch (_drag)
        {
            case DragMode.Rect:
                Overlay.DraftRect = NormRect.FromPoints(_dragStart, p);
                break;
            case DragMode.Brush:
                if (_brushPoints[^1] != p) _brushPoints.Add(p);
                break;
            case DragMode.Move:
                Overlay.MovingRect = (_moveIndex, MaskHitTest.Offset(_moveOriginal, p.X - _dragStart.X, p.Y - _dragStart.Y));
                break;
        }
        Overlay.InvalidateVisual();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || _shownPage is null) { CancelDrag(); return; }
        var mode = _drag;
        if (mode is DragMode.None or DragMode.Pan) return;
        var p = NormAt(e);
        var startPoint = _dragStart;
        var points = _brushPoints.ToList();
        var moved = Overlay.MovingRect;
        CancelDrag();
        switch (mode)
        {
            case DragMode.Rect:
                _vm.AddRect(NormRect.FromPoints(startPoint, p));
                break;
            case DragMode.Brush:
                if (points[^1] != p) points.Add(p);
                _vm.AddStroke(points);
                break;
            case DragMode.Move when moved is { } mv && mv.Rect != _moveOriginal:
                _vm.MoveRect(mv.Index, mv.Rect);
                break;
        }
        e.Handled = true;
    }

    // 中ボタン・右ボタンのドラッグ = パン
    private void OnViewportOtherMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right && _drag == DragMode.None && _shownPage is not null)
            BeginPan(e);
    }

    private void OnViewportOtherMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.Pan && e.ChangedButton is MouseButton.Middle or MouseButton.Right or MouseButton.Left)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private void BeginPan(MouseButtonEventArgs e)
    {
        _drag = DragMode.Pan;
        _panStart = e.GetPosition(Viewport);
        _panMatrixStart = ViewTransform.Matrix;
        _userZoomed = true;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportLostCapture(object sender, MouseEventArgs e)
    {
        if (_drag != DragMode.None) CancelDrag();
    }

    private void CancelDrag()
    {
        var wasDragging = _drag != DragMode.None;
        _drag = DragMode.None;
        Overlay.DraftRect = null;
        Overlay.DraftStroke = null;
        Overlay.MovingRect = null;
        _brushPoints.Clear();
        if (wasDragging && Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        Overlay.InvalidateVisual();
    }

    // ---- ドラッグ＆ドロップ（取り込み） ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _vm is { IsBusy: false }
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ---- 作業中の書類（W5） ----

    private void OnLibraryItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItemViewModel item } && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>閉じる前に保留中の自動保存を確定する（書きかけで終わらない）。</summary>
    private bool _closeAfterFlush;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAfterFlush || _vm is not { HasPendingSave: true } vm) return;
        e.Cancel = true;
        IsEnabled = false;
        try { await vm.FlushSaveAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"終了時の保存に失敗: {ex.GetType().Name}"); }
        _closeAfterFlush = true;
        Close();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Handled = true;
        await _vm.ImportAsync(paths);
    }
}
