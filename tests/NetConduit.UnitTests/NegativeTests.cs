namespace NetConduit.UnitTests;

public sealed class NegativeTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
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
        return (client, server);
    }

    [Fact]
    public async Task ChannelClose_RemoteSide_ReadReturnsZero()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writeChannel = client.OpenChannel("half-close");
        var readChannel = await server.AcceptChannelAsync("half-close", cts.Token);

        byte[] data = [1, 2, 3, 4, 5];
        await writeChannel.WriteAsync(data, cts.Token);
        await writeChannel.DisposeAsync(); // sends FIN

        // Reader should get the data then EOF
        var buffer = new byte[16];
        int totalRead = 0;
        int read;
        while ((read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token)) > 0)
        {
            totalRead += read;
        }

        Assert.Equal(5, totalRead);
        Assert.Equal(data, buffer[..5]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MultiplexerDispose_ClosesAllChannels()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var channels = new WriteChannel[5];
        for (int i = 0; i < 5; i++)
        {
            channels[i] = client.OpenChannel($"disposable-{i}");
        }

        await client.DisposeAsync();

        foreach (var ch in channels)
        {
            Assert.True(ch.State is ChannelState.Closed or ChannelState.Closing);
        }

        Assert.False(client.IsRunning);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteAfterClose_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel("write-after-close");
        await channel.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await channel.WriteAsync(new byte[] { 1, 2, 3 });
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_AfterDispose_Throws()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        await client.DisposeAsync();

        Assert.ThrowsAny<Exception>(() => client.OpenChannel("late"));

        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteEmptyBuffer_Succeeds()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var channel = client.OpenChannel("empty-write");
        var readCh = await server.AcceptChannelAsync("empty-write", cts.Token);

        // Empty write should be a no-op
        await channel.WriteAsync(Array.Empty<byte>(), cts.Token);

        // Subsequent write should work normally
        byte[] data = [42, 43, 44];
        await channel.WriteAsync(data, cts.Token);

        var buffer = new byte[16];
        int read = await readCh.ReadAsync(buffer, cts.Token);
        Assert.True(read >= 3);
        Assert.Equal(data, buffer[..3]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringAccept_ThrowsOperationCanceled()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await server.AcceptChannelAsync("nonexistent", cts.Token);
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentOpenClose_NoDeadlock()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var tasks = new List<Task>();
        for (int t = 0; t < 10; t++)
        {
            int thread = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    var ch = client.OpenChannel($"deadlock-{thread}-{i}");
                    await ch.WriteAsync(new byte[] { 1 }, cts.Token);
                    await ch.DisposeAsync();
                }
            }, cts.Token));
        }

        // Accept all channels on server side
        tasks.Add(Task.Run(async () =>
        {
            int accepted = 0;
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[4];
                while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                accepted++;
                if (accepted >= 100) break;
            }
        }, cts.Token));

        await Task.WhenAll(tasks);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentWrites_SameChannel_NoCorruption()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var channel = client.OpenChannel("concurrent-write");
        var readChannel = await server.AcceptChannelAsync("concurrent-write", cts.Token);

        const int writers = 4;
        const int writesEach = 50;
        const int writeSize = 256;

        var writeTasks = new Task[writers];
        for (int w = 0; w < writers; w++)
        {
            int writer = w;
            writeTasks[w] = Task.Run(async () =>
            {
                for (int i = 0; i < writesEach; i++)
                {
                    var data = new byte[writeSize];
                    Array.Fill(data, (byte)writer);
                    await channel.WriteAsync(data, cts.Token);
                }
            }, cts.Token);
        }

        int expectedBytes = writers * writesEach * writeSize;
        var received = new byte[expectedBytes];
        int totalRead = 0;

        var readTask = Task.Run(async () =>
        {
            while (totalRead < expectedBytes)
            {
                int r = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (r == 0) break;
                totalRead += r;
            }
        }, cts.Token);

        await Task.WhenAll(writeTasks);
        await channel.DisposeAsync();
        await readTask;

        Assert.Equal(expectedBytes, totalRead);

        // Verify each 256-byte block has consistent fill
        for (int offset = 0; offset < expectedBytes; offset += writeSize)
        {
            byte fill = received[offset];
            Assert.True(fill < writers, $"Invalid fill byte {fill} at offset {offset}");
            for (int b = 1; b < writeSize; b++)
            {
                Assert.Equal(fill, received[offset + b]);
            }
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DisposeIdempotent_MultipleDisposesCalls()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Multiple disposes should not throw
        await client.DisposeAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();

        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelDisposeIdempotent()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        var channel = client.OpenChannel("idempotent");

        await channel.DisposeAsync();
        await channel.DisposeAsync();
        await channel.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_WriterCloses_ReaderGetsDataThenEof()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writeChannel = client.OpenChannel("eof-test");
        var readChannel = await server.AcceptChannelAsync("eof-test", cts.Token);

        // Write 3 bytes then close
        await writeChannel.WriteAsync(new byte[] { 10, 20, 30 }, cts.Token);
        await writeChannel.DisposeAsync();

        // Read should get 3 bytes then 0
        var buffer = new byte[16];
        int totalRead = 0;
        int read;
        while ((read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token)) > 0)
        {
            totalRead += read;
        }

        Assert.Equal(3, totalRead);
        Assert.Equal(new byte[] { 10, 20, 30 }, buffer[..3]);
        // Next read should return 0 (EOF)
        Assert.Equal(0, await readChannel.ReadAsync(buffer, cts.Token));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_MultipleSequentialReads_BufferedDataCorrect()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writeChannel = client.OpenChannel("sequential-reads");
        var readChannel = await server.AcceptChannelAsync("sequential-reads", cts.Token);

        // Write 8KB of known data
        var sent = new byte[8192];
        for (int i = 0; i < sent.Length; i++)
            sent[i] = (byte)(i % 256);
        await writeChannel.WriteAsync(sent, cts.Token);
        await writeChannel.DisposeAsync();

        // Read in 256-byte chunks
        var received = new byte[8192];
        int totalRead = 0;
        var smallBuf = new byte[256];
        int read;
        while ((read = await readChannel.ReadAsync(smallBuf, cts.Token)) > 0)
        {
            Buffer.BlockCopy(smallBuf, 0, received, totalRead, read);
            totalRead += read;
        }

        Assert.Equal(8192, totalRead);
        Assert.Equal(sent, received);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
