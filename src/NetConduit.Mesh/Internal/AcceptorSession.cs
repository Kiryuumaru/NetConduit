using System.Threading.Channels;
using NetConduit;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Remote-side routed sub-multiplexer (acceptor). Waits for an inbound route pair to arrive
/// from the dispatcher, then constructs a StreamMultiplexer whose StreamFactory yields the
/// next available pair (initial + reroutes).
/// </summary>
internal sealed class AcceptorSession : IAsyncDisposable
{
    private readonly MeshMultiplexer _mesh;
    private readonly string _sourceNodeId;
    private readonly string _multiplexerId;
    private readonly Channel<(IReadChannel Reader, IWriteChannel Writer)> _incoming;

    private StreamMultiplexer? _subMux;
    private volatile bool _explicit;
    private volatile bool _disposed;

    internal IStreamMultiplexer? SubMultiplexer => _subMux;

    internal AcceptorSession(MeshMultiplexer mesh, string sourceNodeId, string multiplexerId, bool isExplicit)
    {
        _mesh = mesh;
        _sourceNodeId = sourceNodeId;
        _multiplexerId = multiplexerId;
        _explicit = isExplicit;
        _incoming = Channel.CreateUnbounded<(IReadChannel, IWriteChannel)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    internal void MarkExplicit() => _explicit = true;

    internal void Construct()
    {
        if (_subMux is not null) return;

        var sessionId = DeterministicSessionId.Compute(_mesh.NodeId, _sourceNodeId, _multiplexerId);
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
        _mesh.OnSubMultiplexerOpened();
    }

    private void OnSubMuxDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // Terminal disconnect — release mesh-side state synchronously. Do NOT re-enter
        // _subMux.DisposeAsync(); the mux is already tearing itself down. Fire-and-forget
        // here would leak post-Dispose background work and pollute downstream tests.
        if (_disposed) return;
        _disposed = true;
        _incoming.Writer.TryComplete();
        _subMux!.Disconnected -= OnSubMuxDisconnected;
        _mesh.OnSubMultiplexerClosed();
        _mesh.RemoveAcceptor(_sourceNodeId, _multiplexerId);
    }

    private async Task<IStreamPair> CreateRouteStreamAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _mesh.ShutdownToken);
        linked.CancelAfter(_mesh.Options.RouteTimeout);

        try
        {
            while (await _incoming.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                if (_incoming.Reader.TryRead(out var pair))
                {
                    await Task.WhenAll(
                        pair.Reader.WaitForReadyAsync(linked.Token),
                        pair.Writer.WaitForReadyAsync(linked.Token)).ConfigureAwait(false);
                    return new StreamPair(pair.Reader.AsStream(), pair.Writer.AsStream(),
                        new ChannelPairOwner(pair.Reader, pair.Writer));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        _mesh.OnRouteFailed();
        throw new MeshRoutingException(_sourceNodeId,
            $"No incoming route from '{_sourceNodeId}' within RouteTimeout.");
    }

    internal async Task OnIncomingRouteAsync(IReadChannel reader, IWriteChannel writer, ChannelWriter<RoutedMultiplexer> inbox)
    {
        if (_disposed)
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await writer.DisposeAsync().ConfigureAwait(false); } catch { }
            return;
        }

        bool firstArrival = _subMux is null;
        if (firstArrival)
        {
            Construct();
        }

        if (!_incoming.Writer.TryWrite((reader, writer)))
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await writer.DisposeAsync().ConfigureAwait(false); } catch { }
            return;
        }

        if (firstArrival && !_explicit)
        {
            // Emit via AcceptMultiplexersAsync exactly once per acceptor.
            inbox.TryWrite(new RoutedMultiplexer(_sourceNodeId, _multiplexerId, _subMux!));
        }
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
        _incoming.Writer.TryComplete();

        // Drain any unprocessed pairs.
        while (_incoming.Reader.TryRead(out var pair))
        {
            try { await pair.Reader.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await pair.Writer.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (_subMux is not null)
        {
            _subMux.Disconnected -= OnSubMuxDisconnected;
            try { await _subMux.DisposeAsync().ConfigureAwait(false); } catch { }
            _mesh.OnSubMultiplexerClosed();
            _mesh.RemoveAcceptor(_sourceNodeId, _multiplexerId);
        }
    }
}
