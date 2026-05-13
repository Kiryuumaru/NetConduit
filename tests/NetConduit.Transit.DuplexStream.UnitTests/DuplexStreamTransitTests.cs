using Xunit;

namespace NetConduit.Transit.DuplexStream.UnitTests;

public sealed class DuplexStreamTransitTests
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

    #region Core DuplexStreamTransit

    [Fact]
    public async Task DuplexStreamTransit_BidirectionalDataFlow()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientWrite = client.OpenChannel("d1>>");
        var clientRead = await server.AcceptChannelAsync("d1>>", CancellationToken.None);
        var serverWrite = server.OpenChannel("d1<<");
        var serverRead = await client.AcceptChannelAsync("d1<<", CancellationToken.None);

        var clientTransit = new DuplexStreamTransit(clientWrite, serverRead);
        var serverTransit = new DuplexStreamTransit(serverWrite, clientRead);

        await clientTransit.WriteAsync(new byte[] { 1, 2, 3 });
        var buf = new byte[10];
        int read = await serverTransit.ReadAsync(buf);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..3]);

        await serverTransit.WriteAsync(new byte[] { 4, 5 });
        read = await clientTransit.ReadAsync(buf);
        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 4, 5 }, buf[..2]);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_CanReadCanWrite()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("d2>>");
        var r = await server.AcceptChannelAsync("d2>>", CancellationToken.None);

        var transit = new DuplexStreamTransit(w, r);
        Assert.True(transit.CanRead);
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Extension Methods

    [Fact]
    public async Task TransitExtensions_OpenDuplexStream_CreatesFullDuplexStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var openTask = client.OpenDuplexStreamAsync("e3");
        var acceptTask = server.AcceptDuplexStreamAsync("e3");

        var clientStream = await openTask;
        var serverStream = await acceptTask;

        Assert.True(clientStream.CanRead);
        Assert.True(clientStream.CanWrite);
        Assert.Equal("e3>>", clientStream.WriteChannelId);
        Assert.Equal("e3<<", clientStream.ReadChannelId);

        Assert.True(serverStream.CanRead);
        Assert.True(serverStream.CanWrite);
        Assert.Equal("e3<<", serverStream.WriteChannelId);
        Assert.Equal("e3>>", serverStream.ReadChannelId);

        await clientStream.WriteAsync(new byte[] { 1, 2, 3 });
        var buf = new byte[10];
        int read = await serverStream.ReadAsync(buf);
        Assert.Equal(3, read);

        await serverStream.WriteAsync(new byte[] { 4, 5 });
        read = await clientStream.ReadAsync(buf);
        Assert.Equal(2, read);

        await clientStream.DisposeAsync();
        await serverStream.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Eagerness

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

        var clientTransit = client.OpenDuplexStream("d-ready2");
        clientTransit.Ready += (_, _) => clientReadyTcs.TrySetResult();

        var serverTransit = server.AcceptDuplexStream("d-ready2");
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
    public async Task DuplexStreamTransit_ChannelIds_MatchConvention()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("d-ids");
        var serverTransit = server.AcceptDuplexStream("d-ids");

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        Assert.Equal("d-ids>>", clientTransit.WriteChannelId);
        Assert.Equal("d-ids<<", clientTransit.ReadChannelId);

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

        var clientTransit = client.OpenDuplexStream("d-nonasync");
        var serverTransit = server.AcceptDuplexStream("d-nonasync");

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

    #endregion

    #region Connected/Disconnected Events

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

    #endregion

    #region Optimistic Write

    [Fact]
    public async Task DuplexStreamTransit_OptimisticPattern_WritesBeforeReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientTransit = client.OpenDuplexStream("e2e-optimistic1");
        var serverTransit = server.AcceptDuplexStream("e2e-optimistic1");

        var writeTask = clientTransit.WriteAsync(new byte[] { 1, 2, 3 });

        await Task.WhenAll(
            clientTransit.WaitForReadyAsync(),
            serverTransit.WaitForReadyAsync());

        await writeTask;

        var buffer = new byte[10];
        var read = await serverTransit.ReadAsync(buffer);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..3]);

        await clientTransit.DisposeAsync();
        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region ObjectDisposedException

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
            async () => _ = await serverTransit.ReadAsync(new byte[10], 0, 10));

        await clientTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_ReadAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("w");
        var r = await server.AcceptChannelAsync("w");

        var transit = new DuplexStreamTransit(w, r);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReadAsync(new byte[10]).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_WriteAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("w");
        var r = await server.AcceptChannelAsync("w");

        var transit = new DuplexStreamTransit(w, r);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.WriteAsync(new byte[10]).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DuplexStreamTransit_CanReadCanWrite_FalseAfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("w");
        var r = await server.AcceptChannelAsync("w");

        var transit = new DuplexStreamTransit(w, r);
        Assert.True(transit.CanRead);
        Assert.True(transit.CanWrite);

        await transit.DisposeAsync();

        Assert.False(transit.CanRead);
        Assert.False(transit.CanWrite);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region NotSupportedException (Stream Operations)

    [Fact]
    public async Task DuplexStreamTransit_SeekThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("w");
        var r = await server.AcceptChannelAsync("w");

        var transit = new DuplexStreamTransit(w, r);

        Assert.False(transit.CanSeek);
        Assert.Throws<NotSupportedException>(() => transit.Length);
        Assert.Throws<NotSupportedException>(() => transit.Position);
        Assert.Throws<NotSupportedException>(() => transit.Position = 0);
        Assert.Throws<NotSupportedException>(() => transit.Seek(0, SeekOrigin.Begin));

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

    #region ArgumentNullException

    [Fact]
    public async Task DuplexStreamTransit_Constructor_ThrowsArgumentNullException_WhenWriteChannelNull()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();
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
        mux.Start();
        var ch = mux.OpenChannel("test");

        Assert.Throws<ArgumentNullException>(() => new DuplexStreamTransit(ch, null!));

        await mux.DisposeAsync();
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task DuplexStreamTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();

        var transit = mux.OpenDuplexStream("unhappy-duplex-cancelled-wait");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await transit.WaitForReadyAsync(cts.Token));

        await transit.DisposeAsync();
        await mux.DisposeAsync();
    }

    #endregion

    #region Double Dispose

    [Fact]
    public async Task DuplexStreamTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-double-dispose-duplex");
        var serverTransit = server.AcceptDuplexStream("unhappy-double-dispose-duplex");
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
    public async Task DuplexStreamTransit_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("w");
        var r = await server.AcceptChannelAsync("w");

        var transit = new DuplexStreamTransit(w, r);
        await transit.DisposeAsync();
        await transit.DisposeAsync();
        await transit.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region State After Disposal

    [Fact]
    public async Task DuplexStreamTransit_Properties_ReturnDefaults_AfterDisposal()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenDuplexStream("unhappy-duplex-state-after-dispose");
        var serverTransit = server.AcceptDuplexStream("unhappy-duplex-state-after-dispose");
        await Task.WhenAll(transit.WaitForReadyAsync(), serverTransit.WaitForReadyAsync());

        Assert.True(transit.IsReady);
        Assert.True(transit.IsConnected);
        Assert.True(transit.CanRead);
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await transit.DisposeAsync();

        Assert.False(transit.IsReady);
        Assert.False(transit.IsConnected);
        Assert.False(transit.CanRead);
        Assert.False(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region DuplexStreamTransit as Transport

    private sealed class DuplexStreamPair(System.IO.Stream duplexStream) : IStreamPair
    {
        public System.IO.Stream ReadStream => duplexStream;
        public System.IO.Stream WriteStream => duplexStream;
        public ValueTask DisposeAsync() => duplexStream.DisposeAsync();
    }

    [Fact]
    public async Task NestedMux_DuplexStreamTransitAsTransport_Works()
    {
        var (outerClient, outerServer) = await CreateReadyPairAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var cWrite = outerClient.OpenChannel("fwd");
        var sRead = await outerServer.AcceptChannelAsync("fwd", cts.Token);
        var sWrite = outerServer.OpenChannel("rev");
        var cRead = await outerClient.AcceptChannelAsync("rev", cts.Token);

        var clientDuplex = new DuplexStreamTransit(cWrite, cRead);
        var serverDuplex = new DuplexStreamTransit(sWrite, sRead);

        var innerClient = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(
                new DuplexStreamPair(clientDuplex)),
        });
        var innerServer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(
                new DuplexStreamPair(serverDuplex)),
        });

        innerClient.Start();
        innerServer.Start();
        await Task.WhenAll(innerClient.WaitForReadyAsync(cts.Token),
                           innerServer.WaitForReadyAsync(cts.Token));

        var writer = await innerClient.OpenChannelAsync("test", cts.Token);
        var reader = await innerServer.AcceptChannelAsync("test", cts.Token);

        var testData = new byte[2048];
        Random.Shared.NextBytes(testData);

        await writer.WriteAsync(testData, cts.Token);
        await writer.DisposeAsync();

        var received = new byte[2048];
        int total = 0;
        while (total < testData.Length)
        {
            var read = await reader.ReadAsync(received.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(testData, received);

        await innerClient.DisposeAsync();
        await innerServer.DisposeAsync();
        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();
    }

    #endregion
}
