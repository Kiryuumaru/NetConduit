namespace NetConduit.UnitTests;

[Collection("HighMemory")]
public sealed class ReconnectionHeavyChaosTests : IAsyncDisposable
{
    private readonly ReconnectableTransportFactory _factory = new();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", TestCategories.HighMemory)]
    public async Task HeavyReconnect_ManyChannels_ReplaySkipsAlreadyDeliveredBytes()
    {
        var (client, server) = CreateReconnectablePair(maxAutoReconnectAttempts: 5);

        try
        {
            client.Start();
            server.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await Task.WhenAll(
                client.WaitForReadyAsync(cts.Token),
                server.WaitForReadyAsync(cts.Token));

            const int channelCount = 24;
            const int reconnectCycles = 3;
            const int payloadSize = 4096;

            var (writers, readers) = await OpenAcceptedChannelsAsync(
                client,
                server,
                channelCount,
                "heavy-reconnect",
                cts.Token);

            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                var baseline = CreatePayload(channelIndex, cycle: -1, payloadSize);
                await writers[channelIndex].WriteAsync(baseline, cts.Token);
                var received = await ReadExactAsync(readers[channelIndex], baseline.Length, cts.Token);
                Assert.Equal(baseline, received);
            }

            for (int cycle = 0; cycle < reconnectCycles; cycle++)
            {
                await ReconnectBothPeersAsync(client, server, cts.Token);

                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    var payload = CreatePayload(channelIndex, cycle, payloadSize + channelIndex);
                    await writers[channelIndex].WriteAsync(payload, cts.Token);
                }

                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    var expected = CreatePayload(channelIndex, cycle, payloadSize + channelIndex);
                    var received = await ReadExactAsync(readers[channelIndex], expected.Length, cts.Token);
                    Assert.Equal(expected, received);
                }
            }

            Assert.True(client.IsConnected);
            Assert.True(server.IsConnected);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", TestCategories.HighMemory)]
    public async Task ChaosReconnect_UnlimitedAttempts_ChannelChurnSurvivesBeyondBoundedRetryCounts()
    {
        int clientConnectedCount = 0;
        int serverConnectedCount = 0;
        var (client, server) = CreateReconnectablePair(maxAutoReconnectAttempts: -1);
        client.Connected += (_, _) => Interlocked.Increment(ref clientConnectedCount);
        server.Connected += (_, _) => Interlocked.Increment(ref serverConnectedCount);

        try
        {
            client.Start();
            server.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await Task.WhenAll(
                client.WaitForReadyAsync(cts.Token),
                server.WaitForReadyAsync(cts.Token));

            const int persistentChannelCount = 4;
            const int reconnectCycles = 8;

            var (writers, readers) = await OpenAcceptedChannelsAsync(
                client,
                server,
                persistentChannelCount,
                "chaos-persistent",
                cts.Token);

            for (int cycle = 0; cycle < reconnectCycles; cycle++)
            {
                for (int channelIndex = 0; channelIndex < persistentChannelCount; channelIndex++)
                {
                    var beforeReconnect = CreatePayload(channelIndex, cycle * 2, size: 128 + channelIndex);
                    await writers[channelIndex].WriteAsync(beforeReconnect, cts.Token);
                    var received = await ReadExactAsync(readers[channelIndex], beforeReconnect.Length, cts.Token);
                    Assert.Equal(beforeReconnect, received);
                }

                await ReconnectBothPeersAsync(client, server, cts.Token);

                for (int channelIndex = 0; channelIndex < persistentChannelCount; channelIndex++)
                {
                    var afterReconnect = CreatePayload(channelIndex, cycle * 2 + 1, size: 256 + channelIndex);
                    await writers[channelIndex].WriteAsync(afterReconnect, cts.Token);
                    var received = await ReadExactAsync(readers[channelIndex], afterReconnect.Length, cts.Token);
                    Assert.Equal(afterReconnect, received);
                }

                var churnChannelId = $"chaos-churn-{cycle}";
                var churnWriter = client.OpenChannel(churnChannelId);
                var churnReader = await server.AcceptChannelAsync(churnChannelId, cts.Token);
                await churnWriter.WaitForReadyAsync(cts.Token);

                var churnPayload = CreatePayload(cycle, cycle, size: 512 + cycle);
                await churnWriter.WriteAsync(churnPayload, cts.Token);
                var churnReceived = await ReadExactAsync(churnReader, churnPayload.Length, cts.Token);
                Assert.Equal(churnPayload, churnReceived);

                await churnWriter.DisposeAsync();
                await churnReader.DisposeAsync();
            }

            Assert.True(clientConnectedCount >= reconnectCycles + 1);
            Assert.True(serverConnectedCount >= reconnectCycles + 1);
            Assert.True(_factory.ConnectionCount >= reconnectCycles + 1);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private (StreamMultiplexer Client, StreamMultiplexer Server) CreateReconnectablePair(int maxAutoReconnectAttempts)
    {
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = maxAutoReconnectAttempts,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(5),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(5),
            AutoReconnectBackoffMultiplier = 1,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => _factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = maxAutoReconnectAttempts,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(5),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(5),
            AutoReconnectBackoffMultiplier = 1,
        });

        return (client, server);
    }

    private async Task ReconnectBothPeersAsync(StreamMultiplexer client, StreamMultiplexer server, CancellationToken ct)
    {
        var clientReconnect = WaitForNextConnectedAsync(client, ct);
        var serverReconnect = WaitForNextConnectedAsync(server, ct);

        _factory.KillCurrentTransport();

        await Task.WhenAll(clientReconnect, serverReconnect);
        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    private static async Task WaitForNextConnectedAsync(StreamMultiplexer mux, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConnected(object? sender, EventArgs args)
        {
            mux.Connected -= OnConnected;
            tcs.TrySetResult();
        }

        mux.Connected += OnConnected;
        try
        {
            await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            mux.Connected -= OnConnected;
        }
    }

    private static async Task<(IWriteChannel[] Writers, IReadChannel[] Readers)> OpenAcceptedChannelsAsync(
        StreamMultiplexer client,
        StreamMultiplexer server,
        int channelCount,
        string prefix,
        CancellationToken ct)
    {
        var writers = new IWriteChannel[channelCount];
        var readers = new IReadChannel[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            var channelId = $"{prefix}-{i}";
            writers[i] = client.OpenChannel(channelId);
            readers[i] = await server.AcceptChannelAsync(channelId, ct);
            await writers[i].WaitForReadyAsync(ct);
        }

        return (writers, readers);
    }

    private static async Task<byte[]> ReadExactAsync(IReadChannel channel, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        int totalRead = 0;

        while (totalRead < length)
        {
            int read = await channel.ReadAsync(buffer.AsMemory(totalRead), ct);
            Assert.True(read > 0, $"Channel '{channel.ChannelId}' ended after {totalRead} of {length} bytes.");
            totalRead += read;
        }

        return buffer;
    }

    private static byte[] CreatePayload(int channelIndex, int cycle, int size)
    {
        var payload = new byte[size];
        int seed = channelIndex * 17 + cycle * 31;

        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((seed + i) % 251);

        return payload;
    }
}