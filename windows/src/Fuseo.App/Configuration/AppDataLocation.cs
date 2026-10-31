using System.IO;

namespace Fuseo.App.Configuration;

/// <summary>
/// 設定とライブラリを置くフォルダ（既定 <c>%LOCALAPPDATA%\Fuseo</c>）。
/// 環境変数 <c>FUSEO_DATA_DIR</c> に**絶対パス**が入っていれば、その代わりにそのフォルダを使う
/// （画面操作の E2E テストが、利用者の試用データに触れずにテストごとの空のフォルダで起動するため）。
/// 相対パス・空白だけの値は無視して既定のフォルダを使う（起動したフォルダによって保存先が変わらないように）。
/// </summary>
public static class AppDataLocation
{
    public const string EnvironmentVariable = "FUSEO_DATA_DIR";

    /// <summary>実際に使うフォルダ（起動中のプロセスの環境変数で決まる）。</summary>
    public static string Root =>
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>環境変数の値と %LOCALAPPDATA% からフォルダを決める（テスト用に切り出した純粋関数）。</summary>
    public static string Resolve(string? overrideDirectory, string localAppData) =>
        !string.IsNullOrWhiteSpace(overrideDirectory) && Path.IsPathFullyQualified(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(localAppData, "Fuseo");
}
