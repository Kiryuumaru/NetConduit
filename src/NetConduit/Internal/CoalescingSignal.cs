namespace NetConduit.Internal;

/// <summary>
/// Coalescing signal: multiple Signal() calls before the consumer wakes collapse into one wakeup.
/// ManualResetEventSlim spins briefly in user-mode before falling back to kernel wait.
/// Signal() on an already-signaled gate is a volatile write (no lock, no syscall).
/// Consumer must run on a dedicated thread (LongRunning) — Wait blocks the calling thread.
/// </summary>
internal sealed class CoalescingSignal : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(false);
    private volatile bool _disposed;

    /// <summary>
    /// Signal the consumer that work is ready.
    /// Idempotent — calling multiple times before Wait() returns is a no-op volatile write.
    /// </summary>
    public void Signal()
    {
        if (_disposed) return;
        try { _gate.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Block until signaled or cancelled. Resets the gate for the next cycle.
    /// Use only on dedicated (LongRunning) threads — blocks the calling thread.
    /// </summary>
    public void Wait(CancellationToken ct)
    {
        try { _gate.Wait(ct); }
        catch (ObjectDisposedException) when (_disposed) { return; }

        // Dispose() races with this method: Set() inside Dispose() can wake Wait()
        // *without* surfacing cancellation (the Set wins the wakeup), and Dispose()
        // may dispose the underlying gate before a consumer reaches either Wait()
        // or Reset(). Treat disposal as a normal wake/exit path; the consumer's
        // outer loop exits on the next iteration via its own cancellation check.
        if (_disposed) return;
        try { _gate.Reset(); }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wake any waiter so it can observe disposal and exit Wait().
        try { _gate.Set(); }
        catch (ObjectDisposedException) { }

        _gate.Dispose();
    }
}
