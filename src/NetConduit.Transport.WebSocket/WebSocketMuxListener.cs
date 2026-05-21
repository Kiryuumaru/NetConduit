using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.WebSocket;

/// <summary>
/// Manages WebSocket-based multiplexer sessions with reconnection support.
/// Incoming WebSocket connections are routed to existing sessions or spawn new multiplexers.
/// </summary>
public sealed class WebSocketMuxListener : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly Channel<IStreamMultiplexer> _newMuxChannel = Channel.CreateUnbounded<IStreamMultiplexer>();
    private readonly Func<MultiplexerOptions, MultiplexerOptions>? _customize;

    /// <param name="customize">
    /// Optional transformation applied to each new multiplexer's options.
    /// StreamFactory is always restored after transformation.
    /// </param>
    public WebSocketMuxListener(Func<MultiplexerOptions, MultiplexerOptions>? customize = null)
    {
        _customize = customize;
    }

    /// <summary>
    /// Handles an incoming WebSocket connection.
    /// Routes to an existing session for reconnection, or creates a new multiplexer session.
    /// Blocks until the multiplexer releases this WebSocket.
    /// </summary>
    /// <param name="webSocket">The accepted WebSocket connection.</param>
    /// <param name="sessionId">The server session ID for reconnection routing. Pass null for new connections.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task HandleAsync(
        System.Net.WebSockets.WebSocket webSocket,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webSocket);

        var stream = new WebSocketStream(webSocket);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new CompletionStreamPair(new StreamPair(stream, webSocket), completion);

        // Until the pair is sitting inside a mux that has accepted ownership,
        // HandleAsync is responsible for disposing it on any failure.
        // After successful hand-off, the mux owns the pair lifetime.
        var ownedByMux = false;
        try
        {
            if (sessionId.HasValue && _sessions.TryGetValue(sessionId.Value, out var entry))
            {
                await entry.ConnectionChannel.Writer.WriteAsync(pair, cancellationToken);
                ownedByMux = true;
            }
            else
            {
                var connectionChannel = Channel.CreateBounded<CompletionStreamPair>(1);
                await connectionChannel.Writer.WriteAsync(pair, cancellationToken);

                StreamFactoryDelegate factory = async ct =>
                    await connectionChannel.Reader.ReadAsync(ct);

                var options = new MultiplexerOptions { StreamFactory = factory };

                if (_customize is not null)
                {
                    options = _customize(options) with { StreamFactory = factory };
                }

                var mux = StreamMultiplexer.Create(options);
                try
                {
                    // Publish to consumers FIRST so a cancellation between _sessions registration
                    // and _newMuxChannel write cannot strand the mux in _sessions with no consumer.
                    await _newMuxChannel.Writer.WriteAsync(mux, cancellationToken);
                    _sessions[mux.SessionId] = new SessionEntry(mux, connectionChannel);
                    ownedByMux = true;
                }
                catch
                {
                    // Mux never reached a consumer — tear it down. The pair still sitting in
                    // connectionChannel is disposed by the outer catch via ownedByMux == false.
                    connectionChannel.Writer.TryComplete();
                    try { await mux.DisposeAsync(); } catch { }
                    throw;
                }
            }

            await completion.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (!ownedByMux)
            {
                try { await pair.DisposeAsync(); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Yields newly created multiplexer sessions as WebSocket clients connect.
    /// The consumer is responsible for calling Start on each mux.
    /// </summary>
    public async IAsyncEnumerable<IStreamMultiplexer> AcceptMuxesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var mux in _newMuxChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return mux;
        }
    }

    /// <summary>
    /// Removes a session from the listener's routing table.
    /// </summary>
    public bool RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            entry.ConnectionChannel.Writer.TryComplete();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Creates multiplexer options for a client that reconnects through a WebSocketMuxListener.
    /// On reconnection, the client includes the server's session ID as a query parameter.
    /// </summary>
    /// <param name="baseUri">The WebSocket URI to connect to.</param>
    /// <param name="configureWebSocket">Optional action to configure each ClientWebSocket.Options.</param>
    /// <returns>A tuple of options and a bind action to enable session-aware reconnection.</returns>
    public static (MultiplexerOptions Options, Action<IStreamMultiplexer> Bind) CreateReconnectableClientOptions(
        Uri baseUri,
        Action<System.Net.WebSockets.ClientWebSocketOptions>? configureWebSocket = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        IStreamMultiplexer? muxRef = null;

        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var ws = new System.Net.WebSockets.ClientWebSocket();
                configureWebSocket?.Invoke(ws.Options);

                var uri = baseUri;
                if (muxRef is { RemoteSessionId: var rid } && rid != Guid.Empty)
                {
                    var builder = new UriBuilder(baseUri);
                    builder.Query = $"session={rid}";
                    uri = builder.Uri;
                }

                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                var stream = new WebSocketStream(ws);
                return new StreamPair(stream, ws);
            }
        };

        return (options, mux => muxRef = mux);
    }

    /// <inheritdoc cref="CreateReconnectableClientOptions(Uri, Action{System.Net.WebSockets.ClientWebSocketOptions})"/>
    public static (MultiplexerOptions Options, Action<IStreamMultiplexer> Bind) CreateReconnectableClientOptions(
        string url,
        Action<System.Net.WebSockets.ClientWebSocketOptions>? configureWebSocket = null)
    {
        return CreateReconnectableClientOptions(new Uri(url), configureWebSocket);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _newMuxChannel.Writer.TryComplete();
        foreach (var entry in _sessions.Values)
        {
            entry.ConnectionChannel.Writer.TryComplete();
            await entry.Mux.DisposeAsync();
        }
        _sessions.Clear();
    }

    private sealed record SessionEntry(IStreamMultiplexer Mux, Channel<CompletionStreamPair> ConnectionChannel);

    private sealed class CompletionStreamPair(IStreamPair inner, TaskCompletionSource completion) : IStreamPair
    {
        public Stream ReadStream => inner.ReadStream;
        public Stream WriteStream => inner.WriteStream;

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            completion.TrySetResult();
        }
    }
}
