namespace NetConduit;

/// <summary>
/// Statistics for a single channel.
/// </summary>
public sealed class ChannelStats
{
    private long _bytesSent;
    private long _bytesReceived;
    private long _framesSent;
    private long _framesReceived;
    private long _creditsGranted;
    private long _creditsConsumed;
    private long _creditStarvationCount;
    private long _totalWaitTimeForCreditsTicks;
    private long _longestWaitForCreditsTicks;
    private long _currentWaitStartTicks;
    private int _currentlyWaitingForCredits;
    private readonly DateTime _openedAt;

    internal ChannelStats()
    {
        _openedAt = DateTime.UtcNow;
    }

    /// <summary>Total bytes sent on this channel.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);
    
    /// <summary>Total bytes received on this channel.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);
    
    /// <summary>Total frames sent on this channel.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);
    
    /// <summary>Total frames received on this channel.</summary>
    public long FramesReceived => Volatile.Read(ref _framesReceived);
    
    /// <summary>Total credits granted by this side.</summary>
    public long CreditsGranted => Volatile.Read(ref _creditsGranted);
    
    /// <summary>Total credits consumed by this side.</summary>
    public long CreditsConsumed => Volatile.Read(ref _creditsConsumed);
    
    /// <summary>How long the channel has been open.</summary>
    public TimeSpan OpenDuration => DateTime.UtcNow - _openedAt;
    
    /// <summary>Number of times this channel experienced credit starvation (credits hit zero while trying to send).</summary>
    public long CreditStarvationCount => Volatile.Read(ref _creditStarvationCount);
    
    /// <summary>Total time spent waiting for credits to become available.</summary>
    public TimeSpan TotalWaitTimeForCredits => TimeSpan.FromTicks(Volatile.Read(ref _totalWaitTimeForCreditsTicks));
    
    /// <summary>Longest single wait for credits to become available.</summary>
    public TimeSpan LongestWaitForCredits => TimeSpan.FromTicks(Volatile.Read(ref _longestWaitForCreditsTicks));
    
    /// <summary>Whether this channel is currently waiting for credits (experiencing backpressure).</summary>
    public bool IsWaitingForCredits => Volatile.Read(ref _currentlyWaitingForCredits) != 0;
    
    /// <summary>Duration of current credit wait, if waiting. Returns TimeSpan.Zero if not waiting.</summary>
    public TimeSpan CurrentWaitDuration
    {
        get
        {
            if (Volatile.Read(ref _currentlyWaitingForCredits) == 0)
                return TimeSpan.Zero;
            var startTicks = Volatile.Read(ref _currentWaitStartTicks);
            return TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks);
        }
    }

    internal void AddBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
    internal void AddBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);
    internal void IncrementFramesSent() => Interlocked.Increment(ref _framesSent);
    internal void IncrementFramesReceived() => Interlocked.Increment(ref _framesReceived);
    internal void AddCreditsGranted(long credits) => Interlocked.Add(ref _creditsGranted, credits);
    internal void AddCreditsConsumed(long credits) => Interlocked.Add(ref _creditsConsumed, credits);
    
    /// <summary>
    /// Records the start of a credit starvation event.
    /// </summary>
    internal void RecordCreditStarvationStart()
    {
        Interlocked.Increment(ref _creditStarvationCount);
        Volatile.Write(ref _currentWaitStartTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _currentlyWaitingForCredits, 1);
    }
    
    /// <summary>
    /// Records the end of a credit starvation event and updates timing statistics.
    /// </summary>
    internal void RecordCreditStarvationEnd()
    {
        if (Volatile.Read(ref _currentlyWaitingForCredits) == 0)
            return;
            
        var startTicks = Volatile.Read(ref _currentWaitStartTicks);
        var waitTicks = DateTime.UtcNow.Ticks - startTicks;
        
        if (waitTicks > 0)
        {
            Interlocked.Add(ref _totalWaitTimeForCreditsTicks, waitTicks);
            
            // Update longest wait if this was longer
            long currentLongest;
            do
            {
                currentLongest = Volatile.Read(ref _longestWaitForCreditsTicks);
                if (waitTicks <= currentLongest)
                    break;
            } while (Interlocked.CompareExchange(ref _longestWaitForCreditsTicks, waitTicks, currentLongest) != currentLongest);
        }
        
        Interlocked.Exchange(ref _currentlyWaitingForCredits, 0);
    }
}
