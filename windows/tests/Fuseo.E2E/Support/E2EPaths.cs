namespace Fuseo.E2E;

/// <summary>
/// E2E の置き場所。**すべて VM のローカルディスク**（既定 C:\fuseo-e2e）。
/// Parallels の共有で「デスクトップ」「ドキュメント」「ダウンロード」は Mac の実フォルダ（C:\Mac\…・\\Mac\…）につながっているので、
/// そこ（と SpecialFolder の各フォルダ）には何も置かない。違反するパスは例外にする。
/// - FUSEO_E2E_EXE … テストする Fuseo.exe（既定は publish 済みの arm64 版）
/// - FUSEO_E2E_ROOT … 作業フォルダ（既定 C:\fuseo-e2e）。runs\&lt;日時&gt;\&lt;テスト名&gt;\ に入力・出力・データ、results\screenshots\ に失敗時の画面
/// </summary>
internal static class E2EPaths
{
    public const string DataDirVariable = "FUSEO_DATA_DIR";

    public static string Exe { get; } =
        Environment.GetEnvironmentVariable("FUSEO_E2E_EXE") is { Length: > 0 } exe
            ? exe : @"C:\fuseo-e2e\app\Fuseo.exe";

    public static string Root { get; } = Local(
        Environment.GetEnvironmentVariable("FUSEO_E2E_ROOT") is { Length: > 0 } r ? r : @"C:\fuseo-e2e");

    /// <summary>この実行（テストプロセス 1 回）の作業フォルダ。</summary>
    public static string Run { get; } = Path.Combine(Root, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    public static string Screenshots { get; } = Path.Combine(Root, "results", "screenshots");

    public static string Local(string path)
    {
        EnsureLocal(path);
        return Path.GetFullPath(path);
    }

    /// <summary>Mac とつながったフォルダ・利用者のフォルダでないことを確かめる。</summary>
    public static void EnsureLocal(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"E2E のパスはローカルの絶対パスにする: {path}");
        if (full.StartsWith(@"C:\Mac", StringComparison.OrdinalIgnoreCase) || full.Contains(@"\Mac\Home", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mac の共有フォルダは使わない: {path}");
        var userFolders = new[]
        {
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        }.Select(Environment.GetFolderPath).Where(p => p.Length > 0);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var u in userFolders.Append(downloads))
            if (full.StartsWith(u.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase) || full.Equals(u, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"利用者のフォルダ（{u}）の下は使わない: {path}");
    }
}

/// <summary>
/// 利用者の本物のデータ（%LOCALAPPDATA%\Fuseo・試用データがある）に E2E が触れていないことの見張り。
/// 最初の起動の前に一覧（名前・大きさ・更新日時）を控え、各テストの終わりに同じであることを確かめる。読むだけで書き換えない。
/// </summary>
internal static class IsolationGuard
{
    private static readonly string RealDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fuseo");

    private static string? _before;
    private static readonly object Gate = new();

    public static void Snapshot()
    {
        lock (Gate) _before ??= Describe();
    }

    public static void Verify()
    {
        string? before;
        lock (Gate) before = _before;
        if (before is null) return;
        var now = Describe();
        if (now != before)
            throw new Xunit.Sdk.XunitException($"利用者のデータ {RealDir} が変わった（E2E の隔離が効いていない）");
    }

    private static string Describe()
    {
        if (!Directory.Exists(RealDir)) return "(none)";
        return string.Join("\n", Directory.EnumerateFileSystemEntries(RealDir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}"
                : $"{p}|dir"));
    }
}
