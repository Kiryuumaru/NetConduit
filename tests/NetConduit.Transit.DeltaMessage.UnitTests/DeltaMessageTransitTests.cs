using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

public sealed class DeltaMessageTransitTests
{
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

    #region End-to-End

    [Fact]
    public async Task DeltaTransit_FullState_Delta_Reset_Roundtrip()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        var clientWrite = client.OpenChannel("dt1");
        var clientReadCh = await server.AcceptChannelAsync("dt1", ct);

        var sender = new DeltaMessageTransit<JsonObject>(clientWrite, null);
        var receiver = new DeltaMessageTransit<JsonObject>(null, clientReadCh);

        var state1 = new JsonObject { ["name"] = "alice", ["score"] = 100 };
        await sender.SendAsync(state1, ct);
        var received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("alice", received["name"]!.GetValue<string>());
        Assert.Equal(100, received["score"]!.GetValue<int>());

        var state2 = new JsonObject { ["name"] = "alice", ["score"] = 150 };
        await sender.SendAsync(state2, ct);
        received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("alice", received["name"]!.GetValue<string>());
        Assert.Equal(150, received["score"]!.GetValue<int>());

        sender.ResetState();
        var state3 = new JsonObject { ["name"] = "bob", ["score"] = 200 };
        await sender.SendAsync(state3, ct);
        received = await receiver.ReceiveAsync(ct);
        Assert.NotNull(received);
        Assert.Equal("bob", received["name"]!.GetValue<string>());
        Assert.Equal(200, received["score"]!.GetValue<int>());

        await sender.DisposeAsync();
        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Eagerness

    [Fact]
    public async Task DeltaTransit_Open_IsReady_WhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDeltaMessageTransit("dt-ready1", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaMessageTransit("dt-ready1", JsonContext.Default.JsonObject);

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ReadyEvent_FiresOnce()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientTransit = client.OpenDeltaMessageTransit("dt-ready2", JsonContext.Default.JsonObject);
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptDeltaMessageTransit("dt-ready2", JsonContext.Default.JsonObject);
        serverTransit.Ready += (_, _) => serverReadyTcs.TrySetResult();

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        var clientReady = await Task.WhenAny(clientReadyTcs.Task, Task.Delay(100)) == clientReadyTcs.Task || clientTransit.IsReady;
        var serverReady = await Task.WhenAny(serverReadyTcs.Task, Task.Delay(100)) == serverReadyTcs.Task || serverTransit.IsReady;

        Assert.True(clientReady);
        Assert.True(serverReady);
        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_NonAsync_SendReceive_WorksAfterWaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDeltaMessageTransit("dt-nonasync", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaMessageTransit("dt-nonasync", JsonContext.Default.JsonObject);

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        var state = new JsonObject { ["name"] = "test", ["value"] = 42 };
        await clientTransit.SendAsync(state);

        var received = await serverTransit.ReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal("test", received["name"]!.GetValue<string>());
        Assert.Equal(42, received["value"]!.GetValue<int>());

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_Async_EquivalentToNonAsyncPlusWait()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransitTask = client.OpenDeltaMessageTransitAsync("dt-async", JsonContext.Default.JsonObject);
        var serverTransitTask = server.AcceptDeltaMessageTransitAsync("dt-async", JsonContext.Default.JsonObject);

        var clientTransit = await clientTransitTask;
        var serverTransit = await serverTransitTask;

        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_SendOnly_WaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenSendOnlyDeltaMessageTransit("dt-sendonly", JsonContext.Default.JsonObject);

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ReceiveOnly_WaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("dt-recvonly");

        var transit = server.AcceptReceiveOnlyDeltaMessageTransit("dt-recvonly", JsonContext.Default.JsonObject);

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenDeltaTransit_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDeltaMessageTransit("ext-delta1", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaMessageTransit("ext-delta1", JsonContext.Default.JsonObject);

        Assert.NotNull(clientTransit);
        Assert.NotNull(serverTransit);

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Dispose Behavior

    [Fact]
    public async Task DeltaTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDeltaMessageTransit("disp4", JsonContext.Default.JsonObject);
        var acceptTransit = server.AcceptDeltaMessageTransit("disp4", JsonContext.Default.JsonObject);

        await Task.WhenAll(
            transit.WaitForReadyAsync(),
            acceptTransit.WaitForReadyAsync());

        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);

        await acceptTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Double Dispose

    [Fact]
    public async Task DeltaTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDeltaMessageTransit("unhappy-double-dispose-delta", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaMessageTransit("unhappy-double-dispose-delta", JsonContext.Default.JsonObject);
        await Task.WhenAll(transit.WaitForReadyAsync(), serverTransit.WaitForReadyAsync());

        await transit.DisposeAsync();
        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region API Misuse

    [Fact]
    public async Task DeltaTransit_SendAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("delta");
        await w.WaitForReadyAsync();

        var transit = new DeltaMessageTransit<JsonObject>(w, null);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.SendAsync(new JsonObject { ["x"] = 1 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ReceiveAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.OpenChannel("delta");
        var r = await server.AcceptChannelAsync("delta");

        var transit = new DeltaMessageTransit<JsonObject>(null, r);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReceiveAsync().AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_SendWithNoWriteChannel_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();
        client.OpenChannel("delta");
        var r = await server.AcceptChannelAsync("delta");

        var transit = new DeltaMessageTransit<JsonObject>(null, r);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.SendAsync(new JsonObject { ["x"] = 1 }).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_ReceiveWithNoReadChannel_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("delta");
        await w.WaitForReadyAsync();

        var transit = new DeltaMessageTransit<JsonObject>(w, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.ReceiveAsync().AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public void DeltaTransit_PocoWithoutTypeInfo_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeltaMessageTransit<TestPoco>(null, null, null));
    }

    [Fact]
    public async Task DeltaTransit_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("delta");
        await w.WaitForReadyAsync();

        var transit = new DeltaMessageTransit<JsonObject>(w, null);
        await transit.DisposeAsync();
        await transit.DisposeAsync();
        await transit.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_SendBatch_EmptyEnumerable_NoOp()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("delta");
        await w.WaitForReadyAsync();

        var transit = new DeltaMessageTransit<JsonObject>(w, null);

        await transit.SendBatchAsync(Enumerable.Empty<JsonObject>());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}

internal sealed class TestPoco
{
    public string? Name { get; set; }
    public int Value { get; set; }
}
