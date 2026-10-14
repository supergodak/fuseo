using System.Runtime.InteropServices;
using System.Windows;
using Fuseo.App.Properties;
using Fuseo.App.Services;

namespace Fuseo.App.Views;

/// <summary>バージョン情報。アドレスはコピーだけ（このアプリからブラウザを開かない）。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = string.Format(Strings.AboutVersion, AboutInfo.Version);
        LicenseText.Text = AboutInfo.LicenseText;
        ThirdPartyText.Text = AboutInfo.ThirdPartyNotices;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text }) return;
        try
        {
            Clipboard.SetText(text);
            CopiedText.Text = string.Format(Strings.AboutCopied, text);
        }
        catch (COMException)
        {
            // クリップボードを他のアプリが掴んでいる。選択してコピーもできるので黙って戻る。
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
