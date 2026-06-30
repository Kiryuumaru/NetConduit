using NetConduit.Interfaces;
using NetConduit.Mesh.Interfaces;

namespace NetConduit.Mesh;

/// <summary>
/// Convenience extension methods for <see cref="IMeshMultiplexer"/> that combine
/// routed sub-multiplexer construction with readiness waiting. Mirrors the
/// <c>StreamMultiplexerExtensions</c> pattern so the interface itself stays free of
/// pure wait-combine helpers.
/// </summary>
public static class MeshMultiplexerExtensions
{
    /// <summary>
    /// Open a routed sub-multiplexer and wait until it is ready. Equivalent to
    /// <see cref="IMeshMultiplexer.OpenMultiplexer"/> + <see cref="IStreamMultiplexer.WaitForReadyAsync"/>.
    /// If the wait fails, the sub-multiplexer is disposed before the exception propagates.
    /// </summary>
    public static async Task<IStreamMultiplexer> OpenMultiplexerAsync(
        this IMeshMultiplexer mesh, string targetNodeId, string multiplexerId, CancellationToken ct = default)
    {
        var mux = mesh.OpenMultiplexer(targetNodeId, multiplexerId);
        try
        {
            await mux.WaitForReadyAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await mux.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return mux;
    }

    /// <summary>
    /// Accept a routed sub-multiplexer from a specific source and wait until it is ready.
    /// Equivalent to <see cref="IMeshMultiplexer.AcceptMultiplexer"/> + <see cref="IStreamMultiplexer.WaitForReadyAsync"/>.
    /// </summary>
    public static async Task<IStreamMultiplexer> AcceptMultiplexerAsync(
        this IMeshMultiplexer mesh, string sourceNodeId, string multiplexerId, CancellationToken ct = default)
    {
        var mux = mesh.AcceptMultiplexer(sourceNodeId, multiplexerId);
        await mux.WaitForReadyAsync(ct).ConfigureAwait(false);
        return mux;
    }
}
