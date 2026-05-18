using NetConduit;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Local-side routed sub-multiplexer (opener). Holds the StreamMultiplexer instance
/// whose StreamFactory runs BFS, opens route channels on the chosen next-hop neighbor mux,
/// and returns the channel pair as a StreamPair.
/// </summary>
internal sealed class OpenerSession : IAsyncDisposable
{
    private readonly MeshMultiplexer _mesh;
    private readonly string _targetNodeId;
    private readonly string _multiplexerId;
    private StreamMultiplexer? _subMux;
    private volatile bool _disposed;

    internal OpenerSession(MeshMultiplexer mesh, string targetNodeId, string multiplexerId)
    {
        _mesh = mesh;
        _targetNodeId = targetNodeId;
        _multiplexerId = multiplexerId;
    }

    internal IStreamMultiplexer SubMultiplexer
        => _subMux ?? throw new InvalidOperationException("Sub-mux not constructed.");

    internal void Construct()
    {
        var sessionId = DeterministicSessionId.Compute(_mesh.NodeId, _targetNodeId, _multiplexerId);
        var opts = _mesh.Options;

        var muxOptions = new MultiplexerOptions
        {
            StreamFactory = (ct) => CreateRouteStreamAsync(ct),
            SessionId = sessionId,
            DefaultSlabSize = opts.DefaultSlabSize,
            PingInterval = opts.PingInterval,
            PingTimeout = opts.PingTimeout,
            MaxMissedPings = opts.MaxMissedPings,
            GoAwayTimeout = opts.GoAwayTimeout,
            // T5 — MaxRouteRetries = -1 means unbounded. StreamMultiplexer treats
            // MaxAutoReconnectAttempts == 0 as "unlimited", so map -1 to 0.
            MaxAutoReconnectAttempts = opts.MaxRouteRetries < 0 ? 0 : opts.MaxRouteRetries,
            ConnectionTimeout = opts.RouteTimeout,
            DefaultChannelOptions = opts.DefaultChannelOptions,
        };

        _subMux = StreamMultiplexer.Create(muxOptions);
        _subMux.Disconnected += OnSubMuxDisconnected;
        _subMux.Start();
    }

    private void OnSubMuxDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // StreamMultiplexer raises Disconnected only for terminal states (transport error,
        // GoAway received, local dispose). The mux is already tearing itself down — we
        // MUST NOT fire-and-forget a Task that re-enters _subMux.DisposeAsync() because
        // that leaks background work past the user's await routed.DisposeAsync() return,
        // polluting downstream tests. Just release mesh-side state synchronously.
        if (_disposed) return;
        _disposed = true;
        _subMux!.Disconnected -= OnSubMuxDisconnected;
        _mesh.OnSubMultiplexerClosed();
        _mesh.RemoveOpener(_targetNodeId, _multiplexerId);
    }

    private async Task<IStreamPair> CreateRouteStreamAsync(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _mesh.ShutdownToken);
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
        var deadline = DateTime.UtcNow + _mesh.Options.RouteTimeout;
        Exception? lastError = null;

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            // T0 — snapshot the route version BEFORE we read the route table so we can
            // detect a recompute that lands while we're trying to open.
            long versionSeen = _mesh.CurrentRouteVersion;

            bool openAttempted = false;
            if (_mesh.TryGetRoute(_targetNodeId, out string nextHop, out _) &&
                _mesh.TryGetNeighbor(nextHop, out var nextHopSession))
            {
                openAttempted = true;
                long nonce = _mesh.NextNonce();
                string outboundId = MeshChannelNaming.BuildOutboundRoute(
                    _targetNodeId, _mesh.NodeId, _multiplexerId, nonce);
                string inboundId = MeshChannelNaming.BuildInboundRoute(
                    _targetNodeId, _mesh.NodeId, _multiplexerId, nonce);

                var slot = _mesh.Options.DefaultChannelOptions;
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

                    _mesh.OnRouteSucceeded();
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

            // T0 — no route. Wait for the route table to change, bounded by the remaining
            // deadline. This replaces the previous 50ms busy-poll for the "no route" case.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(remaining);
            try
            {
                await _mesh.WaitForRouteChangeAsync(versionSeen, waitCts.Token).ConfigureAwait(false);
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

        _mesh.OnRouteFailed();
        throw new MeshRoutingException(_targetNodeId,
            $"No route to '{_targetNodeId}' within RouteTimeout.", lastError ?? new TimeoutException());
    }

    internal async Task GoAwayAsync(CancellationToken ct)
    {
        if (_subMux is not null)
        {
            try { await _subMux.GoAwayAsync(ct).ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subMux is not null)
        {
            _subMux.Disconnected -= OnSubMuxDisconnected;
            try { await _subMux.DisposeAsync().ConfigureAwait(false); } catch { }
            _mesh.OnSubMultiplexerClosed();
            _mesh.RemoveOpener(_targetNodeId, _multiplexerId);
        }
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
