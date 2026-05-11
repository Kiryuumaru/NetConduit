using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.UnitTests;

[Collection("HighMemorySubMux")]
[Trait("Category", "HighMemory")]
public sealed class MemoryLeakSubMuxTests
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private const int MemorySampleIntervalMs = 2000;

    [Fact(Timeout = 180_000)]
    public async Task MemoryLeak_SubMuxChaos_MemoryStaysBounded()
    {
        var duplex = new DuplexMemoryStream();
        var outerClient = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 65536 },
        });
        var outerServer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 65536 },
        });
        outerClient.Start();
        outerServer.Start();
        await Task.WhenAll(outerClient.WaitForReadyAsync(), outerServer.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TestDuration + TimeSpan.FromSeconds(30));
        var endTime = Environment.TickCount64 + (long)TestDuration.TotalMilliseconds;

        // Warm up
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        var memorySamples = new List<long> { baselineMemory };
        int subMuxCount = 0;

        // Chaos: repeatedly create sub-muxes, transfer data, dispose them
        var chaosTask = Task.Run(async () =>
        {
            int round = 0;
            while (Environment.TickCount64 < endTime && !cts.IsCancellationRequested)
            {
                try
                {
                    var (innerClient, innerServer) = await CreateNestedMuxAsync(
                        outerClient, outerServer,
                        $"fwd-{round}", $"rev-{round}", cts.Token);

                    var innerServerTask = Task.Run(async () =>
                    {
                        int count = 0;
                        await foreach (var ch in innerServer.AcceptChannelsAsync(cts.Token))
                        {
                            var buf = new byte[4096];
                            try
                            {
                                while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                            }
                            catch (OperationCanceledException) { }
                            catch (IOException) { }
                            catch (ChannelClosedException) { }
                            finally
                            {
                                await ch.DisposeAsync();
                            }
                            count++;
                            if (count >= 5) break;
                        }
                    }, CancellationToken.None);

                    for (int i = 0; i < 5; i++)
                    {
                        var writer = innerClient.OpenChannel($"sub-{i}");
                        var data = new byte[Random.Shared.Next(256, 8192)];
                        Random.Shared.NextBytes(data);
                        await writer.WriteAsync(data, cts.Token);
                        await writer.DisposeAsync();
                    }

                    await innerServerTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

                    await innerClient.DisposeAsync();
                    await innerServer.DisposeAsync();

                    Interlocked.Increment(ref subMuxCount);
                    round++;
                }
                catch (OperationCanceledException) { break; }
                catch (MultiplexerException) { }
                catch (IOException) { }
                catch (ChannelClosedException) { }
                catch (TimeoutException) { }
            }
        }, CancellationToken.None);

        // Memory sampler
        var samplerTask = Task.Run(async () =>
        {
            while (Environment.TickCount64 < endTime && !cts.IsCancellationRequested)
            {
                await Task.Delay(MemorySampleIntervalMs, cts.Token);
                long mem = GC.GetTotalMemory(forceFullCollection: false);
                lock (memorySamples)
                {
                    memorySamples.Add(mem);
                }
            }
        }, CancellationToken.None);

        await Task.Delay(TestDuration, cts.Token);
        await cts.CancelAsync();

        try { await chaosTask.WaitAsync(CleanupTimeout); } catch { }
        try { await samplerTask.WaitAsync(CleanupTimeout); } catch { }

        await outerClient.DisposeAsync();
        await outerServer.DisposeAsync();

        // Final measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long finalMemory = GC.GetTotalMemory(forceFullCollection: true);

        double growthFactor = (double)finalMemory / baselineMemory;
        long peakMemory;
        double stabilityRatio;
        lock (memorySamples)
        {
            memorySamples.Add(finalMemory);
            peakMemory = memorySamples.Max();
            int half = memorySamples.Count / 2;
            double firstHalfAvg = memorySamples.Take(half).Average();
            double secondHalfAvg = memorySamples.Skip(half).Average();
            stabilityRatio = secondHalfAvg / firstHalfAvg;
        }

        Assert.True(
            stabilityRatio < 4.0,
            $"Memory appears unbounded: second-half/first-half ratio = {stabilityRatio:F2}x. " +
            $"Baseline {baselineMemory / 1024}KB, final {finalMemory / 1024}KB ({growthFactor:F1}x), " +
            $"peak {peakMemory / 1024}KB. Sub-muxes created: {subMuxCount}.");
    }

    private static async Task<(StreamMultiplexer InnerClient, StreamMultiplexer InnerServer)>
        CreateNestedMuxAsync(
            IStreamMultiplexer outerClient, IStreamMultiplexer outerServer,
            string fwdId, string revId, CancellationToken ct)
    {
        var clientRead = outerClient.AcceptChannel(revId);
        var serverRead = outerServer.AcceptChannel(fwdId);
        var clientWrite = outerClient.OpenChannel(new ChannelOptions { ChannelId = fwdId, SlabSize = 65536 });
        var serverWrite = outerServer.OpenChannel(new ChannelOptions { ChannelId = revId, SlabSize = 65536 });

        await Task.WhenAll(
            clientRead.WaitForReadyAsync(ct),
            serverRead.WaitForReadyAsync(ct),
            clientWrite.WaitForReadyAsync(ct),
            serverWrite.WaitForReadyAsync(ct));

        var transport1 = new ChannelStreamPair(clientWrite, clientRead);
        var transport2 = new ChannelStreamPair(serverWrite, serverRead);

        var innerClient = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(transport1),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 65536 },
        });
        var innerServer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(transport2),
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = 65536 },
        });

        innerClient.Start();
        innerServer.Start();
        await Task.WhenAll(innerClient.WaitForReadyAsync(ct), innerServer.WaitForReadyAsync(ct));
        return (innerClient, innerServer);
    }

    private sealed class ChannelStreamPair(IWriteChannel writeChannel, IReadChannel readChannel) : IStreamPair
    {
        public Stream ReadStream => readChannel.AsStream();
        public Stream WriteStream => writeChannel.AsStream();
        public async ValueTask DisposeAsync()
        {
            await writeChannel.DisposeAsync();
            await readChannel.DisposeAsync();
        }
    }
}
