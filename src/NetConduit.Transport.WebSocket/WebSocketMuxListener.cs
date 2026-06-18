using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetConduit.Events;
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
            // Reconnect routing: route into an existing session's connection channel.
            // The Disconnected handler installed on each mux below evicts the entry
            // from _sessions and completes the channel writer on terminal lifecycle
            // (transport error / GoAway / dispose). If we observe TryGetValue success
            // but the writer was already completed by a concurrent eviction, fall
            // through to the new-session branch instead of throwing
            // ChannelClosedException at the caller.
            bool routedToExistingSession = false;
            if (sessionId.HasValue)
            {
                if (!_sessions.TryGetValue(sessionId.Value, out var entry))
                {
                    // Stale or unknown session id: the client is asking to resume a session
                    // that this listener does not have. Silently spinning up a fresh mux
                    // would (a) make the client's reconnect handshake fail with
                    // SessionMismatch on a session GUID it never asked for, terminally
                    // poisoning its mux, and (b) leak that fresh mux in _sessions under a
                    // key the client cannot reach. Refuse the resume cleanly so the
                    // client can decide whether to fall back to a brand-new session.
                    try
                    {
                        await webSocket.CloseOutputAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                            "Unknown session id.",
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best effort: peer may already be gone.
                    }
                    try { await pair.DisposeAsync().ConfigureAwait(false); } catch { }
                    return;
                }

                try
                {
                    await entry.ConnectionChannel.Writer.WriteAsync(pair, cancellationToken);
                    ownedByMux = true;
                    routedToExistingSession = true;
                }
                catch (ChannelClosedException)
                {
                    // The mux for this session reached a terminal state between
                    // TryGetValue and WriteAsync; its Disconnected handler has
                    // already evicted the entry. Refuse the resume the
                    // same way the unknown-session branch does: falling through
                    // to fresh-mux creation would silently bind this pair to a
                    // brand-new SessionId, which causes the client's reconnect
                    // handshake to fault with SessionMismatch and leaks a
                    // server-side mux under a key the client cannot reach —
                    // the exact defect fixed and re-surfaced.
                    try
                    {
                        await webSocket.CloseOutputAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                            "Session has been evicted.",
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best effort: peer may already be gone.
                    }
                    try { await pair.DisposeAsync().ConfigureAwait(false); } catch { }
                    return;
                }
            }

            if (!routedToExistingSession)
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
                var muxSessionId = mux.SessionId;
                EventHandler<DisconnectedEventArgs> evictHandler =
                    (_, _) => RemoveSession(muxSessionId);
                try
                {
                    // Install the eviction subscription *before* registering in
                    // _sessions so any Disconnected event raised after registration
                    // is guaranteed to be observed and clean up the entry.
                    mux.Disconnected += evictHandler;

                    // Register in _sessions BEFORE publishing so a consumer that
                    // immediately calls Start() and triggers Disconnected can find
                    // the entry to evict. If publish fails (cancellation), the
                    // catch below disposes the mux which fires Disconnected which
                    // invokes evictHandler → RemoveSession (idempotent TryRemove).
                    _sessions[muxSessionId] = new SessionEntry(mux, connectionChannel);

                    await _newMuxChannel.Writer.WriteAsync(mux, cancellationToken);
                    ownedByMux = true;
                }
                catch
                {
                    // Mux never reached a consumer — tear it down. The eviction
                    // handler removes the _sessions entry and completes the
                    // connection-channel writer; the pair sitting in
                    // connectionChannel is disposed by the outer catch via
                    // ownedByMux == false.
                    try { await mux.DisposeAsync(); } catch { }
                    // Defensive: in the (theoretical) case where DisposeAsync
                    // does not raise Disconnected (already-disposed early-return
                    // path), evict directly so the entry cannot leak.
                    RemoveSession(muxSessionId);
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
    /// On reconnection, the client appends the server's session ID as a <c>session=</c> query
    /// parameter while preserving any other query parameters present on <paramref name="baseUri"/>
    /// (e.g. auth tokens or tenant routing keys).
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
                    uri = BuildReconnectUri(baseUri, rid);
                }

                try
                {
                    await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                    var stream = new WebSocketStream(ws);
                    return new StreamPair(stream, ws);
                }
                catch
                {
                    ws.Dispose();
                    throw;
                }
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

    /// <summary>
    /// Appends a <c>session=</c> query parameter to <paramref name="baseUri"/> while preserving
    /// any pre-existing query parameters (auth tokens, tenant ids, routing keys, etc.).
    /// </summary>
    internal static Uri BuildReconnectUri(Uri baseUri, Guid sessionId)
    {
        var builder = new UriBuilder(baseUri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0
            ? $"session={sessionId}"
            : $"{existing}&session={sessionId}";
        return builder.Uri;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _newMuxChannel.Writer.TryComplete();

        // Aggregate per-session dispose failures so a single bad mux cannot
        // strand the remaining sessions or block _sessions.Clear().
        List<Exception>? errors = null;
        foreach (var entry in _sessions.Values)
        {
            try
            {
                entry.ConnectionChannel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            // Drain any buffered CompletionStreamPair items still in the
            // channel reader before disposing the mux. The reader may hold
            // pairs that the mux never consumed (e.g. a reconnect arrived,
            // was queued, but the mux DisposeAsync started before the
            // reconnect handshake began). Without draining, each leaked pair
            // holds an open WebSocket and a pending completion TCS, and the
            // corresponding HandleAsync invocation hangs on
            // completion.Task.WaitAsync until the request cancellation
            // token fires.
            while (entry.ConnectionChannel.Reader.TryRead(out var leakedPair))
            {
                try { await leakedPair.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            }

            try
            {
                await entry.Mux.DisposeAsync();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }
        _sessions.Clear();

        if (errors is { Count: 1 })
        {
            throw errors[0];
        }
        if (errors is { Count: > 1 })
        {
            throw new AggregateException(errors);
        }
    }

    private sealed record SessionEntry(IStreamMultiplexer Mux, Channel<CompletionStreamPair> ConnectionChannel);

    private sealed class CompletionStreamPair(IStreamPair inner, TaskCompletionSource completion) : IStreamPair
    {
        public Stream ReadStream => inner.ReadStream;
        public Stream WriteStream => inner.WriteStream;

        public async ValueTask DisposeAsync()
        {
            // The mux-release contract is that the mux is done with this transport
            // once it calls DisposeAsync. The completion signal must fire regardless
            // of whether the inner dispose succeeds or throws (e.g. StreamPair
            // aggregating inner-stream dispose failures), otherwise
            // HandleAsync hangs on completion.Task.WaitAsync until request
            // cancellation. Inner-dispose exceptions still propagate to the mux's
            // teardown path.
            try
            {
                await inner.DisposeAsync();
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }
}
