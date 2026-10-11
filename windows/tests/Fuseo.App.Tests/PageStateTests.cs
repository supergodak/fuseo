using Fuseo.App.State;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;
using Xunit;

namespace Fuseo.App.Tests;

public class PageStateTests
{
    internal static PageState NewPage() => new(PageBitmap.Solid(100, 50, 255, 255, 255), "a.png", 0, false);

    internal static MaskCandidate Cand(NormRect box, bool on, string id = "r") =>
        new(Guid.NewGuid(), id, "QR", box, CandidateSource.Detector, DetectorId.QrBarcode, 0.9f, on, "basis");

    [Fact]
    public void ConfirmedMasks_AreOnCandidatesPlusManualRectsAndStrokes()
    {
        var p = NewPage();
        var on = Cand(new NormRect(0.1, 0.1, 0.2, 0.2), true);
        var off = Cand(new NormRect(0.5, 0.5, 0.2, 0.2), false);
        p.ReplaceCandidates([on, off]);
        var r = new NormRect(0.6, 0.1, 0.1, 0.1);
        p.AddRect(r);
        var stroke = new BrushStroke([new NormPoint(0.2, 0.8), new NormPoint(0.4, 0.8)], 0.03);
        p.AddStroke(stroke);

        var m = p.BuildConfirmedMasks();
        Assert.Equal([on.Box, r], m.Rects);
        Assert.Equal([stroke], m.Strokes);
        Assert.DoesNotContain(off.Box, m.Rects);
        Assert.Equal(3, p.MaskCount);
    }

    [Fact]
    public void UndoRedo_RestoresEachStep_AndNewEditClearsRedo()
    {
        var p = NewPage();
        var a = new NormRect(0.1, 0.1, 0.1, 0.1);
        var b = new NormRect(0.3, 0.3, 0.1, 0.1);
        p.AddRect(a);
        p.AddRect(b);
        Assert.True(p.Undo());
        Assert.Equal([a], p.Manual.Rects);
        Assert.True(p.Undo());
        Assert.Empty(p.Manual.Rects);
        Assert.False(p.Undo());
        Assert.True(p.Redo());
        Assert.Equal([a], p.Manual.Rects);
        p.AddStroke(new BrushStroke([new NormPoint(0.5, 0.5)], 0.03));
        Assert.False(p.CanRedo);
        Assert.Equal([a], p.Manual.Rects);
        Assert.Single(p.Manual.Strokes);
    }

    [Fact]
    public void Undo_DoesNotMutateEarlierSnapshots()
    {
        var p = NewPage();
        p.AddRect(new NormRect(0.1, 0.1, 0.1, 0.1));
        var before = p.Manual;
        p.ReplaceRect(0, new NormRect(0.2, 0.2, 0.1, 0.1));
        p.RemoveRect(0);
        Assert.Single(before.Rects);
        Assert.Equal(new NormRect(0.1, 0.1, 0.1, 0.1), before.Rects[0]);
        p.Undo();
        Assert.Equal(new NormRect(0.2, 0.2, 0.1, 0.1), p.Manual.Rects[0]);
        p.Undo();
        Assert.Equal(new NormRect(0.1, 0.1, 0.1, 0.1), p.Manual.Rects[0]);
    }

    [Fact]
    public void CandidateToggle_IsUndoable()
    {
        var p = NewPage();
        var c = Cand(new NormRect(0.1, 0.1, 0.2, 0.2), true);
        p.ReplaceCandidates([c]);
        p.SetCandidateOn(c.Id, false);
        Assert.Empty(p.BuildConfirmedMasks().Rects);
        p.Undo();
        Assert.Equal([c.Box], p.BuildConfirmedMasks().Rects);
    }

    [Fact]
    public void NoOpEdits_DoNotPushHistory()
    {
        var p = NewPage();
        var c = Cand(new NormRect(0.1, 0.1, 0.2, 0.2), true);
        p.ReplaceCandidates([c]);
        p.Undo(); p.Redo();
        p.SetCandidateOn(c.Id, true); // 既に ON
        p.RemoveRect(5);              // 範囲外
        p.Undo();
        Assert.Empty(p.Candidates);
        Assert.False(p.CanUndo);
    }

    [Fact]
    public void History_IsCapped()
    {
        var p = NewPage();
        for (var i = 0; i < PageState.MaxHistory + 20; i++) p.AddRect(new NormRect(0, 0, 0.01, 0.01));
        var undos = 0;
        while (p.Undo()) undos++;
        Assert.Equal(PageState.MaxHistory, undos);
        Assert.Equal(20, p.Manual.Rects.Count);
    }

    [Fact]
    public void HitTest_PrefersTopmostManualRect_ThenCandidates()
    {
        var p = NewPage();
        var c = Cand(new NormRect(0, 0, 1, 1), true);
        p.ReplaceCandidates([c]);
        p.AddRect(new NormRect(0.1, 0.1, 0.5, 0.5));
        p.AddRect(new NormRect(0.2, 0.2, 0.5, 0.5));
        Assert.Equal(MaskSelection.Rect(1), MaskHitTest.Hit(p, new NormPoint(0.3, 0.3)));
        Assert.Equal(MaskSelection.Rect(0), MaskHitTest.Hit(p, new NormPoint(0.15, 0.15)));
        Assert.Equal(MaskSelection.Candidate(c.Id), MaskHitTest.Hit(p, new NormPoint(0.9, 0.9)));
    }

    [Fact]
    public void Offset_KeepsSizeAndStaysInsideImage()
    {
        var r = new NormRect(0.7, 0.8, 0.2, 0.1);
        Assert.Equal(new NormRect(0.8, 0.9, 0.2, 0.1), MaskHitTest.Offset(r, 0.5, 0.5));
        Assert.Equal(new NormRect(0, 0, 0.2, 0.1), MaskHitTest.Offset(r, -2, -2));
    }
}
