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

    /// <summary>
    /// Signal the consumer that work is ready.
    /// Idempotent — calling multiple times before Wait() returns is a no-op volatile write.
    /// </summary>
    public void Signal() => _gate.Set();

    /// <summary>
    /// Block until signaled or cancelled. Resets the gate for the next cycle.
    /// Use only on dedicated (LongRunning) threads — blocks the calling thread.
    /// </summary>
    public void Wait(CancellationToken ct)
    {
        _gate.Wait(ct);
        _gate.Reset();
    }

    public void Dispose()
    {
        _gate.Set(); // unblock any waiter so it can observe cancellation
        _gate.Dispose();
    }
}
