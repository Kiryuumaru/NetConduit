using System.Buffers;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Pumps bytes across an intermediate node. Opens the forward leg toward the next-hop,
/// then pipes the two byte streams in both directions until either side closes.
/// </summary>
internal static class RouteForwarder
{
    private const int CopyBufferSize = 64 * 1024;

    internal static async Task RunRelayAsync(
        MeshMultiplexer mesh,
        RouteChannelInfo info,
        IReadChannel inbound,
        IWriteChannel responseWriter,
        CancellationToken ct)
    {
        if (!mesh.TryGetRoute(info.TargetNodeId, out string nextHop, out int hops))
        {
            // No path. Close both legs.
            try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
            try { await responseWriter.CloseAsync(ct).ConfigureAwait(false); } catch { }
            return;
        }
        if (hops + 1 > mesh.Options.MaxHops)
        {
            try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
            try { await responseWriter.CloseAsync(ct).ConfigureAwait(false); } catch { }
            return;
        }
        if (!mesh.TryGetNeighbor(nextHop, out var nextHopSession))
        {
            try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
            try { await responseWriter.CloseAsync(ct).ConfigureAwait(false); } catch { }
            return;
        }

        // Open forward leg on next-hop mux with the same target/source/multiplexerId/nonce.
        string forwardOutboundId = MeshChannelNaming.BuildOutboundRoute(
            info.TargetNodeId, info.SourceNodeId, info.MultiplexerId, info.Nonce);
        string forwardInboundId = MeshChannelNaming.BuildInboundRoute(
            info.TargetNodeId, info.SourceNodeId, info.MultiplexerId, info.Nonce);

        var slot = mesh.Options.DefaultChannelOptions;
        IWriteChannel? forwardWriter = null;
        IReadChannel? forwardReader = null;
        try
        {
            forwardWriter = nextHopSession.Mux.OpenChannel(new ChannelOptions
            {
                ChannelId = forwardOutboundId,
                Priority = slot.Priority,
                SlabSize = slot.SlabSize,
                SendTimeout = slot.SendTimeout,
            });
            forwardReader = nextHopSession.Mux.AcceptChannel(forwardInboundId);

            // Pipe both directions.
            var t1 = PipeAsync(inbound, forwardWriter, mesh, ct);
            var t2 = PipeAsync(forwardReader, responseWriter, mesh, ct);
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
        }
        finally
        {
            if (forwardWriter is not null) { try { await forwardWriter.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (forwardReader is not null) { try { await forwardReader.DisposeAsync().ConfigureAwait(false); } catch { } }
            try { await inbound.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await responseWriter.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task PipeAsync(IReadChannel src, IWriteChannel dst, MeshMultiplexer mesh, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    mesh.RaiseError(ex);
                    return;
                }
                if (read == 0)
                {
                    return;
                }

                try
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    mesh.OnRelayBytesForwarded(read);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    mesh.RaiseError(ex);
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
