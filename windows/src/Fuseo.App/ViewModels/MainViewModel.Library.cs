using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuseo.App.Library;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.Core.Library;

namespace Fuseo.App.ViewModels;

/// <summary>
/// 自動保存ライブラリ（W5・docs/windows-design.md §3.6）の配線。
/// - 1 作業 = いま画面に開いているページ一式。取り込み・編集・チェック印・並べ替え/削除・書き出しのたびに 1 秒のデバウンスで保存する。
/// - 書き出し完了で exported・その後に編集したら inProgress に戻す。
/// - 「新しい書類」は今の作業をライブラリに残したまま（確認なし）空にする。
/// - 空の画面に「作業中の書類」一覧（開く・削除・すべて削除）。
/// ライブラリを渡さなければ（既存のテスト）保存はしない。
/// </summary>
public sealed partial class MainViewModel
{
    private FileWorkLibrary? _library;
    private AutoSaver? _autoSaver;
    private WorkSession? _session;
    private bool _isLibraryBusy;
    private string? _lastSaveError;

    /// <summary>開いている作業の id・作成日時・状態（ページ以外の保存項目）。</summary>
    private sealed class WorkSession(Guid id, DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public WorkStatus Status { get; set; } = WorkStatus.InProgress;
        public string? LastExportedName { get; set; }
    }

    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public bool HasLibraryItems => LibraryItems.Count > 0;

    /// <summary>空の画面に一覧を出すか（ページが無く、保存済みの作業があるとき）。</summary>
    public bool IsLibraryVisible => !HasPages && HasLibraryItems;

    public RelayCommand NewDocumentCommand { get; private set; } = null!;
    public RelayCommand DeleteAllWorksCommand { get; private set; } = null!;

    /// <summary>開いている作業の id（未保存・何も開いていなければ null）。テスト・診断用。</summary>
    public Guid? CurrentWorkId => _session?.Id;

    /// <summary>開いている作業の状態（テスト・診断用）。</summary>
    public WorkStatus? CurrentWorkStatus => _session?.Status;

    public bool IsLibraryBusy
    {
        get => _isLibraryBusy;
        private set { if (SetProperty(ref _isLibraryBusy, value)) RefreshLibraryCommands(); }
    }

    /// <summary>保留中・書き込み中の自動保存があるか（終了時に待つ）。</summary>
    public bool HasPendingSave => _autoSaver?.IsBusy == true;

    private bool CanUseLibrary => _library is not null && !IsBusy && !IsLibraryBusy && !HasPages;

    private void InitLibrary(FileWorkLibrary? library, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        NewDocumentCommand = new RelayCommand(() => _ = NewDocumentAsync(), () => HasPages && !IsBusy);
        DeleteAllWorksCommand = new RelayCommand(() => _ = DeleteAllWorksAsync(), () => CanUseLibrary && HasLibraryItems);
        _library = library;
        if (library is null) return;
        _autoSaver = new AutoSaver(CaptureSnapshot, s => library.Save(s.Document, s.Images), delay);
        _autoSaver.Saved += OnSaved;
    }

    // ---- 自動保存 ----

    /// <summary>編集した（取り込み・マスク・候補・回転・種別・チェック印・並べ替え・削除）。保存を予約し、状態を作業中に戻す。</summary>
    private void NoteEdited()
    {
        if (_autoSaver is null) return;
        if (Pages.Count == 0)
        {
            // 最後のページを消した = 作業に何も残らない。ライブラリからも消す（エスカレーション: 暫定の扱い）。
            if (_session is { } s)
            {
                _session = null;
                _autoSaver.Cancel();
                _ = DeleteWorkSilentlyAsync(s.Id);
            }
            return;
        }
        _session ??= new WorkSession(Guid.NewGuid(), WorkMapper.TruncateToMs(DateTimeOffset.UtcNow));
        _session.Status = WorkStatus.InProgress;
        _autoSaver.Schedule();
    }

    /// <summary>書き出しが終わった。状態を書き出し済みにして保存を予約する。</summary>
    private void NoteExported(string fileName)
    {
        if (_autoSaver is null || Pages.Count == 0) return;
        _session ??= new WorkSession(Guid.NewGuid(), WorkMapper.TruncateToMs(DateTimeOffset.UtcNow));
        _session.Status = WorkStatus.Exported;
        _session.LastExportedName = fileName;
        _autoSaver.Schedule();
    }

    private WorkSnapshot? CaptureSnapshot()
    {
        if (_session is not { } s || Pages.Count == 0) return null;
        var pages = Pages.Select(p => p.State).ToList();
        return WorkMapper.Capture(s.Id, WorkMapper.Title(pages, Strings.WorkTitleMany), s.CreatedAt, DateTimeOffset.UtcNow,
                                  s.Status, s.LastExportedName, pages);
    }

    private void OnSaved(Exception? error)
    {
        if (error is not null)
        {
            // 黙らない: ステータスバーに出す（次の保存で直れば消す）。
            _lastSaveError = string.Format(Strings.StatusSaveFailed, error.Message);
            StatusMessage = _lastSaveError;
        }
        else if (_lastSaveError is not null && StatusMessage == _lastSaveError)
        {
            _lastSaveError = null;
            StatusMessage = null;
        }
    }

    /// <summary>保留中の自動保存を今すぐ確定して待つ（アプリ終了時）。</summary>
    public Task FlushSaveAsync() => _autoSaver?.FlushAsync() ?? Task.CompletedTask;

    // ---- 新しい書類 ----

    /// <summary>今の作業をライブラリに残したまま（確認なし）空にする。</summary>
    public async Task NewDocumentAsync()
    {
        if (IsBusy || !HasPages) return;
        // 写し取りは FlushAsync の中で同期的に済むので、すぐ画面を空にしてよい。
        var flushed = FlushSaveAsync();
        _session = null;
        ClearPages();
        if (StatusMessage != _lastSaveError) StatusMessage = null; // 保存の失敗だけは残す
        await flushed;
        await RefreshLibraryAsync();
    }

    private void ClearPages()
    {
        SelectedPage = null;
        Pages.Clear();
        Renumber();
        OnPropertyChanged(nameof(IsLibraryVisible));
    }

    // ---- 一覧 ----

    /// <summary>一覧を読み直す（起動時・新しい書類・削除の後）。</summary>
    public async Task RefreshLibraryAsync()
    {
        if (_library is not { } lib || _autoSaver is null) return;
        try
        {
            var rows = await _autoSaver.RunAsync(() =>
                lib.List().Select(s => (Summary: s, Thumb: LoadThumbnail(s))).ToList());
            LibraryItems.Clear();
            foreach (var (summary, thumb) in rows) LibraryItems.Add(new LibraryItemViewModel(summary, thumb, this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = string.Format(Strings.StatusLibraryFailed, e.Message);
        }
        OnPropertyChanged(nameof(HasLibraryItems));
        OnPropertyChanged(nameof(IsLibraryVisible));
        RefreshLibraryCommands();
    }

    /// <summary>サムネイル（JPEG・回転 0）を読み、一覧の向きに回す。読めなければ null（一覧は出す）。</summary>
    private static BitmapSource? LoadThumbnail(WorkSummary s)
    {
        if (s.ThumbnailPath is not { } path) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource result = frame;
            if (s.ThumbnailRotation != 0) result = new TransformedBitmap(frame, new RotateTransform(90 * s.ThumbnailRotation));
            result.Freeze();
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    internal bool CanOpenWork(LibraryItemViewModel item) => CanUseLibrary && item.IsReadable;
    internal bool CanDeleteWork(LibraryItemViewModel item) => CanUseLibrary;

    /// <summary>作業を開く（元画像と状態を復元する。検出は走らせない＝すぐ開く）。</summary>
    public async Task OpenWorkAsync(LibraryItemViewModel item)
    {
        if (!CanOpenWork(item) || _library is not { } lib || _autoSaver is null) return;
        IsBusy = true;
        IsLibraryBusy = true;
        try
        {
            var loaded = await _autoSaver.RunAsync(() => lib.Load(item.Id));
            var states = WorkMapper.Restore(loaded);
            _session = new WorkSession(loaded.Document.Id, loaded.Document.CreatedAt)
            {
                Status = loaded.Document.Status,
                LastExportedName = loaded.Document.LastExportedName,
            };
            foreach (var s in states) Pages.Add(new PageViewModel(s));
            Renumber();
            OnPropertyChanged(nameof(IsLibraryVisible));
            if (Pages.Count > 0) SelectedPage = Pages[0];
            StatusMessage = string.Format(Strings.StatusWorkOpened, loaded.Document.Title, Pages.Count);
        }
        catch (Exception e) when (e is WorkLibraryException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.ShowError(Strings.LibraryOpenFailedTitle, string.Format(Strings.LibraryOpenFailed, e.Message));
            IsLibraryBusy = false;
            await RefreshLibraryAsync();
        }
        finally
        {
            IsLibraryBusy = false;
            IsBusy = false;
        }
    }

    /// <summary>1 件削除する（確認あり）。</summary>
    public async Task DeleteWorkAsync(LibraryItemViewModel item)
    {
        if (!CanDeleteWork(item) || _library is not { } lib || _autoSaver is null) return;
        var name = item.IsReadable ? item.Title : Strings.LibraryUnreadable;
        if (!_dialogs.Confirm(Strings.LibraryDeleteTitle, string.Format(Strings.LibraryDeleteMessage, name))) return;
        await RunLibraryMutationAsync(() => lib.Delete(item.Id));
    }

    /// <summary>すべて削除する（確認あり）。</summary>
    public async Task DeleteAllWorksAsync()
    {
        if (!CanUseLibrary || !HasLibraryItems || _library is not { } lib) return;
        if (!_dialogs.Confirm(Strings.LibraryDeleteAllTitle, string.Format(Strings.LibraryDeleteAllMessage, LibraryItems.Count))) return;
        await RunLibraryMutationAsync(lib.DeleteAll);
    }

    private async Task RunLibraryMutationAsync(Action work)
    {
        IsLibraryBusy = true;
        try
        {
            await _autoSaver!.RunAsync(work);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Strings.LibraryDeleteFailedTitle, string.Format(Strings.LibraryDeleteFailed, e.Message));
        }
        finally
        {
            IsLibraryBusy = false;
        }
        await RefreshLibraryAsync();
    }

    private async Task DeleteWorkSilentlyAsync(Guid id)
    {
        if (_library is not { } lib || _autoSaver is null) return;
        try
        {
            await _autoSaver.RunAsync(() => lib.Delete(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = string.Format(Strings.StatusSaveFailed, e.Message);
        }
        await RefreshLibraryAsync();
    }

    private void RefreshLibraryCommands()
    {
        NewDocumentCommand.RaiseCanExecuteChanged();
        DeleteAllWorksCommand.RaiseCanExecuteChanged();
        foreach (var item in LibraryItems) item.RaiseCanExecuteChanged();
    }
}

/// <summary>「作業中の書類」一覧の 1 行。</summary>
public sealed class LibraryItemViewModel : ObservableObject
{
    private readonly WorkSummary _summary;

    internal LibraryItemViewModel(WorkSummary summary, BitmapSource? thumbnail, MainViewModel owner)
    {
        _summary = summary;
        Thumbnail = thumbnail;
        OpenCommand = new RelayCommand(() => _ = owner.OpenWorkAsync(this), () => owner.CanOpenWork(this));
        DeleteCommand = new RelayCommand(() => _ = owner.DeleteWorkAsync(this), () => owner.CanDeleteWork(this));
    }

    public Guid Id => _summary.Id;
    public bool IsReadable => _summary.IsReadable;
    public string Title => _summary.IsReadable ? _summary.Title : Strings.LibraryUnreadable;
    public BitmapSource? Thumbnail { get; }
    public DateTimeOffset UpdatedAt => _summary.UpdatedAt;
    public int PageCount => _summary.PageCount;
    public WorkStatus Status => _summary.Status;

    /// <summary>更新日時（ローカル時刻・表示言語のカルチャで書式化）。</summary>
    public string UpdatedText => FormatUpdated(_summary.UpdatedAt, Strings.Culture);

    public static string FormatUpdated(DateTimeOffset updatedAt, System.Globalization.CultureInfo? culture) =>
        updatedAt == DateTimeOffset.MinValue
            ? ""
            : string.Format(culture ?? Strings.FormatCulture, Strings.Get(nameof(Strings.LibraryUpdated), culture),
                            updatedAt.ToLocalTime().DateTime);

    public string StatusText => !_summary.IsReadable ? Strings.LibraryUnreadableNote
        : _summary.Status == WorkStatus.Exported ? Strings.LibraryStatusExported : Strings.LibraryStatusInProgress;

    public bool IsExported => _summary.IsReadable && _summary.Status == WorkStatus.Exported;

    public string PageCountText => _summary.IsReadable ? string.Format(Strings.LibraryPageCount, _summary.PageCount) : "";

    /// <summary>読めない理由（技術的な文言。ツールチップに出す）。</summary>
    public string? Problem => _summary.Problem;

    public RelayCommand OpenCommand { get; }
    public RelayCommand DeleteCommand { get; }

    internal void RaiseCanExecuteChanged()
    {
        OpenCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
