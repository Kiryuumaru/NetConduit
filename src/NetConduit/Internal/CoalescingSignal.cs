namespace NetConduit.Internal;

/// <summary>
/// Fast coalescing signal: writer sets atomically (~5ns), flusher blocks until set.
/// Multiple signals before the flusher wakes collapse into one flush.
/// ManualResetEventSlim spins briefly in user-mode (~40ns) before falling back to kernel wait.
/// </summary>
internal sealed class CoalescingSignal : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(false);
    private int _flag; // 0 = idle, 1 = flush needed

    /// <summary>
    /// Signal the flusher that data is ready. Lock-free, ~5ns on hot path.
    /// Idempotent — calling multiple times before Wait() returns costs nothing extra.
    /// </summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _flag, 1) == 0)
            _gate.Set();
    }

    /// <summary>
    /// Block until signaled or cancelled. Resets the gate for the next cycle.
    /// </summary>
    public void Wait(CancellationToken ct)
    {
        _gate.Wait(ct);
        _gate.Reset();
        Interlocked.Exchange(ref _flag, 0);
    }

    public void Dispose()
    {
        _gate.Set(); // unblock any waiter so it can observe cancellation
        _gate.Dispose();
    }
}
