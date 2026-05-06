namespace NetConduit;

/// <summary>
/// A bidirectional stream pair used as the transport for multiplexing.
/// </summary>
public interface IStreamPair : IAsyncDisposable
{
    /// <summary>The stream to read incoming data from.</summary>
    Stream ReadStream { get; }

    /// <summary>The stream to write outgoing data to.</summary>
    Stream WriteStream { get; }
}
