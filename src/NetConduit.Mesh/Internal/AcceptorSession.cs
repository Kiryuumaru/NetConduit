using System.Threading.Channels;
using NetConduit;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Remote-side routed sub-multiplexer (acceptor). Buffers inbound route pairs from
/// the mesh dispatcher; the inner StreamMultiplexer's StreamFactory drains them
/// one at a time across initial connect and subsequent reroute cycles.
/// </summary>
internal sealed class AcceptorSession : RoutedSessionBase
{
    private readonly string _sourceNodeId;
    private readonly string _multiplexerId;
    private readonly Channel<(IReadChannel Reader, IWriteChannel Writer)> _incoming;
    private volatile bool _explicit;

    internal AcceptorSession(MeshMultiplexer mesh, string sourceNodeId, string multiplexerId, bool isExplicit)
        : base(mesh)
    {
        _sourceNodeId = sourceNodeId;
        _multiplexerId = multiplexerId;
        _explicit = isExplicit;
        _incoming = Channel.CreateUnbounded<(IReadChannel, IWriteChannel)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    internal void MarkExplicit() => _explicit = true;

    /// <inheritdoc />
    protected override MultiplexerOptions BuildMultiplexerOptions()
    {
        var sessionId = DeterministicSessionId.Compute(Mesh.NodeId, _sourceNodeId, _multiplexerId);
        var opts = Mesh.Options;
        return new MultiplexerOptions
        {
            StreamFactory = CreateRouteStreamAsync,
            SessionId = sessionId,
            DefaultSlabSize = opts.DefaultSlabSize,
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
    protected override void OnConstructed() => Mesh.OnSubMultiplexerOpened();

    /// <inheritdoc />
    protected override void RemoveFromMesh()
        => Mesh.RemoveAcceptor(_sourceNodeId, _multiplexerId);

    /// <inheritdoc />
    protected override async ValueTask OnClosingAsync()
    {
        // Reject further inbound pairs and drain anything still buffered.
        _incoming.Writer.TryComplete();
        while (_incoming.Reader.TryRead(out var pair))
        {
            try { await pair.Reader.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await pair.Writer.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Wait for the next route pair from the dispatcher and hand it to the inner
    /// StreamMultiplexer as a fresh transport. The inner mux already applies
    /// <c>ConnectionTimeout</c> (= <c>RouteTimeout</c>) to the token it passes here,
    /// so no second timer is needed: per-call cancellation is bounded by the inner,
    /// and pair-readiness uses the same token. This avoids the stale-CTS hazard of
    /// applying a method-scoped timer that conflates "wait for next pair" with
    /// "wait for that pair to handshake".
    /// </summary>
    private async Task<IStreamPair> CreateRouteStreamAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Mesh.ShutdownToken);
        var token = linked.Token;

        try
        {
            while (await _incoming.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (_incoming.Reader.TryRead(out var pair))
                {
                    await Task.WhenAll(
                        pair.Reader.WaitForReadyAsync(token),
                        pair.Writer.WaitForReadyAsync(token)).ConfigureAwait(false);
                    return new StreamPair(pair.Reader.AsStream(), pair.Writer.AsStream(),
                        new ChannelPairOwner(pair.Reader, pair.Writer));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Mesh.OnRouteFailed();
        throw new MeshRoutingException(_sourceNodeId,
            $"No incoming route from '{_sourceNodeId}' within RouteTimeout.");
    }

    internal async Task OnIncomingRouteAsync(IReadChannel reader, IWriteChannel writer, ChannelWriter<RoutedMultiplexer> inbox)
    {
        if (IsClosed)
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await writer.DisposeAsync().ConfigureAwait(false); } catch { }
            return;
        }

        bool firstArrival = Inner is null;
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
            inbox.TryWrite(new RoutedMultiplexer(_sourceNodeId, _multiplexerId, UserFacing!));
        }
    }
}
