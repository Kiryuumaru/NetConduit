namespace NetConduit.Enums;

/// <summary>
/// Controls how frame writes are flushed to the underlying stream.
/// </summary>
 public enum FlushMode
{
    /// <summary>
    /// Flush after every frame (highest latency, lowest throughput).
    /// </summary>
    Immediate,
    
    /// <summary>
    /// Flush periodically based on FlushInterval (balanced).
    /// </summary>
    Batched,
    
    /// <summary>
    /// Never explicitly flush, rely on stream buffering (lowest latency for small writes, highest throughput).
    /// </summary>
    Manual
}
