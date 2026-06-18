namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for ReadAsync cancellation token handling:
/// the fast path must respect an already-cancelled token.
/// </summary>
public sealed class ReadAsyncCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

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

    [Fact]
    public async Task ReadAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var write = client.OpenChannel("canceltest");
        var read = await server.AcceptChannelAsync("canceltest", CancellationToken.None);
        await write.WaitForReadyAsync(new CancellationTokenSource(TestTimeout).Token);

        // Write data so it's buffered in the read slab
        await write.WriteAsync(new byte[100]);

        // Allow delivery to the reader slab
        await Task.Delay(50);

        // Read with an already-cancelled token — must throw
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => read.ReadAsync(new byte[10], cts.Token).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_CancelledMidRead_ThrowsOperationCanceledException()
    {
        var (client, server) = await CreateReadyPairAsync();

        var write = client.OpenChannel("canceltest2");
        var read = await server.AcceptChannelAsync("canceltest2", CancellationToken.None);
        await write.WaitForReadyAsync(new CancellationTokenSource(TestTimeout).Token);

        // Write data so it's buffered
        await write.WriteAsync(new byte[200]);

        // Allow delivery to the reader slab
        await Task.Delay(50);

        // Read first chunk without cancellation — should succeed
        using var cts = new CancellationTokenSource();
        int n1 = await read.ReadAsync(new byte[10], cts.Token);
        Assert.Equal(10, n1);

        // Cancel and read again — fast path should reject
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => read.ReadAsync(new byte[10], cts.Token).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsync_NotCancelled_ReturnsBufferedData()
    {
        var (client, server) = await CreateReadyPairAsync();

        var write = client.OpenChannel("nocancel");
        var read = await server.AcceptChannelAsync("nocancel", CancellationToken.None);
        await write.WaitForReadyAsync(new CancellationTokenSource(TestTimeout).Token);

        var data = new byte[50];
        Array.Fill(data, (byte)0xAA);
        await write.WriteAsync(data);

        await Task.Delay(50);

        // Read with non-cancelled token — should return data normally
        using var cts = new CancellationTokenSource();
        var buf = new byte[50];
        int n = await read.ReadAsync(buf, cts.Token);
        Assert.Equal(50, n);
        Assert.All(buf, b => Assert.Equal(0xAA, b));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
