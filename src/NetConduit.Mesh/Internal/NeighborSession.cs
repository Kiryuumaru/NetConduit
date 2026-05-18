using NetConduit;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Per-neighbor session: owns the topology channel pair plus the inbound route-accept loop
/// on a registered neighbor multiplexer.
/// </summary>
internal sealed class NeighborSession : IAsyncDisposable
{
    private readonly MeshMultiplexer _mesh;
    private readonly string _remoteNodeId;
    private readonly IStreamMultiplexer _mux;
    private readonly object _sendLock = new();
    private readonly int _sessionVersion;

    private CancellationTokenSource? _cts;
    private Task? _topologyReadTask;
    private Task? _acceptTask;
    private Task? _topologyWriteTask;
    private IWriteChannel? _topologyWriter;
    private IReadChannel? _topologyReader;

    // T6 — track fire-and-forget inbound route handlers so DisposeAsync can drain them.
    // Without this, a routed sub-mux's relay/dispatch loop survives past the session's
    // own teardown, leaking background work past test end.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _pendingHandlers = new();

    // Single-flight write coalescing state. Guarded by _sendLock.
    private byte[]? _pendingFrame;
    private TaskCompletionSource? _pendingSignal;
    private volatile bool _disposed;
    private volatile bool _isHealthy = true;

    internal string RemoteNodeId => _remoteNodeId;
    internal IStreamMultiplexer Mux => _mux;
    internal string? RemotePoolId { get; }

    /// <summary>
    /// Monotonic version assigned by the mesh when the session is created. Used by health
    /// transitions so a late event from a replaced session does not affect its successor.
    /// </summary>
    internal int Version => _sessionVersion;

    /// <summary>
    /// True when the underlying neighbor mux is currently connected. The neighbor stays
    /// registered across health flips; only the advertised adjacency list excludes
    /// unhealthy neighbors so BFS routes around them.
    /// </summary>
    internal bool IsHealthy => _isHealthy;

    internal void SetHealthy(bool healthy) => _isHealthy = healthy;

    internal NeighborSession(MeshMultiplexer mesh, string remoteNodeId, IStreamMultiplexer mux, string? remotePoolId, int sessionVersion)
    {
        _mesh = mesh;
        _remoteNodeId = remoteNodeId;
        _mux = mux;
        _sessionVersion = sessionVersion;
        _isHealthy = mux.IsConnected;
        RemotePoolId = remotePoolId;
    }

    internal void Start(CancellationToken meshCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(meshCt);
        CancellationToken ct = _cts.Token;

        // Open topology channels on this neighbor mux.
        _topologyWriter = _mux.OpenChannel(new ChannelOptions
        {
            ChannelId = MeshChannelNaming.BuildTopologyChannel(_mesh.NodeId),
            Priority = _mesh.Options.DefaultChannelOptions.Priority,
            SlabSize = _mesh.Options.DefaultChannelOptions.SlabSize,
            SendTimeout = _mesh.Options.DefaultChannelOptions.SendTimeout,
        });
        _topologyReader = _mux.AcceptChannel(MeshChannelNaming.BuildTopologyChannel(_remoteNodeId));

        _topologyReadTask = Task.Run(() => RunTopologyReadLoopAsync(ct), ct);
        _acceptTask = Task.Run(() => RunAcceptLoopAsync(ct), ct);
        _topologyWriteTask = Task.Run(() => RunTopologyWriteLoopAsync(ct), ct);

        // Subscribe to neighbor-mux lifecycle so we can auto-cleanse a dead neighbor and
        // re-broadcast our topology on recovery. Subscription happens after the topology
        // channels are open so an immediate Connected re-broadcast does not race a missing
        // writer.
        _mux.Disconnected += OnMuxDisconnected;
        _mux.Connected += OnMuxConnected;

        // Push our initial full topology.
        SendTopology(_mesh.SnapshotLocalEntries());
    }

    private void OnMuxDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // Neighbor registration is sticky — we do NOT remove the neighbor when its
        // underlying mux drops. Instead we mark the session unhealthy so the mesh
        // re-advertises its local adjacency without this neighbor, causing peers to
        // route around it until it comes back. The neighbor's own mux owns its
        // reconnect strategy; when it recovers we re-mark healthy on Connected.
        if (_disposed) return;
        _mesh.OnNeighborHealthChanged(_remoteNodeId, _sessionVersion, healthy: false);
    }

    private void OnMuxConnected(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _mesh.OnNeighborHealthChanged(_remoteNodeId, _sessionVersion, healthy: true);
        // Help the recovered neighbor relearn our adjacency.
        try { SendTopology(_mesh.SnapshotLocalEntries()); } catch { }
    }

    /// <summary>
    /// Queue a topology frame for transmission. At most one write is in flight per neighbor;
    /// new advertisements while a write is pending replace the queued frame (last-writer-wins).
    /// </summary>
    internal void SendTopology(IReadOnlyCollection<TopologyEntry> entries)
    {
        if (_disposed || _topologyWriter is null)
        {
            return;
        }
        byte[] frame = TopologyWireFormat.Encode(entries);

        TaskCompletionSource? signal = null;
        lock (_sendLock)
        {
            _pendingFrame = frame;          // last-writer-wins
            signal = _pendingSignal;        // wake the writer loop if it's idle
        }
        signal?.TrySetResult();
    }

    private async Task RunTopologyWriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                TaskCompletionSource signal;
                lock (_sendLock)
                {
                    frame = _pendingFrame;
                    _pendingFrame = null;
                    if (frame is null)
                    {
                        signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingSignal = signal;
                    }
                    else
                    {
                        signal = null!;
                    }
                }

                if (frame is null)
                {
                    try
                    {
                        await signal.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    lock (_sendLock)
                    {
                        _pendingSignal = null;
                    }
                    continue;
                }

                try
                {
                    await _topologyWriter!.WriteAsync(frame, ct).ConfigureAwait(false);
                    _mesh.OnTopologySent();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (!_disposed)
                {
                    _mesh.RaiseError(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunTopologyReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await _topologyReader!.WaitForReadyAsync(ct).ConfigureAwait(false);
            using var stream = _topologyReader.AsStream();
            while (!ct.IsCancellationRequested)
            {
                List<TopologyEntry> entries;
                try
                {
                    entries = await TopologyWireFormat.ReadFrameAsync(
                        stream, _mesh.Options.MaxTopologyMessageSize, ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }
                catch (InvalidDataException ex)
                {
                    _mesh.RaiseError(ex);
                    return;
                }
                _mesh.OnTopologyMessageReceived(entries);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            _mesh.RaiseError(ex);
        }
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var inbound in _mux.AcceptChannelsAsync(ct).ConfigureAwait(false))
            {
                if (!MeshChannelNaming.IsReserved(inbound.ChannelId))
                {
                    // Not a mesh channel — ignore (caller handles application channels).
                    continue;
                }

                if (inbound.ChannelId.StartsWith(MeshChannelNaming.TopologyFromPrefix, StringComparison.Ordinal))
                {
                    // Topology channels are already set up via AcceptChannel; not handled in this loop.
                    continue;
                }

                if (!MeshChannelNaming.TryParseOutboundRoute(inbound.ChannelId, out var info))
                {
                    // Malformed or unknown mesh subprefix; close inbound and continue.
                    try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
                    continue;
                }

                // Open paired response channel back over this same neighbor mux.
                var responseId = MeshChannelNaming.BuildInboundRoute(
                    info.TargetNodeId, info.SourceNodeId, info.MultiplexerId, info.Nonce);

                IWriteChannel responseWriter;
                try
                {
                    responseWriter = _mux.OpenChannel(new ChannelOptions
                    {
                        ChannelId = responseId,
                        Priority = _mesh.Options.DefaultChannelOptions.Priority,
                        SlabSize = _mesh.Options.DefaultChannelOptions.SlabSize,
                        SendTimeout = _mesh.Options.DefaultChannelOptions.SendTimeout,
                    });
                }
                catch (Exception ex)
                {
                    _mesh.RaiseError(ex);
                    try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
                    continue;
                }

                var handlerTask = HandleInboundRouteAsync(info, inbound, responseWriter, ct);
                if (!handlerTask.IsCompleted)
                {
                    _pendingHandlers.TryAdd(handlerTask, 0);
                    _ = handlerTask.ContinueWith(static (t, state) =>
                    {
                        var dict = (System.Collections.Concurrent.ConcurrentDictionary<Task, byte>)state!;
                        dict.TryRemove(t, out _);
                    }, _pendingHandlers, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            _mesh.RaiseError(ex);
        }
    }

    private async Task HandleInboundRouteAsync(RouteChannelInfo info, IReadChannel inbound, IWriteChannel responseWriter, CancellationToken ct)
    {
        try
        {
            if (string.Equals(info.TargetNodeId, _mesh.NodeId, StringComparison.Ordinal))
            {
                // Terminus: dispatch to acceptor side.
                await _mesh.DispatchInboundRouteAsync(info.SourceNodeId, info.MultiplexerId, inbound, responseWriter).ConfigureAwait(false);
                return;
            }

            // Relay.
            if (!_mesh.TryReserveRelaySlot())
            {
                try { await inbound.CloseAsync(ct).ConfigureAwait(false); } catch { }
                try { await responseWriter.CloseAsync(ct).ConfigureAwait(false); } catch { }
                return;
            }

            try
            {
                await RouteForwarder.RunRelayAsync(_mesh, info, inbound, responseWriter, ct).ConfigureAwait(false);
            }
            finally
            {
                _mesh.ReleaseRelaySlot();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            _mesh.RaiseError(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _mux.Disconnected -= OnMuxDisconnected;
        _mux.Connected -= OnMuxConnected;

        try { _cts?.Cancel(); } catch { }

        // Wake the write loop so it observes cancellation promptly.
        TaskCompletionSource? signal;
        lock (_sendLock)
        {
            signal = _pendingSignal;
            _pendingSignal = null;
            _pendingFrame = null;
        }
        signal?.TrySetCanceled();

        if (_topologyWriter is not null)
        {
            try { await _topologyWriter.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (_topologyReader is not null)
        {
            try { await _topologyReader.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (_topologyReadTask is not null)
        {
            try { await _topologyReadTask.ConfigureAwait(false); } catch { }
        }
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }
        if (_topologyWriteTask is not null)
        {
            try { await _topologyWriteTask.ConfigureAwait(false); } catch { }
        }

        // T6 — drain inbound route handlers so they don't leak past session disposal.
        var pending = _pendingHandlers.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }

        _cts?.Dispose();
    }
}
