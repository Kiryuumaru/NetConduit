namespace NetConduit.Internal;

/// <summary>
/// Coalescing signal: multiple Signal() calls before the consumer wakes collapse into one wakeup.
/// Uses SemaphoreSlim(0,1) as an auto-reset binary semaphore.
/// Signal() on an already-signaled gate is a no-op (caught SemaphoreFullException).
/// Supports both synchronous Wait (for dedicated threads) and async WaitAsync (for ThreadPool tasks).
/// </summary>
internal sealed class CoalescingSignal : IDisposable
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    /// <summary>
    /// Signal the consumer that work is ready.
    /// Idempotent — calling multiple times before Wait/WaitAsync returns is a no-op.
    /// </summary>
    public void Signal()
    {
        try { _gate.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Block until signaled or cancelled. Auto-resets for the next cycle.
    /// Use only on dedicated (LongRunning) threads — blocks the calling thread.
    /// </summary>
    public void Wait(CancellationToken ct)
    {
        _gate.Wait(ct);
    }

    /// <summary>
    /// Asynchronously wait until signaled or cancelled. Auto-resets for the next cycle.
    /// Preferred over Wait for ThreadPool-hosted tasks to avoid thread starvation.
    /// </summary>
    public Task WaitAsync(CancellationToken ct)
    {
        return _gate.WaitAsync(ct);
    }

    public void Dispose()
    {
        Signal(); // unblock any waiter so it can observe cancellation
        _gate.Dispose();
    }
}
