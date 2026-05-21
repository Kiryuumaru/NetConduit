using Xunit;

namespace NetConduit.Transit.Message.UnitTests;

internal sealed class TestMessage
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public sealed class MessageTransitTests
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

    #region Core MessageTransit

    [Fact]
    public async Task MessageTransit_SendReceive_RoundTrips()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("m1>>");
        var clientReadCh = await server.AcceptChannelAsync("m1>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("m1<<");
        var serverReadCh = await client.AcceptChannelAsync("m1<<", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var clientTransit = new MessageTransit<TestMessage, TestMessage>(clientWrite, serverReadCh);
        var serverTransit = new MessageTransit<TestMessage, TestMessage>(serverWrite, clientReadCh);
#pragma warning restore IL2026, IL3050

        var sent = new TestMessage { Name = "hello", Value = 42 };
        await clientTransit.SendAsync(sent);

        var received = await serverTransit.ReceiveAsync();
        Assert.NotNull(received);
        Assert.Equal("hello", received.Name);
        Assert.Equal(42, received.Value);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAllAsync_StreamsMessages()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("m2>>");
        var clientReadCh = await server.AcceptChannelAsync("m2>>", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var sender = new MessageTransit<TestMessage, TestMessage>(clientWrite, null);
        var receiver = new MessageTransit<TestMessage, TestMessage>(null, clientReadCh);
#pragma warning restore IL2026, IL3050

        await sender.SendAsync(new TestMessage { Name = "a", Value = 1 });
        await sender.SendAsync(new TestMessage { Name = "b", Value = 2 });
        await sender.SendAsync(new TestMessage { Name = "c", Value = 3 });

        await sender.DisposeAsync();

        var messages = new List<TestMessage>();
        await foreach (var msg in receiver.ReceiveAllAsync())
        {
            messages.Add(msg);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal("a", messages[0].Name);
        Assert.Equal("b", messages[1].Name);
        Assert.Equal("c", messages[2].Name);

        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_SendThrowsWhenNoWriteChannel()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m3");
        var r = await server.AcceptChannelAsync("m3", CancellationToken.None);

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(null, r);
#pragma warning restore IL2026, IL3050

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.SendAsync(new TestMessage { Name = "x", Value = 0 }).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveThrowsWhenNoReadChannel()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m4");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, null);
#pragma warning restore IL2026, IL3050

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.ReceiveAsync().AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Eagerness

    [Fact]
    public async Task MessageTransit_Open_IsReady_WhenBothChannelsReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("m-ready1");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("m-ready1");
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
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("m-ready2");
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("m-ready2");
        serverTransit.Ready += (_, _) => serverReadyTcs.TrySetResult();
#pragma warning restore IL2026, IL3050

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
    public async Task MessageTransit_NonAsync_SendReceive_WorksAfterWaitForReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("m-nonasync");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("m-nonasync");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        var sent = new TestMessage { Name = "test", Value = 42 };
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
        var clientTransitTask = client.OpenMessageTransitAsync<TestMessage, TestMessage>("m-async");
        var serverTransitTask = server.AcceptMessageTransitAsync<TestMessage, TestMessage>("m-async");

        var clientTransit = await clientTransitTask;
        var serverTransit = await serverTransitTask;
#pragma warning restore IL2026, IL3050

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
        var transit = client.OpenSendOnlyMessageTransit<TestMessage>("m-sendonly");
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
        var transit = server.AcceptReceiveOnlyMessageTransit<TestMessage>("m-recvonly");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenMessageTransit_ReturnsImmediately()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("ext-msg1");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("ext-msg1");
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

    #endregion

    #region Dispose Behavior

    [Fact]
    public async Task MessageTransit_Dispose_UnsubscribesFromChannelEvents()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMessage, TestMessage>("disp3");
        var acceptTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("disp3");
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

    #endregion

    #region Optimistic Send

    [Fact]
    public async Task MessageTransit_OptimisticPattern_SendBeforeReady()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("e2e-optimistic2");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("e2e-optimistic2");
#pragma warning restore IL2026, IL3050

        var sendTask = clientTransit.SendAsync(new TestMessage { Name = "early", Value = 99 });

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await sendTask;

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

    #region ObjectDisposedException

    [Fact]
    public async Task MessageTransit_Send_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-msg-disposed-send");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-msg-disposed-send");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await clientTransit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await clientTransit.SendAsync(new TestMessage { Name = "fail", Value = 0 }));

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Receive_ThrowsObjectDisposedException_AfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-msg-disposed-recv");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-msg-disposed-recv");
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

    [Fact]
    public async Task MessageTransit_SendAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m1");
        var r = await server.AcceptChannelAsync("m1");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.SendAsync(new TestMessage { Name = "x", Value = 0 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m2");
        var r = await server.AcceptChannelAsync("m2");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReceiveAsync().AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region InvalidOperationException (Wrong Direction)

    [Fact]
    public async Task MessageTransit_Send_ThrowsInvalidOperationException_OnReceiveOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("unhappy-recvonly-send");

#pragma warning disable IL2026, IL3050
        var transit = server.AcceptReceiveOnlyMessageTransit<TestMessage>("unhappy-recvonly-send");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.SendAsync(new TestMessage { Name = "fail", Value = 0 }));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_Receive_ThrowsInvalidOperationException_OnSendOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenSendOnlyMessageTransit<TestMessage>("unhappy-sendonly-recv");
#pragma warning restore IL2026, IL3050

        await transit.WaitForReadyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transit.ReceiveAsync());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region OversizedMessage

    [Fact]
    public async Task MessageTransit_OversizedMessage_ThrowsInvalidOperation()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m3");
        var r = await server.AcceptChannelAsync("m3");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r, maxMessageSize: 100);
#pragma warning restore IL2026, IL3050

        var bigMsg = new TestMessage { Name = new string('x', 200), Value = 1 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.SendAsync(bigMsg).AsTask());

        Assert.Contains("exceeds maximum", ex.Message);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region ReceiveAllAsync

    [Fact]
    public async Task MessageTransit_ReceiveAllAsync_StopsOnChannelClose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m4");
        var r = await server.AcceptChannelAsync("m4");

#pragma warning disable IL2026, IL3050
        var sender = new MessageTransit<TestMessage, TestMessage>(w, null);
        var receiver = new MessageTransit<TestMessage, TestMessage>(null, r);
#pragma warning restore IL2026, IL3050

        await sender.SendAsync(new TestMessage { Name = "only", Value = 1 });
        await sender.DisposeAsync();

        var messages = new List<TestMessage>();
        await foreach (var msg in receiver.ReceiveAllAsync())
        {
            messages.Add(msg);
        }

        Assert.Single(messages);
        Assert.Equal("only", messages[0].Name);

        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task MessageTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();

#pragma warning disable IL2026, IL3050
        var transit = mux.OpenMessageTransit<TestMessage, TestMessage>("unhappy-msg-cancelled-wait");
#pragma warning restore IL2026, IL3050

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.WaitForReadyAsync(cts.Token));

        await transit.DisposeAsync();
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_SendAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-msg-cancelled-send");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-msg-cancelled-send");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(transit.WaitForReadyAsync(), serverTransit.WaitForReadyAsync());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.SendAsync(new TestMessage { Name = "fail", Value = 0 }, cts.Token));

        await transit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var clientTransit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-msg-cancelled-recv");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-msg-cancelled-recv");
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

    #region Double Dispose

    [Fact]
    public async Task MessageTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-double-dispose-msg");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-double-dispose-msg");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(transit.WaitForReadyAsync(), serverTransit.WaitForReadyAsync());

        await transit.DisposeAsync();
        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m");
        var r = await server.AcceptChannelAsync("m");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();
        await transit.DisposeAsync();
        await transit.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Multiplexer Disposed

    [Fact]
    public async Task MessageTransit_Send_FailsGracefully_WhenMultiplexerDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

#pragma warning disable IL2026, IL3050
        var transit = client.OpenMessageTransit<TestMessage, TestMessage>("unhappy-mux-disposed-msg");
        var serverTransit = server.AcceptMessageTransit<TestMessage, TestMessage>("unhappy-mux-disposed-msg");
#pragma warning restore IL2026, IL3050

        await Task.WhenAll(transit.WaitForReadyAsync(), serverTransit.WaitForReadyAsync());

        await client.DisposeAsync();

        var exception = await Record.ExceptionAsync(
            async () => await transit.SendAsync(new TestMessage { Name = "fail", Value = 0 }));

        Assert.True(
            exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException or ChannelClosedException,
            $"Expected ObjectDisposedException, InvalidOperationException, IOException, OperationCanceledException, or ChannelClosedException but got {exception?.GetType().Name}");

        await transit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region IsConnected Precedence

    private sealed class FakeChannel(bool isConnected) : IWriteChannel, IReadChannel
    {
        public string ChannelId => "fake";
        public ChannelState State => ChannelState.Open;
        public bool IsReady => true;
        public bool IsConnected { get; set; } = isConnected;
        public ChannelPriority Priority => ChannelPriority.Normal;
        public ChannelStats Stats { get; } = new();
        public ChannelCloseReason? CloseReason => null;
        public Exception? CloseException => null;

        public event EventHandler? Ready { add { } remove { } }
        public event EventHandler? Connected { add { } remove { } }
        public event EventHandler<DisconnectedEventArgs>? Disconnected { add { } remove { } }
        public event EventHandler<ChannelCloseEventArgs>? Closed { add { } remove { } }

        public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public Stream AsStream() => Stream.Null;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task MessageTransit_IsConnected_ReturnsFalse_AfterDispose_EvenWhenReadChannelStillConnected()
    {
        // Issue #166: MessageTransit.IsConnected was missing parentheses around
        // the `||` group, so the read-channel disjunct bypassed the !_disposed
        // guard. With a read channel that still reports IsConnected == true
        // (e.g. during the window between flipping _disposed and the read
        // channel's IsConnected clearing), IsConnected wrongly returned true
        // for a disposed transit.
        var write = new FakeChannel(isConnected: false);
        var read = new FakeChannel(isConnected: true);

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(write, read);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);
    }

    [Fact]
    public async Task MessageTransit_IsConnected_ReturnsFalse_AfterDispose_EvenWhenWriteChannelStillConnected()
    {
        var write = new FakeChannel(isConnected: true);
        var read = new FakeChannel(isConnected: false);

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(write, read);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);
    }

    [Fact]
    public void MessageTransit_IsConnected_ReturnsTrue_WhenOnlyReadChannelConnected_BeforeDispose()
    {
        // Sanity check: the disjunction itself is preserved — a live transit
        // with only the read side connected still reports IsConnected.
        var write = new FakeChannel(isConnected: false);
        var read = new FakeChannel(isConnected: true);

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(write, read);
#pragma warning restore IL2026, IL3050

        Assert.True(transit.IsConnected);
    }

    #endregion
}
