using NetConduit;
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

    private CancellationTokenSource? _cts;
    private Task? _topologyReadTask;
    private Task? _acceptTask;
    private IWriteChannel? _topologyWriter;
    private IReadChannel? _topologyReader;
    private volatile bool _disposed;

    internal string RemoteNodeId => _remoteNodeId;
    internal IStreamMultiplexer Mux => _mux;
    internal string? RemotePoolId { get; }

    internal NeighborSession(MeshMultiplexer mesh, string remoteNodeId, IStreamMultiplexer mux, string? remotePoolId)
    {
        _mesh = mesh;
        _remoteNodeId = remoteNodeId;
        _mux = mux;
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

        // Push our initial full topology.
        SendTopology(_mesh.SnapshotLocalEntries());
    }

    internal void SendTopology(IReadOnlyCollection<TopologyEntry> entries)
    {
        if (_disposed || _topologyWriter is null)
        {
            return;
        }
        byte[] frame = TopologyWireFormat.Encode(entries);

        Task.Run(async () =>
        {
            try
            {
                // Serialize writes per-neighbor to avoid frame interleaving.
                Task pending;
                lock (_sendLock)
                {
                    pending = _topologyWriter.WriteAsync(frame).AsTask();
                }
                await pending.ConfigureAwait(false);
                _mesh.OnTopologySent();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _mesh.RaiseError(ex);
            }
        });
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

                _ = HandleInboundRouteAsync(info, inbound, responseWriter, ct);
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

        try { _cts?.Cancel(); } catch { }

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

        _cts?.Dispose();
    }
}
