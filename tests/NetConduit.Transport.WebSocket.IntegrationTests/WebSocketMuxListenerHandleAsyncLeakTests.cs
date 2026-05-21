using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketMuxListenerHandleAsyncLeakTests
{
    /// <summary>
    /// Leak A regression: a reconnect HandleAsync call whose target ConnectionChannel is full
    /// (capacity 1) and whose cancellation token fires while WriteAsync is blocked must
    /// dispose the orphan pair (and therefore close the wrapped WebSocket) before propagating
    /// the OperationCanceledException.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Reconnect_BlockedWriteCancelled_DisposesOrphanWebSocket()
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
            // 1. Open the first connection and capture the server-side WebSocket.
            var serverWs1Capture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundQueue.Enqueue(serverWs1Capture);

            var client1 = new ClientWebSocket();
            await client1.ConnectAsync(new Uri(wsUrl), cts.Token);
            var serverWs1 = await serverWs1Capture.Task.WaitAsync(cts.Token);

            // 2. Hand the first WS into the listener as a new session. We never start the mux,
            //    so its ConnectionChannel (bounded capacity 1) is full and stays full.
            var handle1Task = listener.HandleAsync(serverWs1, sessionId: null, cancellationToken: CancellationToken.None);

            // 3. Drain the new mux from AcceptMuxesAsync so we can read its SessionId.
            var sessionId = Guid.Empty;
            await foreach (var mux in listener.AcceptMuxesAsync(cts.Token))
            {
                sessionId = mux.SessionId;
                break;
            }
            Assert.NotEqual(Guid.Empty, sessionId);

            // 4. Open a second connection and capture the server WebSocket.
            var serverWs2Capture = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundQueue.Enqueue(serverWs2Capture);

            var client2 = new ClientWebSocket();
            await client2.ConnectAsync(new Uri(wsUrl), cts.Token);
            var serverWs2 = await serverWs2Capture.Task.WaitAsync(cts.Token);
            Assert.Equal(WebSocketState.Open, serverWs2.State);

            // 5. Reconnect to the existing session. The ConnectionChannel for that session is
            //    full (capacity 1) and no consumer is draining it, so WriteAsync will block.
            using var reconnectCts = new CancellationTokenSource();
            var handle2Task = listener.HandleAsync(serverWs2, sessionId: sessionId, cancellationToken: reconnectCts.Token);

            // Give HandleAsync a moment to block inside WriteAsync.
            await Task.Delay(100, cts.Token);
            Assert.False(handle2Task.IsCompleted);

            // 6. Cancel. Pre-fix: pair2 leaks, serverWs2 stays Open. Post-fix: HandleAsync
            //    disposes the orphan pair, which closes the underlying WebSocket gracefully.
            reconnectCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle2Task);

            // Allow the close frame round-trip to complete.
            await Task.Delay(200, cts.Token);

            Assert.NotEqual(WebSocketState.Open, serverWs2.State);

            // Cleanup: complete the first session to let HandleAsync return.
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
