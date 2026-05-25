using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketMuxListenerStaleSessionTests
{
    /// <summary>
    /// HandleAsync receiving a sessionId that does not exist in _sessions
    /// must reject the reconnect with a PolicyViolation close frame instead of silently
    /// spinning up a fresh server-side mux. Otherwise:
    ///   - the connecting client's reconnect handshake fails terminally with
    ///     SessionMismatch on a session GUID the listener invented;
    ///   - the orphan mux leaks in _sessions under a key the client cannot reach,
    ///     unbounded across stale-session reconnect attempts.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HandleAsync_UnknownSessionId_ClosesWithPolicyViolation_AndDoesNotLeakMux()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        var inboundQueue = new ConcurrentQueue<TaskCompletionSource<System.Net.WebSockets.WebSocket>>();
        var holdForever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            if (inboundQueue.TryDequeue(out var tcs))
            {
                tcs.TrySetResult(ws);
            }
            try { await holdForever.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) { }
        });

        await app.StartAsync(cts.Token);
        var wsUrl = app.Urls.First().Replace("http://", "ws://") + "/ws";

        await using var listener = new WebSocketMuxListener();

        try
        {
            // Open a server-side WebSocket and present an unknown sessionId.
            var serverWsCapture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundQueue.Enqueue(serverWsCapture);

            var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(wsUrl), cts.Token);
            var serverWs = await serverWsCapture.Task.WaitAsync(cts.Token);

            var unknownSessionId = Guid.NewGuid();
            var handleTask = listener.HandleAsync(serverWs, unknownSessionId, cts.Token);

            // Client should observe the close frame.
            var buf = new byte[256];
            var rcv = await client.ReceiveAsync(buf, cts.Token);
            Assert.Equal(WebSocketMessageType.Close, rcv.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);

            // HandleAsync returns cleanly — no exception.
            await handleTask.WaitAsync(TimeSpan.FromSeconds(5));

            // _sessions stays empty — no orphan mux was registered.
            var sessionsField = typeof(WebSocketMuxListener).GetField(
                "_sessions",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var sessions = (System.Collections.IDictionary)sessionsField.GetValue(listener)!;
            Assert.Empty(sessions);
        }
        finally
        {
            holdForever.TrySetResult();
            await app.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Even under repeated stale-session reconnect storms, the listener
    /// must not accumulate orphan muxes in _sessions.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task HandleAsync_RepeatedUnknownSessionIds_DoesNotAccumulateOrphans()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        var inboundQueue = new ConcurrentQueue<TaskCompletionSource<System.Net.WebSockets.WebSocket>>();
        var holdForever2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            if (inboundQueue.TryDequeue(out var tcs))
            {
                tcs.TrySetResult(ws);
            }
            try { await holdForever2.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) { }
        });

        await app.StartAsync(cts.Token);
        var wsUrl = app.Urls.First().Replace("http://", "ws://") + "/ws";

        await using var listener = new WebSocketMuxListener();

        var sessionsField = typeof(WebSocketMuxListener).GetField(
            "_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)sessionsField.GetValue(listener)!;

        try
        {
            const int Attempts = 25;
            for (int i = 0; i < Attempts; i++)
            {
                var serverWsCapture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
                inboundQueue.Enqueue(serverWsCapture);

                var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(wsUrl), cts.Token);
                var serverWs = await serverWsCapture.Task.WaitAsync(cts.Token);

                var staleId = Guid.NewGuid();
                await listener.HandleAsync(serverWs, staleId, cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

                client.Dispose();
            }

            Assert.Empty(sessions);
        }
        finally
        {
            holdForever2.TrySetResult();
            await app.StopAsync(CancellationToken.None);
        }
    }
}
