using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class EvictedSessionReconnectRefusalTests
{
    /// <summary>
    /// When a reconnect HandleAsync(sessionId=X) is blocked on a full ConnectionChannel
    /// and the session is evicted (channel writer completed), the catch of
    /// ChannelClosedException MUST refuse the reconnect with a PolicyViolation close.
    /// Falling through to fresh-session creation silently binds the pair to a brand-new
    /// SessionId Y ≠ X, causing the client's reconnect handshake to fault with
    /// SessionMismatch and leaking a mux under a key the client cannot reach.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReconnectBlockedOnFullChannel_SessionEvicted_RefusesInsteadOfNewMux()
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
            // 1. Open first WebSocket, hand it to the listener as a brand-new session.
            //    No consumer drains the mux, so the bounded (cap 1) ConnectionChannel
            //    stays loaded with the initial pair forever.
            var ws1Capture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundQueue.Enqueue(ws1Capture);

            var client1 = new ClientWebSocket();
            await client1.ConnectAsync(new Uri(wsUrl), cts.Token);
            var serverWs1 = await ws1Capture.Task.WaitAsync(cts.Token);

            var handle1Task = listener.HandleAsync(serverWs1, sessionId: null, cancellationToken: CancellationToken.None);

            // Drain AcceptMuxesAsync to learn the assigned SessionId.
            Guid sessionId = Guid.Empty;
            await foreach (var mux in listener.AcceptMuxesAsync(cts.Token))
            {
                sessionId = mux.SessionId;
                break;
            }
            Assert.NotEqual(Guid.Empty, sessionId);

            // 2. Open a second WebSocket and submit a reconnect against the known sessionId.
            //    The ConnectionChannel is full → WriteAsync blocks.
            var ws2Capture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundQueue.Enqueue(ws2Capture);

            var client2 = new ClientWebSocket();
            await client2.ConnectAsync(new Uri(wsUrl), cts.Token);
            var serverWs2 = await ws2Capture.Task.WaitAsync(cts.Token);

            var handle2Task = listener.HandleAsync(serverWs2, sessionId: sessionId, cancellationToken: cts.Token);

            await Task.Delay(100, cts.Token);
            Assert.False(handle2Task.IsCompleted);

            // 3. Evict the session: completes the ConnectionChannel writer, which makes
            //    the blocked WriteAsync inside HandleAsync#2 throw ChannelClosedException.
            Assert.True(listener.RemoveSession(sessionId));

            // 4. HandleAsync#2 must return cleanly without spinning up a fresh mux.
            await handle2Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 5. Client observes the PolicyViolation close frame — proves the listener
            //    refused the reconnect rather than silently re-binding to a new SessionId.
            var buf = new byte[256];
            var rcv = await client2.ReceiveAsync(buf, cts.Token);
            Assert.Equal(WebSocketMessageType.Close, rcv.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, client2.CloseStatus);

            // 6. No orphan mux is registered under any key after the eviction.
            var sessionsField = typeof(WebSocketMuxListener).GetField(
                "_sessions",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var sessions = (System.Collections.IDictionary)sessionsField.GetValue(listener)!;
            Assert.Empty(sessions);

            // Cleanup: let the first session complete.
            holdForever.TrySetResult();
            try { await handle1Task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        finally
        {
            holdForever.TrySetResult();
            await app.StopAsync(CancellationToken.None);
        }
    }
}
