using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetConduit.Interfaces;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketMultiplexerTests
{
    [Fact]
    public void CreateOptions_NonWebSocketUri_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            WebSocketMultiplexer.CreateOptions("http://localhost:5000/chat"));

        Assert.Equal("url", exception.ParamName);
    }

    [Fact(Timeout = 30000)]
    public async Task CreateOptions_ConnectsAndTransfersData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Gate the request handler on an explicit signal — never on `cts` — so the
        // test can deterministically dispose `serverMux` *while the HttpContext is
        // still alive*, then release the handler. If the handler instead unwound
        // on `cts` cancellation, ASP.NET Core would dispose the HttpContext (and
        // its IFeatureCollection) before `serverMux.DisposeAsync()` ran, and the
        // CTS-registered ManagedWebSocket dispose callback fired by the mux's
        // internal Cancel would then call DefaultHttpContext.Abort() on a dead
        // context, surfacing as ObjectDisposedException("IFeatureCollection has
        // been disposed"). That race was the cause of the
        // WebSocketMultiplexerTests.CreateOptions_ConnectsAndTransfersData flake.
        var serverShutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        var app = builder.Build();
        app.UseWebSockets();

        IStreamMultiplexer? serverMux = null;
        app.Map("/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                var serverOptions = WebSocketMultiplexer.CreateServerOptions(ws);
                serverMux = StreamMultiplexer.Create(serverOptions);
                serverMux.Start();
                await serverMux.WaitForReadyAsync(cts.Token);

                var readChannel = await serverMux.AcceptChannelAsync("test", cts.Token);
                var buffer = new byte[1024];
                int totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }

                // Hold the HttpContext open until the test signals shutdown — the
                // test must dispose serverMux *before* signaling, so the mux's
                // teardown observes a live context.
                await serverShutdownTcs.Task;
            }
        });

        await app.StartAsync(cts.Token);
        var url = app.Urls.First();
        var wsUrl = url.Replace("http://", "ws://") + "/ws";

        var clientOptions = WebSocketMultiplexer.CreateOptions(new Uri(wsUrl));
        await using var client = StreamMultiplexer.Create(clientOptions);
        client.Start();
        await client.WaitForReadyAsync(cts.Token);

        var writeChannel = client.OpenChannel("test");
        var testData = "Hello, WebSocket!"u8.ToArray();
        await writeChannel.WriteAsync(testData, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        await Task.Delay(500, cts.Token);

        Assert.True(client.IsConnected);

        // Strict shutdown ordering:
        //   1. Dispose serverMux while the HttpContext is still alive.
        //   2. Release the handler so ASP.NET Core can tear down the context.
        //   3. Stop the host.
        if (serverMux is not null)
            await serverMux.DisposeAsync();
        serverShutdownTcs.TrySetResult();
        await app.StopAsync();
    }
}
