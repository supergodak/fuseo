using System.Windows;
using Fuseo.App.Properties;
using Fuseo.Core.Contracts;

namespace Fuseo.App.Views;

/// <summary>
/// パスワード入力（PDF）。**値はどこにも残さない**: フィールドに保持せず、閉じた直後に読み出して
/// PasswordBox を消去し、戻り値でだけ渡す（保存・ログ禁止・§4.4）。
/// </summary>
public partial class PasswordDialog : Window
{
    private PasswordDialog(PasswordRequest request)
    {
        InitializeComponent();
        MessageText.Text = string.Format(request.IsRetry ? Strings.PasswordRetryMessage : Strings.PasswordMessage, request.FileName);
    }

    /// <summary>取り消しなら null。</summary>
    public static string? Ask(Window? owner, PasswordRequest request)
    {
        var dlg = new PasswordDialog(request) { Owner = owner };
        var ok = dlg.ShowDialog() == true;
        var value = ok ? dlg.PasswordInput.Password : null;
        dlg.PasswordInput.Clear();
        return value;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
