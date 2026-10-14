using System.ComponentModel;
using System.IO;
using Fuseo.App.Configuration;
using Fuseo.App.Mvvm;
using Fuseo.App.Properties;
using Fuseo.App.Services;

namespace Fuseo.App.ViewModels;

/// <summary>表示言語の選択肢。</summary>
public sealed record LanguageOption(LanguageSetting Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 設定ウィンドウ（W6）。表示言語（再起動後に反映）・作業の保存場所（表示と「フォルダを開く」）・すべての作業を削除。
/// 「すべての作業を削除」は <see cref="MainViewModel.DeleteAllWorksCommand"/>（W5・確認あり）をそのまま使う。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore? _store;
    private readonly MainViewModel _owner;
    private readonly IDialogService _dialogs;
    private LanguageOption _selectedLanguage;

    public SettingsViewModel(SettingsStore? store, string? storagePath, MainViewModel owner, IDialogService dialogs)
    {
        _store = store;
        _owner = owner;
        _dialogs = dialogs;
        StoragePath = storagePath;
        LanguageOptions =
        [
            new(LanguageSetting.Auto, Strings.SettingsLanguageAuto),
            new(LanguageSetting.Japanese, Strings.SettingsLanguageJapanese),
            new(LanguageSetting.English, Strings.SettingsLanguageEnglish),
        ];
        var current = store?.Load().Language ?? LanguageSetting.Auto;
        _selectedLanguage = LanguageOptions.First(o => o.Value == current);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => StoragePath is not null);
        // 削除中の状態変化（一覧の件数・開いている書類）に合わせて案内を出し直す。
        _owner.PropertyChanged += OnOwnerChanged;
    }

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public bool CanChangeLanguage => _store is not null;

    /// <summary>選ぶとすぐ settings.json に保存する（反映は再起動後）。</summary>
    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value) || _store is null) return;
            try
            {
                _store.Save(new AppSettings { Language = value.Value });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(Strings.SettingsTitle, string.Format(Strings.SettingsSaveFailed, e.Message));
            }
        }
    }

    /// <summary>作業の保存場所（ライブラリが無いときは null）。</summary>
    public string? StoragePath { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand DeleteAllCommand => _owner.DeleteAllWorksCommand;

    /// <summary>「すべての作業を削除」の横に出す状態の案内。</summary>
    public string DeleteAllStatus =>
        _owner.HasPages ? Strings.SettingsDeleteAllBlocked
        : !_owner.HasLibraryItems ? Strings.SettingsNoWorks
        : string.Format(Strings.SettingsWorkCount, _owner.LibraryItems.Count);

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasLibraryItems) or nameof(MainViewModel.HasPages))
            OnPropertyChanged(nameof(DeleteAllStatus));
    }

    /// <summary>ウィンドウを閉じたら購読をやめる。</summary>
    public void Detach() => _owner.PropertyChanged -= OnOwnerChanged;

    private void OpenFolder()
    {
        if (StoragePath is not { } path) return;
        try
        {
            Directory.CreateDirectory(path); // まだ 1 度も保存していなければ作ってから開く
            _dialogs.OpenFolder(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _dialogs.ShowError(Strings.SettingsTitle, string.Format(Strings.SettingsOpenFolderFailed, e.Message));
        }
    }
}
