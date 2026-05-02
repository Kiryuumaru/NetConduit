using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetConduit.Models;

namespace NetConduit.WebSocket;

/// <summary>
/// Manages WebSocket-based multiplexer sessions with reconnection support.
/// Incoming WebSocket connections are routed to existing sessions or spawn new multiplexers.
/// Each <see cref="HandleAsync"/> call blocks until the multiplexer releases that WebSocket,
/// keeping the HTTP request alive for the duration of the connection.
/// </summary>
/// <remarks>
/// <para>
/// Server-side usage with ASP.NET Core:
/// <code>
/// var listener = new WebSocketMuxListener(opts => opts with
/// {
///     MaxAutoReconnectAttempts = 0,
///     ConnectionTimeout = TimeSpan.FromSeconds(30)
/// });
///
/// app.MapGet("/ws", async (HttpContext ctx) =>
/// {
///     var ws = await ctx.WebSockets.AcceptWebSocketAsync();
///     Guid.TryParse(ctx.Request.Query["session"], out var sessionId);
///     await listener.HandleAsync(ws, sessionId == Guid.Empty ? null : sessionId, ctx.RequestAborted);
/// });
///
/// await foreach (var mux in listener.AcceptMuxesAsync(ct))
/// {
///     _ = HandleSessionAsync(mux);
/// }
/// </code>
/// </para>
/// <para>
/// Client-side reconnection sends the server's session ID as a query parameter.
/// Use <see cref="CreateReconnectableClientOptions(Uri, Action{System.Net.WebSockets.ClientWebSocketOptions})"/> for a client that works with this listener.
/// </para>
/// </remarks>
public sealed class WebSocketMuxListener : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly Channel<IStreamMultiplexer> _newMuxChannel = Channel.CreateUnbounded<IStreamMultiplexer>();
    private readonly Func<MultiplexerOptions, MultiplexerOptions>? _customize;

    /// <param name="customize">
    /// Optional transformation applied to each new multiplexer's options.
    /// Receives options with <see cref="MultiplexerOptions.StreamFactory"/> already configured.
    /// Use <c>with</c> to set reconnection, ping, and timeout options.
    /// <see cref="MultiplexerOptions.StreamFactory"/> is always restored after the transformation.
    /// </param>
    public WebSocketMuxListener(Func<MultiplexerOptions, MultiplexerOptions>? customize = null)
    {
        _customize = customize;
    }

    /// <summary>
    /// Handles an incoming WebSocket connection.
    /// Routes to an existing session for reconnection, or creates a new multiplexer session.
    /// Blocks until the multiplexer releases this WebSocket (on disconnection, reconnection, or shutdown).
    /// </summary>
    /// <param name="webSocket">The accepted WebSocket connection.</param>
    /// <param name="sessionId">
    /// The server session ID for reconnection routing.
    /// Pass <c>null</c> for new connections.
    /// For reconnections, pass the server's <see cref="IStreamMultiplexer.SessionId"/>
    /// (sent by the client as a query parameter).
    /// </param>
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

        if (sessionId.HasValue && _sessions.TryGetValue(sessionId.Value, out var entry))
        {
            await entry.ConnectionChannel.Writer.WriteAsync(pair, cancellationToken);
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
            _sessions[mux.SessionId] = new SessionEntry(mux, connectionChannel);

            await _newMuxChannel.Writer.WriteAsync(mux, cancellationToken);
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Yields newly created multiplexer sessions as WebSocket clients connect.
    /// The consumer is responsible for calling <see cref="IStreamMultiplexer.Start"/> on each mux.
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
    /// Call when a multiplexer is permanently done (disposed, reconnect failed, etc.).
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
    /// Creates multiplexer options for a client that reconnects through a <see cref="WebSocketMuxListener"/>.
    /// On reconnection, the client includes the server's session ID as a <c>?session=</c> query parameter
    /// so the listener can route the WebSocket to the correct multiplexer.
    /// </summary>
    /// <param name="baseUri">The WebSocket URI to connect to (e.g., <c>ws://host/ws</c>).</param>
    /// <param name="configureWebSocket">Optional action to configure each <see cref="System.Net.WebSockets.ClientWebSocket.Options"/>.</param>
    /// <returns>
    /// A tuple of options and a bind action. After creating the multiplexer with
    /// <see cref="StreamMultiplexer.Create"/>, call the bind action to enable session-aware reconnection.
    /// <code>
    /// var (options, bind) = WebSocketMuxListener.CreateReconnectableClientOptions(new Uri("ws://host/ws"));
    /// var customized = options with { MaxAutoReconnectAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(10) };
    /// var mux = StreamMultiplexer.Create(customized);
    /// bind(mux);
    /// var runTask = mux.Start(ct);
    /// </code>
    /// </returns>
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

    /// <summary>
    /// Wraps an <see cref="IStreamPair"/> and signals a <see cref="TaskCompletionSource"/>
    /// when disposed. This keeps the HTTP request alive until the multiplexer releases the WebSocket.
    /// </summary>
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
