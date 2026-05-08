namespace NetConduit.Interfaces;

/// <summary>
/// Interface for transits that send and receive discrete messages over channel pairs.
/// Uses length-prefixed framing for message boundaries.
/// </summary>
/// <typeparam name="TSend">The type of messages to send.</typeparam>
/// <typeparam name="TReceive">The type of messages to receive.</typeparam>
public interface IMessageTransit<TSend, TReceive> : ITransit
{
    /// <summary>
    /// Sends a message to the remote side.
    /// </summary>
    ValueTask SendAsync(TSend message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives a message from the remote side.
    /// Returns default if the channel is closed.
    /// </summary>
    ValueTask<TReceive?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously enumerates all incoming messages until the channel is closed.
    /// </summary>
    IAsyncEnumerable<TReceive> ReceiveAllAsync(CancellationToken cancellationToken = default);
}
