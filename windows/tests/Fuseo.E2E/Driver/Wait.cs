using System.Diagnostics;

namespace Fuseo.E2E.Driver;

/// <summary>ポーリングで条件を待つ（固定の sleep の代わり）。時間切れは何を待っていたかを書いて失敗させる。</summary>
internal static class Wait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(15);
    private const int PollMs = 50;

    public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Default;
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            try
            {
                if (condition()) return;
                last = null;
            }
            catch (Exception e) when (e is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException or IOException
                                           or UnauthorizedAccessException)
            {
                last = e; // 画面の作り直し中・ファイルの書き換え中など。次の回でまた見る
            }
            if (sw.Elapsed > limit)
                throw new TimeoutException($"{limit.TotalSeconds:0} 秒待っても「{what}」にならない" + (last is null ? "" : $"（最後の例外: {last.GetType().Name}: {last.Message}）"));
            Thread.Sleep(PollMs);
        }
    }

    public static T For<T>(Func<T?> get, string what, TimeSpan? timeout = null) where T : class
    {
        T? result = null;
        Until(() => (result = get()) is not null, what, timeout);
        return result!;
    }

    /// <summary>条件が duration のあいだ一度も成り立たないこと（「出ない」ことの確認）。</summary>
    public static void Never(Func<bool> condition, string what, TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            if (condition()) throw new Xunit.Sdk.XunitException($"「{what}」になってしまった");
            Thread.Sleep(PollMs);
        }
    }
}
