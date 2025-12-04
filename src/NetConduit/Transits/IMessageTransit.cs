namespace NetConduit.Transits;

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
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the send operation.</returns>
    ValueTask SendAsync(TSend message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives a message from the remote side.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The received message, or default if the channel is closed.</returns>
    ValueTask<TReceive?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously enumerates all incoming messages until the channel is closed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of received messages.</returns>
    IAsyncEnumerable<TReceive> ReceiveAllAsync(CancellationToken cancellationToken = default);
}
