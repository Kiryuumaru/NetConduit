using System.Text.Json.Nodes;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

public sealed class DeltaMessageTransitDynamicExtensionTests
{
    [Fact]
    public async Task DynamicJsonObjectAsyncExtensions_OpenAndAcceptWithoutTypeInfo_Roundtrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var clientTransitTask = client.OpenDeltaMessageTransitAsync<JsonObject>(
            "dynamic-ext-async",
            cancellationToken: cts.Token);
        var serverTransitTask = server.AcceptDeltaMessageTransitAsync<JsonObject>(
            "dynamic-ext-async",
            cancellationToken: cts.Token);

        await Task.WhenAll(clientTransitTask, serverTransitTask);
        await using var clientTransit = await clientTransitTask;
        await using var serverTransit = await serverTransitTask;

        var board = new JsonObject
        {
            ["alice"] = 100,
            ["bob"] = 92,
        };

        await clientTransit.SendAsync(board, cts.Token);
        var received = await serverTransit.ReceiveAsync(cts.Token);

        Assert.NotNull(received);
        Assert.Equal(100, received["alice"]!.GetValue<int>());
        Assert.Equal(92, received["bob"]!.GetValue<int>());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DynamicJsonObjectExtensions_OpenAndAcceptWithoutTypeInfo_Roundtrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var clientTransit = client.OpenDeltaMessageTransit<JsonObject>("dynamic-ext-sync");
        await using var serverTransit = server.AcceptDeltaMessageTransit<JsonObject>("dynamic-ext-sync");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(cts.Token),
            serverTransit.WaitForReadyAsync(cts.Token));

        var board = new JsonObject
        {
            ["alice"] = 105,
            ["bob"] = 92,
        };

        await clientTransit.SendAsync(board, cts.Token);
        var received = await serverTransit.ReceiveAsync(cts.Token);

        Assert.NotNull(received);
        Assert.Equal(105, received["alice"]!.GetValue<int>());
        Assert.Equal(92, received["bob"]!.GetValue<int>());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }
}