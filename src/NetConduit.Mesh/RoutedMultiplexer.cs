using NetConduit.Interfaces;

namespace NetConduit.Mesh;

/// <summary>
/// A routed multiplexer accepted on the mesh, with the source node identity and the
/// caller-chosen multiplexer ID that uniquely identifies the routed session between the pair.
/// </summary>
/// <param name="SourceNodeId">The node ID of the opener.</param>
/// <param name="MultiplexerId">The opener-supplied multiplexer ID.</param>
/// <param name="Multiplexer">The routed multiplexer session.</param>
public readonly record struct RoutedMultiplexer(
    string SourceNodeId,
    string MultiplexerId,
    IStreamMultiplexer Multiplexer);
