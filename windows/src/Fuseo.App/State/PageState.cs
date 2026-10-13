using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>
/// 編集状態の 1 時点（Undo/Redo の単位）。**一度作ったら中身を変えない**（リストは共有されうるため、
/// 変更は必ず新しい <see cref="ManualMask"/>／候補リストを作って差し替える）。
/// - <see cref="Rotation"/>: 元画像からの時計回り 90° の回数（0..3）。
/// - 手動マスク・候補の座標は**常にこの Rotation で回した画像の上**の正規化座標（左上原点）。
/// - <see cref="Type"/>: 書類種別。種別を変えると候補（defaultOn）が作り直されるので、候補と一緒に戻せるようここに持つ。
/// </summary>
public sealed record EditSnapshot(
    ManualMask Manual, IReadOnlyList<MaskCandidate> Candidates, int Rotation = 0,
    DocumentType Type = DocumentType.Generic)
{
    public static readonly EditSnapshot Empty = new(new ManualMask(), []);
}

/// <summary>
/// 1 ページの状態（docs/windows-design.md §3.4 の PageState 相当）。UI 非依存（WPF 型を持たない）で、
/// 後で自動保存（W5・fuseo-win/1）の JSON にしやすいよう、値はすべて Core の型か基本型で持つ。
/// - 取り込んだ画像 <see cref="Original"/> は不変（EXIF・PDF の回転は取り込み時に焼き込み済み・§3.3）。
/// - <see cref="Base"/> は「現在の <see cref="Rotation"/> で回した画像」。表示・当たり判定・焼き込み・書き出しはすべてこれを使う。
/// - マスク座標は <see cref="Base"/> 上の正規化座標・左上原点のみ。回転するときは画像と**同じ回数だけ**マスクも回す
///   （<see cref="QuarterTurn"/> 経由のみ）。
/// - Undo/Redo はページごと（手動マスク・候補の ON/OFF・回転・種別を 1 つのスナップショットで持つ）。
/// - 自動検出の生の箱 <see cref="RawDetections"/> は padding 前・回転 0（<see cref="Original"/> 基準）で持ち、
///   種別を変えたら現在の回転まで回してから候補を作り直す。
/// </summary>
public sealed class PageState
{
    /// <summary>1 ページあたりの Undo 履歴の上限（古いものから捨てる）。</summary>
    public const int MaxHistory = 200;

    private readonly LinkedList<EditSnapshot> _undo = new();
    private readonly Stack<EditSnapshot> _redo = new();
    private EditSnapshot _current = EditSnapshot.Empty;
    private readonly object _baseLock = new();
    private (int Rotation, PageBitmap Bitmap)? _rotatedCache;

    public PageState(PageBitmap original, string sourceName, int sourcePageIndex, bool fromPdf)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        SourceName = sourceName;
        SourcePageIndex = sourcePageIndex;
        FromPdf = fromPdf;
    }

    public static PageState From(SourcePage page) =>
        new(page.Bitmap, page.SourceName, page.PageIndex, page.FromPdf);

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>取り込んだままの画像（不変・回転 0）。</summary>
    public PageBitmap Original { get; }

    /// <summary>
    /// 現在の <see cref="Rotation"/> で回した画像（マスク座標の基準）。回転 0 なら <see cref="Original"/> そのもの。
    /// 回転した画像は直近の 1 つだけキャッシュする（50 ページ × 4 向きを抱えない）。**書き換え禁止**。
    /// </summary>
    public PageBitmap Base
    {
        get
        {
            var rotation = Rotation;
            if (rotation == 0) return Original;
            lock (_baseLock)
            {
                if (_rotatedCache is { } c && c.Rotation == rotation) return c.Bitmap;
                var bmp = QuarterTurn.Rotate(Original, rotation);
                _rotatedCache = (rotation, bmp);
                return bmp;
            }
        }
    }
    public string SourceName { get; }
    public int SourcePageIndex { get; }
    public bool FromPdf { get; }

    /// <summary>
    /// 書類種別（既定 generic）。変えても手動マスクは消さない。変更は <see cref="ChangeType"/> で行う
    /// （候補の作り直しと一緒に Undo できる 1 回の変更として記録する）。
    /// </summary>
    public DocumentType Type => _current.Type;

    /// <summary>手動回転（時計回り 90° の回数・0..3）。W5 の JSON（manualRotation）の項目。</summary>
    public int Rotation => _current.Rotation;

    /// <summary>自動検出の生の箱（padding 前・回転 0 = <see cref="Original"/> 基準・左上原点）。</summary>
    public IReadOnlyList<NormRect> RawDetections { get; private set; } = [];

    /// <summary>生の箱を出した検出器（候補の Detector 欄に入れる）。</summary>
    public DetectorId? RawDetector { get; private set; }

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

    /// <summary>候補を差し替える。Undo で戻せる。</summary>
    public void ReplaceCandidates(IEnumerable<MaskCandidate> candidates) =>
        Commit(_current with { Candidates = candidates.ToList() });

    /// <summary>
    /// 取り込み時の検出結果を入れ、現在の種別のプリセットから初期候補を作る。
    /// **Undo 履歴に積まない**（Undo で初期候補が消えないように）。取り込み直後（編集前）に 1 回だけ呼ぶ想定。
    /// </summary>
    public void InitializeDetections(DetectorId detector, IReadOnlyList<NormRect> rawBoxes, DocumentPreset? preset, bool english = false)
    {
        RawDetector = detector;
        RawDetections = rawBoxes.ToList();
        _current = _current with { Candidates = CandidateFactory.Build(preset, detector, RawDetections, Rotation, english) };
    }

    /// <summary>
    /// 種別を変える。生の検出箱から新しい種別の候補（その種別の defaultOn）を作り直し、種別と一緒に 1 回の変更として Commit する。
    /// 手動マスク・回転はそのまま。同じ種別なら何もしない。
    /// </summary>
    public void ChangeType(DocumentType type, DocumentPreset? preset, bool english = false)
    {
        if (type == Type) return;
        // 検出していないページ（検出器なし・検出失敗）は候補に触らない。
        var candidates = RawDetector is { } d
            ? CandidateFactory.Build(preset, d, RawDetections, Rotation, english)
            : Candidates;
        Commit(_current with { Type = type, Candidates = candidates });
    }

    /// <summary>時計回りに 90° 回す（画像・手動マスク・候補を同じ回数だけ回す）。Undo で戻せる。</summary>
    public void RotateClockwise() => RotateBy(+1);

    /// <summary>反時計回りに 90° 回す。Undo で戻せる。</summary>
    public void RotateCounterClockwise() => RotateBy(-1);

    private void RotateBy(int turns)
    {
        var next = _current with
        {
            Manual = QuarterTurn.Rotate(Manual, turns),
            Candidates = Candidates.Select(c => QuarterTurn.Rotate(c, turns)).ToList(),
            Rotation = QuarterTurn.Normalize(Rotation + turns),
        };
        Commit(next);
    }

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
