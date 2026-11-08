using System.IO;
using System.Windows;
using Microsoft.Win32;
using Fuseo.App.Properties;
using Fuseo.App.ViewModels;
using Fuseo.App.Views;
using Fuseo.Core.Contracts;

namespace Fuseo.App.Services;

/// <summary>WPF の標準ダイアログで <see cref="IDialogService"/> を実装する。</summary>
public sealed class WpfDialogService(Window owner) : IDialogService
{
    public IReadOnlyList<string>? AskOpenFiles()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Strings.OpenFilesTitle,
            Filter = Strings.OpenFilesFilter,
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileNames : null;
    }

    public Task<string?> AskPasswordAsync(PasswordRequest request)
    {
        // 取り込み（W2）はバックグラウンドから呼ぶ可能性があるので、必ず UI スレッドで開く。
        var dispatcher = owner.Dispatcher;
        if (dispatcher.CheckAccess()) return Task.FromResult(PasswordDialog.Ask(owner, request));
        return dispatcher.InvokeAsync(() => PasswordDialog.Ask(owner, request)).Task;
    }

    public string? AskSavePath(ExportFormat format, string defaultFileName)
    {
        var dlg = new SaveFileDialog
        {
            Title = Strings.SaveDialogTitle,
            FileName = defaultFileName,
            Filter = format switch
            {
                ExportFormat.Pdf => Strings.SaveFilterPdf,
                ExportFormat.Jpeg => Strings.SaveFilterJpeg,
                _ => Strings.SaveFilterPng,
            },
            DefaultExt = Path.GetExtension(defaultFileName),
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(ActiveOwner(), message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
        == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        MessageBox.Show(ActiveOwner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowExport(ExportViewModel viewModel)
    {
        var win = new ExportWindow { Owner = owner, DataContext = viewModel };
        win.ShowDialog();
    }

    public void ShowSettings(SettingsViewModel viewModel)
    {
        var win = new SettingsWindow { Owner = owner, DataContext = viewModel };
        win.ShowDialog();
    }

    public void ShowAbout()
    {
        var win = new AboutWindow { Owner = owner };
        win.ShowDialog();
    }

    public string? AskFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(ActiveOwner()) == true ? dlg.FolderName : null;
    }

    public void OpenFolder(string path) => FolderLauncher.Open(path);

    public Fuseo.Core.Geometry.Quad? AskCrop(CropRequest request) => CropWindow.Ask(owner, request);

    /// <summary>書き出しシート・設定が開いていればその上にメッセージを出す。</summary>
    private Window ActiveOwner() =>
        owner.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner;
}
