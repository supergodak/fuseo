using Fuseo.App.Configuration;

namespace Fuseo.App.ViewModels;

/// <summary>設定・バージョン情報の入口（W6）。</summary>
public sealed partial class MainViewModel
{
    private readonly SettingsStore? _settings;

    /// <summary>設定ウィンドウ用の ViewModel を作る。</summary>
    public SettingsViewModel CreateSettingsViewModel() => new(_settings, _library?.Root, this, _dialogs);

    private void OpenSettings()
    {
        var vm = CreateSettingsViewModel();
        try { _dialogs.ShowSettings(vm); }
        finally { vm.Detach(); }
    }
}
