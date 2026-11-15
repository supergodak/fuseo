using System.IO;

namespace Fuseo.App.Configuration;

/// <summary>
/// 設定とライブラリを置くフォルダ（既定 <c>%LOCALAPPDATA%\Fuseo</c>）。
/// 環境変数 <c>FUSEO_DATA_DIR</c> に**絶対パス**が入っていれば、その代わりにそのフォルダを使う
/// （画面操作の E2E テストが、利用者の試用データに触れずにテストごとの空のフォルダで起動するため）。
/// 相対パス・空白だけの値は無視して既定のフォルダを使う（起動したフォルダによって保存先が変わらないように）。
/// <para>環境変数を読むのは E2E 用の exe（FUSEO_TESTING を定義してビルドしたもの）だけ。配布用の exe は常に既定のフォルダ。</para>
/// </summary>
public static class AppDataLocation
{
#if FUSEO_TESTING
    // 定数ごと E2E 用の exe だけに置く（配布用の exe には文字列も残さない。配布物の点検で確かめられるように）。
    private const string EnvironmentVariable = "FUSEO_DATA_DIR";
#endif

    /// <summary>実際に使うフォルダ（起動中のプロセスの環境変数で決まる）。</summary>
    public static string Root =>
        Resolve(TestingOverride(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string? TestingOverride()
    {
#if FUSEO_TESTING
        return Environment.GetEnvironmentVariable(EnvironmentVariable);
#else
        return null;
#endif
    }

    /// <summary>環境変数の値と %LOCALAPPDATA% からフォルダを決める（テスト用に切り出した純粋関数）。</summary>
    public static string Resolve(string? overrideDirectory, string localAppData) =>
        !string.IsNullOrWhiteSpace(overrideDirectory) && Path.IsPathFullyQualified(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(localAppData, "Fuseo");
}
