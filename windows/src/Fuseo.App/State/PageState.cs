using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>
/// 編集状態の 1 時点（Undo/Redo の単位）。**一度作ったら中身を変えない**（リストは共有されうるため、
/// 変更は必ず新しい <see cref="ManualMask"/>／候補リストを作って差し替える）。
/// </summary>
public sealed record EditSnapshot(ManualMask Manual, IReadOnlyList<MaskCandidate> Candidates)
{
    public static readonly EditSnapshot Empty = new(new ManualMask(), []);
}

/// <summary>
/// 1 ページの状態（docs/windows-design.md §3.4 の PageState 相当）。UI 非依存（WPF 型を持たない）で、
/// 後で自動保存（W5・fuseo-win/1）の JSON にしやすいよう、値はすべて Core の型か基本型で持つ。
/// - 基準ビットマップ <see cref="Base"/> は取り込み時に回転焼き込み済み（§3.3）。編集で書き換えない。
/// - マスク座標は <see cref="Base"/> 上の正規化座標・左上原点のみ。
/// - Undo/Redo はページごと（手動マスクと候補の ON/OFF を 1 つのスナップショットで持つ）。
/// </summary>
public sealed class PageState
{
    /// <summary>1 ページあたりの Undo 履歴の上限（古いものから捨てる）。</summary>
    public const int MaxHistory = 200;

    private readonly LinkedList<EditSnapshot> _undo = new();
    private readonly Stack<EditSnapshot> _redo = new();
    private EditSnapshot _current = EditSnapshot.Empty;

    public PageState(PageBitmap baseBitmap, string sourceName, int sourcePageIndex, bool fromPdf)
    {
        Base = baseBitmap ?? throw new ArgumentNullException(nameof(baseBitmap));
        SourceName = sourceName;
        SourcePageIndex = sourcePageIndex;
        FromPdf = fromPdf;
    }

    public static PageState From(SourcePage page) =>
        new(page.Bitmap, page.SourceName, page.PageIndex, page.FromPdf);

    public Guid Id { get; } = Guid.NewGuid();
    public PageBitmap Base { get; }
    public string SourceName { get; }
    public int SourcePageIndex { get; }
    public bool FromPdf { get; }

    /// <summary>書類種別（既定 generic）。変えても手動マスクは消さない。</summary>
    public DocumentType Type { get; set; } = DocumentType.Generic;

    /// <summary>
    /// 手動回転（90°×n）。v0.1 の W3 では未実装で常に 0（回転は取り込み時に焼き込み済み）。
    /// JSON（manualRotation）の項目として残す。
    /// </summary>
    public int Rotation => 0;

    public ManualMask Manual => _current.Manual;
    public IReadOnlyList<MaskCandidate> Candidates => _current.Candidates;

    /// <summary>チェックリストで利用者が「塗った」と印を付けたルール id（編集の Undo 対象外）。</summary>
    public ISet<string> CheckedRuleIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int ManualMaskCount => Manual.Rects.Count + Manual.Strokes.Count;
    public int ActiveCandidateCount => Candidates.Count(c => c.IsOn);
    public int MaskCount => ManualMaskCount + ActiveCandidateCount;

    public void AddRect(NormRect rect) =>
        Commit(_current with { Manual = Manual with { Rects = [.. Manual.Rects, rect] } });

    public void AddStroke(BrushStroke stroke) =>
        Commit(_current with { Manual = Manual with { Strokes = [.. Manual.Strokes, stroke] } });

    public void RemoveRect(int index)
    {
        if (index < 0 || index >= Manual.Rects.Count) return;
        var rects = new List<NormRect>(Manual.Rects);
        rects.RemoveAt(index);
        Commit(_current with { Manual = Manual with { Rects = rects } });
    }

    public void ReplaceRect(int index, NormRect rect)
    {
        if (index < 0 || index >= Manual.Rects.Count || Manual.Rects[index] == rect) return;
        var rects = new List<NormRect>(Manual.Rects) { [index] = rect };
        Commit(_current with { Manual = Manual with { Rects = rects } });
    }

    public void SetCandidateOn(Guid candidateId, bool isOn)
    {
        var i = IndexOfCandidate(candidateId);
        if (i < 0 || Candidates[i].IsOn == isOn) return;
        var list = new List<MaskCandidate>(Candidates) { [i] = Candidates[i] with { IsOn = isOn } };
        Commit(_current with { Candidates = list });
    }

    /// <summary>候補を差し替える（W4 の検出結果を入れる口）。Undo で戻せる。</summary>
    public void ReplaceCandidates(IEnumerable<MaskCandidate> candidates) =>
        Commit(_current with { Candidates = candidates.ToList() });

    public int IndexOfCandidate(Guid id)
    {
        for (var i = 0; i < Candidates.Count; i++)
            if (Candidates[i].Id == id) return i;
        return -1;
    }

    public bool Undo()
    {
        if (_undo.Last is not { } last) return false;
        _undo.RemoveLast();
        _redo.Push(_current);
        _current = last.Value;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        PushUndo(_current);
        _current = _redo.Pop();
        return true;
    }

    /// <summary>
    /// 焼き込みに渡す確定マスク = ON の候補の box ＋ 手動矩形（矩形）、手動ストローク（ストローク）。
    /// OFF の候補は含めない。最終判断は書き出しシートの確認で利用者が行う（絶対条件）。
    /// </summary>
    public ConfirmedMasks BuildConfirmedMasks()
    {
        var rects = Candidates.Where(c => c.IsOn).Select(c => c.Box).Concat(Manual.Rects).ToList();
        return new ConfirmedMasks(rects, Manual.Strokes.ToList());
    }

    private void Commit(EditSnapshot next)
    {
        PushUndo(_current);
        _redo.Clear();
        _current = next;
    }

    private void PushUndo(EditSnapshot snapshot)
    {
        _undo.AddLast(snapshot);
        while (_undo.Count > MaxHistory) _undo.RemoveFirst();
    }
}
