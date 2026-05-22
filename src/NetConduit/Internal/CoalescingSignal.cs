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
        _gate.Wait(ct);

        // Dispose() races with this method: Set() inside Dispose() can wake Wait()
        // *without* surfacing cancellation (the Set wins the wakeup), and Dispose()
        // may then dispose the underlying gate before the woken consumer reaches
        // Reset(). Without this guard, Reset() throws ObjectDisposedException on
        // the consumer thread (typically the multiplexer writer/flusher thread) —
        // issue #281. Tolerate the dispose-during-Reset race: the consumer's outer
        // loop will exit on the next iteration via its own cancellation check.
        if (_disposed) return;
        try { _gate.Reset(); }
        catch (ObjectDisposedException) { }
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
