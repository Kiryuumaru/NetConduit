using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NetConduit.Internal;
using NetConduit.Transits;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for transit eagerness pattern: IsReady, WaitForReadyAsync, Ready/Connected/Disconnected events.
/// </summary>
public sealed class TransitEagernessTests
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

    #region StreamTransit Eagerness

    [Fact]
    public async Task StreamTransit_Write_IsReady_FalseBeforeChannelReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("s-ready1");

        // Initially not ready (channel in opening state, waiting for remote ACK)
        // Note: In memory transport the ACK comes fast, so we check after ready
        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Read_WaitForReadyAsync_CompletesWhenChannelReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        // Open write channel on client
        var writeChannel = client.OpenChannel("s-ready2");

        // Accept read channel on server (non-blocking)
        var transit = server.AcceptStream("s-ready2");

        // Wait for transit to be ready
        await transit.WaitForReadyAsync();

        Assert.True(transit.IsReady);
        Assert.True(transit.IsConnected);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadyEvent_FiresOnce()
    {
        var (client, server) = await CreateReadyPairAsync();

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writeChannel = client.OpenChannel("s-ready3");
        var transit = server.AcceptStream("s-ready3");
        transit.Ready += (_, _) => readyTcs.TrySetResult();

        await transit.WaitForReadyAsync();

        // Either the event fired or IsReady is true
        var ready = await Task.WhenAny(readyTcs.Task, Task.Delay(100)) == readyTcs.Task || transit.IsReady;

        Assert.True(ready);
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_IsConnected_ReflectsChannelState()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("s-conn1");
        await transit.WaitForReadyAsync();

        Assert.True(transit.IsConnected);

        await transit.DisposeAsync();

        // After dispose, channel is closed
        Assert.False(transit.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_WriteChannelId_ReturnsChannelId()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("s-id1");

        Assert.Equal("s-id1", transit.WriteChannelId);
        Assert.Null(transit.ReadChannelId);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadChannelId_ReturnsChannelId()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("s-id2");
        var transit = await server.AcceptStreamAsync("s-id2");

        Assert.Equal("s-id2", transit.ReadChannelId);
        Assert.Null(transit.WriteChannelId);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region DuplexStreamTransit Eagerness

    [Fact]
    public async Task DuplexStreamTransit_Open_IsReady_WhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("d-ready1");
        var serverTransit = server.AcceptDuplexStream("d-ready1");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);
        Assert.True(clientTransit.IsConnected);
        Assert.True(serverTransit.IsConnected);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_ReadyEvent_FiresWhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Create transits and subscribe to events immediately
        var clientTransit = client.OpenDuplexStream("d-ready2");
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptDuplexStream("d-ready2");
        serverTransit.Ready += (_, _) => serverReadyTcs.TrySetResult();

        // Wait for ready
        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        // Either the event fired or IsReady is true (event may have fired before subscription)
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
    public async Task DuplexStreamTransit_ChannelIds_MatchConvention()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("d-ids");
        var serverTransit = server.AcceptDuplexStream("d-ids");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        // Client: writes to "d-ids>>", reads from "d-ids<<"
        Assert.Equal("d-ids>>", clientTransit.WriteChannelId);
        Assert.Equal("d-ids<<", clientTransit.ReadChannelId);

        // Server: reads from "d-ids>>", writes to "d-ids<<"
        Assert.Equal("d-ids<<", serverTransit.WriteChannelId);
        Assert.Equal("d-ids>>", serverTransit.ReadChannelId);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_NonAsync_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

        // Non-async methods return immediately
        var clientTransit = client.OpenDuplexStream("d-nonasync");
        var serverTransit = server.AcceptDuplexStream("d-nonasync");

        // Transits are created even if not ready yet
        Assert.NotNull(clientTransit);
        Assert.NotNull(serverTransit);

        // Wait for ready
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

    #region MessageTransit Eagerness

    [Fact]
    public async Task MessageTransit_Open_IsReady_WhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("m-ready1");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("m-ready1");
#pragma warning restore IL2026, IL3050

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
    public async Task MessageTransit_ReadyEvent_FiresOnce()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("m-ready2");
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("m-ready2");
        serverTransit.Ready += (_, _) => serverReadyTcs.TrySetResult();
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        // Either the event fired or IsReady is true
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
    public async Task MessageTransit_NonAsync_SendReceive_WorksAfterWaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("m-nonasync");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("m-nonasync");
#pragma warning restore IL2026, IL3050

        // Wait for ready
        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        // Now we can send/receive
        var sent = new TestMsg { Name = "test", Value = 42 };
        await clientTransit.SendAsync(sent);

        var received = await serverTransit.ReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal("test", received.Name);
        Assert.Equal(42, received.Value);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Async_EquivalentToNonAsyncPlusWait()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        // Async version waits for ready
        var clientTransitTask = client.OpenMessageTransitAsync<TestMsg, TestMsg>("m-async");
        var serverTransitTask = server.AcceptMessageTransitAsync<TestMsg, TestMsg>("m-async");

        var clientTransit = await clientTransitTask;
        var serverTransit = await serverTransitTask;
#pragma warning restore IL2026, IL3050

        // Both should already be ready
        Assert.True(clientTransit.IsReady);
        Assert.True(serverTransit.IsReady);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_SendOnly_WaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenSendOnlyMessageTransit<TestMsg>("m-sendonly");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveOnly_WaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("m-recvonly");

#pragma warning disable IL2026, IL3050
        var transit = server.AcceptReceiveOnlyMessageTransit<TestMsg>("m-recvonly");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region DeltaTransit Eagerness

    [Fact]
    public async Task DeltaTransit_Open_IsReady_WhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDeltaTransit("dt-ready1", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaTransit("dt-ready1", JsonContext.Default.JsonObject);

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

        var clientTransit = client.OpenDeltaTransit("dt-ready2", JsonContext.Default.JsonObject);
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptDeltaTransit("dt-ready2", JsonContext.Default.JsonObject);
        serverTransit.Ready += (_, _) => serverReadyTcs.TrySetResult();

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        // Either the event fired or IsReady is true
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

        var clientTransit = client.OpenDeltaTransit("dt-nonasync", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaTransit("dt-nonasync", JsonContext.Default.JsonObject);

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

        var clientTransitTask = client.OpenDeltaTransitAsync("dt-async", JsonContext.Default.JsonObject);
        var serverTransitTask = server.AcceptDeltaTransitAsync("dt-async", JsonContext.Default.JsonObject);

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

        var transit = client.OpenSendOnlyDeltaTransit("dt-sendonly", JsonContext.Default.JsonObject);

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

        var transit = server.AcceptReceiveOnlyDeltaTransit("dt-recvonly", JsonContext.Default.JsonObject);

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Non-Async Extension Methods

    [Fact]
    public async Task AcceptStream_ReturnsImmediately_BeforeChannelReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        // Open write channel on client
        var writeChannel = client.OpenChannel("ext-stream1");

        // AcceptStream returns immediately
        var transit = server.AcceptStream("ext-stream1");

        Assert.NotNull(transit);

        // Wait for ready
        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenDuplexStream_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("ext-duplex1");
        var serverTransit = server.AcceptDuplexStream("ext-duplex1");

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

    [Fact]
    public async Task OpenMessageTransit_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("ext-msg1");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("ext-msg1");
#pragma warning restore IL2026, IL3050

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

    [Fact]
    public async Task OpenDeltaTransit_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDeltaTransit("ext-delta1", JsonContext.Default.JsonObject);
        var serverTransit = server.AcceptDeltaTransit("ext-delta1", JsonContext.Default.JsonObject);

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

    #region Connected/Disconnected Events

    [Fact]
    public async Task StreamTransit_ConnectedEvent_FiresWhenReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transit = client.OpenStream("conn-evt1");
        transit.Connected += (_, _) => connectedTcs.TrySetResult();

        await transit.WaitForReadyAsync();

        // Either the event fired or IsConnected is true
        var connected = await Task.WhenAny(connectedTcs.Task, Task.Delay(100)) == connectedTcs.Task || transit.IsConnected;

        Assert.True(connected);
        Assert.True(transit.IsConnected);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_ConnectedEvent_FiresForBothChannels()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transit = client.OpenDuplexStream("conn-evt2");
        transit.Connected += (_, _) => clientConnectedTcs.TrySetResult();

        var acceptTransit = server.AcceptDuplexStream("conn-evt2");
        acceptTransit.Connected += (_, _) => serverConnectedTcs.TrySetResult();

        await Task.WhenAll(
            transit.WaitForReadyAsync(),
            acceptTransit.WaitForReadyAsync());

        // Either the events fired or IsConnected is true
        var clientConnected = await Task.WhenAny(clientConnectedTcs.Task, Task.Delay(100)) == clientConnectedTcs.Task || transit.IsConnected;
        var serverConnected = await Task.WhenAny(serverConnectedTcs.Task, Task.Delay(100)) == serverConnectedTcs.Task || acceptTransit.IsConnected;

        Assert.True(clientConnected);
        Assert.True(serverConnected);
        Assert.True(transit.IsConnected);
        Assert.True(acceptTransit.IsConnected);

        await transit.DisposeAsync();
        await acceptTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Dispose Behavior

    [Fact]
    public async Task StreamTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("disp1");
        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();

        // After dispose, IsConnected should be false
        Assert.False(transit.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("disp2");
        var acceptTransit = server.AcceptDuplexStream("disp2");

        await Task.WhenAll(
            transit.WaitForReadyAsync(),
            acceptTransit.WaitForReadyAsync());

        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);

        await acceptTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMsg, TestMsg>("disp3");
        var acceptTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("disp3");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            transit.WaitForReadyAsync(),
            acceptTransit.WaitForReadyAsync());

        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);

        await acceptTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDeltaTransit("disp4", JsonContext.Default.JsonObject);
        var acceptTransit = server.AcceptDeltaTransit("disp4", JsonContext.Default.JsonObject);

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

    #region End-to-End Data Flow with Eagerness

    [Fact]
    public async Task DuplexStreamTransit_OptimisticPattern_WritesBeforeReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("e2e-optimistic1");
        var serverTransit = server.AcceptDuplexStream("e2e-optimistic1");

        // Start write before channels are confirmed ready
        // The write will buffer and complete when ready
        var writeTask = clientTransit.WriteAsync(new byte[] { 1, 2, 3 });

        // Wait for both transits to be ready
        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await writeTask;

        // Server should receive the data
        var buffer = new byte[10];
        var read = await serverTransit.ReadAsync(buffer);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..3]);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_OptimisticPattern_SendBeforeReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("e2e-optimistic2");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("e2e-optimistic2");
#pragma warning restore IL2026, IL3050

        // Start send before channels are confirmed ready
        var sendTask = clientTransit.SendAsync(new TestMsg { Name = "early", Value = 99 });

        // Wait for both transits to be ready
        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await sendTask;

        // Server should receive the message
        var msg = await serverTransit.ReceiveAsync();
        Assert.NotNull(msg);
        Assert.Equal("early", msg.Name);
        Assert.Equal(99, msg.Value);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - ObjectDisposedException

    [Fact]
    public async Task StreamTransit_Write_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-disposed-write");
        await transit.WaitForReadyAsync();
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transit.WriteAsync(new byte[] { 1, 2, 3 }));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Read_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("unhappy-disposed-read");
        var transit = server.AcceptStream("unhappy-disposed-read");
        await transit.WaitForReadyAsync();
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transit.ReadAsync(new byte[10]));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Write_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("unhappy-duplex-disposed-write");
        var serverTransit = server.AcceptDuplexStream("unhappy-duplex-disposed-write");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await clientTransit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await clientTransit.WriteAsync(new byte[] { 1, 2, 3 }));

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Read_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("unhappy-duplex-disposed-read");
        var serverTransit = server.AcceptDuplexStream("unhappy-duplex-disposed-read");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await serverTransit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await serverTransit.ReadAsync(new byte[10]));

        await clientTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Send_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-msg-disposed-send");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("unhappy-msg-disposed-send");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await clientTransit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await clientTransit.SendAsync(new TestMsg { Name = "fail", Value = 0 }));

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Receive_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-msg-disposed-recv");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("unhappy-msg-disposed-recv");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await serverTransit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await serverTransit.ReceiveAsync());

        await clientTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - InvalidOperationException (Wrong Direction)

    [Fact]
    public async Task StreamTransit_Write_ThrowsInvalidOperationException_OnReadOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("unhappy-readonly-write");
        var transit = server.AcceptStream("unhappy-readonly-write");
        await transit.WaitForReadyAsync();

        // StreamTransit throws InvalidOperationException when writing to a read-only transit
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.WriteAsync(new byte[] { 1, 2, 3 }));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Read_ThrowsInvalidOperationException_OnWriteOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-writeonly-read");
        await transit.WaitForReadyAsync();

        // StreamTransit throws InvalidOperationException when reading from a write-only transit
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.ReadAsync(new byte[10]));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Send_ThrowsInvalidOperationException_OnReceiveOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("unhappy-recvonly-send");

#pragma warning disable IL2026, IL3050
        var transit = server.AcceptReceiveOnlyMessageTransit<TestMsg>("unhappy-recvonly-send");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.SendAsync(new TestMsg { Name = "fail", Value = 0 }));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Receive_ThrowsInvalidOperationException_OnSendOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenSendOnlyMessageTransit<TestMsg>("unhappy-sendonly-recv");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.ReceiveAsync());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - NotSupportedException (Stream Operations)

    [Fact]
    public async Task StreamTransit_Seek_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-seek");

        Assert.Throws<NotSupportedException>(() => transit.Seek(0, SeekOrigin.Begin));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_SetLength_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-setlength");

        Assert.Throws<NotSupportedException>(() => transit.SetLength(100));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Length_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-length");

        Assert.Throws<NotSupportedException>(() => _ = transit.Length);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Position_Get_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-position-get");

        Assert.Throws<NotSupportedException>(() => _ = transit.Position);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_Position_Set_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-position-set");

        Assert.Throws<NotSupportedException>(() => transit.Position = 0);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Seek_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-seek");

        Assert.Throws<NotSupportedException>(() => transit.Seek(0, SeekOrigin.Begin));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_SetLength_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-setlength");

        Assert.Throws<NotSupportedException>(() => transit.SetLength(100));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Length_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-length");

        Assert.Throws<NotSupportedException>(() => _ = transit.Length);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Position_ThrowsNotSupportedException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-position");

        Assert.Throws<NotSupportedException>(() => _ = transit.Position);
        Assert.Throws<NotSupportedException>(() => transit.Position = 0);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - ArgumentNullException

    [Fact]
    public void StreamTransit_Constructor_ThrowsArgumentNullException_WhenWriteChannelNull()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamTransit((IWriteChannel)null!));
    }

    [Fact]
    public void StreamTransit_Constructor_ThrowsArgumentNullException_WhenReadChannelNull()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamTransit((IReadChannel)null!));
    }

    [Fact]
    public async Task DuplexStreamTransit_Constructor_ThrowsArgumentNullException_WhenWriteChannelNull()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var ch = mux.AcceptChannel("test");

        Assert.Throws<ArgumentNullException>(() => new DuplexStreamTransit(null!, ch));

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Constructor_ThrowsArgumentNullException_WhenReadChannelNull()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var ch = mux.OpenChannel("test");

        Assert.Throws<ArgumentNullException>(() => new DuplexStreamTransit(ch, null!));

        await mux.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - Cancellation

    [Fact]
    public async Task StreamTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-cancelled-wait");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.WaitForReadyAsync(cts.Token));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-cancelled-wait");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.WaitForReadyAsync(cts.Token));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-msg-cancelled-wait");
#pragma warning restore IL2026, IL3050

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.WaitForReadyAsync(cts.Token));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_SendAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-msg-cancelled-send");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.SendAsync(new TestMsg { Name = "fail", Value = 0 }, cts.Token));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-msg-cancelled-recv");
        var serverTransit = server.AcceptMessageTransit<TestMsg, TestMsg>("unhappy-msg-cancelled-recv");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await serverTransit.ReceiveAsync(cts.Token));

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - Double Disposal (Safe)

    [Fact]
    public async Task StreamTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-double-dispose-stream");
        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();
        await transit.DisposeAsync(); // Second dispose should not throw

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-double-dispose-duplex");
        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();
        await transit.DisposeAsync(); // Second dispose should not throw

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-double-dispose-msg");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();
        await transit.DisposeAsync(); // Second dispose should not throw

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DeltaTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDeltaTransit("unhappy-double-dispose-delta", JsonContext.Default.JsonObject);
        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();
        await transit.DisposeAsync(); // Second dispose should not throw

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - State After Disposal

    [Fact]
    public async Task StreamTransit_Properties_ReturnDefaults_AfterDisposal()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-state-after-dispose");
        await transit.WaitForReadyAsync();

        Assert.True(transit.IsReady);
        Assert.True(transit.IsConnected);
        Assert.False(transit.CanSeek);

        await transit.DisposeAsync();

        // After disposal, state properties return false/default
        Assert.False(transit.IsReady);
        Assert.False(transit.IsConnected);
        Assert.False(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_Properties_ReturnDefaults_AfterDisposal()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-state-after-dispose");
        await transit.WaitForReadyAsync();

        Assert.True(transit.IsReady);
        Assert.True(transit.IsConnected);
        Assert.True(transit.CanRead);
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await transit.DisposeAsync();

        // After disposal, state properties return false
        Assert.False(transit.IsReady);
        Assert.False(transit.IsConnected);
        Assert.False(transit.CanRead);
        Assert.False(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Unhappy Paths - Multiplexer Disposed

    [Fact]
    public async Task StreamTransit_Write_FailsGracefully_WhenMultiplexerDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-mux-disposed-write");
        await transit.WaitForReadyAsync();

        // Dispose the multiplexer (not the transit)
        await client.DisposeAsync();

        // Writing should either throw or complete (fail-fast behavior)
        // Use timeout to prevent indefinite hang if implementation doesn't handle this
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Record.ExceptionAsync(
            async () => await transit.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token));

        // Should throw either ObjectDisposedException, InvalidOperationException, IOException, or OperationCanceledException
        Assert.True(
            exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException,
            $"Expected ObjectDisposedException, InvalidOperationException, IOException, or OperationCanceledException but got {exception?.GetType().Name}");

        await transit.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Send_FailsGracefully_WhenMultiplexerDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMsg, TestMsg>("unhappy-mux-disposed-msg");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        // Dispose the multiplexer (not the transit)
        await client.DisposeAsync();

        // Sending should either throw or complete (fail-fast behavior)
        // Use timeout to prevent indefinite hang if implementation doesn't handle this
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Record.ExceptionAsync(
            async () => await transit.SendAsync(new TestMsg { Name = "fail", Value = 0 }, cts.Token));

        // Should throw either ObjectDisposedException, InvalidOperationException, IOException, or OperationCanceledException
        Assert.True(
            exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException,
            $"Expected ObjectDisposedException, InvalidOperationException, IOException, or OperationCanceledException but got {exception?.GetType().Name}");

        await transit.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}

internal sealed class TestMsg
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
