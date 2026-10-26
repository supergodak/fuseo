using Fuseo.App.State;
using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Export;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Xunit;

namespace Fuseo.App.Tests;

public class MainViewModelTests
{
    private readonly FakePageSource _source = new();
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel NewVm() =>
        new(_source, new MaskRasterizer(), new Exporter(), PresetStore.LoadAll(), _dialogs, () => new DateTime(2026, 10, 11));

    private async Task<MainViewModel> VmWithPages(params string[] names)
    {
        var vm = NewVm();
        await vm.ImportAsync(names);
        return vm;
    }

    [Fact]
    public async Task Import_AppendsPages_SelectsFirstNew_AndNumbers()
    {
        var vm = await VmWithPages("a.png", "b.png");
        await vm.ImportAsync(["c.png"]);
        Assert.Equal(["a.png", "b.png", "c.png"], vm.Pages.Select(p => p.State.SourceName));
        Assert.Equal([1, 2, 3], vm.Pages.Select(p => p.Number));
        Assert.Same(vm.Pages[2], vm.SelectedPage);
        Assert.True(vm.HasPages);
        Assert.False(vm.IsBusy);
        // 合計 50 ページの上限は開いているページも数える（取り込み側に今のページ数を渡す）
        Assert.Equal([0, 2], _source.PagesAlreadyOpen);
    }

    [Fact]
    public async Task Import_Failure_ShowsError_AndKeepsExistingPages()
    {
        var vm = await VmWithPages("a.png");
        _source.Throw = new NotSupportedException("broken.xyz");
        await vm.ImportAsync(["broken.xyz"]);
        Assert.Single(vm.Pages);
        var err = Assert.Single(_dialogs.Errors);
        Assert.Contains("broken.xyz", err.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Import_Cancelled_IsSilent()
    {
        var vm = NewVm();
        _source.Throw = new OperationCanceledException();
        await vm.ImportAsync(["locked.pdf"]);
        Assert.Empty(vm.Pages);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task OpenFilesCommand_UsesDialogPaths()
    {
        var vm = NewVm();
        _dialogs.OpenFiles = ["x.jpg", "y.jpg"];
        vm.OpenFilesCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(["x.jpg", "y.jpg"], Assert.Single(_source.Calls));
    }

    [Fact]
    public async Task MoveAndRemove_ReorderAndRenumber()
    {
        var vm = await VmWithPages("a", "b", "c");
        vm.SelectedPage = vm.Pages[0];
        Assert.False(vm.MovePageUpCommand.CanExecute(null));
        Assert.True(vm.MovePageDownCommand.CanExecute(null));
        vm.MovePageDownCommand.Execute(null);
        Assert.Equal(["b", "a", "c"], vm.Pages.Select(p => p.State.SourceName));
        Assert.Equal("a", vm.SelectedPage!.State.SourceName);
        Assert.Equal(2, vm.SelectedPage.Number);

        vm.SelectedPage = vm.Pages[2];
        Assert.False(vm.MovePageDownCommand.CanExecute(null));
        vm.MovePageUpCommand.Execute(null);
        Assert.Equal(["b", "c", "a"], vm.Pages.Select(p => p.State.SourceName));

        vm.SelectedPage = vm.Pages[2];
        vm.RemovePageCommand.Execute(null);
        Assert.Equal(["b", "c"], vm.Pages.Select(p => p.State.SourceName));
        Assert.Equal([1, 2], vm.Pages.Select(p => p.Number));
        Assert.Equal("c", vm.SelectedPage!.State.SourceName); // 末尾を消したら一つ前を選ぶ

        vm.RemovePageCommand.Execute(null);
        vm.RemovePageCommand.Execute(null);
        Assert.Empty(vm.Pages);
        Assert.Null(vm.SelectedPage);
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task UndoRedo_ArePerPage()
    {
        var vm = await VmWithPages("a", "b");
        vm.SelectedPage = vm.Pages[0];
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        vm.SelectedPage = vm.Pages[1];
        Assert.False(vm.UndoCommand.CanExecute(null)); // ページ b には履歴が無い
        vm.AddStroke([new NormPoint(0.5, 0.5), new NormPoint(0.6, 0.5)]);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Pages[1].State.Manual.Strokes);
        Assert.Single(vm.Pages[0].State.Manual.Rects);    // a は影響を受けない
        Assert.True(vm.RedoCommand.CanExecute(null));
        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Pages[1].State.Manual.Strokes);
    }

    [Fact]
    public async Task TinyRects_AreIgnored_AndBrushUsesSelectedWidth()
    {
        var vm = await VmWithPages("a");
        vm.AddRect(new NormRect(0.1, 0.1, 0.001, 0.2));
        Assert.Empty(vm.SelectedPage!.State.Manual.Rects);

        vm.BrushSize = BrushSize.Thick;
        vm.AddStroke([new NormPoint(0.2, 0.2)]);
        Assert.Equal(MainViewModel.BrushWidthFor(BrushSize.Thick), vm.SelectedPage.State.Manual.Strokes[0].Width);
        Assert.True(MainViewModel.BrushWidthFor(BrushSize.Thin) < MainViewModel.BrushWidthFor(BrushSize.Medium));
        Assert.True(MainViewModel.BrushWidthFor(BrushSize.Medium) < MainViewModel.BrushWidthFor(BrushSize.Thick));
    }

    [Fact]
    public async Task SelectAndDelete_RemovesRect_OrTurnsCandidateOff()
    {
        var vm = await VmWithPages("a");
        var page = vm.SelectedPage!.State;
        var cand = PageStateTests.Cand(new NormRect(0.6, 0.6, 0.3, 0.3), true);
        page.ReplaceCandidates([cand]);
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        vm.Tool = EditorTool.Select;

        Assert.Equal(MaskSelection.Rect(0), vm.SelectAt(new NormPoint(0.15, 0.15)));
        vm.DeleteSelectionCommand.Execute(null);
        Assert.Empty(page.Manual.Rects);

        Assert.Equal(MaskSelection.Candidate(cand.Id), vm.SelectAt(new NormPoint(0.7, 0.7)));
        vm.DeleteSelectionCommand.Execute(null);
        Assert.Single(page.Candidates);              // 候補は一覧に残る
        Assert.False(page.Candidates[0].IsOn);       // OFF になる
        Assert.Null(vm.SelectAt(new NormPoint(0.01, 0.99)));
        Assert.False(vm.DeleteSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task MoveRect_IsOneUndoStep()
    {
        var vm = await VmWithPages("a");
        vm.AddRect(new NormRect(0.1, 0.1, 0.2, 0.2));
        vm.MoveRect(0, new NormRect(0.4, 0.4, 0.2, 0.2));
        Assert.Equal(new NormRect(0.4, 0.4, 0.2, 0.2), vm.SelectedPage!.State.Manual.Rects[0]);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new NormRect(0.1, 0.1, 0.2, 0.2), vm.SelectedPage.State.Manual.Rects[0]);
    }

    [Fact]
    public async Task CandidateList_TogglesPageCandidate()
    {
        var vm = await VmWithPages("a");
        var cand = PageStateTests.Cand(new NormRect(0.6, 0.6, 0.3, 0.3), false);
        vm.SelectedPage!.State.ReplaceCandidates([cand]);
        vm.SelectedPage = vm.Pages[0]; // 同じページ → 再構築されないので明示的に呼ぶ
        vm.SetCandidateOn(cand.Id, true);
        var item = Assert.Single(vm.CandidateItems);
        Assert.True(item.IsOn);
        item.IsOn = false;
        Assert.False(vm.SelectedPage.State.Candidates[0].IsOn);
        Assert.False(vm.HasNoCandidates);
    }

    [Fact]
    public async Task DocumentType_DefaultsToGeneric_AndDrivesChecklistAndBanner()
    {
        var vm = await VmWithPages("a");
        Assert.Equal(DocumentType.Generic, vm.SelectedDocumentType!.Type);
        Assert.Equal(DocumentType.Generic, vm.DocumentTypes[0].Type);
        Assert.Equal(9, vm.DocumentTypes.Count);
        Assert.False(vm.IsBannerVisible); // generic は注意書きなし

        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardBack);
        Assert.Equal(DocumentType.MyNumberCardBack, vm.SelectedPage!.State.Type);
        Assert.True(vm.IsBannerVisible);
        Assert.NotEmpty(vm.BannerWarnings);
        Assert.Contains(vm.Checklist, c => c.IsQrCandidate);
        Assert.Contains(vm.Checklist, c => c.IsManualGuide && !c.HasCheckBox);

        vm.DismissBannerCommand.Execute(null);
        Assert.False(vm.IsBannerVisible);
    }

    [Fact]
    public async Task ChecklistCheck_IsStoredOnPage()
    {
        var vm = await VmWithPages("a", "b");
        vm.SelectedDocumentType = vm.DocumentTypes.Single(t => t.Type == DocumentType.MyNumberCardFront);
        var item = vm.Checklist.First(c => c.HasCheckBox);
        item.IsChecked = true;
        Assert.Contains(item.Row.RuleId, vm.Pages[0].State.CheckedRuleIds);
        vm.SelectedPage = vm.Pages[1];
        vm.SelectedPage = vm.Pages[0];
        Assert.True(vm.Checklist.First(c => c.Row.RuleId == item.Row.RuleId).IsChecked);
    }

    [Fact]
    public async Task Export_OpensSheet_WithPagesInRailOrder()
    {
        var vm = await VmWithPages("a", "b");
        vm.SelectedPage = vm.Pages[1];
        vm.MovePageUpCommand.Execute(null);
        ExportViewModel? shown = null;
        _dialogs.OnShowExport = e => shown = e;
        vm.ExportCommand.Execute(null);
        Assert.NotNull(shown);
        await shown!.PrepareAsync();
        Assert.Equal(2, shown.Previews.Count);
        Assert.Equal(vm.Pages[0].State.Base.Width, shown.Previews[0].Page.Bitmap.Width);
    }
}
