using Fuseo.Core.Analysis;
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
/// - <see cref="Crop"/>: 手動 4 隅切り抜き（W4.5）。**元画像（回転 0）の上**の四隅。null = 切り抜きなし。
/// - <see cref="Findings"/>: 自動検出の材料（QR・顔の生の箱は R0 = 切り抜いた画像の回転 0、OCR は OcrTurns の向き。§10.15）。
///   切り抜き・読み直しで変わるので、それらと一緒に戻せるようここに持つ（null = 空）。
/// - <see cref="TypeIsManual"/>: 利用者が種別を選んだか（true なら以後の再解析で自動判定に戻さない＝Mac の forcedType）。
/// </summary>
public sealed record EditSnapshot(
    ManualMask Manual, IReadOnlyList<MaskCandidate> Candidates, int Rotation = 0,
    DocumentType Type = DocumentType.Generic, Quad? Crop = null, PageFindings? Findings = null, bool TypeIsManual = false)
{
    public static readonly EditSnapshot Empty = new(new ManualMask(), []);
}

/// <summary>
/// 1 ページの状態（docs/windows-design.md §3.4 の PageState 相当）。UI 非依存（WPF 型を持たない）で、
/// 後で自動保存（W5・fuseo-win/1）の JSON にしやすいよう、値はすべて Core の型か基本型で持つ。
/// - 取り込んだ画像 <see cref="Original"/> は不変（EXIF・PDF の回転は取り込み時に焼き込み済み・§3.3）。
/// - <see cref="Base"/> は「<see cref="Original"/> を <see cref="Crop"/> で台形補正し（<see cref="Cropped"/>）、現在の <see cref="Rotation"/> で回した画像」。
///   表示・当たり判定・焼き込み・書き出しはすべてこれを使う（切り抜いた外側は書き出しに入らない）。
/// - マスク座標は <see cref="Base"/> 上の正規化座標・左上原点のみ。回転するときは画像と**同じ回数だけ**マスクも回す
///   （<see cref="QuarterTurn"/> 経由のみ）。
/// - Undo/Redo はページごと（手動マスク・候補の ON/OFF・回転・種別を 1 つのスナップショットで持つ）。
/// - 自動検出の材料 <see cref="Findings"/>（§10.15）から <see cref="CandidateBuilder"/> で候補を作る。
///   取り込み時の解析は Undo 履歴に積まない。切り抜き・読み直し・種別の変更はそれぞれ 1 回の変更。回転では読み直さない。
/// </summary>
public sealed class PageState
{
    /// <summary>1 ページあたりの Undo 履歴の上限（古いものから捨てる）。</summary>
    public const int MaxHistory = 200;

    private readonly LinkedList<EditSnapshot> _undo = new();
    private readonly Stack<EditSnapshot> _redo = new();
    private EditSnapshot _current = EditSnapshot.Empty;
    private readonly object _baseLock = new();
    private (Quad Crop, PageBitmap Bitmap)? _croppedCache;
    private (Quad? Crop, int Rotation, PageBitmap Bitmap)? _rotatedCache;

    public PageState(PageBitmap original, string sourceName, int sourcePageIndex, bool fromPdf, Guid? id = null,
                     Guid? sourceDocId = null)
    {
        Id = id ?? Guid.NewGuid();
        // 指定が無ければ 1 ページで 1 つの書類（取り込み・復元は必ず渡す）。
        SourceDocId = sourceDocId is { } d && d != Guid.Empty ? d : Guid.NewGuid();
        Original = original ?? throw new ArgumentNullException(nameof(original));
        SourceName = sourceName;
        SourcePageIndex = sourcePageIndex;
        FromPdf = fromPdf;
    }

    public static PageState From(SourcePage page, Guid? sourceDocId = null) =>
        new(page.Bitmap, page.SourceName, page.PageIndex, page.FromPdf, sourceDocId: sourceDocId);

    /// <summary>
    /// 書類のまとまり（W9-4・§10.17）。取り込み時に元ファイルごとに 1 つ（PDF・複数フレームの TIFF の全ページは同じ値）。
    /// 並べ替えでページが離れても書類はこの値で決まる（書類内のページ順は画面のページ順）。不変。
    /// </summary>
    public Guid SourceDocId { get; }

    /// <summary>
    /// いまの編集状態（不変の 1 時点）。編集・Undo のたびに別のインスタンスになるので、一覧のサムネイルの作り直しの判定に使う。
    /// </summary>
    public EditSnapshot Snapshot => _current;

    /// <summary>ページの id（自動保存のファイル名 pages/&lt;id&gt;.png に使う。復元時は保存時と同じ値）。</summary>
    public Guid Id { get; }

    /// <summary>
    /// 自動保存（W5）から復元する。検出は走らせない（保存した候補・生の箱をそのまま戻す）。
    /// **Undo 履歴は空**（履歴は保存しない）。座標は保存時と同じ「<paramref name="rotation"/> で回した画像の上」。
    /// OCR の結果は保存しないので、復元後は Ocr = null・OcrTurns = 0（種別を変えたときに必要なら読み直す・§10.15）。
    /// </summary>
    public static PageState Restore(
        Guid id, PageBitmap original, string sourceName, int sourcePageIndex, bool fromPdf,
        DocumentType type, int rotation, ManualMask manual, IReadOnlyList<MaskCandidate> candidates,
        IReadOnlyList<NormRect> rawQr, IEnumerable<string> checkedRuleIds,
        Quad? crop = null, IReadOnlyList<NormRect>? rawFaces = null, bool typeIsManual = false, Guid? sourceDocId = null)
    {
        if (rotation is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(rotation), "回転は 0..3");
        if (crop is { } q && !q.IsConvex) throw new ArgumentException("切り抜きの四隅が凸四角形でない", nameof(crop));
        var state = new PageState(original, sourceName, sourcePageIndex, fromPdf, id, sourceDocId);
        state._current = new EditSnapshot(
            new ManualMask { Rects = [.. manual.Rects], Strokes = [.. manual.Strokes] },
            candidates.ToList(), rotation, type, NormalizeCrop(crop),
            new PageFindings(0, null, rawQr.ToList(), (rawFaces ?? []).ToList()), typeIsManual);
        foreach (var r in checkedRuleIds) state.CheckedRuleIds.Add(r);
        return state;
    }

    /// <summary>取り込んだままの画像（不変・回転 0）。</summary>
    public PageBitmap Original { get; }

    /// <summary>
    /// <see cref="Original"/> を <see cref="Crop"/> で台形補正した画像（回転 0・検出の生の箱の基準）。切り抜きなしなら <see cref="Original"/> そのもの。
    /// 直近の 1 つだけキャッシュする。**書き換え禁止**。
    /// </summary>
    public PageBitmap Cropped => CroppedFor(Crop);

    private PageBitmap CroppedFor(Quad? crop)
    {
        if (crop is not { } q) return Original;
        lock (_baseLock)
        {
            if (_croppedCache is { } c && c.Crop == q) return c.Bitmap;
            var bmp = Perspective.Warp(Original, q);
            _croppedCache = (q, bmp);
            return bmp;
        }
    }

    /// <summary>
    /// 現在の切り抜き・<see cref="Rotation"/> の画像（マスク座標の基準）。切り抜きなし・回転 0 なら <see cref="Original"/> そのもの。
    /// 回転した画像は直近の 1 つだけキャッシュする（50 ページ × 4 向きを抱えない）。**書き換え禁止**。
    /// </summary>
    public PageBitmap Base
    {
        get
        {
            var current = _current;
            return BaseFor(current.Crop, current.Rotation);
        }
    }

    /// <summary>
    /// 指定の切り抜き・回転での基準の画像（<see cref="Base"/> と同じ作り方・同じキャッシュ）。<see cref="Snapshot"/> の値を渡せば、
    /// UI スレッドで写し取った時点の画像を別スレッドで作れる（一覧のサムネイル用）。**書き換え禁止**。
    /// </summary>
    public PageBitmap BaseFor(Quad? crop, int rotation)
    {
        var cropped = CroppedFor(crop);
        if (rotation == 0) return cropped;
        lock (_baseLock)
        {
            if (_rotatedCache is { } c && c.Crop == crop && c.Rotation == rotation) return c.Bitmap;
            var bmp = QuarterTurn.Rotate(cropped, rotation);
            _rotatedCache = (crop, rotation, bmp);
            return bmp;
        }
    }

    /// <summary>手動 4 隅切り抜き（元画像・回転 0 の上の四隅）。null = 切り抜きなし。W5 の JSON（manualQuad）の項目。</summary>
    public Quad? Crop => _current.Crop;
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

    /// <summary>自動検出の材料（§10.15）。</summary>
    public PageFindings Findings => _current.Findings ?? PageFindings.Empty;

    /// <summary>QR／バーコードの生の箱（padding 前・R0 = <see cref="Cropped"/> の回転 0・左上原点）。= <see cref="Findings"/>.Qr。</summary>
    public IReadOnlyList<NormRect> RawDetections => Findings.Qr;

    /// <summary>利用者が種別を選んだか（true なら切り抜き・読み直しで自動判定に戻さない）。</summary>
    public bool TypeIsManual => _current.TypeIsManual;

    /// <summary>
    /// ページ＝カード 1 枚か（固定領域・位置推定を候補にする条件・§10.15）: 4 隅切り抜き済み、または切り抜き後の画像の縦横比がカード。
    /// </summary>
    public bool CardFramed => CardFramedFor(Crop);

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
    /// 取り込み時の解析結果を入れ、初期の向き・種別（自動判定）と候補を決める（§10.15）。
    /// **Undo 履歴に積まない**（Undo で初期候補・自動の回転が消えないように）。取り込み直後（編集前）に 1 回だけ呼ぶ想定。
    /// <paramref name="preset"/> は <paramref name="autoType"/> のプリセット。種別は自動扱い（TypeIsManual = false）。
    /// </summary>
    public void InitializeAnalysis(PageFindings findings, int initialRotation, DocumentType autoType, DocumentPreset? preset,
                                   bool english = false)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var rotation = QuarterTurn.Normalize(initialRotation);
        var turns = rotation - Rotation;
        _current = _current with
        {
            Rotation = rotation,
            Manual = QuarterTurn.Rotate(Manual, turns),
            Type = autoType,
            TypeIsManual = false,
            Findings = findings,
            Candidates = CandidateBuilder.Build(preset, findings, rotation, CardFramedFor(Crop), english),
        };
    }

    /// <summary>
    /// 4 隅切り抜きを変える（W4.5・§10.15）。<paramref name="crop"/> は**元画像（回転 0）の上**の四隅（null・全体 = 切り抜きなし）。
    /// 画像が変わるので**手動マスクは消し**、候補は新しい画像での検出の材料 <paramref name="findings"/> から
    /// <paramref name="type"/>（呼び出し側が決める: 手動なら今の種別、自動なら判定結果）の defaultOn で作り直す。
    /// 回転はそのまま。1 回の変更として Undo で戻せる（消したマスクも戻る）。
    /// <paramref name="cropped"/> は呼び出し側が検出のために作った台形補正済みの画像（あればキャッシュに入れて作り直さない）。
    /// </summary>
    public void ApplyCrop(Quad? crop, PageFindings findings, DocumentType type, DocumentPreset? preset,
                          bool english = false, PageBitmap? cropped = null)
    {
        if (crop is { } c && !c.IsConvex) throw new ArgumentException("切り抜きの四隅が凸四角形でない", nameof(crop));
        ArgumentNullException.ThrowIfNull(findings);
        var next = NormalizeCrop(crop);
        if (next is { } q && cropped is not null)
            lock (_baseLock) _croppedCache = (q, cropped);
        var candidates = CandidateBuilder.Build(preset, findings, Rotation, CardFramedFor(next), english);
        Commit(_current with { Crop = next, Findings = findings, Type = type, Manual = new ManualMask(), Candidates = candidates });
    }

    /// <summary>
    /// 文字を読み直した結果を入れる（§10.15「文字を読み直す」）。<paramref name="type"/> は呼び出し側が決める
    /// （手動なら今の種別、自動なら判定結果）。手動マスク・回転・切り抜きはそのまま。1 回の変更として Undo で戻せる。
    /// </summary>
    public void ApplyReread(PageFindings findings, DocumentType type, DocumentPreset? preset, bool english = false)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var candidates = CandidateBuilder.Build(preset, findings, Rotation, CardFramed, english);
        Commit(_current with { Findings = findings, Type = type, Candidates = candidates });
    }

    /// <summary>切り抜きなしなら <see cref="Cropped"/> = <see cref="Original"/> なので、その縦横比で判定する。</summary>
    private bool CardFramedFor(Quad? crop) => crop is not null || CardFrame.IsCardAspect(Original.Width, Original.Height);

    /// <summary>全体と同じ四隅は「切り抜きなし」（null）にそろえる。</summary>
    private static Quad? NormalizeCrop(Quad? crop) => crop is { IsFullImage: true } ? null : crop;

    /// <summary>
    /// 種別を変える（利用者の選択＝以後は手動扱い・§10.15）。検出の材料から新しい種別の候補（その種別の defaultOn）を作り直し、
    /// 種別と一緒に 1 回の変更として Commit する。<paramref name="findings"/> を渡したら（種別を変える前に読み直した OCR）材料も差し替える。
    /// 手動マスク・回転はそのまま。同じ種別なら何もしない。
    /// </summary>
    public void ChangeType(DocumentType type, DocumentPreset? preset, bool english = false, PageFindings? findings = null)
    {
        if (type == Type) return;
        var f = findings ?? Findings;
        var candidates = CandidateBuilder.Build(preset, f, Rotation, CardFramed, english);
        Commit(_current with { Type = type, TypeIsManual = true, Findings = f, Candidates = candidates });
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
    public ConfirmedMasks BuildConfirmedMasks() => ConfirmedMasksOf(_current);

    /// <summary>ある時点（<see cref="Snapshot"/>）の確定マスク（<see cref="BuildConfirmedMasks"/> と同じ組み立て）。</summary>
    public static ConfirmedMasks ConfirmedMasksOf(EditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rects = snapshot.Candidates.Where(c => c.IsOn).Select(c => c.Box).Concat(snapshot.Manual.Rects).ToList();
        return new ConfirmedMasks(rects, snapshot.Manual.Strokes.ToList());
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
