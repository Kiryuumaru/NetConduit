using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketStreamDisposeTests
{
    [Fact(Timeout = 30000)]
    public async Task DisposeAsync_SendsCloseFrameToPeer_AndDisposesUnderlyingWebSocket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        var serverWsCaptured = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            serverWsCaptured.TrySetResult(ws);
            // Hold the request open — do not call ReceiveAsync here so the test code
            // can observe the inbound Close frame itself.
            await serverHold.Task;
        });

        await app.StartAsync(cts.Token);
        try
        {
            var url = app.Urls.First();
            var wsUrl = url.Replace("http://", "ws://") + "/ws";

            var clientWs = new ClientWebSocket();
            await clientWs.ConnectAsync(new Uri(wsUrl), cts.Token);

            var stream = new WebSocketStream(clientWs);

            var serverWs = await serverWsCaptured.Task.WaitAsync(cts.Token);
            Assert.Equal(WebSocketState.Open, clientWs.State);
            Assert.Equal(WebSocketState.Open, serverWs.State);

            await stream.DisposeAsync();

            // Pre-fix: WebSocketStream.Dispose was a no-op — client WS stayed Open,
            // peer never received a Close frame, and the FD leaked.
            Assert.NotEqual(WebSocketState.Open, clientWs.State);

            var buffer = new byte[256];
            var result = await serverWs.ReceiveAsync(buffer.AsMemory(), cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        finally
        {
            serverHold.TrySetResult();
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task SyncDispose_SendsCloseFrameToPeer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        var serverWsCaptured = new TaskCompletionSource<System.Net.WebSockets.WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            serverWsCaptured.TrySetResult(ws);
            await serverHold.Task;
        });

        await app.StartAsync(cts.Token);
        try
        {
            var url = app.Urls.First();
            var wsUrl = url.Replace("http://", "ws://") + "/ws";

            var clientWs = new ClientWebSocket();
            await clientWs.ConnectAsync(new Uri(wsUrl), cts.Token);

            var stream = new WebSocketStream(clientWs);
            var serverWs = await serverWsCaptured.Task.WaitAsync(cts.Token);

            // Sync Dispose on a non-pooled thread so the GetAwaiter().GetResult()
            // does not deadlock the test's async context.
            await Task.Run(stream.Dispose);

            Assert.NotEqual(WebSocketState.Open, clientWs.State);

            var buffer = new byte[256];
            var result = await serverWs.ReceiveAsync(buffer.AsMemory(), cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        finally
        {
            serverHold.TrySetResult();
            await app.StopAsync(CancellationToken.None);
        }
    }
}
