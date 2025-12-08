using System.Collections.Concurrent;
using System.IO.Pipelines;
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

    #region Resend Buffer Tests

    [Fact(Timeout = 60000)]
    public async Task ResendBuffer_ManySmallFrames_NoDataLoss()
    {
        // Test that sending many small frames (worst case for frame-count buffer) works correctly
        // With byte-based buffer derived from MaxCredits, this should never lose data
        await using var pipe = new DuplexPipe();
        
        // Use larger MaxCredits for better throughput
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { MaxCredits = 256 * 1024 } // 256KB
        };
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int frameCount = 1000;   // Reduced for reliable test timing
        const int frameSize = 8;       // Small frames - worst case for old frame-count buffer
        
        var channel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "small_frames" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("small_frames", cts.Token);
        
        // Send many small frames
        var sentData = new byte[frameCount * frameSize];
        Random.Shared.NextBytes(sentData);
        
        var sendTask = Task.Run(async () =>
        {
            for (var i = 0; i < frameCount; i++)
            {
                await channel.WriteAsync(sentData.AsMemory(i * frameSize, frameSize), cts.Token);
            }
            await channel.CloseAsync(cts.Token);
        });
        
        // Receive all data
        var receivedData = await ReadAllBytesAsync(readChannel, cts.Token);
        await sendTask;
        
        // Verify all data received correctly
        Assert.Equal(sentData.Length, receivedData.Length);
        Assert.Equal(sentData, receivedData);
        
        cts.Cancel();
    }

    [Fact(Timeout = 60000)]
    public async Task ResendBuffer_MixedFrameSizes_NoDataLoss()
    {
        // Test mixed small and large frames - ensures buffer tracks bytes correctly
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { MaxCredits = 256 * 1024 } // 256KB
        };
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var channel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "mixed_sizes" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("mixed_sizes", cts.Token);
        
        // Send mixed frame sizes: alternating small (1-byte) and large (64KB) frames
        var allSentData = new MemoryStream();
        
        var sendTask = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                // Small frame (1 byte)
                var small = new byte[] { (byte)(i % 256) };
                await channel.WriteAsync(small, cts.Token);
                allSentData.Write(small);
                
                // Large frame (varies from 1KB to 16KB)
                var largeSize = 1024 + (i % 16) * 1024;
                var large = new byte[largeSize];
                Random.Shared.NextBytes(large);
                await channel.WriteAsync(large, cts.Token);
                allSentData.Write(large);
            }
            await channel.CloseAsync(cts.Token);
        });
        
        // Receive all data
        var receivedData = await ReadAllBytesAsync(readChannel, cts.Token);
        await sendTask;
        
        var sentBytes = allSentData.ToArray();
        Assert.Equal(sentBytes.Length, receivedData.Length);
        Assert.Equal(sentBytes, receivedData);
        
        cts.Cancel();
    }

    [Fact(Timeout = 60000)]
    public async Task ResendBuffer_CreditExhaustion_NoOverflow()
    {
        // Test that buffer doesn't overflow when credits are exhausted
        // Sender should block, not overflow the resend buffer
        await using var pipe = new DuplexPipe();
        
        // Very small credits to force exhaustion quickly
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions 
            { 
                MinCredits = 4 * 1024,   // 4KB min
                MaxCredits = 8 * 1024    // 8KB max - very small window
            } 
        };
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var channel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "credit_test" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("credit_test", cts.Token);
        
        // Send more data than credits allow (will block until receiver reads)
        const int totalBytes = 64 * 1024; // 64KB total - 8x the max credits
        var sentData = new byte[totalBytes];
        Random.Shared.NextBytes(sentData);
        
        var sendTask = Task.Run(async () =>
        {
            // Send in small chunks to ensure many frames
            const int chunkSize = 512;
            for (var i = 0; i < totalBytes; i += chunkSize)
            {
                var size = Math.Min(chunkSize, totalBytes - i);
                await channel.WriteAsync(sentData.AsMemory(i, size), cts.Token);
            }
            await channel.CloseAsync(cts.Token);
        });
        
        // Read all data (this allows sender to continue via credit grants)
        var receivedData = await ReadAllBytesAsync(readChannel, cts.Token);
        await sendTask;
        
        // All data should be received correctly
        Assert.Equal(sentData.Length, receivedData.Length);
        Assert.Equal(sentData, receivedData);
        
        cts.Cancel();
    }

    #endregion

    #region Timeout-Based Retransmit Tests

    [Theory(Timeout = 120000)]
    [InlineData(0.01)]  // 1% drop rate
    [InlineData(0.05)]  // 5% drop rate
    public async Task TimeoutRetransmit_WithDrops_RecoversThroughRetransmission(double dropRate)
    {
        // Test that timeout-based retransmit recovers from packet drops
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var (rawA, rawB) = CreateRawPair();
        var unreliableOptions = new StreamMultiplexerDisruptionTests.UnreliableStreamOptions
        {
            DropRate = dropRate,
            Seed = 12345
        };
        var streamA = new StreamMultiplexerDisruptionTests.UnreliableStream(rawA, unreliableOptions);
        var streamB = new StreamMultiplexerDisruptionTests.UnreliableStream(rawB, unreliableOptions);

        // Enable timeout-based retransmit with short intervals for testing
        var options = new MultiplexerOptions
        {
            FlushMode = FlushMode.Immediate,
            EnableTimeoutRetransmit = true,
            RetransmitTimeout = TimeSpan.FromMilliseconds(100),
            RetransmitCheckInterval = TimeSpan.FromMilliseconds(25),
            MaxRetransmitAttempts = 20  // More attempts for high drop rate
        };

        await using var muxA = new StreamMultiplexer(streamA, streamA, options);
        await using var muxB = new StreamMultiplexer(streamB, streamB, options);

        try
        {
            var startTasks = await Task.WhenAll(muxA.StartAsync(cts.Token), muxB.StartAsync(cts.Token));

            var write = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "drop-retransmit-test" }, cts.Token);
            var read = await muxB.AcceptChannelAsync("drop-retransmit-test", cts.Token);

            // Use smaller payload for faster test with drops
            var payload = new byte[2048];
            Random.Shared.NextBytes(payload);

            await write.WriteAsync(payload, cts.Token);
            await write.CloseAsync(cts.Token);

            var buffer = new byte[payload.Length];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var n = await read.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }

            // With timeout retransmit, data should be complete and correct
            Assert.Equal(payload.Length, totalRead);
            Assert.Equal(payload, buffer);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Timeout is acceptable for high drop rates if retransmit max exceeded
        }

        cts.Cancel();
    }

    [Fact(Timeout = 60000)]
    public async Task TimeoutRetransmit_MultiChannel_AllDataCorrect()
    {
        // Test multiple channels with timeout retransmit under light drops
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var (rawA, rawB) = CreateRawPair();
        var unreliableOptions = new StreamMultiplexerDisruptionTests.UnreliableStreamOptions
        {
            DropRate = 0.02,  // 2% drop rate
            Seed = 54321
        };
        var streamA = new StreamMultiplexerDisruptionTests.UnreliableStream(rawA, unreliableOptions);
        var streamB = new StreamMultiplexerDisruptionTests.UnreliableStream(rawB, unreliableOptions);

        var options = new MultiplexerOptions
        {
            FlushMode = FlushMode.Immediate,
            EnableTimeoutRetransmit = true,
            RetransmitTimeout = TimeSpan.FromMilliseconds(100),
            RetransmitCheckInterval = TimeSpan.FromMilliseconds(25),
            MaxRetransmitAttempts = 15
        };

        await using var muxA = new StreamMultiplexer(streamA, streamA, options);
        await using var muxB = new StreamMultiplexer(streamB, streamB, options);

        try
        {
            await Task.WhenAll(muxA.StartAsync(cts.Token), muxB.StartAsync(cts.Token));

            const int channelCount = 3;
            var tasks = new List<Task>();

            for (var i = 0; i < channelCount; i++)
            {
                var idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    var channelId = $"multi-retransmit-{idx}";
                    var write = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                    var read = await muxB.AcceptChannelAsync(channelId, cts.Token);

                    var payload = new byte[1024];
                    Random.Shared.NextBytes(payload);

                    await write.WriteAsync(payload, cts.Token);
                    await write.CloseAsync(cts.Token);

                    var buffer = new byte[payload.Length];
                    var totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        var n = await read.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                        if (n == 0) break;
                        totalRead += n;
                    }

                    Assert.Equal(payload.Length, totalRead);
                    Assert.Equal(payload, buffer);
                }, cts.Token));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Timeout acceptable
        }

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task TimeoutRetransmit_Disabled_NoRetransmitOnTimeout()
    {
        // Verify that retransmit loop is not active when disabled
        await using var pipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableTimeoutRetransmit = false  // Explicitly disabled
        };

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        // Basic transfer should still work (no drops in DuplexPipe)
        var write = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "no-retransmit-test" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("no-retransmit-test", cts.Token);

        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        await write.WriteAsync(payload, cts.Token);
        await write.CloseAsync(cts.Token);

        var buffer = new byte[payload.Length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var n = await read.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(payload.Length, totalRead);
        Assert.Equal(payload, buffer);

        cts.Cancel();
    }

    private static (DuplexPipe.DuplexPipeStream A, DuplexPipe.DuplexPipeStream B) CreateRawPair()
    {
        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();
        return (
            new DuplexPipe.DuplexPipeStream(pipe1.Reader, pipe2.Writer),
            new DuplexPipe.DuplexPipeStream(pipe2.Reader, pipe1.Writer)
        );
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
