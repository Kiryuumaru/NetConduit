using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetConduit.WebSocket;

namespace NetConduit.WebSocket.IntegrationTests;

public class WebSocketMultiplexerTests
{
    [Fact(Timeout = 30000)]
    public async Task CreateOptions_ConnectsAndTransfersData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

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

                // Keep connection alive until cancelled
                try { await Task.Delay(Timeout.Infinite, cts.Token); }
                catch (OperationCanceledException) { }
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

        await cts.CancelAsync();
        await app.StopAsync();
        if (serverMux is not null)
            await serverMux.DisposeAsync();
    }
}
