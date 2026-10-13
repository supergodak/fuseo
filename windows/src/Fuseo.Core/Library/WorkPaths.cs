using System.Text.RegularExpressions;

namespace Fuseo.Core.Library;

/// <summary>
/// 作業フォルダ内の相対パスの形の検査（純粋・ファイルシステムに触らない）。
/// document.json の imageFile / thumbnailFile は**その作業フォルダ内の相対パスだけ**を許す。
/// 許すのは '/' 区切りで、各要素が英数字と <c>. _ -</c> だけ・"." と ".." 以外・末尾がドットでない・Windows の予約名でないもの。
/// 絶対パス・ドライブ指定（C:）・UNC・'\' 区切り・".."・代替データストリーム（':'）はすべて拒否する。
/// 実際の解決先が作業フォルダの中かどうかは FileWorkLibrary が完全パスでもう一度確かめる（二重の防御）。
/// </summary>
public static partial class WorkPaths
{
    public const int MaxLength = 200;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength) return false;
        var segments = path.Split('/');
        foreach (var s in segments)
        {
            if (s is "" or "." or "..") return false;
            if (!SegmentPattern().IsMatch(s)) return false;
            if (s.EndsWith('.')) return false;
            if (ReservedName().IsMatch(s)) return false;
        }
        return true;
    }

    /// <summary>ページ画像の既定パス（<c>pages/&lt;pageId&gt;.png</c>）。</summary>
    public static string PageImage(Guid pageId) => $"pages/{pageId:D}.png";

    /// <summary>サムネイルの既定パス（<c>thumbs/&lt;pageId&gt;.jpg</c>）。</summary>
    public static string Thumbnail(Guid pageId) => $"thumbs/{pageId:D}.jpg";

    /// <summary><see cref="PageImage"/> の形ならページ id を取り出す（違う形なら null）。</summary>
    public static Guid? PageIdFromImageFile(string imageFile)
    {
        const string prefix = "pages/", suffix = ".png";
        if (!imageFile.StartsWith(prefix, StringComparison.Ordinal) || !imageFile.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var core = imageFile[prefix.Length..^suffix.Length];
        return Guid.TryParseExact(core, "D", out var id) ? id : null;
    }
}
