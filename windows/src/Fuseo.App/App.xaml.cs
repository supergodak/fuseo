using System.Windows;
using System.Windows.Threading;
using Fuseo.App.Properties;
using Fuseo.App.Services;
using Fuseo.App.ViewModels;
using Fuseo.Core.Export;
using Fuseo.Core.Model;
using Fuseo.Core.Rendering;
using Fuseo.Input;

namespace Fuseo.App;

/// <summary>合成の起点（依存の組み立てはここだけ）。アプリは通信機能を持たない（更新確認も無し）。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var presets = PresetStore.LoadAll();
        var window = new MainWindow();
        var dialogs = new WpfDialogService(window);
        var vm = new MainViewModel(
            new PageSource(),
            new MaskRasterizer(),
            new Exporter(),
            presets,
            dialogs);
        window.DataContext = vm;
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, string.Format(Strings.UnexpectedError, e.Exception.Message), Strings.AppTitle,
                        MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
