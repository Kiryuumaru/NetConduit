namespace NetConduit.Internal;

/// <summary>
/// Adaptive flow control state for a channel.
/// Automatically adjusts credit window size based on throughput patterns.
/// Starts at MaxCredits for maximum initial throughput, shrinks if idle, grows back under load.
/// Lock-free on the hot path (RecordConsumptionAndGetGrant).
/// </summary>
internal sealed class AdaptiveFlowControl
{
    private readonly uint _minCredits;
    private readonly uint _maxCredits;
    private readonly object _shrinkLock = new();
    
    private uint _currentWindowSize;
    private long _bytesConsumedInWindow;
    private long _lastActivityTime;
    private long _lastGrantTime;
    
    // Adaptive parameters
    private const int ShrinkIdleMs = 5000; // Shrink after N ms of inactivity
    private const double ShrinkFactor = 0.5; // Halve window on shrink
    private const int GrowthThresholdMs = 100; // Grow if window consumed within this time
    
    public AdaptiveFlowControl(uint minCredits, uint maxCredits)
    {
        _minCredits = minCredits;
        _maxCredits = maxCredits;
        // Start at max for immediate high throughput - will shrink if idle
        _currentWindowSize = maxCredits;
        _lastActivityTime = Environment.TickCount64;
        _lastGrantTime = Environment.TickCount64;
    }
    
    /// <summary>
    /// Gets the current adaptive window size.
    /// </summary>
    public uint CurrentWindowSize => Volatile.Read(ref _currentWindowSize);
    
    /// <summary>
    /// Records bytes consumed and determines how many credits to grant back.
    /// Returns 0 if no grant is needed yet.
    /// Lock-free: single reader per channel guarantees no contention on _bytesConsumedInWindow.
    /// </summary>
    public uint RecordConsumptionAndGetGrant(int bytesConsumed)
    {
        var newTotal = Interlocked.Add(ref _bytesConsumedInWindow, bytesConsumed);
        Volatile.Write(ref _lastActivityTime, Environment.TickCount64);
        
        var windowSize = Volatile.Read(ref _currentWindowSize);
        var quarterWindow = windowSize / 4;
        var threshold = (long)Math.Min(Math.Max(quarterWindow, 1), windowSize);
        
        if (newTotal < threshold)
            return 0;
        
        // Claim accumulated bytes — single reader per channel, so Exchange won't race
        var claimed = Interlocked.Exchange(ref _bytesConsumedInWindow, 0);
        if (claimed <= 0)
            return 0;
        
        // Window growth: if grants are happening faster than threshold, the channel
        // is actively consuming — grow window back toward max (mirrors TCP slow start)
        var now = Environment.TickCount64;
        var lastGrant = Volatile.Read(ref _lastGrantTime);
        Volatile.Write(ref _lastGrantTime, now);
        
        var elapsed = now - lastGrant;
        if (elapsed >= 0 && elapsed < GrowthThresholdMs && windowSize < _maxCredits)
        {
            var newWindow = (uint)Math.Min((long)windowSize * 2, _maxCredits);
            Volatile.Write(ref _currentWindowSize, newWindow);
        }
        
        return (uint)claimed;
    }
    
    /// <summary>
    /// Called periodically to check if window should shrink due to inactivity.
    /// Returns true if window was shrunk.
    /// </summary>
    public bool TryShrinkIfIdle()
    {
        lock (_shrinkLock)
        {
            var idleTime = Environment.TickCount64 - Volatile.Read(ref _lastActivityTime);
            if (idleTime > ShrinkIdleMs && _currentWindowSize > _minCredits)
            {
                // Shrink window
                var newSize = (uint)Math.Max(_currentWindowSize * ShrinkFactor, _minCredits);
                Volatile.Write(ref _currentWindowSize, newSize);
                return true;
            }
            return false;
        }
    }
    
    /// <summary>
    /// Gets the initial credits to grant when channel opens.
    /// </summary>
    public uint GetInitialCredits() => Volatile.Read(ref _currentWindowSize);
}
