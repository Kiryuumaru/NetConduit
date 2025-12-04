using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NetConduit.UnitTests;

/// <summary>
/// Stress tests for data integrity verification under heavy load.
/// These tests use larger scales and longer timeouts.
/// Run with: dotnet test --filter "Category=Stress"
/// </summary>
[Trait("Category", "Stress")]
public class DataIntegrityStressTests
{
    #region Large Scale Stress Tests

    [Fact(Timeout = 300000)]
    public async Task Stress_MultiTransitStress_ConcurrentBidirectional_AllDataCorrect()
    {
        // Multiple bidirectional transit streams with heavy concurrent load
        // Original scale: 100 pairs, 50 messages per direction
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int pairCount = 100;
        const int messagesPerDirection = 50;
        
        var aToBSent = new ConcurrentDictionary<string, List<byte[]>>();
        var bToASent = new ConcurrentDictionary<string, List<byte[]>>();
        var aToBReceived = new ConcurrentDictionary<string, List<byte[]>>();
        var bToAReceived = new ConcurrentDictionary<string, List<byte[]>>();

        // Create bidirectional channel pairs sequentially to avoid race conditions
        for (var i = 0; i < pairCount; i++)
        {
            var aToBId = $"a_to_b_{i}";
            var bToAId = $"b_to_a_{i}";
            
            // A opens channel to B
            var aToBWrite = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = aToBId }, cts.Token);
            
            // B opens channel to A
            var bToAWrite = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = bToAId }, cts.Token);
            
            // Accept channels
            var aToBRead = await muxB.AcceptChannelAsync(aToBId, cts.Token);
            var bToARead = await muxA.AcceptChannelAsync(bToAId, cts.Token);

            // Initialize lists
            aToBSent[aToBId] = new List<byte[]>();
            bToASent[bToAId] = new List<byte[]>();
            aToBReceived[aToBId] = new List<byte[]>();
            bToAReceived[bToAId] = new List<byte[]>();

            // Send data in both directions
            for (var j = 0; j < messagesPerDirection; j++)
            {
                var dataAToB = new byte[Random.Shared.Next(100, 500)];
                Random.Shared.NextBytes(dataAToB);
                aToBSent[aToBId].Add(dataAToB);
                await aToBWrite.WriteAsync(dataAToB, cts.Token);

                var dataBToA = new byte[Random.Shared.Next(100, 500)];
                Random.Shared.NextBytes(dataBToA);
                bToASent[bToAId].Add(dataBToA);
                await bToAWrite.WriteAsync(dataBToA, cts.Token);
            }

            await aToBWrite.CloseAsync(cts.Token);
            await bToAWrite.CloseAsync(cts.Token);

            // Read all data
            aToBReceived[aToBId].Add(await ReadAllBytesAsync(aToBRead, cts.Token));
            bToAReceived[bToAId].Add(await ReadAllBytesAsync(bToARead, cts.Token));
        }

        // Verify data integrity - concatenated bytes should match
        foreach (var kvp in aToBSent)
        {
            var sent = ConcatBytes(kvp.Value);
            var received = ConcatBytes(aToBReceived[kvp.Key]);
            Assert.Equal(sent.Length, received.Length);
            Assert.True(sent.SequenceEqual(received), $"Data mismatch on channel {kvp.Key}");
        }

        foreach (var kvp in bToASent)
        {
            var sent = ConcatBytes(kvp.Value);
            var received = ConcatBytes(bToAReceived[kvp.Key]);
            Assert.Equal(sent.Length, received.Length);
            Assert.True(sent.SequenceEqual(received), $"Data mismatch on channel {kvp.Key}");
        }

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    public async Task Stress_ChannelIndexLimit_HighChannelCount_NoCollision()
    {
        // Test that high channel counts work correctly with per-direction index space
        // Original scale: 1000 channels per side
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        // Open 1000 channels from each side
        const int channelsPerSide = 1000;
        
        var aChannels = new ConcurrentDictionary<string, WriteChannel>();
        var bChannels = new ConcurrentDictionary<string, WriteChannel>();
        var aReceived = new ConcurrentDictionary<string, byte[]>();
        var bReceived = new ConcurrentDictionary<string, byte[]>();

        // Accept tasks
        var acceptATask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxA.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        aReceived[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= channelsPerSide) break;
            }
        });

        var acceptBTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        bReceived[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= channelsPerSide) break;
            }
        });

        // Open channels from side A sequentially (for reliability)
        for (var i = 0; i < channelsPerSide; i++)
        {
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = $"from_a_{i}" }, cts.Token);
            aChannels[ch.ChannelId] = ch;
            
            var data = BitConverter.GetBytes(i);
            await ch.WriteAsync(data, cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        // Open channels from side B sequentially (for reliability)
        for (var i = 0; i < channelsPerSide; i++)
        {
            var ch = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = $"from_b_{i}" }, cts.Token);
            bChannels[ch.ChannelId] = ch;
            
            var data = BitConverter.GetBytes(i + 10000);
            await ch.WriteAsync(data, cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await Task.WhenAll(acceptATask, acceptBTask);
        await Task.Delay(2000); // Wait for all to be processed

        // Verify all channels were created and data received
        Assert.Equal(channelsPerSide, aChannels.Count);
        Assert.Equal(channelsPerSide, bChannels.Count);
        Assert.Equal(channelsPerSide, aReceived.Count);
        Assert.Equal(channelsPerSide, bReceived.Count);

        // Verify data integrity
        for (var i = 0; i < channelsPerSide; i++)
        {
            Assert.True(bReceived.TryGetValue($"from_a_{i}", out var dataFromA));
            Assert.Equal(i, BitConverter.ToInt32(dataFromA));

            Assert.True(aReceived.TryGetValue($"from_b_{i}", out var dataFromB));
            Assert.Equal(i + 10000, BitConverter.ToInt32(dataFromB));
        }

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    public async Task Stress_ChannelIndexLimit_RapidOpenClose_IndexReuse()
    {
        // Test rapid channel open/close cycles - indices should work correctly
        // Original scale: 5000 cycles
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int cycles = 5000;
        var successCount = 0;

        // Accept task - continuously accept and read
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var data = await ReadAllBytesAsync(channel, cts.Token);
                        if (data.Length == 4)
                        {
                            Interlocked.Increment(ref successCount);
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= cycles) break;
            }
        });

        // Rapidly open and close channels
        for (var i = 0; i < cycles; i++)
        {
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = $"cycle_{i}" }, cts.Token);
            await ch.WriteAsync(BitConverter.GetBytes(i), cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.Delay(5000); // Wait for all to be processed
        
        Assert.Equal(cycles, successCount);

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    public async Task Stress_LargeScale_TenThousandChannels()
    {
        // This test verifies we can handle 10,000 channels
        // Original scale: 10,000 channels
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int testChannels = 10000;
        var channels = new List<WriteChannel>();
        
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                // Just accept, don't read
                if (++count >= testChannels) break;
            }
        });

        for (var i = 0; i < testChannels; i++)
        {
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = $"scale_test_{i}" }, cts.Token);
            channels.Add(ch);
        }

        await acceptTask;

        // Verify all channels were created with unique IDs
        var uniqueIds = channels.Select(c => c.ChannelId).Distinct().Count();
        Assert.Equal(testChannels, uniqueIds);

        // Clean up
        foreach (var ch in channels)
        {
            await ch.CloseAsync(cts.Token);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    public async Task Stress_HeavyLoad_DataIntegrity_ChecksumVerification()
    {
        // Heavy load test with SHA256 checksum verification
        // Scale: 50 channels, 200 messages per channel, 8KB messages
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 50;
        const int messagesPerChannel = 200;
        const int messageSize = 8192;

        var sentChecksums = new ConcurrentDictionary<string, List<string>>();
        var receivedChecksums = new ConcurrentDictionary<string, List<string>>();

        // Accept task
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    var checksums = new List<string>();
                    var buffer = new byte[messageSize];
                    
                    while (true)
                    {
                        var totalRead = 0;
                        while (totalRead < messageSize)
                        {
                            var read = await channel.ReadAsync(buffer.AsMemory(totalRead, messageSize - totalRead), cts.Token);
                            if (read == 0) break;
                            totalRead += read;
                        }
                        if (totalRead == 0) break;
                        
                        var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, totalRead)));
                        checksums.Add(hash);
                    }
                    
                    receivedChecksums[channel.ChannelId] = checksums;
                });
                
                if (++count >= channelCount) break;
            }
        });

        // Send task
        var sendTasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            var channelId = $"integrity_ch_{i}";
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
            
            var checksums = new List<string>();
            for (var j = 0; j < messagesPerChannel; j++)
            {
                var data = new byte[messageSize];
                Random.Shared.NextBytes(data);
                
                var hash = Convert.ToHexString(SHA256.HashData(data));
                checksums.Add(hash);
                
                await ch.WriteAsync(data, cts.Token);
            }
            
            sentChecksums[channelId] = checksums;
            await ch.CloseAsync(cts.Token);
        });

        await Task.WhenAll(sendTasks);
        await acceptTask;
        await Task.Delay(2000);

        // Verify checksums match
        Assert.Equal(channelCount, sentChecksums.Count);
        Assert.Equal(channelCount, receivedChecksums.Count);

        foreach (var kvp in sentChecksums)
        {
            Assert.True(receivedChecksums.TryGetValue(kvp.Key, out var received), 
                $"Channel {kvp.Key} not received");
            Assert.Equal(kvp.Value.Count, received.Count);
            
            for (var i = 0; i < kvp.Value.Count; i++)
            {
                Assert.Equal(kvp.Value[i], received[i]);
            }
        }

        cts.Cancel();
    }

    #endregion

    #region Helpers

    private static async Task<byte[]> ReadAllBytesAsync(ReadChannel channel, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await channel.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static byte[] ConcatBytes(List<byte[]> arrays)
    {
        var totalLength = arrays.Sum(a => a.Length);
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }
        return result;
    }

    #endregion
}
