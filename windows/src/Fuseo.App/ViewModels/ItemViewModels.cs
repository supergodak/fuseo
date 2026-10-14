using System.Windows.Media.Imaging;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.State;
using Fuseo.Core.Contracts;
using Fuseo.Core.Model;

namespace Fuseo.App.ViewModels;

/// <summary>ページレールの 1 行。</summary>
public sealed class PageViewModel(PageState state) : ObservableObject
{
    private int _number;
    private BitmapSource? _thumbnail;
    private int _thumbnailRotation;

    public PageState State { get; } = state;

    /// <summary>1 始まりのページ番号（並べ替えで変わる）。</summary>
    public int Number
    {
        get => _number;
        set { if (SetProperty(ref _number, value)) OnPropertyChanged(nameof(Title)); }
    }

    public string Title => State.FromPdf
        ? string.Format(Strings.PageTitlePdf, Number, State.SourceName, State.SourcePageIndex + 1)
        : string.Format(Strings.PageTitleImage, Number, State.SourceName);

    public string MaskCountText => State.MaskCount == 0
        ? Strings.PageNoMask
        : string.Format(Strings.PageMaskCount, State.MaskCount);

    /// <summary>縮小画像（初めて表示されたとき・回転が変わったときに作る）。</summary>
    public BitmapSource Thumbnail
    {
        get
        {
            if (_thumbnail is null || _thumbnailRotation != State.Rotation)
            {
                _thumbnail = BitmapConvert.Thumbnail(State.Base, 240);
                _thumbnailRotation = State.Rotation;
            }
            return _thumbnail;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(MaskCountText));
        if (_thumbnail is not null && _thumbnailRotation != State.Rotation) OnPropertyChanged(nameof(Thumbnail));
    }
}

/// <summary>種別ピッカーの選択肢。</summary>
public sealed record PresetOption(DocumentType Type, string DisplayName, DocumentPreset Preset)
{
    public override string ToString() => DisplayName;
}

/// <summary>チェックリストの 1 行。チェックの状態はページ側（<see cref="PageState.CheckedRuleIds"/>）に持つ。</summary>
public sealed class ChecklistItemViewModel(ChecklistRow row, PageState page, Action? changed = null) : ObservableObject
{
    public ChecklistRow Row { get; } = row;
    public string Label => Row.Label;
    public string Basis => Row.Basis;
    public bool HasCheckBox => Row.Kind == ChecklistRowKind.Check;
    public bool IsManualGuide => Row.Kind == ChecklistRowKind.ManualGuide;
    public bool IsQrCandidate => Row.Kind == ChecklistRowKind.QrCandidate;

    public string? Note => Row.Kind switch
    {
        ChecklistRowKind.ManualGuide => Strings.ChecklistManualGuide,
        ChecklistRowKind.QrCandidate => Strings.ChecklistQrNote,
        _ => null,
    };

    public bool IsChecked
    {
        get => page.CheckedRuleIds.Contains(Row.RuleId);
        set
        {
            var done = value ? page.CheckedRuleIds.Add(Row.RuleId) : page.CheckedRuleIds.Remove(Row.RuleId);
            OnPropertyChanged();
            if (done) changed?.Invoke(); // 自動保存の予約（W5）
        }
    }
}

/// <summary>候補一覧の 1 行（ON/OFF）。変更は MainViewModel 経由でページに記録する（Undo 対象）。</summary>
public sealed class CandidateViewModel(MaskCandidate candidate, Action<Guid, bool> setOn, (string Label, string Basis)? text = null)
    : ObservableObject
{
    public Guid Id => candidate.Id;
    public string Label => text?.Label ?? candidate.Label;
    public string Basis => text?.Basis ?? candidate.Basis;
    public string? ConfidenceText => candidate.Confidence is { } c ? $"{(int)Math.Round(c * 100)}%" : null;

    public bool IsOn
    {
        get => candidate.IsOn;
        set
        {
            if (value == candidate.IsOn) return;
            setOn(candidate.Id, value);
        }
    }
}

/// <summary>書き出しシートのプレビュー 1 枚（焼き込み済みの画像そのもの）。</summary>
public sealed class PreviewItem(int number, BurnedPage page)
{
    public int Number { get; } = number;
    public BurnedPage Page { get; } = page;
    public string Title => string.Format(Strings.PreviewPageTitle, Number);

    /// <summary>
    /// 表示のたびに作る（キャッシュしない）。仮想化された一覧で見えている分だけ実体を持つ＝50 ページでもメモリを倍にしない。
    /// 書き出すのは <see cref="Page"/> そのもので、この画像は表示専用。
    /// </summary>
    public BitmapSource Image => BitmapConvert.ToBitmapSource(Page.Bitmap);
}
