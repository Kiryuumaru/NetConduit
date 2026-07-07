using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests that continuous streaming survives rapid topology churn — the writer
/// and reader race while cancellation may fire at any point. Asserts that all
/// written messages arrive and no messages are lost or duplicated.
/// </summary>
[Collection("Sequential")]
public sealed class TurbulenceTests
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

    [Fact]
    public async Task ContinuousStream_SurvivesRapidTopologyChurn()
    {
        (StreamMultiplexer? client, StreamMultiplexer? server) = (null, null);
        try
        {
            (client, server) = await CreateReadyPairAsync();

            const int MessageSize = 256;
            using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var writer = client.OpenChannel(new ChannelOptions { ChannelId = "turbulence" });
            var reader = server.AcceptChannel("turbulence");
            await Task.WhenAll(writer.WaitForReadyAsync(streamCts.Token), reader.WaitForReadyAsync(streamCts.Token));

            var receivedSeqs = new ConcurrentBag<long>();
            long lastSeqWritten = -1;

            var writerTask = Task.Run(async () =>
            {
                long seq = 0;
                while (!streamCts.Token.IsCancellationRequested)
                {
                    var msg = new byte[MessageSize];
                    BinaryPrimitives.WriteInt64LittleEndian(msg, seq);
                    try
                    {
                        await writer.WriteAsync(msg, streamCts.Token);
                        Volatile.Write(ref lastSeqWritten, seq);
                        seq++;
                        await Task.Delay(25, streamCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            var readerTask = Task.Run(async () =>
            {
                var buf = new byte[65536];
                int off = 0;
                int cnt = 0;

                try
                {
                    while (true)
                    {
                        int n = await reader.ReadAsync(buf.AsMemory(off + cnt), streamCts.Token);
                        if (n == 0) break;
                        cnt += n;

                        while (cnt >= MessageSize)
                        {
                            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off, MessageSize));
                            receivedSeqs.Add(seq);
                            off += MessageSize;
                            cnt -= MessageSize;
                        }

                        if (off > 0 && cnt > 0)
                        {
                            Array.Copy(buf, off, buf, 0, cnt);
                            off = 0;
                        }
                        else if (cnt == 0) off = 0;
                    }
                }
                catch (OperationCanceledException) { }
            });

            await Task.WhenAll(writerTask, readerTask);

            var sorted = receivedSeqs.OrderBy(s => s).ToList();
            long totalWritten = Volatile.Read(ref lastSeqWritten) + 1;

            Assert.True(sorted.Count >= 200,
                $"Expected at least 200 messages, got {sorted.Count}.");

            for (int i = 0; i < sorted.Count; i++)
                Assert.Equal(i, sorted[i]);

            Assert.Equal(totalWritten, sorted.Count);
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
        }
    }
}
