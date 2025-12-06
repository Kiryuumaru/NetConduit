using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetConduit.UnitTests;

public class PerformanceTests
{
    [Fact(Timeout = 120000)]
    public async Task Performance_Throughput_SingleChannel()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannel = ch;
                break;
            }
        });

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "throughput_test" }, cts.Token);
        await acceptTask;

        const int totalMB = 100;
        const int chunkSize = 64 * 1024; // 64KB chunks
        var totalBytes = totalMB * 1024 * 1024L;
        var chunk = new byte[chunkSize];
        new Random(42).NextBytes(chunk);

        long bytesReceived = 0;
        var receiveTask = Task.Run(async () =>
        {
            var buffer = new byte[chunkSize];
            while (Volatile.Read(ref bytesReceived) < totalBytes)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                Interlocked.Add(ref bytesReceived, read);
            }
        });

        var sw = Stopwatch.StartNew();

        long bytesSent = 0;
        while (bytesSent < totalBytes)
        {
            await writeChannel.WriteAsync(chunk, cts.Token);
            bytesSent += chunkSize;
        }

        // Wait for all data to be received
        while (Volatile.Read(ref bytesReceived) < totalBytes && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10);
        }

        sw.Stop();

        var throughputMBps = totalMB / sw.Elapsed.TotalSeconds;
        
        // Log throughput (will be visible in test output)
        Console.WriteLine($"Throughput: {throughputMBps:F2} MB/s ({totalMB}MB in {sw.Elapsed.TotalSeconds:F2}s)");

        Assert.True(bytesReceived >= totalBytes, 
            $"Expected {totalBytes} bytes, got {bytesReceived}");
        Assert.True(throughputMBps > 10, 
            $"Throughput {throughputMBps:F2} MB/s is too low");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Performance_ChannelOpenLatency()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        const int iterations = 100;
        var latencies = new List<double>();

        // Accept all channels
        var acceptedChannels = new List<ReadChannel>();
        var acceptLoopReady = new SemaphoreSlim(0, 1);
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            acceptLoopReady.Release();
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                acceptedChannels.Add(ch);
                if (++count >= iterations) break;
            }
        });

        // Wait for accept loop to start
        await acceptLoopReady.WaitAsync(cts.Token);
        await Task.Delay(10, cts.Token);

        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"latency_{i}" }, cts.Token);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            await channel.DisposeAsync();
        }

        await acceptTask;

        // Cleanup accepted channels
        foreach (var ch in acceptedChannels)
        {
            await ch.DisposeAsync();
        }

        var avgLatency = latencies.Average();
        var p95Latency = latencies.OrderBy(x => x).ElementAt((int)(iterations * 0.95));
        var p99Latency = latencies.OrderBy(x => x).ElementAt((int)(iterations * 0.99));

        Console.WriteLine($"Channel open latency - Avg: {avgLatency:F2}ms, P95: {p95Latency:F2}ms, P99: {p99Latency:F2}ms");

        // Relaxed thresholds for CI environments which can have high latency spikes
        Assert.True(avgLatency < 100, $"Average latency {avgLatency:F2}ms is too high");
        Assert.True(p99Latency < 500, $"P99 latency {p99Latency:F2}ms is too high");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Performance_ManyChannels_MemoryEfficient()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        const int channelCount = 1000;
        var channels = new List<WriteChannel>();
        var readChannels = new ConcurrentBag<ReadChannel>();

        // Accept all channels
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannels.Add(ch);
                if (readChannels.Count >= channelCount) break;
            }
        });

        var memoryBefore = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();

        // Open many channels
        for (var i = 0; i < channelCount; i++)
        {
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"memory_{i}" }, cts.Token);
            channels.Add(channel);
        }

        sw.Stop();
        await Task.Delay(500); // Wait for accept

        // Force aggressive GC collection - important for CI/CD stability
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        await Task.Delay(200);
        var memoryAfter = GC.GetTotalMemory(true);
        var memoryPerChannel = (memoryAfter - memoryBefore) / channelCount;
        var timePerChannel = sw.Elapsed.TotalMilliseconds / channelCount;

        Console.WriteLine($"Opened {channelCount} channels in {sw.Elapsed.TotalMilliseconds:F0}ms ({timePerChannel:F2}ms/channel)");
        Console.WriteLine($"Memory per channel: ~{memoryPerChannel / 1024.0:F1}KB");

        Assert.Equal(channelCount, channels.Count);
        Assert.Equal(channelCount, readChannels.Count);
        // Relaxed threshold for CI environments which can have higher memory overhead
        // Increased from 150KB to 165KB to accommodate WriteChannel._writeLock for thread-safety
        Assert.True(memoryPerChannel < 200 * 1024, $"Memory per channel {memoryPerChannel / 1024.0:F1}KB is too high");

        // Cleanup
        foreach (var ch in channels)
            await ch.DisposeAsync();
        foreach (var ch in readChannels)
            await ch.DisposeAsync();

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Performance_ParallelDataTransfer_MultipleChannels()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        const int channelCount = 10;
        const int mbPerChannel = 10;
        const int chunkSize = 32 * 1024;
        var bytesPerChannel = mbPerChannel * 1024 * 1024;

        var receivedBytes = new ConcurrentDictionary<string, long>();

        // Accept and read
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                receivedBytes[channel.ChannelId] = 0;
                
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[chunkSize];
                    while (receivedBytes[channel.ChannelId] < bytesPerChannel)
                    {
                        var read = await channel.ReadAsync(buffer, cts.Token);
                        if (read == 0) break;
                        receivedBytes.AddOrUpdate(channel.ChannelId, read, (_, v) => v + read);
                    }
                });
                
                if (++count >= channelCount) break;
            }
        });

        var sw = Stopwatch.StartNew();

        // Open channels and send data in parallel
        var sendTasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"parallel_{i}" }, cts.Token);
            var chunk = new byte[chunkSize];
            new Random(i).NextBytes(chunk);
            
            long sent = 0;
            while (sent < bytesPerChannel)
            {
                await channel.WriteAsync(chunk, cts.Token);
                sent += chunkSize;
            }
            return channel;
        });

        var writeChannels = await Task.WhenAll(sendTasks);

        // Wait for all data
        var timeout = TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (receivedBytes.Values.All(v => v >= bytesPerChannel))
                break;
            await Task.Delay(100);
        }

        sw.Stop();

        var totalMB = channelCount * mbPerChannel;
        var throughput = totalMB / sw.Elapsed.TotalSeconds;

        Console.WriteLine($"Parallel transfer: {totalMB}MB across {channelCount} channels in {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Aggregate throughput: {throughput:F2} MB/s");

        // Verify all data received
        foreach (var kvp in receivedBytes)
        {
            Assert.True(kvp.Value >= bytesPerChannel,
                $"Channel {kvp.Key} received {kvp.Value} bytes, expected {bytesPerChannel}");
        }

        foreach (var ch in writeChannels)
            await ch.DisposeAsync();

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Performance_SmallMessages_HighFrequency()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                readChannel = ch;
                break;
            }
        });

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "small_messages" }, cts.Token);
        await acceptTask;

        const int messageCount = 10000;
        const int messageSize = 64; // Small messages
        var message = new byte[messageSize];
        new Random(42).NextBytes(message);

        var receivedCount = 0;
        var receiveTask = Task.Run(async () =>
        {
            var buffer = new byte[messageSize];
            while (receivedCount < messageCount)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                Interlocked.Add(ref receivedCount, read / messageSize);
            }
        });

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < messageCount; i++)
        {
            await writeChannel.WriteAsync(message, cts.Token);
        }

        // Wait for all messages
        while (Volatile.Read(ref receivedCount) < messageCount && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }

        sw.Stop();

        var messagesPerSecond = messageCount / sw.Elapsed.TotalSeconds;

        Console.WriteLine($"Small messages: {messageCount} messages ({messageSize}B each) in {sw.Elapsed.TotalMilliseconds:F0}ms");
        Console.WriteLine($"Rate: {messagesPerSecond:F0} msg/s");

        Assert.True(messagesPerSecond > 1000, $"Message rate {messagesPerSecond:F0}/s is too low");

        cts.Cancel();
    }
}
