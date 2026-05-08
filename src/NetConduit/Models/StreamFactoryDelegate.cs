using NetConduit.Interfaces;

namespace NetConduit.Models;

/// <summary>
/// Delegate that creates a new bidirectional stream pair for transport.
/// </summary>
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
