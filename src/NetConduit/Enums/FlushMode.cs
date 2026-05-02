namespace NetConduit;

/// <summary>
/// Controls when the writer thread flushes to the transport.
/// </summary>
public enum FlushMode
{
    /// <summary>Writer drains immediately on signal.</summary>
    Immediate,
    /// <summary>Writer wakes on timer (FlushInterval) or signal. Default, throughput-optimal.</summary>
    Batched,
    /// <summary>Writer only drains on explicit Flush call.</summary>
    Manual,
}
