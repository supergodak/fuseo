using System.IO;
using System.Reflection;

namespace Fuseo.App.Services;

/// <summary>
/// バージョン情報の中身（W6）。ライセンス本文はリポジトリ直下 LICENSE と windows/THIRD-PARTY-NOTICES.txt を
/// 埋め込みリソースにしたもの（Fuseo.App.csproj）。アドレスは表示とコピーだけ（このアプリからブラウザは開かない）。
/// </summary>
public static class AboutInfo
{
    public const string WebsiteAddress = "https://fuseo.ati-mirai.co.jp/";
    public const string SourceCodeAddress = "https://github.com/supergodak/fuseo";

    public const string LicenseResource = "Fuseo.LICENSE.txt";
    public const string ThirdPartyResource = "Fuseo.THIRD-PARTY-NOTICES.txt";

    /// <summary>表示用のバージョン（InformationalVersion の「+コミット」部分は落とす）。</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(AboutInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public static string LicenseText => ReadResource(LicenseResource);
    public static string ThirdPartyNotices => ReadResource(ThirdPartyResource);

    private static string ReadResource(string name)
    {
        using var stream = typeof(AboutInfo).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"埋め込みリソースが無い: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
