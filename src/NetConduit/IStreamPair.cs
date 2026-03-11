namespace NetConduit;

/// <summary>
/// Represents a pair of streams (read and write) that can be disposed together.
/// Enables proper resource lifecycle management for StreamFactory.
/// </summary>
public interface IStreamPair : IAsyncDisposable
{
    /// <summary>
    /// The stream used for reading data.
    /// </summary>
    Stream ReadStream { get; }

    /// <summary>
    /// The stream used for writing data.
    /// </summary>
    Stream WriteStream { get; }
}
