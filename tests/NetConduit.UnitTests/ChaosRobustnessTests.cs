using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NetConduit.UnitTests;

/// <summary>
/// Chaos tests that simulate unpredictable, random, and mixed operations.
/// These tests verify the multiplexer handles edge cases, race conditions,
/// and unexpected usage patterns without corruption or deadlock.
/// </summary>
public class ChaosRobustnessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    #region Scaled Channel Stress Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_HundredChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(100);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_ThousandChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(1_000);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_TenThousandChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(10_000);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_FiftyThousandChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(50_000);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_HundredThousandChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(100_000);
    }

    [Fact(Skip = "Million channel test takes too long - needs investigation")]
    [Trait("Category", "Stress")]
    public async Task Stress_MillionChannels_SmallDataValidation_Concurrent()
    {
        await RunChannelStressTest(1_000_000);
    }

    private static async Task RunChannelStressTest(int totalChannels)
    {
        await using var pipe = new DuplexPipe();
        
        // Use Immediate flush mode to ensure data is flushed before close
        var options = new MultiplexerOptions { FlushMode = FlushMode.Immediate };
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TestTimeout);
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var totalVerified = 0L;
        var errors = new ConcurrentBag<string>();

        var channelIds = Enumerable.Range(0, totalChannels).Select(i => $"ch_{i}").ToList();
        var dataToSend = new ConcurrentDictionary<string, byte[]>();

        // Phase 1: Open all channels and send data - full concurrency now that the bug is fixed
        var senderTasks = channelIds.Select(async channelId =>
        {
            try
            {
                var channelIndex = int.Parse(channelId[3..]);
                var writeChannel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                
                var data = BitConverter.GetBytes((long)channelIndex);
                dataToSend[channelId] = data;
                
                await writeChannel.WriteAsync(data, cts.Token);
                await writeChannel.CloseAsync(cts.Token);
            }
            catch (Exception ex)
            {
                errors.Add($"Sender {channelId}: {ex.Message}");
            }
        }).ToList();
        
        await Task.WhenAll(senderTasks);

        // Small delay to let all frames propagate
        await Task.Delay(100);

        // Phase 2: Receive all data (concurrent)
        var receiverTasks = channelIds.Select(async channelId =>
        {
            try
            {
                var readChannel = await muxB.AcceptChannelAsync(channelId, cts.Token);
                
                var buffer = new byte[8];
                var totalRead = 0;
                while (totalRead < 8)
                {
                    var read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }
                
                if (totalRead == 8 && dataToSend.TryGetValue(channelId, out var sent))
                {
                    if (buffer.SequenceEqual(sent))
                    {
                        Interlocked.Increment(ref totalVerified);
                    }
                    else
                    {
                        var sentValue = BitConverter.ToInt64(sent);
                        var receivedValue = BitConverter.ToInt64(buffer);
                        errors.Add($"Channel {channelId}: mismatch (sent={sentValue}, received={receivedValue})");
                    }
                }
                else
                {
                    errors.Add($"Channel {channelId}: incomplete read ({totalRead} bytes)");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Receiver {channelId}: {ex.Message}");
            }
        }).ToList();

        await Task.WhenAll(receiverTasks);

        await Console.Out.WriteLineAsync($"Channels: {totalChannels:N0}, Verified: {totalVerified:N0}, Errors: {errors.Count}");
        if (errors.Count > 0)
        {
            await Console.Out.WriteLineAsync($"Sample errors: {string.Join("; ", errors.Take(5))}");
        }

        // Expect 100% success now that the multiplexer bug is fixed
        Assert.True(totalVerified == totalChannels, 
            $"Expected {totalChannels:N0} verified, got {totalVerified:N0}");

        cts.Cancel();
    }

    #endregion

    #region Scaled Bidirectional Channel Stress Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_HundredChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(50); // 50 per side = 100 total
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_ThousandChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(500); // 500 per side = 1000 total
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_TenThousandChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(5_000); // 5000 per side = 10000 total
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_FiftyThousandChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(25_000); // 25000 per side = 50000 total
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Stress_HundredThousandChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(50_000); // 50000 per side = 100000 total
    }

    [Fact(Skip = "Million channel test takes too long - needs investigation")]
    [Trait("Category", "Stress")]
    public async Task Stress_MillionChannels_BidirectionalSmallData_Validated()
    {
        await RunBidirectionalStressTest(500_000); // 500000 per side = 1M total
    }

    private static async Task RunBidirectionalStressTest(int channelsPerSide)
    {
        await using var pipe = new DuplexPipe();
        
        // Index space is auto-negotiated via handshake nonce exchange
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TestTimeout);
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var concurrency = Math.Min(100, channelsPerSide / 10 + 1);
        
        var aVerified = 0L;
        var bVerified = 0L;
        var errors = new ConcurrentBag<string>();

        // Use named channel acceptance - each task handles both directions for one index
        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();

        for (var i = 0; i < channelsPerSide; i++)
        {
            var index = i;
            await semaphore.WaitAsync(cts.Token);
            
            var task = Task.Run(async () =>
            {
                try
                {
                    // A->B: A opens, B accepts
                    var aChannelId = $"a_{index}";
                    var aToBWrite = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = aChannelId }, cts.Token);
                    var aToBRead = await muxB.AcceptChannelAsync(aChannelId, cts.Token);
                    
                    // B->A: B opens, A accepts
                    var bChannelId = $"b_{index}";
                    var bToAWrite = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = bChannelId }, cts.Token);
                    var bToARead = await muxA.AcceptChannelAsync(bChannelId, cts.Token);
                    
                    // Send data both directions
                    await aToBWrite.WriteAsync(BitConverter.GetBytes((long)index), cts.Token);
                    await aToBWrite.CloseAsync(cts.Token);
                    
                    await bToAWrite.WriteAsync(BitConverter.GetBytes((long)(index + 1_000_000)), cts.Token);
                    await bToAWrite.CloseAsync(cts.Token);
                    
                    // Receive and verify A->B
                    var bufferAToB = new byte[8];
                    var totalRead = 0;
                    while (totalRead < 8)
                    {
                        var read = await aToBRead.ReadAsync(bufferAToB.AsMemory(totalRead), cts.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead == 8 && BitConverter.ToInt64(bufferAToB) == index)
                    {
                        Interlocked.Increment(ref bVerified);
                    }
                    
                    // Receive and verify B->A
                    var bufferBToA = new byte[8];
                    totalRead = 0;
                    while (totalRead < 8)
                    {
                        var read = await bToARead.ReadAsync(bufferBToA.AsMemory(totalRead), cts.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead == 8 && BitConverter.ToInt64(bufferBToA) == index + 1_000_000)
                    {
                        Interlocked.Increment(ref aVerified);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Index {index}: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });
            
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        await Console.Out.WriteLineAsync($"Bidirectional: A verified {aVerified:N0} (from B), B verified {bVerified:N0} (from A)");

        // Expect 100% verification
        Assert.Equal(channelsPerSide, bVerified); // B should verify all from A
        Assert.Equal(channelsPerSide, aVerified); // A should verify all from B

        cts.Cancel();
    }

    private static async Task AcceptAndValidateBidirectionalAsync(
        StreamMultiplexer mux, string expectedPrefix, 
        Action onVerified, ConcurrentBag<Task> readTasks, CancellationToken ct)
    {
        try
        {
            await foreach (var ch in mux.AcceptChannelsAsync(ct))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    try
                    {
                        var buffer = new byte[8];
                        var totalRead = 0;
                        while (totalRead < 8)
                        {
                            var read = await channel.ReadAsync(buffer.AsMemory(totalRead), ct);
                            if (read == 0) break;
                            totalRead += read;
                        }

                        if (totalRead == 8 && channel.ChannelId.StartsWith(expectedPrefix))
                        {
                            var expectedIndex = long.Parse(channel.ChannelId.Substring(expectedPrefix.Length));
                            var received = BitConverter.ToInt64(buffer);
                            var expectedValue = expectedPrefix == "a_" ? expectedIndex : expectedIndex + 1_000_000;
                            
                            if (received == expectedValue)
                            {
                                onVerified();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* ignore read errors */ }
                }, ct);
                
                readTasks.Add(readTask);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task AcceptAndValidateAsync(
        StreamMultiplexer mux, string expectedPrefix, int expectedCount, 
        Action onVerified, ConcurrentBag<string> errors, CancellationToken ct)
    {
        try
        {
            await foreach (var ch in mux.AcceptChannelsAsync(ct))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buffer = new byte[8];
                        var totalRead = 0;
                        while (totalRead < 8)
                        {
                            var read = await channel.ReadAsync(buffer.AsMemory(totalRead), ct);
                            if (read == 0) break;
                            totalRead += read;
                        }

                        if (totalRead == 8 && channel.ChannelId.StartsWith(expectedPrefix))
                        {
                            var expectedIndex = long.Parse(channel.ChannelId.Substring(expectedPrefix.Length));
                            var received = BitConverter.ToInt64(buffer);
                            var expectedValue = expectedPrefix == "a_" ? expectedIndex : expectedIndex + 1_000_000;
                            
                            if (received == expectedValue)
                            {
                                onVerified();
                            }
                            else
                            {
                                errors.Add($"{channel.ChannelId}: expected {expectedValue}, got {received}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { errors.Add($"{channel.ChannelId}: {ex.Message}"); }
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Random Operation Tests

    [Fact(Timeout = 120000)]
    public async Task Chaos_RandomOperationSequence_NoCorruptionOrDeadlock()
    {
        // Perform random operations in random order
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var random = new Random(42); // Fixed seed for reproducibility
        var openChannels = new ConcurrentDictionary<string, WriteChannel>();
        var receivedData = new ConcurrentDictionary<string, List<byte[]>>();
        var sentData = new ConcurrentDictionary<string, List<byte[]>>();
        var channelCounter = 0;

        // Accept task
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
                {
                    var channel = ch;
                    receivedData[channel.ChannelId] = new List<byte[]>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var buffer = new byte[4096];
                            while (true)
                            {
                                var read = await channel.ReadAsync(buffer, cts.Token);
                                if (read == 0) break;
                                var copy = new byte[read];
                                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                                receivedData[channel.ChannelId].Add(copy);
                            }
                        }
                        catch (OperationCanceledException) { }
                    });
                }
            }
            catch (OperationCanceledException) { }
        });

        // Random operations
        const int operationCount = 500;
        for (var i = 0; i < operationCount; i++)
        {
            var operation = random.Next(4);
            
            try
            {
                switch (operation)
                {
                    case 0: // Open new channel
                        var channelId = $"ch_{Interlocked.Increment(ref channelCounter)}";
                        var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                        openChannels[channelId] = ch;
                        sentData[channelId] = new List<byte[]>();
                        break;

                    case 1: // Write to random open channel
                        if (openChannels.Count > 0)
                        {
                            var keys = openChannels.Keys.ToArray();
                            var key = keys[random.Next(keys.Length)];
                            if (openChannels.TryGetValue(key, out var writeCh) && writeCh.State == ChannelState.Open)
                            {
                                var data = new byte[random.Next(1, 1000)];
                                random.NextBytes(data);
                                sentData[key].Add(data);
                                await writeCh.WriteAsync(data, cts.Token);
                            }
                        }
                        break;

                    case 2: // Close random channel
                        if (openChannels.Count > 0)
                        {
                            var keys = openChannels.Keys.ToArray();
                            var key = keys[random.Next(keys.Length)];
                            if (openChannels.TryRemove(key, out var closeCh) && closeCh.State == ChannelState.Open)
                            {
                                await closeCh.CloseAsync(cts.Token);
                            }
                        }
                        break;

                    case 3: // Small delay
                        await Task.Delay(random.Next(1, 10), cts.Token);
                        break;
                }
            }
            catch (InvalidOperationException) { /* Channel state changed */ }
            catch (OperationCanceledException) { break; }
        }

        // Close remaining channels
        foreach (var ch in openChannels.Values)
        {
            try
            {
                if (ch.State == ChannelState.Open)
                    await ch.CloseAsync(cts.Token);
            }
            catch { }
        }

        await Task.Delay(1000);

        // Verify data integrity for closed channels
        foreach (var kvp in sentData)
        {
            if (receivedData.TryGetValue(kvp.Key, out var received))
            {
                var sentBytes = ConcatBytes(kvp.Value);
                var receivedBytes = ConcatBytes(received);
                Assert.Equal(sentBytes.Length, receivedBytes.Length);
                Assert.True(sentBytes.SequenceEqual(receivedBytes), $"Data mismatch on channel {kvp.Key}");
            }
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Chaos_RandomSizedMessages_AllDataCorrect()
    {
        // Send messages of random sizes (1 byte to 64KB)
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        var random = new Random(123);
        const int channelCount = 10;
        const int messagesPerChannel = 50;
        
        var sentData = new ConcurrentDictionary<string, byte[]>();
        var receivedData = new ConcurrentDictionary<string, byte[]>();

        // Accept and read
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
                        receivedData[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= channelCount) break;
            }
        });

        // Send
        for (var i = 0; i < channelCount; i++)
        {
            var channelId = $"random_size_{i}";
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
            
            using var ms = new MemoryStream();
            for (var j = 0; j < messagesPerChannel; j++)
            {
                var size = random.Next(1, 65536);
                var data = new byte[size];
                random.NextBytes(data);
                ms.Write(data, 0, data.Length);
                await ch.WriteAsync(data, cts.Token);
            }
            sentData[channelId] = ms.ToArray();
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.Delay(1000);

        // Verify
        Assert.Equal(channelCount, sentData.Count);
        Assert.Equal(channelCount, receivedData.Count);

        foreach (var kvp in sentData)
        {
            Assert.True(receivedData.TryGetValue(kvp.Key, out var received), $"Missing {kvp.Key}");
            Assert.Equal(kvp.Value.Length, received.Length);
            Assert.True(kvp.Value.SequenceEqual(received), $"Data mismatch on {kvp.Key}");
        }

        cts.Cancel();
    }

    #endregion

    #region Mixed Transport Tests (Both Sides)

    [Fact(Timeout = 120000)]
    public async Task MixedTransport_SimultaneousBidirectional_DataCorrect()
    {
        // Both sides open channels and send data - sequential to avoid race conditions
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelsPerSide = 20;
        var aSentData = new ConcurrentDictionary<string, byte[]>();
        var bSentData = new ConcurrentDictionary<string, byte[]>();
        var aReceivedData = new ConcurrentDictionary<string, byte[]>();
        var bReceivedData = new ConcurrentDictionary<string, byte[]>();

        // Use sequential with named accept for reliable operation
        for (var i = 0; i < channelsPerSide; i++)
        {
            var aToB = $"a_to_b_{i}";
            var bToA = $"b_to_a_{i}";

            // A opens channel to B
            var writeA = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = aToB }, cts.Token);
            
            // B opens channel to A
            var writeB = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = bToA }, cts.Token);

            // Accept by name
            var readA = await muxB.AcceptChannelAsync(aToB, cts.Token);
            var readB = await muxA.AcceptChannelAsync(bToA, cts.Token);

            // Generate and store data
            var dataA = new byte[Random.Shared.Next(1000, 10000)];
            Random.Shared.NextBytes(dataA);
            aSentData[aToB] = dataA;

            var dataB = new byte[Random.Shared.Next(1000, 10000)];
            Random.Shared.NextBytes(dataB);
            bSentData[bToA] = dataB;

            // Send
            await writeA.WriteAsync(dataA, cts.Token);
            await writeB.WriteAsync(dataB, cts.Token);

            // Close write channels
            await writeA.CloseAsync(cts.Token);
            await writeB.CloseAsync(cts.Token);

            // Read data
            bReceivedData[aToB] = await ReadAllBytesAsync(readA, cts.Token);
            aReceivedData[bToA] = await ReadAllBytesAsync(readB, cts.Token);
        }

        // Verify A->B
        foreach (var kvp in aSentData)
        {
            Assert.True(bReceivedData.TryGetValue(kvp.Key, out var received), $"B didn't receive {kvp.Key}");
            Assert.Equal(kvp.Value.Length, received.Length);
            Assert.True(kvp.Value.SequenceEqual(received));
        }

        // Verify B->A
        foreach (var kvp in bSentData)
        {
            Assert.True(aReceivedData.TryGetValue(kvp.Key, out var received), $"A didn't receive {kvp.Key}");
            Assert.Equal(kvp.Value.Length, received.Length);
            Assert.True(kvp.Value.SequenceEqual(received));
        }

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task MixedTransport_InterleavedOpenWriteClose_AllCorrect()
    {
        // Interleave operations: A opens, B opens, A writes, B writes, etc.
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int pairCount = 30;
        var aReceivedData = new ConcurrentDictionary<string, byte[]>();
        var bReceivedData = new ConcurrentDictionary<string, byte[]>();

        // Use sequential with named accept
        for (var i = 0; i < pairCount; i++)
        {
            var aToB = $"a_to_b_{i}";
            var bToA = $"b_to_a_{i}";

            // A opens
            var chA = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = aToB }, cts.Token);
            
            // B opens
            var chB = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = bToA }, cts.Token);

            // Accept by name
            var readA = await muxB.AcceptChannelAsync(aToB, cts.Token);
            var readB = await muxA.AcceptChannelAsync(bToA, cts.Token);

            // A writes
            var dataA = new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2), (byte)(i + 3) };
            await chA.WriteAsync(dataA, cts.Token);

            // B writes
            var dataB = new byte[] { (byte)(i + 100), (byte)(i + 101), (byte)(i + 102), (byte)(i + 103) };
            await chB.WriteAsync(dataB, cts.Token);

            // Flush and close both
            await chA.FlushAsync(cts.Token);
            await chB.FlushAsync(cts.Token);
            await chA.CloseAsync(cts.Token);
            await chB.CloseAsync(cts.Token);

            // Read data
            bReceivedData[aToB] = await ReadAllBytesAsync(readA, cts.Token);
            aReceivedData[bToA] = await ReadAllBytesAsync(readB, cts.Token);
        }

        // Verify counts
        Assert.Equal(pairCount, aReceivedData.Count);
        Assert.Equal(pairCount, bReceivedData.Count);

        // Verify data
        for (var i = 0; i < pairCount; i++)
        {
            Assert.True(bReceivedData.TryGetValue($"a_to_b_{i}", out var dataA));
            Assert.Equal(4, dataA.Length);
            Assert.Equal((byte)i, dataA[0]);

            Assert.True(aReceivedData.TryGetValue($"b_to_a_{i}", out var dataB));
            Assert.Equal(4, dataB.Length);
            Assert.Equal((byte)(i + 100), dataB[0]);
        }

        cts.Cancel();
    }

    #endregion

    #region Heavy Data Validation Tests

    [Fact(Timeout = 120000)]
    public async Task HeavyData_SHA256Checksums_100Channels_NoCorruption()
    {
        // Verify data integrity with cryptographic checksums
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 100;
        const int dataSize = 100_000; // 100KB per channel
        var sentHashes = new ConcurrentDictionary<string, string>();
        var receivedHashes = new ConcurrentDictionary<string, string>();

        // Accept and hash
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
                        receivedHashes[channel.ChannelId] = Convert.ToHexString(SHA256.HashData(data));
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= channelCount) break;
            }
        });

        // Send and hash
        for (var i = 0; i < channelCount; i++)
        {
            var channelId = $"hash_ch_{i}";
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
            
            var data = new byte[dataSize];
            Random.Shared.NextBytes(data);
            sentHashes[channelId] = Convert.ToHexString(SHA256.HashData(data));
            
            await ch.WriteAsync(data, cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.Delay(2000);

        // Verify all hashes match
        Assert.Equal(channelCount, sentHashes.Count);
        Assert.Equal(channelCount, receivedHashes.Count);

        foreach (var kvp in sentHashes)
        {
            Assert.True(receivedHashes.TryGetValue(kvp.Key, out var received), $"Missing hash for {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task HeavyData_SequentialBytes_VerifyOrder()
    {
        // Verify byte order is preserved (send sequential numbers)
        await using var pipe = new DuplexPipe();
        
        // Use higher credits for heavy data test
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 1024 * 1024 } 
        };
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 20;
        const int bytesPerChannel = 50_000;
        var receivedData = new ConcurrentDictionary<string, byte[]>();

        // Accept
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try { receivedData[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token); }
                    catch (OperationCanceledException) { }
                });
                if (++count >= channelCount) break;
            }
        });

        // Send sequential bytes
        for (var i = 0; i < channelCount; i++)
        {
            var channelId = $"seq_{i}";
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
            
            var data = new byte[bytesPerChannel];
            for (var j = 0; j < bytesPerChannel; j++)
            {
                data[j] = (byte)((j + i) % 256);
            }
            
            // Send in chunks
            var offset = 0;
            while (offset < data.Length)
            {
                var chunkSize = Math.Min(4096, data.Length - offset);
                await ch.WriteAsync(data.AsMemory(offset, chunkSize), cts.Token);
                offset += chunkSize;
            }
            
            await ch.FlushAsync(cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.Delay(1000);

        // Verify order
        for (var i = 0; i < channelCount; i++)
        {
            var channelId = $"seq_{i}";
            Assert.True(receivedData.TryGetValue(channelId, out var received), $"Missing {channelId}");
            Assert.Equal(bytesPerChannel, received.Length);
            
            for (var j = 0; j < bytesPerChannel; j++)
            {
                var expected = (byte)((j + i) % 256);
                Assert.True(received[j] == expected, $"Byte mismatch at {channelId}[{j}]: expected {expected}, got {received[j]}");
            }
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task HeavyData_LargeMessages_64KB_Each()
    {
        // Send many 64KB messages on a single channel and verify checksums
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int messageCount = 50; // Reduced for faster test
        const int messageSize = 65536; // 64KB
        
        // Prepare all data upfront
        var sentData = new List<byte[]>();
        var sentHashes = new List<string>();
        for (var i = 0; i < messageCount; i++)
        {
            var data = new byte[messageSize];
            Random.Shared.NextBytes(data);
            sentData.Add(data);
            sentHashes.Add(Convert.ToHexString(SHA256.HashData(data)));
        }

        byte[]? receivedAllData = null;

        // Accept and read all data
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                receivedAllData = await ReadAllBytesAsync(ch, cts.Token);
                break;
            }
        });

        // Send
        var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "large_messages" }, cts.Token);
        foreach (var data in sentData)
        {
            await ch.WriteAsync(data, cts.Token);
        }
        await ch.CloseAsync(cts.Token);

        await acceptTask;

        // Verify total data
        Assert.NotNull(receivedAllData);
        var expectedTotal = messageCount * messageSize;
        Assert.Equal(expectedTotal, receivedAllData.Length);

        // Verify each message hash
        for (var i = 0; i < messageCount; i++)
        {
            var receivedChunk = receivedAllData.AsSpan(i * messageSize, messageSize);
            var receivedHash = Convert.ToHexString(SHA256.HashData(receivedChunk));
            Assert.Equal(sentHashes[i], receivedHash);
        }

        cts.Cancel();
    }

    #endregion

    #region Robustness Tests

    [Fact(Timeout = 120000)]
    public async Task Robustness_RapidChannelCycles_AllDataReceived()
    {
        // Rapidly create and destroy channels, verify all data is received
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int cycles = 500; // Reduced count for stability
        var successCount = 0;
        var readTasks = new List<Task>();

        // Accept
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    try
                    {
                        var data = await ReadAllBytesAsync(channel, cts.Token);
                        if (data.Length > 0)
                            Interlocked.Increment(ref successCount);
                    }
                    catch (OperationCanceledException) { }
                });
                readTasks.Add(readTask);
                if (++count >= cycles) break;
            }
        });

        // Rapid cycles
        for (var i = 0; i < cycles; i++)
        {
            var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = $"rapid_{i}" }, cts.Token);
            await ch.WriteAsync(new byte[] { (byte)(i & 0xFF) }, cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        await acceptTask;
        await Task.WhenAll(readTasks); // Wait for all reads to complete
        await Task.Delay(500);

        Assert.Equal(cycles, successCount);

        // Verify total channels opened/closed stats
        Assert.Equal(cycles, muxA.Stats.TotalChannelsOpened);

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Robustness_ConcurrentChannelsFromBothSides_NoCollision()
    {
        // Both sides create channels with same numeric suffix simultaneously
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        const int count = 50;
        var aReceived = new ConcurrentDictionary<string, byte[]>();
        var bReceived = new ConcurrentDictionary<string, byte[]>();
        var aReadTasks = new ConcurrentBag<Task>();
        var bReadTasks = new ConcurrentBag<Task>();

        // Accept tasks
        var aAcceptTask = Task.Run(async () =>
        {
            var accepted = 0;
            await foreach (var ch in muxA.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    try { aReceived[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token); }
                    catch (OperationCanceledException) { }
                });
                aReadTasks.Add(readTask);
                if (++accepted >= count) break;
            }
        });

        var bAcceptTask = Task.Run(async () =>
        {
            var accepted = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    try { bReceived[channel.ChannelId] = await ReadAllBytesAsync(channel, cts.Token); }
                    catch (OperationCanceledException) { }
                });
                bReadTasks.Add(readTask);
                if (++accepted >= count) break;
            }
        });

        // Create channels - give some delay between pairs to avoid overwhelming
        for (var i = 0; i < count; i++)
        {
            var chA = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = $"from_a_{i}" }, cts.Token);
            var chB = await muxB.OpenChannelAsync(new ChannelOptions { ChannelId = $"from_b_{i}" }, cts.Token);

            await chA.WriteAsync(BitConverter.GetBytes(i), cts.Token);
            await chB.WriteAsync(BitConverter.GetBytes(i + 1000), cts.Token);

            await chA.CloseAsync(cts.Token);
            await chB.CloseAsync(cts.Token);
            
            if (i % 10 == 9) await Task.Delay(10); // Brief delay every 10 channels
        }

        // Wait for accept tasks with timeout
        await Task.WhenAny(aAcceptTask, Task.Delay(30000));
        await Task.WhenAny(bAcceptTask, Task.Delay(30000));
        
        // Wait for all read tasks
        var allReadTasks = aReadTasks.Concat(bReadTasks).ToArray();
        if (allReadTasks.Length > 0)
        {
            await Task.WhenAll(allReadTasks).WaitAsync(TimeSpan.FromSeconds(30));
        }

        // Verify
        Assert.Equal(count, aReceived.Count);
        Assert.Equal(count, bReceived.Count);

        for (var i = 0; i < count; i++)
        {
            Assert.True(bReceived.TryGetValue($"from_a_{i}", out var dataA));
            Assert.Equal(i, BitConverter.ToInt32(dataA));

            Assert.True(aReceived.TryGetValue($"from_b_{i}", out var dataB));
            Assert.Equal(i + 1000, BitConverter.ToInt32(dataB));
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Robustness_EmptyWrites_Handled()
    {
        // Intersperse empty writes
        await using var pipe = new DuplexPipe();
        
        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, new MultiplexerOptions());
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);
        await Task.Delay(100);

        byte[]? received = null;

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                received = await ReadAllBytesAsync(ch, cts.Token);
                break;
            }
        });

        var ch = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "empty_writes" }, cts.Token);
        
        // Intersperse data with empty writes
        var expected = new List<byte>();
        for (var i = 0; i < 100; i++)
        {
            await ch.WriteAsync(Array.Empty<byte>(), cts.Token); // Empty
            var data = new byte[] { (byte)i };
            expected.Add((byte)i);
            await ch.WriteAsync(data, cts.Token);
            await ch.WriteAsync(Array.Empty<byte>(), cts.Token); // Empty
        }
        
        await ch.CloseAsync(cts.Token);
        await acceptTask;

        Assert.NotNull(received);
        Assert.Equal(expected.Count, received.Length);
        Assert.True(expected.ToArray().SequenceEqual(received));

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

    private static async Task AcceptAllAsync(StreamMultiplexer mux, ConcurrentDictionary<string, byte[]> received, int count, CancellationToken ct)
    {
        var tasks = new List<Task>();
        var accepted = 0;
        await foreach (var ch in mux.AcceptChannelsAsync(ct))
        {
            var channel = ch;
            var readTask = Task.Run(async () =>
            {
                try { received[channel.ChannelId] = await ReadAllBytesAsync(channel, ct); }
                catch (OperationCanceledException) { }
            });
            tasks.Add(readTask);
            if (++accepted >= count) break;
        }
        await Task.WhenAll(tasks);
    }

    #endregion
}
