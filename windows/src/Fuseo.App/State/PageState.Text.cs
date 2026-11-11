using Fuseo.Core.Geometry;
using Fuseo.Core.Model;

namespace Fuseo.App.State;

/// <summary>
/// 文字枠の編集（文字入れ v0.3・docs/windows-design.md §11・Mac の PageState の文字枠の操作と同じ型）。
/// 作成・移動・大きさ・文字の確定・設定・削除・複製がそれぞれ 1 手（スナップショット方式・Undo/Redo は手動マスクと同じ履歴）。
/// 新しい枠は「作成＋最初の文字の確定」で 1 手にまとめる（空のまま確定・取り消しした枠は Undo に残さない）。
/// 入力中・ドラッグ中は Undo に積まずに <see cref="Texts"/> を直接書き換えて画面に即反映し、確定のときに入力前との差分を 1 手として積む。
/// ほかの編集（マスク・回転・Undo など）は先に入力を確定してから行う（各操作の先頭で <see cref="CommitTextEditing"/>）。
/// </summary>
public sealed partial class PageState
{
    private IReadOnlyList<TextBox>? _textsBeforeEdit;

    /// <summary>入力中（入力欄を出している）の文字枠。null = 入力していない。</summary>
    public Guid? EditingTextId { get; private set; }

    public TextBox? FindText(Guid id)
    {
        foreach (var t in Texts) if (t.Id == id) return t;
        return null;
    }

    private int IndexOfText(Guid id)
    {
        var texts = Texts;
        for (var i = 0; i < texts.Count; i++) if (texts[i].Id == id) return i;
        return -1;
    }

    /// <summary>書き出しで描かれる文字枠の数（空の枠は描かれないので数えない）。</summary>
    public int TextCount => Texts.Count(t => t.Text.Length > 0);

    /// <summary>Undo に積まずに文字枠を差し替える（入力中・ドラッグ中のライブ更新）。</summary>
    private void SetTextsLive(IReadOnlyList<TextBox> texts) => _current = _current with { Texts = texts };

    /// <summary>
    /// <paramref name="before"/> から今の文字枠への変更を 1 手として積む（ライブ更新の確定）。変わっていなければ何もしない。
    /// 文字枠以外は変わっていない前提（ライブ更新の間はほかの操作をしない・先に確定する）。
    /// </summary>
    private bool CommitTextsFrom(IReadOnlyList<TextBox> before)
    {
        if (before.SequenceEqual(Texts)) return false;
        PushUndo(_current with { Texts = before });
        _redo.Clear();
        return true;
    }

    /// <summary>文字枠の一覧を変える 1 手（設定・数値指定・削除・複製）。変わらなければ積まない。</summary>
    private bool CommitTexts(IReadOnlyList<TextBox> next)
    {
        if (next.SequenceEqual(Texts)) return false;
        Commit(_current with { Texts = next });
        return true;
    }

    /// <summary>新しい枠を作って入力を始める（Undo への登録は確定時）。設定は範囲に丸める。</summary>
    public Guid BeginNewText(NormRect box, TextStyle style)
    {
        CommitTextEditing();
        var before = Texts;
        var t = new TextBox(Guid.NewGuid(), box, "", style);
        SetTextsLive([.. before, t]);
        _textsBeforeEdit = before;
        EditingTextId = t.Id;
        return t.Id;
    }

    /// <summary>既存の枠の入力を始める（ダブルクリック）。入力中の枠なら何もしない。</summary>
    public bool BeginTextEditing(Guid id)
    {
        if (FindText(id) is null) return false;
        if (EditingTextId == id) return true;
        CommitTextEditing();
        _textsBeforeEdit = Texts;
        EditingTextId = id;
        return true;
    }

    /// <summary>入力中の文字（キー入力ごと・Undo なし）。改行は \n にそろえる（WPF の TextBox は \r\n を入れる）。</summary>
    public bool SetEditingText(string text)
    {
        if (EditingTextId is not { } id) return false;
        var i = IndexOfText(id);
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (i < 0 || Texts[i].Text == s) return false;
        var list = Texts.ToList();
        list[i] = list[i] with { Text = s };
        SetTextsLive(list);
        return true;
    }

    /// <summary>
    /// 入力の確定（Enter・フォーカスが外れた・ほかの操作の前）。空になった枠は消す。入力前との差分を 1 手として積む。
    /// 戻り値は文字枠が変わったか（Undo に積んだか）。
    /// </summary>
    public bool CommitTextEditing()
    {
        if (EditingTextId is not { } id || _textsBeforeEdit is not { } before) return false;
        EditingTextId = null;
        _textsBeforeEdit = null;
        var i = IndexOfText(id);
        if (i >= 0 && Texts[i].Text.Length == 0) SetTextsLive(Texts.Where(t => t.Id != id).ToList());
        return CommitTextsFrom(before);
    }

    /// <summary>入力の取り消し（Esc）。入力前に戻す（新しい枠はそのまま消える）。</summary>
    public void CancelTextEditing()
    {
        if (EditingTextId is null || _textsBeforeEdit is not { } before) return;
        EditingTextId = null;
        _textsBeforeEdit = null;
        SetTextsLive(before);
    }

    /// <summary>ドラッグ中のライブ更新（Undo なし。確定は <see cref="CommitTextBox"/>）。</summary>
    public void SetTextBoxLive(Guid id, NormRect box)
    {
        var i = IndexOfText(id);
        if (i < 0 || Texts[i].Box == box) return;
        var list = Texts.ToList();
        list[i] = list[i] with { Box = box };
        SetTextsLive(list);
    }

    /// <summary>ドラッグの確定: 開始時の枠 <paramref name="from"/> との差分を 1 手として積む（移動・大きさ変更）。</summary>
    public bool CommitTextBox(Guid id, NormRect from)
    {
        var i = IndexOfText(id);
        if (i < 0 || Texts[i].Box == from) return false;
        var before = Texts.ToList();
        before[i] = before[i] with { Box = from };
        return CommitTextsFrom(before);
    }

    /// <summary>枠を設定する（数値入力・矢印キー。1 回 = 1 手）。</summary>
    public bool SetTextBox(Guid id, NormRect box)
    {
        CommitTextEditing();
        var i = IndexOfText(id);
        if (i < 0) return false;
        var list = Texts.ToList();
        list[i] = list[i] with { Box = box };
        return CommitTexts(list);
    }

    /// <summary>設定（書体・サイズ・行間・字間・揃え・マス数・色）を変える（1 回 = 1 手・範囲は丸める）。</summary>
    public bool SetTextStyle(Guid id, TextStyle style)
    {
        CommitTextEditing();
        var i = IndexOfText(id);
        if (i < 0) return false;
        var list = Texts.ToList();
        list[i] = list[i].WithStyle(style);
        return CommitTexts(list);
    }

    /// <summary>枠を消す（Delete・一覧のごみ箱）。入力中の枠なら入力を取り消してから消す。</summary>
    public bool RemoveText(Guid id)
    {
        if (EditingTextId == id) CancelTextEditing();
        else CommitTextEditing();
        if (IndexOfText(id) < 0) return false;
        return CommitTexts(Texts.Where(t => t.Id != id).ToList());
    }

    /// <summary>枠を複製して <paramref name="box"/> に置く（Ctrl+D）。複製の id を返す（無ければ null）。</summary>
    public Guid? DuplicateText(Guid id, NormRect box)
    {
        CommitTextEditing();
        if (FindText(id) is not { } t) return null;
        var copy = t with { Id = Guid.NewGuid(), Box = box };
        CommitTexts([.. Texts, copy]);
        return copy.Id;
    }
}
