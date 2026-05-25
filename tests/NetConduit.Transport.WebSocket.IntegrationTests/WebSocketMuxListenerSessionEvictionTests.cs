using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NetConduit.Interfaces;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

/// <summary>
/// <see cref="WebSocketMuxListener"/> must automatically evict session
/// entries when a mux reaches a terminal lifecycle (Disconnected event), and
/// must not hang when a reconnect attempt references a dead session.
/// </summary>
public class WebSocketMuxListenerSessionEvictionTests
{
    [Fact(Timeout = 30000)]
    public async Task SessionsDict_IsEvictedWhenServerMuxDisposed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await ListenerHarness.StartAsync(cts.Token);

        var sessions = GetSessionsDictionary(harness.Listener);

        // Open one client mux.
        var (clientMux, _) = await harness.OpenClientAsync(cts.Token);
        await using (clientMux)
        {
            // Wait for server-side mux to be accepted, started, and registered.
            var serverMux = await harness.NextStartedServerMuxAsync(cts.Token);
            await PollUntilAsync(() => sessions.Count == 1, TimeSpan.FromSeconds(5), cts.Token);
            Assert.Single(sessions);

            // Dispose server mux: Disconnected event must fire → eviction handler runs.
            await serverMux.DisposeAsync();

            await PollUntilAsync(() => sessions.Count == 0, TimeSpan.FromSeconds(5), cts.Token);
            Assert.Empty(sessions);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Reconnect_WithDeadSessionId_IsRejectedAfterAutoEviction()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await ListenerHarness.StartAsync(cts.Token);

        var sessions = GetSessionsDictionary(harness.Listener);

        // 1. Open first session, capture sessionId, then dispose its server mux.
        //    The auto-eviction removes the entry from _sessions; combined
        //    with master's (reject unknown session ids), a subsequent reconnect
        //    using the same session id must be cleanly rejected rather than silently
        //    promoted to a fresh session under a GUID the client never asked for.
        var (clientMux1, _) = await harness.OpenClientAsync(cts.Token);
        var serverMux1 = await harness.NextStartedServerMuxAsync(cts.Token);
        await PollUntilAsync(() => sessions.Count == 1, TimeSpan.FromSeconds(5), cts.Token);

        var deadSessionId = serverMux1.SessionId;
        Assert.NotEqual(Guid.Empty, deadSessionId);

        await serverMux1.DisposeAsync();
        await PollUntilAsync(() => sessions.Count == 0, TimeSpan.FromSeconds(5), cts.Token);

        try { await clientMux1.DisposeAsync(); } catch { }

        // 2. Reconnect with the dead session id. The server should refuse the
        //    resume (PolicyViolation close) instead of hanging on the dead
        //    ConnectionChannel — the listener must not be left in a stuck state.
        //    The client connect attempt is expected to fail; what matters is that
        //    the listener does not deadlock and never registers a new session
        //    under the dead id.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var (clientMux2, _) = await harness.OpenClientAsync(cts.Token, reconnectSessionId: deadSessionId);
            try { await clientMux2.DisposeAsync(); } catch { }
        });

        await PollUntilAsync(() => sessions.Count == 0, TimeSpan.FromSeconds(5), cts.Token);
        Assert.Empty(sessions);
    }

    [Fact(Timeout = 60000)]
    public async Task ManySessions_NaturallyEnding_DoNotAccumulate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var harness = await ListenerHarness.StartAsync(cts.Token);

        var sessions = GetSessionsDictionary(harness.Listener);

        const int sessionCount = 8;
        var clients = new List<IStreamMultiplexer>();
        var servers = new List<IStreamMultiplexer>();
        try
        {
            for (int i = 0; i < sessionCount; i++)
            {
                var (clientMux, _) = await harness.OpenClientAsync(cts.Token);
                clients.Add(clientMux);
                servers.Add(await harness.NextStartedServerMuxAsync(cts.Token));
            }

            await PollUntilAsync(() => sessions.Count == sessionCount, TimeSpan.FromSeconds(10), cts.Token);

            foreach (var s in servers)
                await s.DisposeAsync();

            await PollUntilAsync(() => sessions.Count == 0, TimeSpan.FromSeconds(20), cts.Token);
            Assert.Empty(sessions);
        }
        finally
        {
            foreach (var c in clients) { try { await c.DisposeAsync(); } catch { } }
        }
    }

    private static System.Collections.IDictionary GetSessionsDictionary(WebSocketMuxListener listener)
    {
        var field = typeof(WebSocketMuxListener).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_sessions field not found");
        return (System.Collections.IDictionary)field.GetValue(listener)!;
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20, ct);
        }
    }

    /// <summary>
    /// End-to-end harness: runs an ASP.NET WebSocket server that hands every
    /// incoming WS to a <see cref="WebSocketMuxListener"/>, a background pump
    /// that drains <see cref="WebSocketMuxListener.AcceptMuxesAsync"/> and
    /// starts each mux, and exposes a helper to open client muxes via the
    /// real <see cref="WebSocketMultiplexer.CreateOptions(Uri)"/> path.
    /// </summary>
    private sealed class ListenerHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly CancellationTokenSource _pumpCts = new();
        private readonly Task _pumpTask;
        private readonly ConcurrentQueue<IStreamMultiplexer> _startedServerMuxes = new();

        public WebSocketMuxListener Listener { get; }
        public Uri WsUri { get; }

        private ListenerHarness(WebApplication app, WebSocketMuxListener listener, Uri wsUri)
        {
            _app = app;
            Listener = listener;
            WsUri = wsUri;
            _pumpTask = RunPumpAsync(_pumpCts.Token);
        }

        public static async Task<ListenerHarness> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();

            var listener = new WebSocketMuxListener();

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                var ws = await context.WebSockets.AcceptWebSocketAsync();

                Guid? sessionId = null;
                if (Guid.TryParse(context.Request.Query["session"], out var parsed))
                    sessionId = parsed;

                try { await listener.HandleAsync(ws, sessionId, context.RequestAborted); }
                catch { }
            });

            await app.StartAsync(ct);
            var url = app.Urls.First();
            var wsUri = new Uri(url.Replace("http://", "ws://") + "/ws");

            return new ListenerHarness(app, listener, wsUri);
        }

        private async Task RunPumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var mux in Listener.AcceptMuxesAsync(ct))
                {
                    mux.Start();
                    _startedServerMuxes.Enqueue(mux);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        public async Task<(IStreamMultiplexer Mux, Action<IStreamMultiplexer> Bind)> OpenClientAsync(
            CancellationToken ct, Guid? reconnectSessionId = null)
        {
            var uri = reconnectSessionId.HasValue
                ? new Uri($"{WsUri}?session={reconnectSessionId.Value}")
                : WsUri;

            var (options, bind) = WebSocketMuxListener.CreateReconnectableClientOptions(uri);
            var mux = StreamMultiplexer.Create(options);
            bind(mux);
            mux.Start();
            await mux.WaitForReadyAsync(ct);
            return (mux, bind);
        }

        public async Task<IStreamMultiplexer> NextStartedServerMuxAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_startedServerMuxes.TryDequeue(out var mux))
                {
                    await mux.WaitForReadyAsync(ct);
                    return mux;
                }
                await Task.Delay(20, ct);
            }
            throw new TimeoutException("No server mux was accepted within the timeout.");
        }

        public async ValueTask DisposeAsync()
        {
            _pumpCts.Cancel();
            try { await _pumpTask; } catch { }
            try { await Listener.DisposeAsync(); } catch { }
            try { await _app.StopAsync(); } catch { }
            try { await _app.DisposeAsync(); } catch { }
            _pumpCts.Dispose();
        }
    }
}
