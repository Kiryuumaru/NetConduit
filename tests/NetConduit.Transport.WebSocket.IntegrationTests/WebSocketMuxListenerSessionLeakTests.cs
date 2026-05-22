using System.Reflection;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

/// <summary>
/// #279: <see cref="WebSocketMuxListener"/> must evict <c>_sessions</c> entries
/// automatically once the underlying mux transitions to a terminal state
/// (LocalDispose, GoAwayReceived). Otherwise long-lived listeners leak unbounded
/// memory, and reconnect attempts against a dead session GUID are black-holed
/// into a disposed mux's connection channel.
/// </summary>
public class WebSocketMuxListenerSessionLeakTests
{
    [Fact(Timeout = 60000)]
    public async Task DisposedMux_IsAutomaticallyEvictedFromSessions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var harness = await ListenerHarness.StartAsync(cts.Token);

        var (clientMux, serverMux) = await harness.OpenSessionAsync(cts.Token);

        // Dispose the client side FIRST so it stops attempting to reconnect.
        // Otherwise the client's StreamFactory will spin up a fresh WebSocket
        // immediately after we tear down the server side, and that fresh pair
        // would register as a brand-new session entry, masking the eviction.
        await clientMux.DisposeAsync();
        await serverMux.DisposeAsync();
        Assert.Equal(DisconnectReason.LocalDispose, serverMux.DisconnectReason);

        var sessions = GetSessions(harness.Listener);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (sessions.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }
        Assert.Empty(sessions);
    }

    [Fact(Timeout = 60000)]
    public async Task ReconnectToDeadSession_EvictsAndCreatesNewSession_DoesNotBlackHole()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var harness = await ListenerHarness.StartAsync(cts.Token);

        var (clientMux1, serverMux1) = await harness.OpenSessionAsync(cts.Token);
        var deadSessionId = serverMux1.SessionId;

        await serverMux1.DisposeAsync();
        Assert.Equal(DisconnectReason.LocalDispose, serverMux1.DisconnectReason);
        await clientMux1.DisposeAsync();

        // Reconnect with the dead session GUID. Pre-fix: HandleAsync would route
        // the new WS pair into the dead session's connection channel and the
        // disposed mux's StreamFactory would never read it; the test would hang.
        // Post-fix: routing detects IsTerminallyDead, evicts the entry, falls
        // through to the new-session branch, and a fresh mux is produced.
        var (clientMux2, serverMux2) = await harness.OpenSessionAsync(cts.Token, sessionId: deadSessionId);
        Assert.NotEqual(deadSessionId, serverMux2.SessionId);

        await serverMux2.DisposeAsync();
        await clientMux2.DisposeAsync();
    }

    private static System.Collections.IDictionary GetSessions(WebSocketMuxListener listener)
    {
        var field = typeof(WebSocketMuxListener).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_sessions field not found");
        return (System.Collections.IDictionary)field.GetValue(listener)!;
    }

    private sealed class ListenerHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<IStreamMultiplexer> _serverMuxQueue;
        private readonly Task _consumerTask;
        private readonly CancellationTokenSource _consumerCts;
        private readonly string _wsUrl;

        public WebSocketMuxListener Listener { get; }

        private ListenerHarness(
            WebApplication app,
            WebSocketMuxListener listener,
            Channel<IStreamMultiplexer> queue,
            Task consumerTask,
            CancellationTokenSource consumerCts,
            string wsUrl)
        {
            _app = app;
            Listener = listener;
            _serverMuxQueue = queue;
            _consumerTask = consumerTask;
            _consumerCts = consumerCts;
            _wsUrl = wsUrl;
        }

        public static async Task<ListenerHarness> StartAsync(CancellationToken ct)
        {
            var listener = new WebSocketMuxListener();
            var queue = Channel.CreateUnbounded<IStreamMultiplexer>();
            var consumerCts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                Guid? sessionId = null;
                if (Guid.TryParse(context.Request.Query["session"], out var parsed))
                {
                    sessionId = parsed;
                }

                var ws = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    await listener.HandleAsync(ws, sessionId, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
            });

            await app.StartAsync(ct);
            var wsUrl = app.Urls.First().Replace("http://", "ws://") + "/ws";

            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var mux in listener.AcceptMuxesAsync(consumerCts.Token))
                    {
                        mux.Start();
                        await queue.Writer.WriteAsync(mux, consumerCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, consumerCts.Token);

            return new ListenerHarness(app, listener, queue, consumerTask, consumerCts, wsUrl);
        }

        public async Task<(IStreamMultiplexer Client, IStreamMultiplexer Server)> OpenSessionAsync(
            CancellationToken ct,
            Guid? sessionId = null)
        {
            var uri = sessionId is null
                ? new Uri(_wsUrl)
                : new Uri($"{_wsUrl}?session={sessionId.Value}");

            var clientOptions = WebSocketMultiplexer.CreateOptions(uri);
            var clientMux = StreamMultiplexer.Create(clientOptions);
            clientMux.Start();

            var serverMux = await _serverMuxQueue.Reader.ReadAsync(ct);
            await clientMux.WaitForReadyAsync(ct);
            await serverMux.WaitForReadyAsync(ct);

            return (clientMux, serverMux);
        }

        public async ValueTask DisposeAsync()
        {
            _consumerCts.Cancel();
            try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await Listener.DisposeAsync();
            try { await _app.StopAsync(CancellationToken.None); } catch { }
            await _app.DisposeAsync();
            _consumerCts.Dispose();
        }
    }
}
