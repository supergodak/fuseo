namespace Fuseo.App.Library;

/// <summary>
/// 自動保存のデバウンスと、ライブラリ操作の直列化（W5）。
/// - <see cref="Schedule"/> を何度呼んでも、最後の呼び出しから <see cref="Debounce"/>（1 秒）たってから 1 回だけ保存する。
/// - 保存の中身は**発火した時点で** <c>capture</c>（呼び出し元のスレッド＝UI スレッド）で写し取り、書き込みは別スレッドで行う
///   （UI を塞がない・書いている最中の編集と混ざらない）。
/// - 書き込みと、<see cref="RunAsync{T}"/> で頼まれた操作（一覧・読み込み・削除）は 1 本の列で順に実行する。
/// - 待ち時間は差し替えられる（テストで時計を進める）。
/// </summary>
public sealed class AutoSaver
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly Func<WorkSnapshot?> _capture;
    private readonly Action<WorkSnapshot> _write;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _pending;
    private Task _chain = Task.CompletedTask;
    private Task _lastSave = Task.CompletedTask;
    private int _inFlight;

    public AutoSaver(Func<WorkSnapshot?> capture, Action<WorkSnapshot> write,
                     Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _capture = capture;
        _write = write;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>保存が終わったとき（成功は null、失敗は例外）。保存を頼んだスレッドの文脈で呼ぶ。</summary>
    public event Action<Exception?>? Saved;

    /// <summary>まだ発火していない保存の予約があるか。</summary>
    public bool HasPending => _pending is not null;

    /// <summary>予約か書き込み中のものがあるか（終了時に待つ必要があるか）。</summary>
    public bool IsBusy => HasPending || Volatile.Read(ref _inFlight) > 0;

    /// <summary>保存を予約する（デバウンス）。</summary>
    public void Schedule()
    {
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = FireAfterDelayAsync(cts);
    }

    /// <summary>予約を取り消す（作業を閉じた・消したとき）。書き込み中のものは止めない。</summary>
    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
    }

    /// <summary>
    /// 予約があれば今すぐ写し取って保存し、列が空になるまで待つ。写し取りはこのメソッドの中で**同期的に**行うので、
    /// 呼んだ直後に画面を空にしてよい（「新しい書類」・終了時）。
    /// </summary>
    public Task FlushAsync()
    {
        if (_pending is not null)
        {
            Cancel();
            StartSave();
        }
        return WaitChainAsync();
    }

    private async Task FireAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await _delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(_pending, cts)) return;
        _pending = null;
        await StartSave();
    }

    /// <summary>保存を始め、その完了（結果の通知まで）を <see cref="WaitChainAsync"/> で待てるようにする。</summary>
    private Task StartSave()
    {
        var previous = _lastSave;
        var task = SaveNowAsync();
        _lastSave = previous.IsCompleted ? task : Task.WhenAll(previous, task);
        return task;
    }

    private async Task SaveNowAsync()
    {
        var snapshot = _capture();
        if (snapshot is null) return;
        Exception? error = null;
        try
        {
            await RunAsync(() => { _write(snapshot); return true; });
        }
        catch (Exception e)
        {
            error = e;
        }
        Saved?.Invoke(error);
    }

    /// <summary>ライブラリ操作を書き込みと同じ列で（別スレッドで）実行する。</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        Interlocked.Increment(ref _inFlight);
        var previous = _chain;
        var task = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* 前の操作の失敗はその呼び出し元に返してある */ }
            try { return work(); }
            finally { Interlocked.Decrement(ref _inFlight); }
        });
        _chain = task;
        return task;
    }

    public Task RunAsync(Action work) => RunAsync(() => { work(); return true; });

    /// <summary>列が空になり、保存の結果の通知（<see cref="Saved"/>）まで済むのを待つ。</summary>
    private async Task WaitChainAsync()
    {
        try { await _chain; }
        catch { /* 失敗は各呼び出し元に返してある */ }
        await _lastSave; // SaveNowAsync は例外を外に出さない
    }
}
