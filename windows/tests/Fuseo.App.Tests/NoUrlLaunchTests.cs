using System.IO;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// 通信ゼロ（W6 追加・<see cref="NoNetworkTests"/> の補強）: ソースを文字列で検査し、ブラウザ・URL を起動する経路が無いことを確かめる。
/// - プロセス起動（Process.Start / ProcessStartInfo）は <c>Services/FolderLauncher.cs</c> の 1 か所だけ。そこに "http" は無い。
/// - URL 起動に使われる API（ShellExecute・Launcher.LaunchUri・Hyperlink の NavigateUri・WebBrowser 等）を使っていない。
/// - "http(s)://" を含むソースは表示・コピー用のアドレス定数（Services/AboutInfo.cs）だけ（XAML の名前空間宣言は除く）。
/// </summary>
public class NoUrlLaunchTests
{
    private static string WindowsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuseo.Windows.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Fuseo.Windows.sln が見つからない");
    }

    private static readonly Lazy<List<(string Rel, string Text)>> AllSources = new(() =>
    {
        var src = Path.Combine(WindowsRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
        return files;
    });

    private static List<(string Rel, string Text)> Sources()
    {
        var files = AllSources.Value;
        Assert.True(files.Count > 40, $"ソースが少なすぎる（{files.Count}）: 検査対象の場所が違う");
        return files;
    }

    [Fact]
    public void ProcessLaunch_OnlyInFolderLauncher_WithoutHttp()
    {
        var hits = Sources().Where(s => s.Text.Contains("Process.Start", StringComparison.Ordinal)
                                        || s.Text.Contains("ProcessStartInfo", StringComparison.Ordinal))
            .Select(s => s.Rel).ToList();
        Assert.Equal(["Fuseo.App/Services/FolderLauncher.cs"], hits);
        var launcher = Sources().Single(s => s.Rel == "Fuseo.App/Services/FolderLauncher.cs").Text;
        Assert.DoesNotContain("http", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UseShellExecute = false", launcher, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ShellExecute(")]
    [InlineData("UseShellExecute = true")]
    [InlineData("LaunchUri")]
    [InlineData("NavigateUri")]
    [InlineData("Hyperlink")]
    [InlineData("WebBrowser")]
    [InlineData("WebView")]
    [InlineData("RequestNavigate")]
    [InlineData("System.Net")]
    public void NoUrlLaunchApis(string api)
    {
        var hits = Sources().Where(s => s.Text.Contains(api, StringComparison.Ordinal)).Select(s => s.Rel).ToList();
        Assert.True(hits.Count == 0, $"{api} を使っている: {string.Join(", ", hits)}");
    }

    [Fact]
    public void HttpAddresses_OnlyAsDisplayConstants()
    {
        static bool HasUrl(string text) => text.Split('"').Any(t =>
            (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && !t.StartsWith("http://schemas.microsoft.com/", StringComparison.Ordinal)); // XAML の名前空間（識別子）
        var hits = Sources().Where(s => HasUrl(s.Text)).Select(s => s.Rel).ToList();
        Assert.Equal(["Fuseo.App/Services/AboutInfo.cs"], hits);
    }
}
