using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Geometry;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 文字入れ（v0.3・docs/windows-design.md §11・仕様の正は docs/form-fill-design.md）の画面側。
/// 道具「文字」でドラッグ（クリックだけなら既定の大きさ）→ 枠ができて入力。選択ツールで枠を選び、つまみ・ドラッグ・矢印キーで動かす。
/// 右ペインの「文字」パネルは、枠を選んでいればその枠、いなければ「次に作る枠の設定」を変える（前に使った設定はマス数以外を引き継ぐ）。
/// この型は画面座標を知らない（ビューが正規化・基準ビットマップの px にしてから呼ぶ）。
/// </summary>
public sealed partial class MainViewModel
{
    private TextStyle _nextTextStyle = TextStyle.Default;
    private Guid? _selectedTextId;

    /// <summary>文字の寸法（WPF）。画面・はみ出しの判定・焼き込みで同じ物を使う。</summary>
    public WpfTextMetrics TextMetrics { get; } = WpfTextMetrics.Instance;

    public ObservableCollection<TextItemViewModel> TextItems { get; } = [];
    /// <summary>パレットの色（黒・青・赤・緑・茶・灰。Mac と同じ順・同じ値）。</summary>
    public IReadOnlyList<TextColorOption> TextColorOptions { get; } =
        TextColor.Palette.Select(p => new TextColorOption(p.Key, Strings.Get("TextColor_" + p.Key), p.Color)).ToList();

    private RelayCommand? _duplicateTextCommand;
    private RelayCommand? _fitTextCommand;
    private RelayCommand? _pickTextColorCommand;
    private RelayCommand? _stepTextCommand;
    private RelayCommand? _setTextColorCommand;

    /// <summary>Ctrl+D: 選択中の文字枠を少しずらして複製する。</summary>
    public RelayCommand DuplicateTextCommand => _duplicateTextCommand ??=
        new RelayCommand(DuplicateSelectedText, () => SelectedText is not null && !IsBusy);

    /// <summary>「枠の高さに合わせる」（1 回だけ計算して入れる・自動では追従しない）。</summary>
    public RelayCommand FitTextToFrameCommand => _fitTextCommand ??=
        new RelayCommand(FitSelectedTextToFrame, () => SelectedText is not null && !IsBusy);

    /// <summary>「その他の色」（Windows 標準の色の設定）。</summary>
    public RelayCommand PickTextColorCommand => _pickTextColorCommand ??= new RelayCommand(PickCustomTextColor, () => !IsBusy);

    /// <summary>パレットの色（パラメータ = <see cref="TextColorOption"/>）。</summary>
    public RelayCommand SetTextColorCommand => _setTextColorCommand ??=
        new RelayCommand(p => { if (p is TextColorOption o) ApplyTextStyle(s => s with { Color = o.Color }); });

    /// <summary>
    /// 数値の上下ボタン（パラメータ = "size+" / "size-" / "line+" / "line-" / "letter+" / "letter-" / "cells+" / "cells-"）。
    /// </summary>
    public RelayCommand StepTextCommand => _stepTextCommand ??= new RelayCommand(p => StepText(p as string));

    // ---- 道具・選択 ----

    public bool IsTextTool { get => Tool == EditorTool.Text; set { if (value) Tool = EditorTool.Text; } }

    /// <summary>選択中の文字枠（候補・手動矩形の選択とは排他）。</summary>
    public Guid? SelectedTextId
    {
        get => _selectedTextId;
        private set
        {
            if (_selectedTextId == value) return;
            _selectedTextId = value;
            if (value is not null && _selection is not null)
            {
                _selection = null;
                OnPropertyChanged(nameof(Selection));
            }
            OnPropertyChanged();
            OnTextStateChanged();
        }
    }

    public TextBox? SelectedText => SelectedTextId is { } id ? SelectedPage?.State.FindText(id) : null;

    /// <summary>入力中の文字枠（入力欄を出している）。</summary>
    public Guid? EditingTextId => SelectedPage?.State.EditingTextId;

    public TextBox? EditingText => EditingTextId is { } id ? SelectedPage?.State.FindText(id) : null;

    /// <summary>次に作る文字枠の設定（前に使った設定を引き継ぐ・文字そのものは引き継がない）。</summary>
    public TextStyle NextTextStyle
    {
        get => _nextTextStyle;
        private set
        {
            if (_nextTextStyle == value) return;
            _nextTextStyle = value;
            OnTextStateChanged();
        }
    }

    /// <summary>文字枠を選ぶ（一覧のクリック・画面のクリック）。null で選択解除。入力中なら先に確定する。</summary>
    public void SelectText(Guid? id)
    {
        if (SelectedPage is not { } p) { SelectedTextId = null; return; }
        if (p.State.EditingTextId is { } editing && editing != id) CommitTextEditing();
        SelectedTextId = id is { } x && p.State.FindText(x) is not null ? x : null;
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画面のクリックで文字枠を探して選ぶ（後から足した枠を優先）。見つからなければ選択はそのまま null を返す。</summary>
    public TextBox? HitText(NormPoint p) =>
        SelectedPage is { } page ? TextGeometry.HitText(page.State.Texts, p) : null;

    // ---- 作成・入力 ----

    /// <summary>ドラッグで描いた枠で新しい文字枠を作り、入力を始める（設定は <see cref="NextTextStyle"/>）。</summary>
    public Guid? BeginNewText(NormRect box)
    {
        if (SelectedPage is not { } p || IsBusy) return null;
        var id = p.State.BeginNewText(box, NextTextStyle);
        SelectedTextId = id;
        AfterTextLive();
        OnPropertyChanged(nameof(EditingTextId));
        return id;
    }

    /// <summary>クリックだけで既定の大きさの枠を作る（クリック位置 = 枠の左端・縦の中央）。</summary>
    public Guid? BeginNewTextAt(NormPoint click)
    {
        if (SelectedPage is not { } p) return null;
        var bmp = p.State.Base;
        return TextGeometry.DefaultBox(click, NextTextStyle, bmp.Width, bmp.Height) is { } box ? BeginNewText(box) : null;
    }

    /// <summary>既存の枠の入力を始める（ダブルクリック）。</summary>
    public void BeginTextEditing(Guid id)
    {
        if (SelectedPage is not { } p || IsBusy) return;
        var changed = p.State.EditingTextId is not null && p.State.EditingTextId != id;
        if (!p.State.BeginTextEditing(id)) return;
        SelectedTextId = id;
        if (changed) AfterEdit(); else AfterTextLive();
        OnPropertyChanged(nameof(EditingTextId));
    }

    /// <summary>入力中の文字（キー入力ごと・Undo なし）。画面は焼き込みと同じ描画で即反映する。</summary>
    public void SetEditingText(string text)
    {
        if (SelectedPage is not { } p || !p.State.SetEditingText(text)) return;
        AfterTextLive();
    }

    /// <summary>入力の確定（Enter・フォーカスが外れた）。空になった枠は消す。入力前との差分を 1 手として積む。</summary>
    public void CommitTextEditing()
    {
        if (SelectedPage is not { } p || p.State.EditingTextId is null) return;
        p.State.CommitTextEditing();
        DropStaleTextSelection();
        OnPropertyChanged(nameof(EditingTextId));
        AfterEdit();
    }

    /// <summary>入力の取り消し（Esc）。入力前に戻す（新しい枠はそのまま消える）。</summary>
    public void CancelTextEditing()
    {
        if (SelectedPage is not { } p || p.State.EditingTextId is null) return;
        p.State.CancelTextEditing();
        DropStaleTextSelection();
        OnPropertyChanged(nameof(EditingTextId));
        AfterEdit();
    }

    // ---- 移動・大きさ・矢印キー・mm ----

    /// <summary>ドラッグ中のライブ更新（Undo なし。確定は <see cref="CommitTextBox"/>）。</summary>
    public void SetTextBoxLive(Guid id, NormRect box)
    {
        if (SelectedPage is not { } p) return;
        p.State.SetTextBoxLive(id, box);
        CanvasChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ドラッグの確定（移動・大きさ変更 = 1 手）。</summary>
    public void CommitTextBox(Guid id, NormRect from)
    {
        if (SelectedPage is not { } p) return;
        p.State.CommitTextBox(id, from);
        AfterEdit();
    }

    /// <summary>選択中の枠を基準ビットマップの px で動かす（矢印キー 1px・Shift+矢印 10px。dy は画面の下向きが正）。1 回 = 1 手。</summary>
    public void NudgeSelectedText(double dxPx, double dyPx)
    {
        if (SelectedPage is not { } p || SelectedText is not { } t || IsBusy) return;
        var bmp = p.State.Base;
        p.State.SetTextBox(t.Id, TextGeometry.Offset(t.Box, dxPx, dyPx, bmp.Width, bmp.Height));
        AfterEdit();
    }

    /// <summary>選択中の枠の位置と大きさ（mm・ページ左上から）。写真の取り込みでは 300dpi 換算の目安。</summary>
    public FrameMM? SelectedTextFrameMM =>
        SelectedPage is { } p && SelectedText is { } t ? TextGeometry.ToMM(t.Box, p.State.Base.Width, p.State.Base.Height) : null;

    /// <summary>mm で枠を指定する（1 回 = 1 手）。ページの外に出る分は収める。</summary>
    public void SetSelectedTextFrameMM(FrameMM frame)
    {
        if (SelectedPage is not { } p || IsBusy) return;
        CommitTextEditing();
        if (SelectedText is not { } t) return;
        var bmp = p.State.Base;
        if (TextGeometry.FromMM(frame, bmp.Width, bmp.Height) is not { } box) return;
        p.State.SetTextBox(t.Id, box);
        AfterEdit();
    }

    public double TextFrameX { get => SelectedTextFrameMM?.X ?? 0; set => EditFrame(f => f with { X = value }); }
    public double TextFrameY { get => SelectedTextFrameMM?.Y ?? 0; set => EditFrame(f => f with { Y = value }); }
    public double TextFrameWidth { get => SelectedTextFrameMM?.Width ?? 0; set => EditFrame(f => f with { Width = value }); }
    public double TextFrameHeight { get => SelectedTextFrameMM?.Height ?? 0; set => EditFrame(f => f with { Height = value }); }

    private void EditFrame(Func<FrameMM, FrameMM> change)
    {
        if (SelectedTextFrameMM is { } f) SetSelectedTextFrameMM(change(f));
    }

    // ---- 削除・複製 ----

    /// <summary>選択中の枠を消す（Delete）。</summary>
    public void DeleteSelectedText()
    {
        if (SelectedPage is not { } p || SelectedTextId is not { } id || IsBusy) return;
        var wasEditing = p.State.EditingTextId is not null;
        p.State.RemoveText(id);
        SelectedTextId = null;
        if (wasEditing) OnPropertyChanged(nameof(EditingTextId));
        AfterEdit();
    }

    /// <summary>一覧のごみ箱: その枠を消す。</summary>
    public void DeleteText(Guid id)
    {
        if (SelectedPage is null || IsBusy) return;
        SelectedTextId = id;
        DeleteSelectedText();
    }

    /// <summary>選択中の枠を少し（2mm）右下へずらして複製する（Ctrl+D）。複製した枠を選ぶ。</summary>
    public void DuplicateSelectedText()
    {
        if (SelectedPage is not { } p || IsBusy) return;
        CommitTextEditing();
        if (SelectedText is not { } t) return;
        var bmp = p.State.Base;
        var offset = TextGeometry.PixelsFromMM(TextGeometry.DuplicateOffsetMM);
        var box = TextGeometry.Offset(t.Box, offset, offset, bmp.Width, bmp.Height);
        if (p.State.DuplicateText(t.Id, box) is { } copy) SelectedTextId = copy;
        AfterEdit();
    }

    // ---- 設定（パネル）----

    /// <summary>
    /// 文字パネルの設定変更（Mac の applyTextStyle と同じ）。文字枠を選んでいればその枠を変え（1 回 = 1 手）、選んでいなければ
    /// 「次に作る枠の設定」を変える。どちらも次の枠へ引き継ぐ（ただしマス数は、選んだ枠から変えたときは引き継がない
    /// ＝個人番号欄の 12 マスが次の氏名欄に付いてしまわないように）。範囲は丸める。
    /// </summary>
    public void ApplyTextStyle(Func<TextStyle, TextStyle> change)
    {
        if (IsBusy) return;
        if (SelectedPage is { } p && SelectedTextId is { } id && p.State.FindText(id) is not null)
        {
            CommitTextEditing();
            if (p.State.FindText(id) is not { } current) return;
            var s = change(current.Style).Clamped();
            p.State.SetTextStyle(id, s);
            NextTextStyle = s with { Cells = NextTextStyle.Cells };
            AfterEdit();
        }
        else
        {
            NextTextStyle = change(NextTextStyle).Clamped();
        }
    }

    /// <summary>「枠の高さに合わせる」: 選択中の枠の文字サイズを 1 回だけ計算して入れる。</summary>
    public void FitSelectedTextToFrame()
    {
        if (SelectedPage is not { } p || IsBusy) return;
        CommitTextEditing();
        if (SelectedText is not { } t) return;
        var size = TextLayout.FittingFontSizePt(t, p.State.Base.Width, p.State.Base.Height, TextMetrics);
        ApplyTextStyle(s => s with { FontSizePt = size });
    }

    private void PickCustomTextColor()
    {
        if (_dialogs.AskColor(CurrentTextStyle.Color) is { } c && c != CurrentTextStyle.Color)
            ApplyTextStyle(s => s with { Color = c });
    }

    private void StepText(string? what)
    {
        switch (what)
        {
            case "size+": ApplyTextStyle(s => s with { FontSizePt = s.FontSizePt + TextBox.FontSizeStep }); break;
            case "size-": ApplyTextStyle(s => s with { FontSizePt = s.FontSizePt - TextBox.FontSizeStep }); break;
            case "line+": ApplyTextStyle(s => s with { LineSpacing = s.LineSpacing + TextBox.LineSpacingStep }); break;
            case "line-": ApplyTextStyle(s => s with { LineSpacing = s.LineSpacing - TextBox.LineSpacingStep }); break;
            case "letter+": if (IsLetterSpacingEnabled) ApplyTextStyle(s => s with { LetterSpacing = s.LetterSpacing + TextBox.LetterSpacingStep }); break;
            case "letter-": if (IsLetterSpacingEnabled) ApplyTextStyle(s => s with { LetterSpacing = s.LetterSpacing - TextBox.LetterSpacingStep }); break;
            case "cells+": ApplyTextStyle(s => s with { Cells = s.Cells + 1 }); break;
            case "cells-": ApplyTextStyle(s => s with { Cells = s.Cells - 1 }); break;
        }
    }

    /// <summary>パネルに出す設定（選んだ枠の設定、無ければ次に作る枠の設定）。</summary>
    public TextStyle CurrentTextStyle => SelectedText?.Style ?? NextTextStyle;

    /// <summary>文字パネルを出すか（文字ツールのとき、または文字枠を選んでいるとき）。</summary>
    public bool IsTextPanelVisible => SelectedPage is not null && (Tool == EditorTool.Text || SelectedText is not null);

    public bool HasSelectedText => SelectedText is not null;

    public string TextPanelTarget => SelectedText is not null ? Strings.TextPanelSelected : Strings.TextPanelNext;

    public bool IsTextGothic { get => CurrentTextStyle.Font == TextFont.Gothic; set { if (value) ApplyTextStyle(s => s with { Font = TextFont.Gothic }); } }
    public bool IsTextMincho { get => CurrentTextStyle.Font == TextFont.Mincho; set { if (value) ApplyTextStyle(s => s with { Font = TextFont.Mincho }); } }

    public double TextFontSize { get => CurrentTextStyle.FontSizePt; set => ApplyTextStyle(s => s with { FontSizePt = value }); }
    public double TextLineSpacing { get => CurrentTextStyle.LineSpacing; set => ApplyTextStyle(s => s with { LineSpacing = value }); }

    public double TextLetterSpacing
    {
        get => CurrentTextStyle.LetterSpacing;
        set { if (IsLetterSpacingEnabled) ApplyTextStyle(s => s with { LetterSpacing = value }); }
    }

    /// <summary>マス目の欄では字間を使わない（枠の幅とマス数で位置が決まる）。</summary>
    public bool IsLetterSpacingEnabled => CurrentTextStyle.Cells == 0;

    public string LetterSpacingTip => IsLetterSpacingEnabled ? Strings.TextLetterSpacingTip : Strings.TextLetterSpacingCellsTip;

    public int TextCells { get => CurrentTextStyle.Cells; set => ApplyTextStyle(s => s with { Cells = value }); }

    public bool IsTextAlignLeft { get => CurrentTextStyle.HAlign == TextHAlign.Left; set { if (value) ApplyTextStyle(s => s with { HAlign = TextHAlign.Left }); } }
    public bool IsTextAlignCenter { get => CurrentTextStyle.HAlign == TextHAlign.Center; set { if (value) ApplyTextStyle(s => s with { HAlign = TextHAlign.Center }); } }
    public bool IsTextAlignRight { get => CurrentTextStyle.HAlign == TextHAlign.Right; set { if (value) ApplyTextStyle(s => s with { HAlign = TextHAlign.Right }); } }
    public bool IsTextVAlignTop { get => CurrentTextStyle.VAlign == TextVAlign.Top; set { if (value) ApplyTextStyle(s => s with { VAlign = TextVAlign.Top }); } }
    public bool IsTextVAlignCenter { get => CurrentTextStyle.VAlign == TextVAlign.Center; set { if (value) ApplyTextStyle(s => s with { VAlign = TextVAlign.Center }); } }
    public bool IsTextVAlignBottom { get => CurrentTextStyle.VAlign == TextVAlign.Bottom; set { if (value) ApplyTextStyle(s => s with { VAlign = TextVAlign.Bottom }); } }

    // ---- はみ出し・書き出しの数 ----

    /// <summary>その枠がはみ出しているか（画面の赤い破線）。</summary>
    public bool TextOverflows(PageState page, TextBox t) => TextLayout.Overflows(t, page.Base.Width, page.Base.Height, TextMetrics);

    // ---- 内部 ----

    /// <summary>Undo などで消えた枠を選んだままにしない。</summary>
    private void DropStaleTextSelection()
    {
        if (SelectedTextId is { } id && SelectedPage?.State.FindText(id) is null) SelectedTextId = null;
    }

    /// <summary>選択中のページの入力を確定する（ほかの操作・ページの切り替えの前）。</summary>
    private void EndTextEditing(PageViewModel? page = null)
    {
        var p = page ?? SelectedPage;
        if (p?.State.EditingTextId is null) return;
        p.State.CommitTextEditing();
        OnPropertyChanged(nameof(EditingTextId));
    }

    /// <summary>入力中・ドラッグ中のライブ更新の後（Undo に積まない）。画面と自動保存の予約だけ。</summary>
    private void AfterTextLive()
    {
        CanvasChanged?.Invoke(this, EventArgs.Empty);
        RebuildTextItems();
        NoteEdited();
    }

    /// <summary>文字パネル・一覧の表示を今の状態に合わせる。</summary>
    private void OnTextStateChanged()
    {
        var current = CurrentTextStyle.Color;
        foreach (var o in TextColorOptions) o.IsSelected = o.Color == current;
        foreach (var name in TextPanelProperties) OnPropertyChanged(name);
        DuplicateTextCommand.RaiseCanExecuteChanged();
        FitTextToFrameCommand.RaiseCanExecuteChanged();
        DeleteSelectionCommand.RaiseCanExecuteChanged();
        RebuildTextItems();
    }

    private static readonly string[] TextPanelProperties =
    [
        nameof(SelectedText), nameof(CurrentTextStyle), nameof(IsTextPanelVisible), nameof(HasSelectedText), nameof(TextPanelTarget),
        nameof(IsTextGothic), nameof(IsTextMincho), nameof(TextFontSize), nameof(TextLineSpacing), nameof(TextLetterSpacing),
        nameof(IsLetterSpacingEnabled), nameof(LetterSpacingTip), nameof(TextCells),
        nameof(IsTextAlignLeft), nameof(IsTextAlignCenter), nameof(IsTextAlignRight),
        nameof(IsTextVAlignTop), nameof(IsTextVAlignCenter), nameof(IsTextVAlignBottom),
        nameof(SelectedTextFrameMM), nameof(TextFrameX), nameof(TextFrameY), nameof(TextFrameWidth), nameof(TextFrameHeight),
        nameof(CustomTextColorBrush), nameof(IsCustomTextColor),
    ];

    /// <summary>パレットに無い色を使っているか（「その他」に印を付ける）。</summary>
    public bool IsCustomTextColor => TextColor.Palette.All(p => p.Color != CurrentTextStyle.Color);

    public Brush CustomTextColorBrush
    {
        get
        {
            var c = CurrentTextStyle.Color;
            var b = new SolidColorBrush(Color.FromRgb(c.R8, c.G8, c.B8));
            b.Freeze();
            return b;
        }
    }

    /// <summary>右ペインの文字の一覧（Mac の「文字」一覧）を作り直す。</summary>
    private void RebuildTextItems()
    {
        var texts = SelectedPage?.State.Texts ?? [];
        var selected = SelectedTextId;
        if (TextItems.Count == texts.Count)
        {
            var same = true;
            for (var i = 0; i < texts.Count && same; i++) same = TextItems[i].Id == texts[i].Id;
            if (same)
            {
                for (var i = 0; i < texts.Count; i++) TextItems[i].Update(RowLabel(texts[i], i), texts[i].Id == selected);
                OnPropertyChanged(nameof(HasTextItems));
                return;
            }
        }
        TextItems.Clear();
        for (var i = 0; i < texts.Count; i++)
            TextItems.Add(new TextItemViewModel(texts[i].Id, RowLabel(texts[i], i), texts[i].Id == selected, SelectText, DeleteText));
        OnPropertyChanged(nameof(HasTextItems));
    }

    public bool HasTextItems => TextItems.Count > 0;

    private static string RowLabel(TextBox t, int index)
    {
        var lines = TextLayout.SplitLines(t.Text);
        var first = lines[0];
        var n = (index + 1).ToString(CultureInfo.InvariantCulture);
        if (first.Length == 0 && lines.Length <= 1) return string.Format(Strings.TextRowEditing, n);
        return lines.Length > 1 ? string.Format(Strings.TextRowMultiLine, n, first, lines.Length) : string.Format(Strings.TextRow, n, first);
    }
}

/// <summary>右ペインの文字の一覧の 1 行。</summary>
public sealed class TextItemViewModel : ObservableObject
{
    private string _label;
    private bool _isSelected;

    public TextItemViewModel(Guid id, string label, bool isSelected, Action<Guid?> select, Action<Guid> delete)
    {
        Id = id;
        _label = label;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(id));
        DeleteCommand = new RelayCommand(() => delete(id));
    }

    public Guid Id { get; }
    public string Label => _label;
    public bool IsSelected => _isSelected;
    public RelayCommand SelectCommand { get; }
    public RelayCommand DeleteCommand { get; }

    internal void Update(string label, bool isSelected)
    {
        if (_label != label) { _label = label; OnPropertyChanged(nameof(Label)); }
        if (_isSelected != isSelected) { _isSelected = isSelected; OnPropertyChanged(nameof(IsSelected)); }
    }
}

/// <summary>パレットの色 1 つ（右ペインの丸いボタン）。</summary>
public sealed class TextColorOption : ObservableObject
{
    private bool _isSelected;

    public TextColorOption(string key, string name, TextColor color)
    {
        Key = key;
        Name = name;
        Color = color;
        var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R8, color.G8, color.B8));
        b.Freeze();
        Brush = b;
    }

    public string Key { get; }
    public string Name { get; }
    public TextColor Color { get; }
    public Brush Brush { get; }
    public string AutomationId => "text.color." + Key.ToLowerInvariant();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
