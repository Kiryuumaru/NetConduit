using System.Collections.Concurrent;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// High-concurrency tests targeting gaps not covered by ConcurrencyTests or ChaosRobustnessTests:
/// - Concurrent named accept with data verification
/// - Parallel open + write + close from both sides simultaneously
/// - Channel creation rate under heavy load
/// </summary>
[Collection("HighMemory")]
[Trait("Category", "HighMemory")]
public class HighConcurrencyEdgeTests
{
    [Fact(Timeout = 120000)]
    [Trait("Category", "Stress")]
    public async Task ConcurrentNamedAccept_AllChannelsMatchCorrectly()
    {
        await using var pipe = new DuplexPipe();
        await using var muxA = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var muxB = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runA = muxA.Start(cts.Token);
        var runB = muxB.Start(cts.Token);
        await Task.Delay(100);

        const int channelCount = 50;
        var receivedData = new ConcurrentDictionary<string, byte[]>();

        // Start named accept tasks for all channels before any are opened
        var acceptTasks = new List<Task>();
        for (int i = 0; i < channelCount; i++)
        {
            var id = $"named_{i}";
            acceptTasks.Add(Task.Run(async () =>
            {
                var reader = await muxB.AcceptChannelAsync(id, cts.Token);
                receivedData[id] = await ReadAllBytesAsync(reader, cts.Token);
            }));
        }

        // Open all channels and write unique data
        var sentData = new ConcurrentDictionary<string, byte[]>();
        var sem = new SemaphoreSlim(10);
        var writeTasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            await sem.WaitAsync(cts.Token);
            try
            {
                var id = $"named_{i}";
                var data = new byte[256];
                Random.Shared.NextBytes(data);
                sentData[id] = data;

                var writer = await muxA.OpenChannelAsync(
                    new ChannelOptions { ChannelId = id }, cts.Token);
                await writer.WriteAsync(data, cts.Token);
                await writer.CloseAsync();
            }
            finally { sem.Release(); }
        }).ToArray();

        await Task.WhenAll(writeTasks);
        await Task.WhenAll(acceptTasks);

        // Every channel should have received its own data
        Assert.Equal(channelCount, receivedData.Count);
        foreach (var kvp in sentData)
        {
            Assert.True(receivedData.TryGetValue(kvp.Key, out var received));
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Stress")]
    public async Task ParallelOpenWriteClose_BothSides_NoDeadlock()
    {
        await using var pipe = new DuplexPipe();
        await using var muxA = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var muxB = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runA = muxA.Start(cts.Token);
        var runB = muxB.Start(cts.Token);
        await Task.Delay(100);

        const int perSide = 20;
        var aRecv = new ConcurrentDictionary<string, byte[]>();
        var bRecv = new ConcurrentDictionary<string, byte[]>();

        // Accept from both sides
        var aAccept = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxA.AcceptChannelsAsync(cts.Token))
            {
                var c = ch;
                _ = Task.Run(async () =>
                {
                    aRecv[c.ChannelId] = await ReadAllBytesAsync(c, cts.Token);
                });
                if (++count >= perSide) break;
            }
        });

        var bAccept = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var c = ch;
                _ = Task.Run(async () =>
                {
                    bRecv[c.ChannelId] = await ReadAllBytesAsync(c, cts.Token);
                });
                if (++count >= perSide) break;
            }
        });

        // A opens channels to B
        var aSend = Task.Run(async () =>
        {
            for (int i = 0; i < perSide; i++)
            {
                var w = await muxA.OpenChannelAsync(
                    new ChannelOptions { ChannelId = $"a2b_{i}" }, cts.Token);
                await w.WriteAsync(new byte[] { (byte)(i + 1) }, cts.Token);
                await w.CloseAsync();
            }
        });

        // B opens channels to A
        var bSend = Task.Run(async () =>
        {
            for (int i = 0; i < perSide; i++)
            {
                var w = await muxB.OpenChannelAsync(
                    new ChannelOptions { ChannelId = $"b2a_{i}" }, cts.Token);
                await w.WriteAsync(new byte[] { (byte)(i + 100) }, cts.Token);
                await w.CloseAsync();
            }
        });

        await Task.WhenAll(aSend, bSend);
        await Task.Delay(500); // Allow accept tasks to complete
        await Task.WhenAll(aAccept, bAccept);

        Assert.True(bRecv.Count >= perSide - 1);
        Assert.True(aRecv.Count >= perSide - 1);

        // Verify data integrity for received channels
        foreach (var kvp in bRecv)
        {
            if (kvp.Key.StartsWith("a2b_"))
            {
                var idx = int.Parse(kvp.Key[4..]);
                Assert.Equal(new byte[] { (byte)(idx + 1) }, kvp.Value);
            }
        }

        foreach (var kvp in aRecv)
        {
            if (kvp.Key.StartsWith("b2a_"))
            {
                var idx = int.Parse(kvp.Key[4..]);
                Assert.Equal(new byte[] { (byte)(idx + 100) }, kvp.Value);
            }
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Stress")]
    public async Task RapidSequentialChannels_StatsAccumulate()
    {
        await using var pipe = new DuplexPipe();
        await using var muxA = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var muxB = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runA = muxA.Start(cts.Token);
        var runB = muxB.Start(cts.Token);
        await Task.Delay(100);

        const int iterations = 100;

        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var buf = new byte[1024];
                while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                if (++count >= iterations) break;
            }
        });

        for (int i = 0; i < iterations; i++)
        {
            var w = await muxA.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"seq_{i}" }, cts.Token);
            await w.WriteAsync(new byte[100], cts.Token);
            await w.CloseAsync();
        }

        await acceptTask;

        Assert.Equal(iterations, muxA.Stats.TotalChannelsOpened);
        Assert.True(muxA.Stats.BytesSent > 0);

        cts.Cancel();
    }

    private static async Task<byte[]> ReadAllBytesAsync(ReadChannel channel, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await channel.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
