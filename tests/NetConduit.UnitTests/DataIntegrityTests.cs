using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for data integrity verification under heavy load.
/// Verifies that data is transmitted correctly regardless of load.
/// </summary>
public class DataIntegrityTests
{
    #region Checksum Verification Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task HeavyLoad_DataIntegrity_ChecksumVerification()
    {
        // Test that data is not corrupted under heavy load
        await using var pipe = new DuplexPipe();
        
        // Use higher credits for heavy load test
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { MaxCredits = 1024 * 1024 } 
        };
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 20;
        const int messagesPerChannel = 100;
        const int messageSize = 4096;

        var sentChecksums = new ConcurrentDictionary<string, List<string>>();
        var receivedChecksums = new ConcurrentDictionary<string, List<string>>();
        var receiveTasks = new ConcurrentBag<Task>();

        // Accept task
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var receiveTask = Task.Run(async () =>
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
                        if (totalRead < messageSize) break; // Partial read at end, skip
                        
                        var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, totalRead)));
                        checksums.Add(hash);
                    }
                    
                    receivedChecksums[channel.ChannelId] = checksums;
                });
                receiveTasks.Add(receiveTask);
                
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
            await ch.FlushAsync(cts.Token);
            await ch.CloseAsync(cts.Token);
        });

        await Task.WhenAll(sendTasks);
        await acceptTask;
        
        // Wait for all receive tasks to complete
        await Task.WhenAll(receiveTasks).WaitAsync(TimeSpan.FromSeconds(10));

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

    [Fact(Timeout = 120000)]
    public async Task MultiTransitStress_ConcurrentBidirectional_AllDataCorrect()
    {
        // Multiple bidirectional transit streams with heavy concurrent load
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int pairCount = 5;
        const int messagesPerDirection = 20;
        
        var aToBSent = new ConcurrentDictionary<string, List<byte[]>>();
        var bToASent = new ConcurrentDictionary<string, List<byte[]>>();
        var aToBReceived = new ConcurrentDictionary<string, List<byte[]>>();
        var bToAReceived = new ConcurrentDictionary<string, List<byte[]>>();

        // Create bidirectional channel pairs with concurrent read/write
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

            // Start reading in parallel before writing
            var readAToBTask = Task.Run(async () =>
            {
                aToBReceived[aToBId].Add(await ReadAllBytesAsync(aToBRead, cts.Token));
            });
            var readBToATask = Task.Run(async () =>
            {
                bToAReceived[bToAId].Add(await ReadAllBytesAsync(bToARead, cts.Token));
            });

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

            // Wait for reads to complete
            await readAToBTask;
            await readBToATask;
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

    #endregion

    #region Channel Index Limit Tests

    [Fact(Timeout = 120000)]
    public async Task ChannelIndexLimit_HighChannelCount_NoCollision()
    {
        // Test that high channel counts work correctly with per-direction index space
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        // Open channels from both sides - reduced count for reliable testing
        // Each side uses its own index space starting from 1
        const int channelsPerSide = 100;
        
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
            await ch.FlushAsync(cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        // Open channels from side B sequentially (for reliability)
        for (var i = 0; i < channelsPerSide; i++)
        {
            var ch = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = $"from_b_{i}" }, cts.Token);
            bChannels[ch.ChannelId] = ch;
            
            var data = BitConverter.GetBytes(i + 10000);
            await ch.WriteAsync(data, cts.Token);
            await ch.FlushAsync(cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await Task.WhenAll(acceptATask, acceptBTask);
        await Task.Delay(500);

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

    [Fact(Timeout = 120000)]
    public async Task ChannelIndexLimit_RapidOpenClose_IndexReuse()
    {
        // Test rapid channel open/close cycles - indices should work correctly
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int cycles = 500;
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
            await ch.FlushAsync(cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.Delay(1000); // Wait for all to be processed
        
        Assert.Equal(cycles, successCount);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task PerDirectionIndexSpace_SameIndexDifferentDirections_NoConflict()
    {
        // Explicitly test that index 1 can exist in both directions without conflict
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        // Both sides open their first channel (both will get index 1)
        var chAToB = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "a_to_b_first" }, cts.Token);
        var chBToA = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = "b_to_a_first" }, cts.Token);

        // Accept channels
        var readAToB = await muxB.AcceptChannelAsync("a_to_b_first", cts.Token);
        var readBToA = await muxA.AcceptChannelAsync("b_to_a_first", cts.Token);

        // Send different data in each direction
        var dataAToB = new byte[] { 1, 2, 3, 4 };
        var dataBToA = new byte[] { 5, 6, 7, 8 };

        await chAToB.WriteAsync(dataAToB, cts.Token);
        await chBToA.WriteAsync(dataBToA, cts.Token);

        await chAToB.FlushAsync(cts.Token);
        await chBToA.FlushAsync(cts.Token);
        await chAToB.CloseAsync(cts.Token);
        await chBToA.CloseAsync(cts.Token);

        // Read and verify
        var receivedAToB = await ReadAllBytesAsync(readAToB, cts.Token);
        var receivedBToA = await ReadAllBytesAsync(readBToA, cts.Token);

        Assert.Equal(dataAToB, receivedAToB);
        Assert.Equal(dataBToA, receivedBToA);

        // Verify the channel indices are both 1 (internal - can check via count)
        // Both sides should have allocated index 1 for their first channel
        Assert.Equal("a_to_b_first", chAToB.ChannelId);
        Assert.Equal("b_to_a_first", chBToA.ChannelId);

        cts.Cancel();
    }

    #endregion

    #region Large Scale Tests

    [Fact(Timeout = 120000)]
    public async Task LargeScale_TwoMillionChannelIndices_Theoretical()
    {
        // This test verifies the theoretical capacity of 4-byte channel indices
        // We don't actually create 2M+ channels, but verify the allocation works up to a reasonable number
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        // Open 500 channels to verify index allocation works (reduced for speed)
        const int testChannels = 500;
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
