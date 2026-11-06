using System.IO;
using System.Text;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// 配布 zip に入れるテキスト（W7・scripts/publish.ps1）。バージョン情報の画面にも出すので、
/// 文字化けの元（UTF-8 でないバイト）が混ざっていないことと、README に必須の案内があることを確かめる。
/// </summary>
public class DistributionFilesTests
{
    private static string WindowsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuseo.Windows.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Fuseo.Windows.sln が見つからない");
    }

    private static readonly UTF8Encoding Strict = new(false, true);

    [Theory]
    [InlineData("THIRD-PARTY-NOTICES.txt")]
    [InlineData("packaging/README.txt")]
    [InlineData("../LICENSE")]
    public void DistributedText_IsValidUtf8(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(WindowsRoot(), relative));
        var text = Strict.GetString(bytes); // 不正なバイトがあれば DecoderFallbackException
        Assert.DoesNotContain('�', text);
    }

    [Fact]
    public void Readme_ExplainsWarningStorageAndUninstall()
    {
        var text = File.ReadAllText(Path.Combine(WindowsRoot(), "packaging/README.txt"), Strict);
        Assert.Contains("{VERSION}", text);                 // publish.ps1 が版番号に置き換える
        Assert.Contains("「詳細情報」→「実行」", text);     // SmartScreen の通し方（無署名）
        Assert.Contains(@"%LOCALAPPDATA%\Fuseo\Library", text); // 塗る前の画像を含む保存場所
        Assert.Contains("Get-FileHash", text);              // 改ざんの確認
        Assert.Contains("削除（アンインストール）", text);
        Assert.Contains("More info", text);                 // 英語の案内も
    }

    [Fact]
    public void NoticesListEveryBundledComponent()
    {
        var text = File.ReadAllText(Path.Combine(WindowsRoot(), "THIRD-PARTY-NOTICES.txt"), Strict);
        foreach (var name in new[] { "PDFiumCore", "pdfium-binaries", "PDFium", "ZXing.Net", ".NET Runtime", "Windows Presentation Foundation",
                                   "Microsoft.Windows.SDK.NET", "WinRT.Runtime" })
            Assert.Contains(name, text);
    }
}
