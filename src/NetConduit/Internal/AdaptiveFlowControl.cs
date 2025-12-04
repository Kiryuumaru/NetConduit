namespace NetConduit.Internal;

/// <summary>
/// Adaptive flow control state for a channel.
/// Automatically adjusts credit window size based on throughput patterns.
/// Starts at MaxCredits for maximum initial throughput, then shrinks if idle.
/// </summary>
internal sealed class AdaptiveFlowControl
{
    private readonly uint _minCredits;
    private readonly uint _maxCredits;
    private readonly object _lock = new();
    
    private uint _currentWindowSize;
    private long _bytesConsumedInWindow;
    private long _lastActivityTime;
    
    // Adaptive parameters
    private const int ShrinkIdleMs = 5000; // Shrink after N ms of inactivity
    private const double ShrinkFactor = 0.5; // Halve window on shrink
    
    public AdaptiveFlowControl(uint minCredits, uint maxCredits)
    {
        _minCredits = minCredits;
        _maxCredits = maxCredits;
        // Start at max for immediate high throughput - will shrink if idle
        _currentWindowSize = maxCredits;
        _lastActivityTime = Environment.TickCount64;
    }
    
    /// <summary>
    /// Gets the current adaptive window size.
    /// </summary>
    public uint CurrentWindowSize
    {
        get
        {
            lock (_lock)
            {
                return _currentWindowSize;
            }
        }
    }
    
    /// <summary>
    /// Records bytes consumed and determines how many credits to grant back.
    /// Returns 0 if no grant is needed yet.
    /// </summary>
    public uint RecordConsumptionAndGetGrant(int bytesConsumed)
    {
        lock (_lock)
        {
            _bytesConsumedInWindow += bytesConsumed;
            _lastActivityTime = Environment.TickCount64;
            
            // Grant when we've consumed at least 50% of the current window
            var threshold = _currentWindowSize / 2;
            if (_bytesConsumedInWindow < threshold)
                return 0;
            
            // Calculate grant amount and reset counter
            var toGrant = (uint)_bytesConsumedInWindow;
            _bytesConsumedInWindow = 0;
            
            return toGrant;
        }
    }
    
    /// <summary>
    /// Called periodically to check if window should shrink due to inactivity.
    /// Returns true if window was shrunk.
    /// </summary>
    public bool TryShrinkIfIdle()
    {
        lock (_lock)
        {
            var idleTime = Environment.TickCount64 - _lastActivityTime;
            if (idleTime > ShrinkIdleMs && _currentWindowSize > _minCredits)
            {
                // Shrink window
                var newSize = (uint)Math.Max(_currentWindowSize * ShrinkFactor, _minCredits);
                _currentWindowSize = newSize;
                return true;
            }
            return false;
        }
    }
    
    /// <summary>
    /// Gets the initial credits to grant when channel opens.
    /// </summary>
    public uint GetInitialCredits()
    {
        lock (_lock)
        {
            return _currentWindowSize;
        }
    }
}
