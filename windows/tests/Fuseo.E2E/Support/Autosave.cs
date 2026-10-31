using Fuseo.Core.Library;
using Fuseo.E2E.Driver;

namespace Fuseo.E2E;

/// <summary>
/// 自動保存（テスト専用のデータフォルダの Library\&lt;id&gt;\document.json）を読む。画面の操作が状態にどう効いたか
/// （矩形の座標・回転・チェック印・状態）を確かめる「答え合わせ」に使う。読むだけ。
/// </summary>
internal static class Autosave
{
    public static string LibraryDir(string dataDir) => Path.Combine(dataDir, "Library");

    public static List<WorkDocument> All(string dataDir)
    {
        var lib = LibraryDir(dataDir);
        if (!Directory.Exists(lib)) return [];
        var docs = new List<WorkDocument>();
        foreach (var dir in Directory.EnumerateDirectories(lib))
        {
            var json = Path.Combine(dir, "document.json");
            if (!File.Exists(json)) continue;
            docs.Add(WorkDocumentJson.Parse(File.ReadAllBytes(json)));
        }
        return docs;
    }

    /// <summary>いちばん新しく保存された作業。</summary>
    public static WorkDocument? Latest(string dataDir) => All(dataDir).OrderByDescending(d => d.UpdatedAt).FirstOrDefault();

    /// <summary>保存された作業が条件を満たすまで待つ（自動保存は 1 秒のデバウンス）。</summary>
    public static WorkDocument WaitLatest(string dataDir, Func<WorkDocument, bool> condition, string what)
    {
        WorkDocument? doc = null;
        Wait.Until(() => (doc = Latest(dataDir)) is { } d && condition(d), $"自動保存が「{what}」になる", TimeSpan.FromSeconds(20));
        return doc!;
    }

    public static int WorkCount(string dataDir) =>
        Directory.Exists(LibraryDir(dataDir)) ? Directory.EnumerateDirectories(LibraryDir(dataDir)).Count() : 0;
}
