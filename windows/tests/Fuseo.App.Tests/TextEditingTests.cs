using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.App.Library;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.App.Views;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Library;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;
using TextBox = Fuseo.Core.Model.TextBox;

namespace Fuseo.App.Tests;

/// <summary>
/// 文字入れ W10-2 のアプリ層（docs/windows-design.md §11・Mac の FuseoTests/TextEditingTests を移したもの）。
/// 作成・移動・大きさ・文字・設定・削除・複製の Undo、設定の引き継ぎ（マス数を除く）、種別変更で保持・回転/切り抜きで破棄（確認あり）、
/// ページ削除/並べ替えで追従、保存→開き直し、書き出しの数え方、mm 指定、吸い付き、焼き込み（マスクは文字の上で全黒）、画面描画＝焼き込み。
/// </summary>
public class TextEditingTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();

    public TextEditingTests()
    {
        // 600×400 の白いページ（取り込み順に幅 601, 602… でページを見分ける）。
        var n = 0;
        _source.Handler = paths => paths.Select(p => new SourcePage(PageBitmap.Solid(600 + ++n, 400, 255, 255, 255), p, 0, false)).ToList();
    }

    private MainViewModel NewVm(IExporter? exporter = null, FileWorkLibrary? library = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_source, new MaskRasterizer(), exporter ?? new Exporter(), PresetStore.LoadAll(), _dialogs, () => new DateTime(2026, 11, 11),
            library: library, saveDelay: delay);

    private async Task<MainViewModel> VmWithPages(int n, IExporter? exporter = null, FileWorkLibrary? library = null,
                                                  Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var vm = NewVm(exporter, library, delay);
        await vm.ImportAsync(Enumerable.Range(1, n).Select(i => $"t{i}.png").ToList());
        vm.SelectedPage = vm.Pages[0];
        return vm;
    }

    /// <summary>文字の道具で枠を作って打って確定する（1 手）。</summary>
    private static Guid AddText(MainViewModel vm, string text, NormRect? box = null)
    {
        var id = vm.BeginNewText(box ?? new NormRect(0.1, 0.2, 0.4, 0.2))!.Value;
        vm.SetEditingText(text);
        vm.CommitTextEditing();
        return id;
    }

    private static PageState State(MainViewModel vm) => vm.SelectedPage!.State;

    // ---- 作成・入力 ----

    [Fact]
    public async Task CreateText_IsOneUndoStep_AndRedoBringsItBack()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var id = vm.BeginNewText(new NormRect(0.1, 0.6, 0.4, 0.1))!.Value;
        Assert.Equal(id, vm.EditingTextId);   // 作った枠はそのまま入力状態
        Assert.Equal(id, vm.SelectedTextId);
        vm.SetEditingText("山田 太郎");
        Assert.Equal("山田 太郎", page.Texts.Single().Text);   // 入力中も画面に即反映
        Assert.False(page.CanUndo, "入力中は Undo に積まない");
        vm.CommitTextEditing();
        Assert.Null(vm.EditingTextId);
        Assert.Single(page.Texts);

        vm.UndoCommand.Execute(null);
        Assert.Empty(page.Texts);   // 作成＋最初の文字が 1 手で消える
        Assert.Null(vm.SelectedTextId);   // 消えた枠を選んだままにしない
        Assert.False(page.CanUndo);

        vm.RedoCommand.Execute(null);
        Assert.Equal(["山田 太郎"], page.Texts.Select(t => t.Text));
    }

    [Fact]
    public async Task CancelOrEmptyCommit_OnNewBox_RemovesItWithoutUndoStep()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        vm.BeginNewText(new NormRect(0.1, 0.1, 0.2, 0.1));
        vm.SetEditingText("途中");
        vm.CancelTextEditing();
        Assert.Empty(page.Texts);   // Esc で新しい枠ごと取り消す
        Assert.Null(vm.SelectedTextId);

        vm.BeginNewText(new NormRect(0.1, 0.1, 0.2, 0.1));
        vm.CommitTextEditing();
        Assert.Empty(page.Texts);   // 空のまま確定した枠は残さない
        Assert.False(page.CanUndo);
    }

    [Fact]
    public async Task EditExistingText_CancelReverts_CommitIsOneUndoStep()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var id = AddText(vm, "東京都");

        vm.BeginTextEditing(id);
        vm.SetEditingText("東京都千代田区");
        vm.CancelTextEditing();
        Assert.Equal("東京都", page.FindText(id)!.Text);   // Esc で入力前に戻る
        Assert.Equal(id, vm.SelectedTextId);   // 既存の枠は選んだまま

        vm.BeginTextEditing(id);
        vm.SetEditingText("東京都千代田区\r\n1-1");   // WPF の TextBox は \r\n を入れる
        vm.CommitTextEditing();
        Assert.Equal("東京都千代田区\n1-1", page.FindText(id)!.Text);   // 改行は \n にそろえる
        vm.UndoCommand.Execute(null);
        Assert.Equal("東京都", page.FindText(id)!.Text);
        vm.UndoCommand.Execute(null);
        Assert.Empty(page.Texts);
    }

    [Fact]
    public async Task ClearingTextOfExistingBox_DeletesIt_Undoable()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var id = AddText(vm, "消す");
        vm.BeginTextEditing(id);
        vm.SetEditingText("");
        vm.CommitTextEditing();
        Assert.Empty(page.Texts);
        vm.UndoCommand.Execute(null);
        Assert.Equal("消す", page.FindText(id)!.Text);
    }

    [Fact]
    public async Task OtherEdits_CommitTypingFirst()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        vm.BeginNewText(new NormRect(0.1, 0.1, 0.3, 0.1));
        vm.SetEditingText("確定される");
        vm.AddRect(new NormRect(0.5, 0.5, 0.2, 0.2));   // 入力中にマスクを足す → 先に文字を確定
        Assert.Null(vm.EditingTextId);
        vm.UndoCommand.Execute(null);
        Assert.Single(page.Texts);   // マスクだけ戻る
        vm.UndoCommand.Execute(null);
        Assert.Empty(page.Texts);    // 次の 1 手で文字が戻る
    }

    // ---- 移動・大きさ・矢印キー・mm ----

    [Fact]
    public async Task MoveAndResize_AreEachOneUndoStep()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var start = new NormRect(0.1, 0.6, 0.4, 0.1);
        var id = AddText(vm, "枠", start);

        vm.SetTextBoxLive(id, new NormRect(0.15, 0.6, 0.4, 0.1));   // ドラッグ中は何度更新しても 1 手
        vm.SetTextBoxLive(id, new NormRect(0.2, 0.55, 0.4, 0.1));
        vm.CommitTextBox(id, start);
        var moved = new NormRect(0.2, 0.55, 0.4, 0.1);
        Assert.Equal(moved, page.FindText(id)!.Box);

        vm.SetTextBoxLive(id, new NormRect(0.2, 0.55, 0.5, 0.2));
        vm.CommitTextBox(id, moved);

        vm.UndoCommand.Execute(null);
        Assert.Equal(moved, page.FindText(id)!.Box);   // 大きさ変更が 1 手で戻る
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, page.FindText(id)!.Box);   // 移動が 1 手で戻る
        vm.RedoCommand.Execute(null);
        Assert.Equal(moved, page.FindText(id)!.Box);
    }

    [Fact]
    public async Task Nudge_MovesByImagePixels_ShiftTen_AndStaysInsidePage()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);   // 601×400
        double w = page.Base.Width, h = page.Base.Height;
        var id = AddText(vm, "a", TextGeometry.FromPx(new PxRect(100, 100, 200, 40), w, h));
        vm.SelectText(id);

        vm.NudgeSelectedText(1, 0);
        vm.NudgeSelectedText(0, 10);   // Shift+↓ = 画面の下へ 10px
        var px = TextLayout.FramePx(page.FindText(id)!.Box, w, h);
        Assert.Equal(101, px.X, 3);
        Assert.Equal(110, px.Y, 3);   // 画面の下向き（左上原点の y が増える）
        Assert.Equal(200, px.W, 3);

        vm.UndoCommand.Execute(null);
        Assert.Equal(100, TextLayout.FramePx(page.FindText(id)!.Box, w, h).Y, 3);   // 矢印 1 回 = 1 手

        vm.NudgeSelectedText(-10_000, -10_000);
        var clamped = TextLayout.FramePx(page.FindText(id)!.Box, w, h);
        Assert.Equal(0, clamped.X, 3);
        Assert.Equal(0, clamped.Y, 3);
        Assert.Equal(200, clamped.W, 3);   // ページ端で止まり、大きさは変わらない
    }

    [Fact]
    public async Task FrameInMillimeters_RoundTripsAt300dpi()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        double w = page.Base.Width, h = page.Base.Height;
        var id = AddText(vm, "mm", TextGeometry.FromPx(new PxRect(300, 118.110236, 236.220472, 59.055118), w, h));
        vm.SelectText(id);

        Assert.Equal(25.4, vm.TextFrameX, 2);   // 300px = 1 インチ = 25.4mm
        Assert.Equal(10, vm.TextFrameY, 2);     // y はページの左上から
        Assert.Equal(20, vm.TextFrameWidth, 2);
        Assert.Equal(5, vm.TextFrameHeight, 2);

        vm.SetSelectedTextFrameMM(new FrameMM(5, 7.5, 30, 6));
        Assert.Equal(5, vm.TextFrameX, 2);
        Assert.Equal(7.5, vm.TextFrameY, 2);
        Assert.Equal(30, vm.TextFrameWidth, 2);
        Assert.Equal(6, vm.TextFrameHeight, 2);
        vm.UndoCommand.Execute(null);
        Assert.Equal(25.4, vm.TextFrameX, 2);   // 数値指定も 1 手

        vm.TextFrameWidth = 999;   // 右ペインの欄から（ページより大きい指定はページに収める）
        var px = TextLayout.FramePx(page.FindText(id)!.Box, w, h);
        Assert.Equal(0, px.X, 2);
        Assert.Equal(w, px.W, 2);
    }

    [Fact]
    public void DefaultBoxForClick_PutsClickAtLeftEdgeAndVerticalCenter()
    {
        const double w = 2480, h = 3508;   // A4 300dpi
        var click = CoordinateSpace.FromView(500, 1000, w, h, w, h);
        var px = TextLayout.FramePx(TextGeometry.DefaultBox(click, TextStyle.Default, w, h)!.Value, w, h);
        var fontPx = 10.5 * 300 / 72;
        Assert.Equal(500, px.X, 1);
        Assert.Equal(1000, px.MidY, 1);
        Assert.Equal(fontPx * 2, px.H, 1);
        Assert.Equal(fontPx * 10, px.W, 1);
    }

    [Fact]
    public async Task ClickWithTextTool_MakesDefaultBox_WithNextStyle()
    {
        var vm = await VmWithPages(1);
        vm.Tool = EditorTool.Text;
        vm.ApplyTextStyle(s => s with { FontSizePt = 12 });
        var id = vm.BeginNewTextAt(new NormPoint(0.05, 0.5))!.Value;
        var t = State(vm).FindText(id)!;
        Assert.Equal(12, t.FontSizePt);
        Assert.Equal(0.05, t.Box.X, 6);
        Assert.Equal(0.5, t.Box.Y + t.Box.H / 2, 6);
    }

    // ---- 設定と引き継ぎ ----

    [Fact]
    public async Task StyleChange_OnSelectedBox_IsOneUndoStep_AndCarriesToNextBoxExceptCells()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var id = AddText(vm, "123456789012");
        vm.SelectText(id);

        vm.ApplyTextStyle(s => s with { FontSizePt = 14 });
        vm.TextCells = 12;   // 右ペインの欄から
        vm.ApplyTextStyle(s => s with { LineSpacing = 1.5, Font = TextFont.Mincho, Color = TextColor.Blue });
        Assert.Equal(14, page.FindText(id)!.FontSizePt);
        Assert.Equal(12, page.FindText(id)!.Cells);
        Assert.Equal(TextFont.Mincho, page.FindText(id)!.Font);

        Assert.Equal(14, vm.NextTextStyle.FontSizePt);   // 前に使った設定を次の枠に引き継ぐ
        Assert.Equal(1.5, vm.NextTextStyle.LineSpacing);
        Assert.Equal(TextFont.Mincho, vm.NextTextStyle.Font);
        Assert.Equal(TextColor.Blue, vm.NextTextStyle.Color);
        Assert.Equal(0, vm.NextTextStyle.Cells);   // マス数は引き継がない

        vm.TextCells = 0;
        var custom = new TextColor(0.3, 0.6, 0.9);
        vm.ApplyTextStyle(s => s with { LetterSpacing = 0.5, Color = custom });   // 字間と任意の色も引き継ぐ
        Assert.Equal(0.5, page.FindText(id)!.LetterSpacing);
        Assert.Equal(0.5, vm.NextTextStyle.LetterSpacing);
        Assert.Equal(custom, vm.NextTextStyle.Color);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, page.FindText(id)!.LetterSpacing);   // 字間の変更も 1 手で戻る
        Assert.Equal(TextColor.Blue, page.FindText(id)!.Color);
        vm.UndoCommand.Execute(null);
        Assert.Equal(12, page.FindText(id)!.Cells);

        vm.UndoCommand.Execute(null);
        Assert.Equal(TextFont.Gothic, page.FindText(id)!.Font);   // 設定の変更は 1 回 = 1 手
        Assert.Equal(12, page.FindText(id)!.Cells);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, page.FindText(id)!.Cells);
        vm.UndoCommand.Execute(null);
        Assert.Equal(10.5, page.FindText(id)!.FontSizePt);

        // 新しい枠は NextTextStyle で作られる（文字そのものは引き継がない）
        vm.SelectText(null);
        var id2 = vm.BeginNewText(new NormRect(0.1, 0.1, 0.3, 0.1))!.Value;
        Assert.Equal(14, page.FindText(id2)!.FontSizePt);
        Assert.Equal(TextFont.Mincho, page.FindText(id2)!.Font);
        Assert.Equal("", page.FindText(id2)!.Text);
    }

    [Fact]
    public async Task StyleChange_WithoutSelection_EditsNextStyle_AndClamps()
    {
        var vm = await VmWithPages(1);
        vm.Tool = EditorTool.Text;
        Assert.True(vm.IsTextPanelVisible);
        Assert.Equal(Properties.Strings.TextPanelNext, vm.TextPanelTarget);
        vm.TextFontSize = 200;
        vm.TextLineSpacing = 0.1;
        vm.TextCells = 7;
        Assert.Equal(72, vm.NextTextStyle.FontSizePt);   // 範囲外は丸める
        Assert.Equal(0.8, vm.NextTextStyle.LineSpacing);
        Assert.Equal(7, vm.NextTextStyle.Cells);   // 選んでいないときはマス数も次の枠の設定として持つ
        Assert.False(State(vm).CanUndo);   // 次の枠の設定は書類の編集ではない
        vm.TextFontSize = 10.26;
        Assert.Equal(10.5, vm.NextTextStyle.FontSizePt);   // 0.5pt 刻み
        vm.StepTextCommand.Execute("size+");
        Assert.Equal(11, vm.NextTextStyle.FontSizePt);
        vm.StepTextCommand.Execute("line-");
        Assert.Equal(0.8, vm.NextTextStyle.LineSpacing);   // 下限で止まる
    }

    [Fact]
    public async Task LetterSpacing_IsDisabledForCells()
    {
        var vm = await VmWithPages(1);
        vm.Tool = EditorTool.Text;
        vm.TextCells = 4;
        Assert.False(vm.IsLetterSpacingEnabled);
        vm.TextLetterSpacing = 1;
        vm.StepTextCommand.Execute("letter+");
        Assert.Equal(0, vm.NextTextStyle.LetterSpacing);
        vm.TextCells = 0;
        Assert.True(vm.IsLetterSpacingEnabled);
        vm.TextLetterSpacing = 1;
        Assert.Equal(1, vm.NextTextStyle.LetterSpacing);
    }

    [Fact]
    public async Task Palette_AndCustomColor()
    {
        var vm = await VmWithPages(1);
        var id = AddText(vm, "色");
        vm.SelectText(id);
        Assert.Equal(6, vm.TextColorOptions.Count);
        Assert.True(vm.TextColorOptions[0].IsSelected);   // 既定は黒
        vm.SetTextColorCommand.Execute(vm.TextColorOptions[2]);
        Assert.Equal(TextColor.Red, State(vm).FindText(id)!.Color);
        Assert.True(vm.TextColorOptions[2].IsSelected);
        Assert.False(vm.IsCustomTextColor);

        var picked = TextColor.FromBytes(10, 200, 30);
        _dialogs.ColorResult = picked;
        vm.PickTextColorCommand.Execute(null);
        Assert.Equal(TextColor.Red, Assert.Single(_dialogs.ColorRequests));   // 今の色で開く
        Assert.Equal(picked, State(vm).FindText(id)!.Color);
        Assert.True(vm.IsCustomTextColor);
        Assert.DoesNotContain(vm.TextColorOptions, o => o.IsSelected);

        _dialogs.ColorResult = null;   // 取り消し
        vm.PickTextColorCommand.Execute(null);
        Assert.Equal(picked, State(vm).FindText(id)!.Color);
    }

    [Fact]
    public async Task FitToFrame_SetsFontSizeOnce()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        var id = AddText(vm, "一行目\n二行目", new NormRect(0.1, 0.3, 0.6, 0.5));
        vm.SelectText(id);
        var expected = TextLayout.FittingFontSizePt(page.FindText(id)!, page.Base.Width, page.Base.Height, vm.TextMetrics);
        vm.FitTextToFrameCommand.Execute(null);
        Assert.Equal(expected, page.FindText(id)!.FontSizePt);
        Assert.True(expected > 12, $"{expected}");
        vm.UndoCommand.Execute(null);
        Assert.Equal(10.5, page.FindText(id)!.FontSizePt);   // 1 手で戻る
        vm.RedoCommand.Execute(null);
        // 枠を変えても自動では追従しない
        vm.SetTextBoxLive(id, new NormRect(0.1, 0.3, 0.6, 0.1));
        vm.CommitTextBox(id, new NormRect(0.1, 0.3, 0.6, 0.5));
        Assert.Equal(expected, page.FindText(id)!.FontSizePt);
    }

    // ---- 選択・削除・複製 ----

    [Fact]
    public async Task Selection_IsExclusiveWithMaskSelection()
    {
        var vm = await VmWithPages(1);
        vm.AddRect(new NormRect(0.5, 0.5, 0.1, 0.1));
        var id = AddText(vm, "a", new NormRect(0.1, 0.1, 0.2, 0.1));
        vm.Tool = EditorTool.Select;

        vm.SelectAt(new NormPoint(0.55, 0.55));
        Assert.NotNull(vm.Selection);
        Assert.Null(vm.SelectedTextId);
        vm.SelectText(id);
        Assert.Null(vm.Selection);
        Assert.True(vm.HasSelectedText);
        vm.SelectAt(new NormPoint(0.55, 0.55));
        Assert.Null(vm.SelectedTextId);
        vm.SelectAt(new NormPoint(0.9, 0.9));   // 何も無い所
        Assert.Null(vm.Selection);

        Assert.Equal(id, vm.HitText(new NormPoint(0.15, 0.15))!.Id);
        Assert.Null(vm.HitText(new NormPoint(0.9, 0.9)));
    }

    [Fact]
    public async Task DeleteAndDuplicate_AreUndoable()
    {
        var vm = await VmWithPages(1);
        var page = State(vm);
        double w = page.Base.Width, h = page.Base.Height;
        var id = AddText(vm, "複製", TextGeometry.FromPx(new PxRect(100, 100, 120, 30), w, h));
        vm.SelectText(id);

        Assert.True(vm.DuplicateTextCommand.CanExecute(null));
        vm.DuplicateTextCommand.Execute(null);   // Ctrl+D
        Assert.Equal(2, page.Texts.Count);
        var copy = page.Texts[1];
        Assert.NotEqual(id, copy.Id);
        Assert.Equal(copy.Id, vm.SelectedTextId);   // 複製した枠を選ぶ
        Assert.Equal("複製", copy.Text);
        var offset = TextGeometry.PixelsFromMM(2);
        var px = TextLayout.FramePx(copy.Box, w, h);
        Assert.Equal(100 + offset, px.X, 2);   // 少し右下へずらす
        Assert.Equal(100 + offset, px.Y, 2);

        Assert.True(vm.DeleteSelectionCommand.CanExecute(null));
        vm.DeleteSelectionCommand.Execute(null);   // Delete キー
        Assert.Equal([id], page.Texts.Select(t => t.Id));
        Assert.Null(vm.SelectedTextId);

        vm.UndoCommand.Execute(null);
        Assert.Equal(2, page.Texts.Count);   // 削除が 1 手で戻る
        vm.UndoCommand.Execute(null);
        Assert.Equal([id], page.Texts.Select(t => t.Id));   // 複製が 1 手で戻る

        vm.TextItems.Single().DeleteCommand.Execute(null);   // 一覧のごみ箱
        Assert.Empty(page.Texts);
    }

    [Fact]
    public async Task TextList_ShowsRows_AndSelects()
    {
        var vm = await VmWithPages(1);
        var a = AddText(vm, "氏名");
        AddText(vm, "住所\n二行目");
        vm.BeginNewText(new NormRect(0.5, 0.5, 0.2, 0.1));
        Assert.Equal(["文字 1: 氏名", "文字 2: 住所（2 行）", "文字 3（入力中）"], vm.TextItems.Select(i => i.Label));
        vm.CancelTextEditing();
        vm.TextItems[0].SelectCommand.Execute(null);
        Assert.Equal(a, vm.SelectedTextId);
        Assert.True(vm.TextItems[0].IsSelected);
    }

    [Fact]
    public async Task SwitchingToPaintTool_CommitsTyping_AndClearsTextSelection()
    {
        var vm = await VmWithPages(1);
        vm.Tool = EditorTool.Text;
        vm.BeginNewText(new NormRect(0.1, 0.1, 0.3, 0.1));
        vm.SetEditingText("確定");
        vm.Tool = EditorTool.Rect;
        Assert.Null(vm.EditingTextId);
        Assert.Null(vm.SelectedTextId);
        Assert.Equal("確定", State(vm).Texts.Single().Text);
        Assert.False(vm.IsTextPanelVisible);
    }

    // ---- 再解析（種別変更で保持・回転/切り抜きで破棄） ----

    [Fact]
    public async Task TypeChange_KeepsTexts()
    {
        var vm = await VmWithPages(1);
        AddText(vm, "保持");
        vm.SelectedDocumentType = vm.DocumentTypes.First(o => o.Type == DocumentType.MyNumberCardFront);
        Assert.Equal(DocumentType.MyNumberCardFront, State(vm).Type);
        Assert.Equal(["保持"], State(vm).Texts.Select(t => t.Text));
    }

    [Fact]
    public async Task Rotate_DiscardsTexts_AfterConfirmation_AndUndoBringsThemBack()
    {
        var vm = await VmWithPages(1);
        vm.RotateRightCommand.Execute(null);
        Assert.Empty(_dialogs.Confirms);   // 文字が無ければ確認しない（マスクは一緒に回る）
        var id = AddText(vm, "消える");
        vm.SelectText(id);

        _dialogs.ConfirmResult = false;
        vm.RotateRightCommand.Execute(null);
        Assert.Contains("文字", Assert.Single(_dialogs.Confirms));
        Assert.Equal(1, State(vm).Rotation);   // 取り消したら回さない
        Assert.Single(State(vm).Texts);

        _dialogs.ConfirmResult = true;
        vm.RotateRightCommand.Execute(null);
        Assert.Equal(2, State(vm).Rotation);
        Assert.Empty(State(vm).Texts);   // 基準画像が変わるので文字枠は破棄
        Assert.Null(vm.SelectedTextId);
        Assert.Null(vm.EditingTextId);
        vm.UndoCommand.Execute(null);
        Assert.Single(State(vm).Texts);   // 元に戻すで戻る
        Assert.Equal(1, State(vm).Rotation);
    }

    [Fact]
    public async Task Crop_DiscardsTexts_AndConfirmsEvenWithoutManualMasks()
    {
        var vm = await VmWithPages(1);
        AddText(vm, "消える");
        _dialogs.ConfirmResult = false;
        await vm.ApplyCropAsync(vm.SelectedPage!, new Quad(new(0.1, 0.1), new(0.9, 0.1), new(0.9, 0.9), new(0.1, 0.9)));
        Assert.Contains("文字", Assert.Single(_dialogs.Confirms));
        Assert.Single(State(vm).Texts);
        _dialogs.ConfirmResult = true;
        await vm.ApplyCropAsync(vm.SelectedPage!, new Quad(new(0.1, 0.1), new(0.9, 0.1), new(0.9, 0.9), new(0.1, 0.9)));
        Assert.Empty(State(vm).Texts);
        vm.UndoCommand.Execute(null);
        Assert.Single(State(vm).Texts);
    }

    // ---- ページの削除・並べ替え ----

    [Fact]
    public async Task PageRemoveAndMove_CarryTexts()
    {
        var vm = await VmWithPages(3);
        vm.SelectedPage = vm.Pages[1];
        AddText(vm, "二枚目");
        vm.SelectedPage = vm.Pages[2];
        AddText(vm, "三枚目");

        vm.MovePageUpCommand.Execute(null);   // [1, 3, 2]
        Assert.Equal([null, "三枚目", "二枚目"], vm.Pages.Select(p => p.State.Texts.FirstOrDefault()?.Text));
        vm.SelectedPage = vm.Pages[0];
        vm.RemovePageCommand.Execute(null);   // [3, 2]
        Assert.Equal(["三枚目", "二枚目"], vm.Pages.Select(p => p.State.Texts.FirstOrDefault()?.Text));
    }

    [Fact]
    public async Task SwitchingPage_CommitsTyping()
    {
        var vm = await VmWithPages(2);
        vm.BeginNewText(new NormRect(0.1, 0.1, 0.3, 0.1));
        vm.SetEditingText("確定");
        vm.SelectedPage = vm.Pages[1];
        Assert.Null(vm.Pages[0].State.EditingTextId);
        Assert.True(vm.Pages[0].State.CanUndo);
        Assert.Null(vm.SelectedTextId);
    }

    // ---- 保存→開き直し ----

    [Fact]
    public async Task SaveAndReopen_KeepsTexts_AndWritesSchemaV2_OnlyWithTexts()
    {
        using var dir = new TempDir();
        var lib = new FileWorkLibrary(dir.Path);
        var delay = new ManualDelay();
        var vm = await VmWithPages(2, library: lib, delay: delay.Delay);
        vm.ApplyTextStyle(s => s with { FontSizePt = 12, LineSpacing = 1.5, Font = TextFont.Mincho, Color = TextColor.Blue,
                                        HAlign = TextHAlign.Center, VAlign = TextVAlign.Top, LetterSpacing = 0.25 });
        AddText(vm, "氏名\n二行目", new NormRect(0.2, 0.1, 0.5, 0.15));
        vm.SelectedPage = vm.Pages[1];
        vm.TextCells = 12;
        AddText(vm, "123456789012", new NormRect(0.1, 0.7, 0.8, 0.08));
        vm.BeginNewText(new NormRect(0.5, 0.5, 0.1, 0.1));   // 入力しかけの空の枠は保存しない
        var before = vm.Pages.Select(p => p.State.Texts.Where(t => t.Text.Length > 0).ToList()).ToList();
        var id = vm.CurrentWorkId!.Value;
        delay.FireAll();
        await vm.FlushSaveAsync();

        var json = File.ReadAllText(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName));
        Assert.Contains("\"schemaVersion\": \"fuseo-win/2\"", json);   // 文字があるときだけ版を上げる
        Assert.DoesNotContain("\"text\": \"\"", json);

        await vm.NewDocumentAsync();
        await vm.OpenWorkAsync(vm.LibraryItems.Single(i => i.Id == id));
        Assert.Equal(before[0], vm.Pages[0].State.Texts);   // 位置・文字・設定がすべて同じ
        Assert.Equal(before[1], vm.Pages[1].State.Texts);

        // 文字を消して保存すると版は元に戻る（0.2.x でまた開ける）
        foreach (var p in vm.Pages)
        {
            vm.SelectedPage = p;
            foreach (var t in p.State.Texts.ToList()) vm.DeleteText(t.Id);
        }
        delay.FireAll();
        await vm.FlushSaveAsync();
        json = File.ReadAllText(Path.Combine(lib.DirectoryOf(id), FileWorkLibrary.DocumentFileName));
        Assert.Contains("\"schemaVersion\": \"fuseo-win/1\"", json);
        Assert.DoesNotContain("\"texts\"", json);
    }

    // ---- 書き出しの確認・焼き込み ----

    [Fact]
    public async Task Export_CountsNonEmptyBoxes_AndOverflow_ButNotAsMasks()
    {
        var vm = await VmWithPages(2);
        AddText(vm, "a");
        vm.SelectedPage = vm.Pages[1];
        AddText(vm, "b");
        vm.SelectText(null);
        vm.ApplyTextStyle(s => s with { FontSizePt = 40 });
        AddText(vm, "はみ出す長い文字", new NormRect(0.1, 0.1, 0.05, 0.02));   // 小さな枠に大きな文字
        vm.BeginNewText(new NormRect(0.5, 0.5, 0.2, 0.1));   // 入力中の空の枠は数えない（書き出しの前に確定して消える）
        var ex = vm.CreateExportViewModel();
        Assert.Equal(3, ex.TextCount);
        Assert.Equal(1, ex.OverflowingTextCount);
        Assert.Contains("3", ex.TextSummaryText);
        Assert.True(ex.HasOverflowingTexts);
        Assert.Contains("文字 3 箇所", ex.TextSummaryText);
        Assert.StartsWith("マスク 0 箇所", ex.SummaryText);   // 文字はマスクの数に入れない
        Assert.True(ex.HasZeroMaskPage);   // 文字だけのページは「マスクなし」のまま
        Assert.True(vm.TextOverflows(vm.Pages[1].State, vm.Pages[1].State.Texts[1]));
    }

    [Fact]
    public async Task Export_NoTexts_HidesTextSummary()
    {
        var vm = await VmWithPages(1);
        var ex = vm.CreateExportViewModel();
        Assert.False(ex.HasTexts);
        Assert.Equal("", ex.TextSummaryText);
    }

    private static bool IsBlue((byte B, byte G, byte R, byte A) p) => p.B > 100 && p.R < 80;

    /// <summary>焼き込み順 画像 → 文字 → マスク: 文字の上の矩形マスクの内側は全部 #000000FF、マスクの外には文字の画素が残る。</summary>
    [Fact]
    public async Task BurnIn_MaskIsAboveText_AllBlackInside_TextOutside()
    {
        var exporter = new RecordingExporter();
        var vm = await VmWithPages(1, exporter);
        vm.ApplyTextStyle(s => s with { FontSizePt = 24, Color = TextColor.Blue });
        AddText(vm, "123456789012", new NormRect(0.05, 0.3, 0.9, 0.4));
        var mask = new NormRect(0.3, 0.2, 0.3, 0.6);
        vm.AddRect(mask);

        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        var ex = vm.CreateExportViewModel();
        await ex.PrepareAsync();
        await ex.SaveAsync();
        var bmp = Assert.Single(exporter.Pages!).Bitmap;
        var m = CoordinateSpace.ToPixel(mask, bmp.Width, bmp.Height);
        var nonBlack = 0;
        for (var y = m.Y; y < m.Bottom; y++)
            for (var x = m.X; x < m.Right; x++)
                if (bmp.GetPixel(x, y) != (0, 0, 0, 255)) nonBlack++;
        Assert.Equal(0, nonBlack);   // マスクの中に文字の画素が残っていない

        var blueOutside = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < m.X - 2; x++)
                if (IsBlue(bmp.GetPixel(x, y))) blueOutside++;
        Assert.True(blueOutside > 20, $"マスクの外に青い文字が残る: {blueOutside}");
        Assert.True(State(vm).Base.GetPixel(bmp.Width / 2, bmp.Height / 2) == (255, 255, 255, 255));   // 基準の画像は書き換えない
    }

    /// <summary>入力した文字はラスタに焼き込むだけ（PDF にテキスト・フォント・書体名を残さない・form-fill-design §2）。</summary>
    [Fact]
    public async Task PdfExport_TypedTextIsBurned_NotTextOrFont()
    {
        var vm = await VmWithPages(1);   // 本物の Exporter
        vm.ApplyTextStyle(s => s with { FontSizePt = 20 });
        AddText(vm, "TYPEDSECRET", new NormRect(0.05, 0.3, 0.9, 0.4));
        using var dir = new TempDir();
        _dialogs.SavePath = dir.File("out.pdf");
        var ex = vm.CreateExportViewModel();
        await ex.PrepareAsync();
        await ex.SaveAsync();
        var bytes = File.ReadAllBytes(dir.File("out.pdf"));
        var latin = System.Text.Encoding.Latin1.GetString(bytes);
        foreach (var bad in new[] { "TYPEDSECRET", "/Font", "Yu Gothic", "YuGothic", "/Annots" })
            Assert.DoesNotContain(bad, latin);
        Assert.NotNull(InkBounds(Assert.Single(ex.Previews).Page.Bitmap));   // 文字は画像に描かれている
    }

    [Fact]
    public void Compose_DrawsInsideFrame_LeavesOtherPixelsUntouched_AndNoTextIsNoOp()
    {
        var page = PageBitmap.Solid(400, 200, 30, 200, 120);
        Assert.Same(page, TextPainter.Compose(page, [], 400, 200));
        Assert.Same(page, TextPainter.Compose(page, [new TextBox(Guid.NewGuid(), new NormRect(0, 0, 1, 1), "")], 400, 200));

        var r = new NormRect(0.1, 0.1, 0.5, 0.5);
        var text = new TextBox(Guid.NewGuid(), r, "■■", new TextStyle { FontSizePt = 12, VAlign = TextVAlign.Top });
        var outBmp = TextPainter.Compose(page, [text], 400, 200);
        Assert.NotSame(page, outBmp);
        Assert.True(page.GetPixel(60, 30) == (30, 200, 120, 255));   // 元は書き換えない
        var frame = CoordinateSpace.ToPixel(r, 400, 200);
        var inside = 0;
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 400; x++)
            {
                var changed = outBmp.GetPixel(x, y) != page.GetPixel(x, y);
                var inFrame = x >= frame.X - 1 && x < frame.Right + 1 && y >= frame.Y - 1 && y < frame.Bottom + 1;
                if (changed && inFrame) inside++;
                Assert.False(changed && !inFrame, $"枠の外の画素が変わった: {x},{y}");
            }
        Assert.True(inside > 100, $"枠の中に文字のインクがある: {inside}");
    }

    [Fact]
    public void Compose_ScalesToThumbnail()
    {
        // 縮小画像（一覧のサムネイル）にも同じ位置に描く（基準の大きさで配置して縮尺を掛ける）。
        var r = new NormRect(0.5, 0.5, 0.4, 0.4);
        var text = new TextBox(Guid.NewGuid(), r, "■■■", new TextStyle { FontSizePt = 30, VAlign = TextVAlign.Top });
        var small = TextPainter.Compose(PageBitmap.Solid(200, 100, 255, 255, 255), [text], 2000, 1000);
        var ink = InkBounds(small)!.Value;
        Assert.True(ink.X >= 99 && ink.Y >= 49, $"枠の左上より右下: {ink}");
    }

    [Fact]
    public void MissingFont_DoesNotCrash_AndStillDraws()
    {
        var metrics = new WpfTextMetrics("NoSuchFont Fuseo Test", "NoSuchFont Fuseo Test 2");
        Assert.False(metrics.IsInstalled(TextFont.Gothic));
        Assert.True(metrics.Ascent(TextFont.Gothic, 100) > 0);
        Assert.True(metrics.Descent(TextFont.Mincho, 100) >= 0);
        Assert.True(metrics.Advance("あA", TextFont.Gothic, 100) > 0);
        var text = new TextBox(Guid.NewGuid(), new NormRect(0.05, 0.1, 0.9, 0.8), "代替で描く ABC",
                               new TextStyle { FontSizePt = 20, LetterSpacing = 0.2 });
        var outBmp = TextPainter.Compose(PageBitmap.Solid(600, 200, 255, 255, 255), [text], 600, 200, metrics);
        Assert.NotNull(InkBounds(outBmp));
    }

    [Fact]
    public void LetterSpacing_And_Cells_RenderWithoutError_AndCellsIgnoreSpacing()
    {
        var r = new NormRect(0.05, 0.2, 0.9, 0.6);
        PageBitmap Render(double ls) => TextPainter.Compose(PageBitmap.Solid(600, 150, 255, 255, 255),
            [new TextBox(Guid.NewGuid(), r, "1234", new TextStyle { Cells = 4, FontSizePt = 20, LetterSpacing = ls })], 600, 150);
        Assert.Equal(Render(0).Pixels, Render(1.5).Pixels);   // マス目では字間を使わない（出力が同じ）

        var plain = InkBounds(TextPainter.Compose(PageBitmap.Solid(600, 150, 255, 255, 255),
            [new TextBox(Guid.NewGuid(), r, "ABCDE", new TextStyle { FontSizePt = 12 })], 600, 150))!.Value;
        var spaced = InkBounds(TextPainter.Compose(PageBitmap.Solid(600, 150, 255, 255, 255),
            [new TextBox(Guid.NewGuid(), r, "ABCDE", new TextStyle { FontSizePt = 12, LetterSpacing = 0.5 })], 600, 150))!.Value;
        var kern = 12 * 300.0 / 72 * 0.5;
        Assert.InRange(spaced.W - plain.W, kern * 4 - 6, kern * 4 + 6);   // 字間 4 か所ぶん広がる
    }

    /// <summary>画面のキャンバス（MaskOverlay）と書き出しの焼き込みで、文字が同じ位置に出ること（上下も反転していない）。</summary>
    [Fact]
    public void CanvasOverlay_InksSamePlaceAsBurnIn()
    {
        const int w = 300, h = 200;
        var box = TextGeometry.FromPx(new PxRect(20, 20, 200, 60), w, h);
        var page = new PageState(PageBitmap.Solid(w, h, 255, 255, 255), "a.png", 0, false);
        var id = page.BeginNewText(box, new TextStyle { FontSizePt = 8, VAlign = TextVAlign.Top });
        page.SetEditingText("■■■");
        page.CommitTextEditing();

        var burned = new MaskRasterizer().BurnIn(TextPainter.Compose(page.Base, page.Texts, w, h), ConfirmedMasks.None).Bitmap;
        var shown = RunSta(() =>
        {
            // キャンバスと同じ形: 白の下地の上に、画像の画素数の大きさの MaskOverlay を重ねる。
            var overlay = new MaskOverlay { Page = page, Width = w, Height = h, ShowTextFrames = false };
            var host = new System.Windows.Controls.Grid { Background = Brushes.White, Width = w, Height = h };
            host.Children.Add(overlay);
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            return new PageBitmap(w, h, px);
        });
        var a = InkBounds(burned)!.Value;
        var b = InkBounds(shown)!.Value;
        Assert.True(a.Y < 60, $"枠の上寄り（左上原点）に出る: {a}");
        Assert.InRange(b.X, a.X - 2, a.X + 2);
        Assert.InRange(b.Y, a.Y - 2, a.Y + 2);   // 上下が反転していない
        Assert.InRange(b.Right, a.Right - 2, a.Right + 2);
        Assert.InRange(b.Bottom, a.Bottom - 2, a.Bottom + 2);
        Assert.Equal(id, page.Texts.Single().Id);
    }

    [Fact]
    public async Task GridThumbnail_IncludesTexts()
    {
        var vm = await VmWithPages(1);
        vm.IsGridView = true;
        await vm.WhenGridThumbnailsReadyAsync();
        var before = vm.Pages[0].GridThumbnail!;
        vm.IsGridView = false;
        vm.ApplyTextStyle(s => s with { FontSizePt = 72 });
        AddText(vm, "■■", new NormRect(0, 0, 1, 1));
        vm.IsGridView = true;
        await vm.WhenGridThumbnailsReadyAsync();
        var after = vm.Pages[0].GridThumbnail!;
        Assert.NotNull(InkBounds(ToPage(after)));
        Assert.Null(InkBounds(ToPage(before)));
    }

    // ---- 吸い付き ----

    [Fact]
    public void SnapMove_AlignsLeftRightAndRowCenter_WithinThreshold()
    {
        var other = new PxRect(100, 200, 80, 20);   // midY = 210
        var r = new PxRect(103, 197, 50, 20);       // 左端 3px・中央 3px ずれ
        var res = TextSnap.SnapMove(r, [other], 4);
        Assert.Equal(100, res.Rect.X);
        Assert.Equal(210, res.Rect.MidY);
        Assert.Equal(new HashSet<TextSnap.Guide> { new(TextSnap.Axis.Vertical, 100), new(TextSnap.Axis.Horizontal, 210) },
                     res.Guides.ToHashSet());

        var right = new PxRect(128, 300, 50, 20);   // 右端 178 → 180 へ
        Assert.Equal(180, TextSnap.SnapMove(right, [other], 4).Rect.Right);

        var far = new PxRect(110, 300, 50, 20);
        var none = TextSnap.SnapMove(far, [other], 4);
        Assert.Equal(far, none.Rect);   // 離れていれば吸い付かない
        Assert.Empty(none.Guides);
    }

    [Fact]
    public void SnapResize_OnlyMovesTheDraggedEdge()
    {
        var other = new PxRect(100, 0, 80, 20);
        var r = new PxRect(50, 100, 128, 30);   // 右端 178
        var res = TextSnap.SnapResize(r, false, true, [other], 4);
        Assert.Equal(50, res.Rect.X);
        Assert.Equal(180, res.Rect.Right);
        Assert.Equal(30, res.Rect.H);
        Assert.Equal(r, TextSnap.SnapResize(r, true, false, [other], 4).Rect);   // 左端は遠いので吸い付かない
    }

    [Fact]
    public void Handles_HitAndResize_KeepOppositeEdge_AndMinimumSize()
    {
        var f = new PxRect(100, 100, 200, 50);
        Assert.Equal(TextHandle.BottomRight, TextGeometry.HandleAt(f, 302, 151, 6));
        Assert.Equal(TextHandle.Top, TextGeometry.HandleAt(f, 200, 98, 6));
        Assert.Null(TextGeometry.HandleAt(f, 200, 125, 6));
        var left = TextGeometry.Resize(f, TextHandle.Left, 30, 99, 1000, 1000);
        Assert.Equal(new PxRect(130, 100, 170, 50), left);   // 縦は動かない・右端は固定
        var tiny = TextGeometry.Resize(f, TextHandle.BottomRight, -1000, -1000, 1000, 1000);
        Assert.Equal(TextGeometry.MinFramePx, tiny.W);
        Assert.Equal(TextGeometry.MinFramePx, tiny.H);
        var outside = TextGeometry.Resize(f, TextHandle.TopLeft, -1000, -1000, 1000, 1000);
        Assert.Equal((0.0, 0.0), (outside.X, outside.Y));   // ページの内側で止まる
    }

    // ---- 補助 ----

    private static T RunSta<T>(Func<T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
        return result;
    }

    private static PageBitmap ToPage(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[conv.PixelWidth * conv.PixelHeight * 4];
        conv.CopyPixels(px, conv.PixelWidth * 4, 0);
        return new PageBitmap(conv.PixelWidth, conv.PixelHeight, px);
    }

    /// <summary>暗い画素（RGB の和 &lt; 300）の外接矩形（左上原点の px）。無ければ null。</summary>
    private static PxRect? InkBounds(PageBitmap b)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var (bl, g, r, _) = b.GetPixel(x, y);
                if (bl + g + r < 300)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        return maxX < 0 ? null : new PxRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
