using Fuseo.Core.Geometry;

namespace Fuseo.App.State;

public enum SelectionKind { ManualRect, Candidate }

/// <summary>選択中のマスク（手動矩形は添字、候補は Id で指す）。</summary>
public readonly record struct MaskSelection(SelectionKind Kind, int RectIndex, Guid CandidateId)
{
    public static MaskSelection Rect(int index) => new(SelectionKind.ManualRect, index, Guid.Empty);
    public static MaskSelection Candidate(Guid id) => new(SelectionKind.Candidate, -1, id);
}

/// <summary>選択ツールの当たり判定と矩形の移動（すべて正規化座標・左上原点のまま計算する）。</summary>
public static class MaskHitTest
{
    /// <summary>手前（後から足した手動矩形）を優先し、次に候補を見る。ストロークは対象外（Undo で消す）。</summary>
    public static MaskSelection? Hit(PageState page, NormPoint p)
    {
        var rects = page.Manual.Rects;
        for (var i = rects.Count - 1; i >= 0; i--)
            if (Contains(rects[i], p)) return MaskSelection.Rect(i);
        var cands = page.Candidates;
        for (var i = cands.Count - 1; i >= 0; i--)
            if (Contains(cands[i].Box, p)) return MaskSelection.Candidate(cands[i].Id);
        return null;
    }

    public static bool Contains(NormRect r, NormPoint p) =>
        p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom;

    /// <summary>矩形を平行移動する。大きさは変えず、画像の外へははみ出させない。</summary>
    public static NormRect Offset(NormRect r, double dx, double dy) =>
        new(Math.Clamp(r.X + dx, 0, Math.Max(0, 1 - r.W)), Math.Clamp(r.Y + dy, 0, Math.Max(0, 1 - r.H)), r.W, r.H);
}
