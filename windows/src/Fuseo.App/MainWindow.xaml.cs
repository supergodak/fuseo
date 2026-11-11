using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Geometry;
using Fuseo.Core.Rendering;

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
    private enum DragMode { None, Rect, Brush, Move, Pan, TextCreate, TextMove, TextResize }

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

    // 文字入れ（v0.3）のドラッグ: 対象の枠・開始時の枠（正規化と px）・動かしているつまみ。
    private Guid _textId;
    private NormRect _textOriginal;
    private PxRect _textStartPx;
    private TextHandle _textHandle;
    private Point _dragStartScreen;
    /// <summary>入力欄の中身を画面側から入れている間（TextChanged・LostFocus を ViewModel に返さない）。</summary>
    private bool _editorSync;
    private Guid? _editorShownFor;

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
        if (e.PropertyName == nameof(MainViewModel.Tool))
        {
            CancelDrag();
            OnCanvasChanged(this, EventArgs.Empty);   // 文字枠の線の出し方が道具で変わる
        }
        if (e.PropertyName is nameof(MainViewModel.EditingTextId) or nameof(MainViewModel.SelectedPage)) SyncTextEditor();
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
        Overlay.TextMetrics = _vm?.TextMetrics;
        Overlay.SelectedTextId = _vm?.SelectedTextId;
        Overlay.EditingTextId = _vm?.EditingTextId;
        Overlay.ShowTextFrames = _vm?.Tool is EditorTool.Select or EditorTool.Text;
        Overlay.InvalidateVisual();
        SyncTextEditor();
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
        PlaceTextEditor();
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
            PlaceTextEditor();
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
        if (IsInEditor(e.OriginalSource)) return;   // 入力欄の中のクリックは入力欄のもの
        Viewport.Focus();   // 入力中なら入力欄のフォーカスが外れて確定する（OnTextEditorLostFocus）
        if (_vm is null || _shownPage is null || _drag != DragMode.None) return;
        if (Keyboard.IsKeyDown(Key.Space)) { BeginPan(e); return; }

        var p = NormAt(e);
        if (_vm.Tool is EditorTool.Select or EditorTool.Text && BeginTextPointer(e, p))
        {
            if (_drag != DragMode.None) Viewport.CaptureMouse();
            Overlay.InvalidateVisual();
            e.Handled = true;
            return;
        }
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
            PlaceTextEditor();
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
            case DragMode.TextCreate:
                Overlay.DraftTextRect = NormRect.FromPoints(_dragStart, p);
                break;
            case DragMode.TextMove:
            case DragMode.TextResize:
                DragText(e);
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
        var screenNow = e.GetPosition(Viewport);
        var (textId, textOriginal) = (_textId, _textOriginal);
        var screenStart = _dragStartScreen;
        CancelDrag();
        switch (mode)
        {
            case DragMode.TextCreate:
                FinishTextCreate(startPoint, p, screenStart, screenNow);
                break;
            case DragMode.TextMove:
            case DragMode.TextResize:
                _vm.CommitTextBox(textId, textOriginal);
                break;
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
        Overlay.DraftTextRect = null;
        Overlay.SnapGuides = null;
        _brushPoints.Clear();
        if (wasDragging && Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        Overlay.InvalidateVisual();
    }

    // ---- 文字入れ（v0.3）: 枠の作成・選択・移動・つまみ・入力欄 ----

    /// <summary>
    /// 選択・文字の道具のマウスダウン（文字枠の操作）。扱ったら true。順に: 選択中の枠のつまみ → 枠の中（ダブルクリックで入力・
    /// それ以外は選んで移動）→ 文字の道具なら新しい枠のドラッグ。選択の道具で枠の外ならマスクの選択（false を返して既存の処理へ）。
    /// </summary>
    private bool BeginTextPointer(MouseButtonEventArgs e, NormPoint p)
    {
        if (_vm is null || _shownPage is null) return false;
        var bmp = _shownPage.State.Base;
        var px = new Point(p.X * bmp.Width, p.Y * bmp.Height);
        var tolerance = Views.MaskOverlay.HandleScreenSize / Math.Max(ViewTransform.Matrix.M11, 0.01);
        if (_vm.SelectedText is { } sel && _vm.EditingTextId != sel.Id)
        {
            var frame = TextLayout.FramePx(sel.Box, bmp.Width, bmp.Height);
            if (TextGeometry.HandleAt(frame, px.X, px.Y, tolerance) is { } h)
            {
                StartTextDrag(DragMode.TextResize, sel.Id, sel.Box, p, e);
                _textHandle = h;
                return true;
            }
        }
        if (_vm.HitText(p) is { } t)
        {
            if (e.ClickCount >= 2)
            {
                _vm.BeginTextEditing(t.Id);
                return true;
            }
            _vm.SelectText(t.Id);
            StartTextDrag(DragMode.TextMove, t.Id, t.Box, p, e);
            return true;
        }
        if (_vm.Tool != EditorTool.Text) return false;
        _vm.SelectText(null);
        _drag = DragMode.TextCreate;
        _dragStart = p;
        _dragStartScreen = e.GetPosition(Viewport);
        Overlay.DraftTextRect = NormRect.FromPoints(p, p);
        return true;
    }

    private void StartTextDrag(DragMode mode, Guid id, NormRect box, NormPoint p, MouseButtonEventArgs e)
    {
        var bmp = _shownPage!.State.Base;
        _drag = mode;
        _dragStart = p;
        _dragStartScreen = e.GetPosition(Viewport);
        _textId = id;
        _textOriginal = box;
        _textStartPx = TextLayout.FramePx(box, bmp.Width, bmp.Height);
    }

    /// <summary>移動・つまみのドラッグ中（px で計算して吸い付き → 正規化に戻してライブ更新）。Ctrl を押している間は吸い付かない。</summary>
    private void DragText(MouseEventArgs e)
    {
        if (_vm is null || _shownPage is null) return;
        var bmp = _shownPage.State.Base;
        var now = NormAt(e);
        var dx = (now.X - _dragStart.X) * bmp.Width;
        var dy = (now.Y - _dragStart.Y) * bmp.Height;
        var snap = (Keyboard.Modifiers & ModifierKeys.Control) == 0;
        var threshold = TextSnap.ScreenThreshold / Math.Max(ViewTransform.Matrix.M11, 0.01);
        var others = _shownPage.State.Texts.Where(t => t.Id != _textId)
            .Select(t => TextLayout.FramePx(t.Box, bmp.Width, bmp.Height)).ToList();
        PxRect rect;
        IReadOnlyList<TextSnap.Guide>? guides = null;
        if (_drag == DragMode.TextMove)
        {
            rect = TextGeometry.Move(_textStartPx, dx, dy, bmp.Width, bmp.Height);
            if (snap && others.Count > 0)
            {
                var r = TextSnap.SnapMove(rect, others, threshold);
                rect = TextGeometry.Move(r.Rect, 0, 0, bmp.Width, bmp.Height);
                guides = r.Guides;
            }
        }
        else
        {
            rect = TextGeometry.Resize(_textStartPx, _textHandle, dx, dy, bmp.Width, bmp.Height);
            if (snap && others.Count > 0)
            {
                var r = TextSnap.SnapResize(rect, TextGeometry.MovesLeft(_textHandle), TextGeometry.MovesRight(_textHandle), others, threshold);
                rect = r.Rect;
                guides = r.Guides;
            }
        }
        Overlay.SnapGuides = guides;
        _vm.SetTextBoxLive(_textId, TextGeometry.FromPx(rect, bmp.Width, bmp.Height));
    }

    /// <summary>
    /// 新しい枠のドラッグを終えた。ほとんど動かしていなければ（画面で 4px 未満）クリックとみなして既定の大きさの枠、
    /// そうでなければ描いた枠（最小の大きさまで広げる）。どちらもそのまま入力を始める。
    /// </summary>
    private void FinishTextCreate(NormPoint start, NormPoint end, Point screenStart, Point screenEnd)
    {
        if (_vm is null || _shownPage is null) return;
        if (Math.Abs(screenEnd.X - screenStart.X) < 4 && Math.Abs(screenEnd.Y - screenStart.Y) < 4)
        {
            _vm.BeginNewTextAt(start);
            return;
        }
        var bmp = _shownPage.State.Base;
        var px = TextLayout.FramePx(NormRect.FromPoints(start, end), bmp.Width, bmp.Height);
        var w = Math.Min(Math.Max(px.W, TextGeometry.MinFramePx), bmp.Width);
        var h = Math.Min(Math.Max(px.H, TextGeometry.MinFramePx), bmp.Height);
        var fixedPx = TextGeometry.Move(new PxRect(px.X, px.Y, w, h), 0, 0, bmp.Width, bmp.Height);
        _vm.BeginNewText(TextGeometry.FromPx(fixedPx, bmp.Width, bmp.Height));
    }

    private bool IsInEditor(object? source) =>
        source is DependencyObject d && (ReferenceEquals(d, TextEditor) || (d is Visual v && TextEditor.IsAncestorOf(v)));

    /// <summary>入力欄の表示を ViewModel の入力中の枠に合わせる（出す・隠す・中身を入れる・位置）。</summary>
    private void SyncTextEditor()
    {
        var editing = _vm?.EditingText;
        if (editing is null)
        {
            _editorShownFor = null;
            if (TextEditor.Visibility == Visibility.Visible)
            {
                var hadFocus = TextEditor.IsKeyboardFocusWithin;
                _editorSync = true;
                TextEditor.Visibility = Visibility.Collapsed;
                _editorSync = false;
                if (hadFocus) Viewport.Focus();
            }
            return;
        }
        if (_editorShownFor != editing.Id)
        {
            _editorShownFor = editing.Id;
            _editorSync = true;
            TextEditor.Text = editing.Text.Replace("\n", Environment.NewLine);
            TextEditor.Visibility = Visibility.Visible;
            _editorSync = false;
            TextEditor.CaretIndex = TextEditor.Text.Length;
            PlaceTextEditor();
            // 表示が反映されてからフォーカスを移す（Collapsed のままでは受け取れない）。
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_vm?.EditingTextId == editing.Id) TextEditor.Focus();
            });
            return;
        }
        PlaceTextEditor();
    }

    /// <summary>入力欄を枠のすぐ下（下に余地が無ければ上）に置く。枠の位置は PageImage → Viewport の変換（ズーム・パン込み）で求める。</summary>
    private void PlaceTextEditor()
    {
        if (TextEditor.Visibility != Visibility.Visible || _vm?.EditingText is not { } t || _shownPage is null) return;
        var bmp = _shownPage.State.Base;
        var frame = TextLayout.FramePx(t.Box, bmp.Width, bmp.Height);
        var topLeft = PageImage.TranslatePoint(new Point(frame.X, frame.Y), Viewport);
        var bottomRight = PageImage.TranslatePoint(new Point(frame.Right, frame.Bottom), Viewport);
        var width = Math.Max(TextEditor.MinWidth, bottomRight.X - topLeft.X);
        TextEditor.Width = width;
        var height = TextEditor.ActualHeight > 0 ? TextEditor.ActualHeight : 30;
        var top = bottomRight.Y + 4;
        if (top + height > Viewport.ActualHeight && topLeft.Y - height - 4 >= 0) top = topLeft.Y - height - 4;
        var left = Math.Clamp(topLeft.X, 0, Math.Max(0, Viewport.ActualWidth - width));
        Canvas.SetLeft(TextEditor, left);
        Canvas.SetTop(TextEditor, Math.Clamp(top, 0, Math.Max(0, Viewport.ActualHeight - height)));
    }

    private void OnTextEditorSizeChanged(object sender, SizeChangedEventArgs e) => PlaceTextEditor();

    private void OnTextEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editorSync || _vm is null) return;
        _vm.SetEditingText(TextEditor.Text);
    }

    /// <summary>Enter で確定・Shift+Enter で改行（TextBox が入れる）・Esc で取り消し。IME の変換中の Enter は IME のもの（Key = ImeProcessed）。</summary>
    private void OnTextEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _editorSync = true;
            _vm.CommitTextEditing();
            _editorSync = false;
            SyncTextEditor();
            Viewport.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _editorSync = true;
            _vm.CancelTextEditing();
            _editorSync = false;
            SyncTextEditor();
            Viewport.Focus();
        }
    }

    /// <summary>入力欄からフォーカスが外れた（画面・右ペインのクリックなど）= 確定。</summary>
    private void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editorSync || _vm?.EditingTextId is null) return;
        _editorSync = true;
        _vm.CommitTextEditing();
        _editorSync = false;
        SyncTextEditor();
    }

    /// <summary>右ペインの数値の欄: Enter でその場で反映する（既定ではフォーカスが外れたときだけ）。</summary>
    private void OnPanelNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    /// <summary>
    /// 道具の 1 文字キー（V 選択・R 矩形・B ブラシ・T 文字）と、選択中の文字枠の矢印キー（1px・Shift で 10px）。
    /// 文字の入力欄・右ペインの数値の欄・ドロップダウンにフォーカスがあるときは扱わない（打った文字を横取りしない）。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            EditorTool? tool = e.Key switch
            {
                Key.V => EditorTool.Select,
                Key.R => EditorTool.Rect,
                Key.B => EditorTool.Brush,
                Key.T => EditorTool.Text,
                _ => null,
            };
            if (tool is { } t)
            {
                _vm.SetToolCommand.Execute(t);
                e.Handled = true;
                return;
            }
        }
        if (_vm.SelectedText is not null && (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
            && (ReferenceEquals(Keyboard.FocusedElement, Viewport) || Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this)))
        {
            var step = mods == ModifierKeys.Shift ? 10 : 1;
            (double X, double Y)? d = e.Key switch
            {
                Key.Left => (-step, 0),
                Key.Right => (step, 0),
                Key.Up => (0, -step),
                Key.Down => (0, step),
                _ => null,
            };
            if (d is { } v)
            {
                _vm.NudgeSelectedText(v.X, v.Y);
                e.Handled = true;
            }
        }
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

    /// <summary>終了時の保存待ちがこれを超えたら「保存しています…」を出す。</summary>
    private static readonly TimeSpan ClosingNoticeDelay = TimeSpan.FromSeconds(3);

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAfterFlush || _vm is not { HasPendingSave: true } vm) return;
        e.Cancel = true;
        IsEnabled = false;
        var flush = vm.FlushSaveAsync();
        // 待ちが長いときだけ「保存しています…」を出す（タイムアウトはしない＝書きかけで終わらせない）。
        if (await Task.WhenAny(flush, Task.Delay(ClosingNoticeDelay)) != flush) ClosingOverlay.Visibility = Visibility.Visible;
        try { await flush; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"終了時の保存に失敗: {ex.GetType().Name}"); }
        _closeAfterFlush = true;
        Close();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Handled = true;
        // 取り込みは Drop が戻ってから始める。Drop の中でエラーやパスワードのダイアログを出すと、閉じるまで
        // ドラッグ元（エクスプローラー）の DoDragDrop が戻らず固まる（E2E で発見）。ImportAsync は例外を中で表示する。
        var vm = _vm;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => vm.ImportAsync(paths));
    }
}
