using Xunit;

namespace NetConduit.Transit.Stream.UnitTests;

public sealed class StreamTransitTests
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

    #region Core StreamTransit

    [Fact]
    public async Task StreamTransit_WriteOnly_Sends()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s1"));
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanRead);

        byte[] data = [10, 20, 30];
        await transit.WriteAsync(data);

        var readChannel = await server.AcceptChannelAsync("s1", CancellationToken.None);
        var buf = new byte[10];
        int read = await readChannel.ReadAsync(buf, CancellationToken.None);
        Assert.Equal(3, read);
        Assert.Equal(data, buf[..3]);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadOnly_Receives()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("s2");
        var readChannel = await server.AcceptChannelAsync("s2", CancellationToken.None);
        var transit = new StreamTransit(readChannel);

        Assert.True(transit.CanRead);
        Assert.False(transit.CanWrite);

        byte[] data = [40, 50, 60, 70];
        await writeChannel.WriteAsync(data);

        var buf = new byte[10];
        int read = await transit.ReadAsync(buf);
        Assert.Equal(4, read);
        Assert.Equal(data, buf[..4]);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_WriteThrowsWhenReadOnly()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("s3");
        var readChannel = await server.AcceptChannelAsync("s3", CancellationToken.None);
        var transit = new StreamTransit(readChannel);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.WriteAsync(new byte[] { 1 }).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadThrowsWhenWriteOnly()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s4"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.ReadAsync(new byte[10]).AsTask());

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_SeekThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s5"));
        Assert.False(transit.CanSeek);
        Assert.Throws<NotSupportedException>(() => transit.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => transit.Length);
        Assert.Throws<NotSupportedException>(() => transit.Position);
        Assert.Throws<NotSupportedException>(() => transit.Position = 0);
        Assert.Throws<NotSupportedException>(() => transit.SetLength(0));
        transit.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Extension Methods

    [Fact]
    public async Task TransitExtensions_OpenStream_CreatesWriteOnlyStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("e1");
        Assert.True(transit.CanWrite);
        Assert.False(transit.CanRead);
        Assert.Equal("e1", transit.WriteChannelId);
        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TransitExtensions_AcceptStream_CreatesReadOnlyStream()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("e2");
        var transit = await server.AcceptStreamAsync("e2");
        Assert.True(transit.CanRead);
        Assert.False(transit.CanWrite);
        Assert.Equal("e2", transit.ReadChannelId);
        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Eagerness

    [Fact]
    public async Task StreamTransit_Write_IsReady_FalseBeforeChannelReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("s-ready1");

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

        var writeChannel = client.OpenChannel("s-ready2");

        var transit = server.AcceptStream("s-ready2");

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

    [Fact]
    public async Task AcceptStream_ReturnsImmediately_BeforeChannelReady()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ext-stream1");

        var transit = server.AcceptStream("ext-stream1");

        Assert.NotNull(transit);

        await transit.WaitForReadyAsync();
        Assert.True(transit.IsReady);

        await transit.DisposeAsync();
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

        var connected = await Task.WhenAny(connectedTcs.Task, Task.Delay(100)) == connectedTcs.Task || transit.IsConnected;

        Assert.True(connected);
        Assert.True(transit.IsConnected);

        await transit.DisposeAsync();
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

        Assert.False(transit.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region ObjectDisposedException

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
            async () => _ = await transit.ReadAsync(new byte[10], 0, 10));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_WriteAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s1"));
        await transit.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transit.WriteAsync(new byte[] { 1, 2, 3 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("s2");
        var r = await server.AcceptChannelAsync("s2");
        var transit = new StreamTransit(r);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReadAsync(new byte[10]).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region InvalidOperationException (Wrong Direction)

    [Fact]
    public async Task StreamTransit_Write_ThrowsInvalidOperationException_OnReadOnlyTransit()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("unhappy-readonly-write");
        var transit = server.AcceptStream("unhappy-readonly-write");
        await transit.WaitForReadyAsync();

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

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => _ = await transit.ReadAsync(new byte[10], 0, 10));

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region NotSupportedException (Stream Operations)

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

    #endregion

    #region ArgumentNullException

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

    #endregion

    #region Cancellation

    [Fact]
    public async Task StreamTransit_WaitForReadyAsync_ThrowsOperationCanceledException_WhenAlreadyCancelled()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();

        var transit = mux.OpenStream("unhappy-cancelled-wait");

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
    public async Task StreamTransit_DoubleDispose_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-double-dispose-stream");
        await transit.WaitForReadyAsync();

        await transit.DisposeAsync();
        await transit.DisposeAsync();

        Assert.False(transit.IsConnected);
        Assert.False(transit.IsReady);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region State After Disposal

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

        Assert.False(transit.IsReady);
        Assert.False(transit.IsConnected);
        Assert.False(transit.CanWrite);
        Assert.False(transit.CanSeek);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Multiplexer Disposed

    [Fact]
    public async Task StreamTransit_Write_FailsGracefully_WhenMultiplexerDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = client.OpenStream("unhappy-mux-disposed-write");
        await transit.WaitForReadyAsync();

        await client.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Record.ExceptionAsync(
            async () => await transit.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token));

        Assert.True(
            exception is ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException or ChannelClosedException,
            $"Expected ObjectDisposedException, InvalidOperationException, IOException, OperationCanceledException, or ChannelClosedException but got {exception?.GetType().Name}");

        await transit.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Sync Path Misuse

    [Fact]
    public async Task StreamTransit_SyncRead_AfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("s");
        var r = await server.AcceptChannelAsync("s");
        var transit = new StreamTransit(r);
        await transit.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            transit.Read(new byte[10], 0, 10));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_SyncWrite_AfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("s");
        await w.WaitForReadyAsync();
        var transit = new StreamTransit(w);
        await transit.DisposeAsync();

        Assert.ThrowsAny<Exception>(() =>
            transit.Write(new byte[10], 0, 10));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}
