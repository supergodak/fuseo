using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Fuseo.App.Configuration;
using Fuseo.App.Library;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Export;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;
using Fuseo.Input.Detection;

namespace Fuseo.App;

/// <summary>合成の起点（依存の組み立てはここだけ）。アプリは通信機能を持たない（更新確認も無し）。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // 表示言語は起動時に 1 回だけ決める（設定の変更は再起動後に反映・W6）。画面を作る前に決めること
        // （XAML の {x:Static p:Strings.*} は生成時に読まれる）。
        var settingsStore = new SettingsStore(); // %LOCALAPPDATA%\Fuseo\settings.json（FUSEO_DATA_DIR があればその下・AppDataLocation）
        var culture = UiLanguage.Resolve(settingsStore.Load().Language, CultureInfo.CurrentUICulture);
        Strings.Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var presets = PresetStore.LoadAll();
        var window = new MainWindow();
        var dialogs = new WpfDialogService(window);
        var vm = new MainViewModel(
            new PageSource(),
            new MaskRasterizer(),
            new Exporter(),
            presets,
            dialogs,
            detector: new QrDetector(),
            library: new FileWorkLibrary(), // %LOCALAPPDATA%\Fuseo\Library（このパソコンの中だけ・FUSEO_DATA_DIR があればその下）
            settings: settingsStore);
        window.DataContext = vm;
        MainWindow = window;
        window.Show();
        _ = vm.RefreshLibraryAsync();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, string.Format(Strings.UnexpectedError, e.Exception.Message), Strings.AppTitle,
                        MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
