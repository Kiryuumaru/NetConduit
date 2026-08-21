using NetConduit;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Local-side routed sub-multiplexer (opener). The inner StreamMultiplexer's
/// StreamFactory runs BFS, opens route channels on the chosen next-hop neighbor mux,
/// and returns the channel pair as a StreamPair.
/// </summary>
internal sealed class OpenerSession : RoutedSessionBase
{
    private readonly string _targetNodeId;
    private readonly string _multiplexerId;

    internal OpenerSession(MeshMultiplexer mesh, string targetNodeId, string multiplexerId)
        : base(mesh)
    {
        _targetNodeId = targetNodeId;
        _multiplexerId = multiplexerId;
    }

    /// <inheritdoc />
    protected override MultiplexerOptions BuildMultiplexerOptions()
    {
        var sessionId = DeterministicSessionId.Compute(Mesh.NodeId, _targetNodeId, _multiplexerId);
        var opts = Mesh.Options;
        return new MultiplexerOptions
        {
            StreamFactory = CreateRouteStreamAsync,
            SessionId = sessionId,
            PingInterval = opts.PingInterval,
            PingTimeout = opts.PingTimeout,
            MaxMissedPings = opts.MaxMissedPings,
            GoAwayTimeout = opts.GoAwayTimeout,
            // MaxRouteRetries shares the same convention as MaxAutoReconnectAttempts:
            // -1 = unbounded retries (replay enabled), 0 = no retries, >0 = bounded.
            MaxAutoReconnectAttempts = opts.MaxRouteRetries,
            ConnectionTimeout = opts.RouteTimeout,
            DefaultChannelOptions = opts.DefaultChannelOptions,
        };
    }

    /// <inheritdoc />
    protected override void RemoveFromMesh()
        => Mesh.RemoveOpener(_targetNodeId, _multiplexerId);

    private async Task<IStreamPair> CreateRouteStreamAsync(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Mesh.ShutdownToken);
        try
        {
            return await OpenRouteAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            linked.Dispose();
        }
    }

    private async Task<IStreamPair> OpenRouteAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Mesh.Options.RouteTimeout;
        Exception? lastError = null;

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            // Snapshot the route version BEFORE we read the route table so we can
            // detect a recompute that lands while we're trying to open.
            long versionSeen = Mesh.CurrentRouteVersion;

            bool openAttempted = false;
            if (Mesh.TryGetRoute(_targetNodeId, out string nextHop, out _) &&
                Mesh.TryGetNeighbor(nextHop, out var nextHopSession))
            {
                openAttempted = true;
                long nonce = Mesh.NextNonce();
                string outboundId = MeshChannelNaming.BuildOutboundRoute(
                    _targetNodeId, Mesh.NodeId, _multiplexerId, nonce);
                string inboundId = MeshChannelNaming.BuildInboundRoute(
                    _targetNodeId, Mesh.NodeId, _multiplexerId, nonce);

                var slot = Mesh.Options.DefaultChannelOptions;
                IWriteChannel? writer = null;
                IReadChannel? reader = null;
                try
                {
                    writer = nextHopSession.Mux.OpenChannel(new ChannelOptions
                    {
                        ChannelId = outboundId,
                        Priority = slot.Priority,
                        SlabSize = slot.SlabSize,
                        SendTimeout = slot.SendTimeout,
                    });
                    reader = nextHopSession.Mux.AcceptChannel(inboundId);

                    await Task.WhenAll(
                        writer.WaitForReadyAsync(ct),
                        reader.WaitForReadyAsync(ct)).ConfigureAwait(false);

                    Mesh.OnRouteSucceeded();
                    return new StreamPair(reader.AsStream(), writer.AsStream(),
                        new ChannelPairOwner(reader, writer));
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (writer is not null) { try { await writer.DisposeAsync().ConfigureAwait(false); } catch { } }
                    if (reader is not null) { try { await reader.DisposeAsync().ConfigureAwait(false); } catch { } }
                }
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            if (openAttempted)
            {
                // We had a route but the open itself failed (channel collision, neighbor
                // mux transient hiccup, etc.). Short backoff then retry — waiting on a
                // route-table change here would deadlock because the route hasn't moved.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            // No route: wait for the route table to change, bounded by the remaining
            // deadline. Event-driven so we wake immediately on recompute instead of
            // busy-polling.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(remaining);
            try
            {
                await Mesh.WaitForRouteChangeAsync(versionSeen, waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Deadline elapsed — exit the outer loop.
                break;
            }
        }

        Mesh.OnRouteFailed();
        throw new MeshRoutingException(_targetNodeId,
            $"No route to '{_targetNodeId}' within RouteTimeout.", lastError ?? new TimeoutException());
    }
}

/// <summary>Disposes a route channel pair when the wrapping StreamPair is disposed.</summary>
internal sealed class ChannelPairOwner : IAsyncDisposable
{
    private readonly IReadChannel _reader;
    private readonly IWriteChannel _writer;

    internal ChannelPairOwner(IReadChannel reader, IWriteChannel writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _reader.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
