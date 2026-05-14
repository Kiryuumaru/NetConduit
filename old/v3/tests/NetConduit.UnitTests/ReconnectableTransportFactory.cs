using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Coordinates paired transports for testing reconnection scenarios.
/// Each time either side calls StreamFactory, a new DuplexMemoryStream is created
/// and both sides get opposite ends of it. The factory blocks the second caller
/// until both sides have requested a new transport.
/// </summary>
internal sealed class ReconnectableTransportFactory : IAsyncDisposable
{
    private readonly object _lock = new();
    private DuplexMemoryStream? _pendingDuplex;
    private KillableStreamPair? _currentSideA;
    private KillableStreamPair? _currentSideB;
    private readonly SemaphoreSlim _sideAReady = new(0);
    private readonly SemaphoreSlim _sideBReady = new(0);
    private int _connectionCount;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// StreamFactory for the "client" side (gets SideA of each DuplexMemoryStream).
    /// </summary>
    public async Task<IStreamPair> CreateSideA(CancellationToken ct)
    {
        DuplexMemoryStream duplex;
        lock (_lock)
        {
            if (_pendingDuplex is null)
            {
                _pendingDuplex = new DuplexMemoryStream();
            }
            duplex = _pendingDuplex;
        }

        var killable = new KillableStreamPair(duplex.SideA);
        _currentSideA = killable;

        // Signal that side A is ready
        _sideAReady.Release();

        // Wait for side B to be ready too
        await _sideBReady.WaitAsync(ct);

        lock (_lock)
        {
            _pendingDuplex = null;
            Interlocked.Increment(ref _connectionCount);
        }

        return killable;
    }

    /// <summary>
    /// StreamFactory for the "server" side (gets SideB of each DuplexMemoryStream).
    /// </summary>
    public async Task<IStreamPair> CreateSideB(CancellationToken ct)
    {
        DuplexMemoryStream duplex;
        lock (_lock)
        {
            if (_pendingDuplex is null)
            {
                _pendingDuplex = new DuplexMemoryStream();
            }
            duplex = _pendingDuplex;
        }

        var killable = new KillableStreamPair(duplex.SideB);
        _currentSideB = killable;

        // Signal that side B is ready
        _sideBReady.Release();

        // Wait for side A to be ready too
        await _sideAReady.WaitAsync(ct);

        lock (_lock)
        {
            _pendingDuplex = null;
        }

        return killable;
    }

    /// <summary>
    /// Kill the current transport on both sides, simulating network failure.
    /// </summary>
    public void KillCurrentTransport()
    {
        _currentSideA?.Kill();
        _currentSideB?.Kill();
    }

    public ValueTask DisposeAsync()
    {
        _sideAReady.Dispose();
        _sideBReady.Dispose();
        return ValueTask.CompletedTask;
    }
}
