using System.Windows;
using Fuseo.App.Properties;

namespace Fuseo.App.Views;

/// <summary>
/// パスワード入力（PDF）。**値はどこにも残さない**: フィールドに保持せず、閉じた直後に読み出して
/// PasswordBox を消去し、戻り値でだけ渡す（保存・ログ禁止・§4.4）。
/// </summary>
public partial class PasswordDialog : Window
{
    private PasswordDialog(string fileName)
    {
        InitializeComponent();
        MessageText.Text = string.Format(Strings.PasswordMessage, fileName);
    }

    /// <summary>取り消しなら null。</summary>
    public static string? Ask(Window? owner, string fileName)
    {
        var dlg = new PasswordDialog(fileName) { Owner = owner };
        var ok = dlg.ShowDialog() == true;
        var value = ok ? dlg.PasswordInput.Password : null;
        dlg.PasswordInput.Clear();
        return value;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
