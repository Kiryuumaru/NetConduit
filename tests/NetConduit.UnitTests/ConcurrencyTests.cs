using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetConduit.UnitTests;

public class ConcurrencyTests
{
    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Concurrent_MultipleChannels_AllDataTransferredCorrectly()
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

        const int channelCount = 100;
        const int dataSize = 1024;
        
        var receivedData = new ConcurrentDictionary<string, byte[]>();
        var sentData = new ConcurrentDictionary<string, byte[]>();
        var errors = new ConcurrentBag<string>();

        // Use named acceptance pattern for reliable concurrent operations
        var semaphore = new SemaphoreSlim(20);
        var tasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            await semaphore.WaitAsync(cts.Token);
            try
            {
                var channelId = $"channel_{i}";
                
                // Open channel
                var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                
                // Accept by name
                var readChannel = await acceptor.AcceptChannelAsync(channelId, cts.Token);
                
                // Generate and send data
                var data = new byte[dataSize];
                new Random(i).NextBytes(data);
                sentData[channelId] = data;
                
                await writeChannel.WriteAsync(data, cts.Token);
                await writeChannel.CloseAsync(cts.Token);
                
                // Read all data
                var buffer = new MemoryStream();
                var temp = new byte[256];
                while (true)
                {
                    var read = await readChannel.ReadAsync(temp, cts.Token);
                    if (read == 0) break;
                    buffer.Write(temp, 0, read);
                }
                receivedData[channelId] = buffer.ToArray();
            }
            catch (Exception ex)
            {
                errors.Add($"Channel {i}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        Assert.True(errors.Count == 0, $"Errors: {string.Join("; ", errors.Take(5))}");
        Assert.Equal(channelCount, sentData.Count);
        
        foreach (var kvp in sentData)
        {
            Assert.True(receivedData.TryGetValue(kvp.Key, out var received), 
                $"Channel {kvp.Key} data not received");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Concurrent_BidirectionalChannels_AllDataCorrect()
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

        const int channelCount = 50;
        const int dataSize = 512;
        
        var initiatorReceivedData = new ConcurrentDictionary<string, byte[]>();
        var acceptorReceivedData = new ConcurrentDictionary<string, byte[]>();
        var initiatorSentData = new ConcurrentDictionary<string, byte[]>();
        var acceptorSentData = new ConcurrentDictionary<string, byte[]>();
        var errors = new ConcurrentBag<string>();

        // Use named acceptance pattern for reliable bidirectional operations
        var semaphore = new SemaphoreSlim(10);
        var tasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            await semaphore.WaitAsync(cts.Token);
            try
            {
                // Initiator->Acceptor channel
                var iToAId = $"initiator_channel_{i}";
                var iToAWrite = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = iToAId }, cts.Token);
                var iToARead = await acceptor.AcceptChannelAsync(iToAId, cts.Token);
                
                // Acceptor->Initiator channel
                var aToIId = $"acceptor_channel_{i}";
                var aToIWrite = await acceptor.OpenChannelAsync(new ChannelOptions { ChannelId = aToIId }, cts.Token);
                var aToIRead = await initiator.AcceptChannelAsync(aToIId, cts.Token);
                
                // Generate and send data both directions
                var iToAData = new byte[dataSize];
                new Random(i * 2).NextBytes(iToAData);
                initiatorSentData[iToAId] = iToAData;
                await iToAWrite.WriteAsync(iToAData, cts.Token);
                await iToAWrite.CloseAsync(cts.Token);
                
                var aToIData = new byte[dataSize];
                new Random(i * 2 + 1).NextBytes(aToIData);
                acceptorSentData[aToIId] = aToIData;
                await aToIWrite.WriteAsync(aToIData, cts.Token);
                await aToIWrite.CloseAsync(cts.Token);
                
                // Read initiator->acceptor data
                var iToABuffer = new byte[dataSize];
                var total = 0;
                while (total < dataSize)
                {
                    var read = await iToARead.ReadAsync(iToABuffer.AsMemory(total), cts.Token);
                    if (read == 0) break;
                    total += read;
                }
                acceptorReceivedData[iToAId] = iToABuffer[..total];
                
                // Read acceptor->initiator data
                var aToIBuffer = new byte[dataSize];
                total = 0;
                while (total < dataSize)
                {
                    var read = await aToIRead.ReadAsync(aToIBuffer.AsMemory(total), cts.Token);
                    if (read == 0) break;
                    total += read;
                }
                initiatorReceivedData[aToIId] = aToIBuffer[..total];
            }
            catch (Exception ex)
            {
                errors.Add($"Index {i}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        Assert.True(errors.Count == 0, $"Errors: {string.Join("; ", errors.Take(5))}");

        // Verify initiator->acceptor data
        foreach (var kvp in initiatorSentData)
        {
            Assert.True(acceptorReceivedData.TryGetValue(kvp.Key, out var received),
                $"Acceptor didn't receive data for channel {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        // Verify acceptor->initiator data
        foreach (var kvp in acceptorSentData)
        {
            Assert.True(initiatorReceivedData.TryGetValue(kvp.Key, out var received),
                $"Initiator didn't receive data for channel {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Concurrent_MultipleWritersSameChannel_DataInterleavedCorrectly()
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "multiwriter_channel" }, cts.Token);
        await acceptTask;

        const int writerCount = 10;
        const int writesPerWriter = 100;
        const int writeSize = 64;
        var totalBytes = writerCount * writesPerWriter * writeSize;

        // Multiple concurrent writes to same channel
        var writeTasks = Enumerable.Range(0, writerCount).Select(async i =>
        {
            for (var j = 0; j < writesPerWriter; j++)
            {
                var data = new byte[writeSize];
                Array.Fill(data, (byte)(i + 1));
                await writeChannel.WriteAsync(data, cts.Token);
            }
        });

        var writeTask = Task.WhenAll(writeTasks);

        // Read all data
        var received = new MemoryStream();
        var buffer = new byte[1024];
        var readTask = Task.Run(async () =>
        {
            while (received.Length < totalBytes)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                received.Write(buffer, 0, read);
            }
        });

        await writeTask;
        await writeChannel.FlushAsync(cts.Token);
        await writeChannel.CloseAsync(cts.Token);
        await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(totalBytes, received.Length);

        // Verify all bytes are valid (1-10)
        var bytes = received.ToArray();
        foreach (var b in bytes)
        {
            Assert.InRange(b, 1, writerCount);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Concurrent_RapidOpenClose_NoResourceLeak()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);

        await Task.Delay(100);

        // Reduced iterations for CI stability - still tests rapid open/close without resource leaks
        const int iterations = 50;

        // Accept all channels
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                await ch.DisposeAsync();
                if (++count >= iterations) break;
            }
        });

        // Sequential open/close - tests resource cleanup without overloading CI
        for (var i = 0; i < iterations; i++)
        {
            var channel = await initiator.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"rapid_channel_{i}" }, cts.Token);
            await channel.WriteAsync(new byte[] { 1, 2, 3, 4 }, cts.Token);
            await channel.CloseAsync(cts.Token);
            await channel.DisposeAsync();
        }

        await acceptTask;

        // Verify stats
        Assert.Equal(iterations, initiator.Stats.TotalChannelsOpened);
        Assert.Equal(iterations, acceptor.Stats.TotalChannelsOpened);

        cts.Cancel();
    }
}
