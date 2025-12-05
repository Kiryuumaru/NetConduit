using System;

namespace NetConduit;

/// <summary>
/// Abstraction for transport-specific multiplexer connections that wrap a concrete multiplexer instance.
/// </summary>
public interface IStreamMultiplexerConnection : IStreamMultiplexer
{
    /// <summary>The underlying multiplexer.</summary>
    StreamMultiplexer Multiplexer { get; }
}
