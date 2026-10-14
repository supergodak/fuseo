using System.Diagnostics;
using System.IO;

namespace Fuseo.App.Services;

/// <summary>
/// エクスプローラーでローカルのフォルダを開く（設定の「フォルダを開く」）。
/// **アプリで唯一のプロセス起動**。渡せるのは存在するローカルの絶対パスのフォルダだけで、URL やファイルの関連付け起動はしない
/// （UseShellExecute=false・explorer.exe を直接起動・引数は ArgumentList で 1 つだけ）。NoUrlLaunchTests がこの約束を静的に検査する。
/// </summary>
public static class FolderLauncher
{
    /// <summary>開いてよいパスか（ローカルの絶対パス・UNC や URL でない・存在するフォルダ）。</summary>
    public static bool IsAllowed(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains("://", StringComparison.Ordinal)
        && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(path)
        && Directory.Exists(path);

    public static void Open(string path)
    {
        if (!IsAllowed(path)) throw new ArgumentException(path, nameof(path));
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
        info.ArgumentList.Add(path);
        using var _ = Process.Start(info);
    }
}
