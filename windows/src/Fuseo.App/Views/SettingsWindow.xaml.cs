using System.Windows;

namespace Fuseo.App.Views;

/// <summary>設定ウィンドウ（中身は <see cref="ViewModels.SettingsViewModel"/>）。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
