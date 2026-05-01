namespace NetConduit.Models;

/// <summary>
/// Delegate that creates a new stream pair for connection/reconnection.
/// The returned <see cref="IStreamPair"/> will be disposed on reconnect or shutdown.
/// </summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A stream pair containing read and write streams with proper disposal.</returns>
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
