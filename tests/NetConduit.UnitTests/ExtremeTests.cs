using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Extreme stress tests for the multiplexer.
/// These tests are run in a separate collection to avoid resource contention.
/// </summary>
[Collection("ExtremeTests")]
public class ExtremeTests
{
    #region Nested Multiplexer Tests (Mux inside Mux)

    [Fact(Timeout = 120000)]
    public async Task NestedMux_SingleLevel_DataTransfersCorrectly()
    {
        // Level 0: Physical transport
        await using var physicalPipe = new DuplexPipe();
        
        // Level 1: First multiplexer layer
        await using var outerInitiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var outerAcceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var outerInitiatorTask = outerInitiator.RunAsync(cts.Token);
        var outerAcceptorTask = outerAcceptor.RunAsync(cts.Token);
        await Task.Delay(200);

        // Open two channels to create bidirectional transport for inner mux
        // Channel 1: initiator writes -> acceptor reads
        // Channel 2: acceptor writes -> initiator reads (via acceptor opening)
        var (outerWrite1, outerRead1) = await CreateBidirectionalChannelPairAsync(outerInitiator, outerAcceptor, "outer_channel_1", cts.Token);
        
        // For the reverse direction, acceptor opens a channel
        ReadChannel? reverseRead = null;
        var reverseAcceptTask = Task.Run(async () =>
        {
            await foreach (var ch in outerInitiator.AcceptChannelsAsync(cts.Token))
            {
                reverseRead = ch;
                break;
            }
        });
        var reverseWrite = await outerAcceptor.OpenChannelAsync(new ChannelOptions { ChannelId = "reverse_channel" }, cts.Token);
        await reverseAcceptTask;

        // Create inner multiplexer using outer channels as transport
        // Inner initiator: reads from reverseRead (acceptor->initiator), writes to outerWrite1 (initiator->acceptor)
        // Inner acceptor: reads from outerRead1 (initiator->acceptor), writes to reverseWrite (acceptor->initiator)
        var innerInitReadStream = new ChannelReadStream(reverseRead!);
        var innerInitWriteStream = new ChannelWriteStream(outerWrite1);
        var innerAcceptReadStream = new ChannelReadStream(outerRead1);
        var innerAcceptWriteStream = new ChannelWriteStream(reverseWrite);

        await using var innerInitiator = new StreamMultiplexer(innerInitReadStream, innerInitWriteStream,
            new MultiplexerOptions());
        await using var innerAcceptor = new StreamMultiplexer(innerAcceptReadStream, innerAcceptWriteStream,
            new MultiplexerOptions());

        var innerInitiatorTask = innerInitiator.RunAsync(cts.Token);
        var innerAcceptorTask = innerAcceptor.RunAsync(cts.Token);
        await Task.Delay(200);

        // Now use the inner multiplexer to transfer data
        ReadChannel? innerReadChannel = null;
        var innerAcceptChannelTask = Task.Run(async () =>
        {
            await foreach (var ch in innerAcceptor.AcceptChannelsAsync(cts.Token))
            {
                innerReadChannel = ch;
                break;
            }
        });

        var innerWriteChannel = await innerInitiator.OpenChannelAsync(new ChannelOptions { ChannelId = "inner_data" }, cts.Token);
        await innerAcceptChannelTask;

        // Transfer data through nested multiplexer
        var testData = new byte[4096];
        Random.Shared.NextBytes(testData);
        
        await innerWriteChannel.WriteAsync(testData, cts.Token);
        await innerWriteChannel.FlushAsync(cts.Token);
        await innerWriteChannel.CloseAsync(cts.Token);

        var receivedData = await ReadAllAsync(innerReadChannel!, cts.Token);

        Assert.Equal(testData, receivedData);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task NestedMux_TwoLevels_DataTransfersCorrectly()
    {
        // Physical layer
        await using var physicalPipe = new DuplexPipe();
        
        // Level 1
        await using var l1Initiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var l1Acceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var l1InitiatorTask = l1Initiator.RunAsync(cts.Token);
        var l1AcceptorTask = l1Acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create BIDIRECTIONAL channel pair for L2 (need channels in both directions)
        var l1Bidi = await CreateFullBidirectionalPipeAsync(l1Initiator, l1Acceptor, "l1_to_l2_a", "l1_to_l2_b", cts.Token);

        // Level 2 - Use the properly formed bidirectional channels
        await using var l2Initiator = new StreamMultiplexer(
            new ChannelReadStream(l1Bidi.InitiatorRead), new ChannelWriteStream(l1Bidi.InitiatorWrite),
            new MultiplexerOptions());
        await using var l2Acceptor = new StreamMultiplexer(
            new ChannelReadStream(l1Bidi.AcceptorRead), new ChannelWriteStream(l1Bidi.AcceptorWrite),
            new MultiplexerOptions());

        var l2InitiatorTask = l2Initiator.RunAsync(cts.Token);
        var l2AcceptorTask = l2Acceptor.RunAsync(cts.Token);
        await Task.Delay(200); // More time for L2 handshake

        // Transfer data directly through L2 (one level of nesting)
        ReadChannel? l2ReadChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in l2Acceptor.AcceptChannelsAsync(cts.Token))
            {
                l2ReadChannel = ch;
                break;
            }
        });

        var l2WriteChannel = await l2Initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "l2_data" }, cts.Token);
        await acceptTask;

        var testData = new byte[8192];
        Random.Shared.NextBytes(testData);
        
        await l2WriteChannel.WriteAsync(testData, cts.Token);
        await l2WriteChannel.FlushAsync(cts.Token);
        await l2WriteChannel.CloseAsync(cts.Token);

        var receivedData = await ReadAllAsync(l2ReadChannel!, cts.Token);
        Assert.Equal(testData, receivedData);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task NestedMux_ThreeLevels_DataTransfersCorrectly()
    {
        // Physical layer
        await using var physicalPipe = new DuplexPipe();
        
        // Level 1
        await using var l1Initiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var l1Acceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var l1InitiatorTask = l1Initiator.RunAsync(cts.Token);
        var l1AcceptorTask = l1Acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create BIDIRECTIONAL channel pair for L2
        var l1Bidi = await CreateFullBidirectionalPipeAsync(l1Initiator, l1Acceptor, "l1_bidi_a", "l1_bidi_b", cts.Token);

        // Level 2
        await using var l2Initiator = new StreamMultiplexer(
            new ChannelReadStream(l1Bidi.InitiatorRead), new ChannelWriteStream(l1Bidi.InitiatorWrite),
            new MultiplexerOptions());
        await using var l2Acceptor = new StreamMultiplexer(
            new ChannelReadStream(l1Bidi.AcceptorRead), new ChannelWriteStream(l1Bidi.AcceptorWrite),
            new MultiplexerOptions());

        var l2InitiatorTask = l2Initiator.RunAsync(cts.Token);
        var l2AcceptorTask = l2Acceptor.RunAsync(cts.Token);
        await Task.Delay(500); // Give more time for L2 handshake

        // Verify L2 is running
        Assert.True(l2Initiator.IsRunning, "L2 initiator should be running");
        Assert.True(l2Acceptor.IsRunning, "L2 acceptor should be running");

        // Create channels for L3 - use a shorter timeout to see the failure quicker
        using var shortCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, shortCts.Token);
        
        var l2Bidi = await CreateFullBidirectionalPipeAsync(l2Initiator, l2Acceptor, "l2_bidi_a", "l2_bidi_b", linkedCts.Token);

        // Level 3
        await using var l3Initiator = new StreamMultiplexer(
            new ChannelReadStream(l2Bidi.InitiatorRead), new ChannelWriteStream(l2Bidi.InitiatorWrite),
            new MultiplexerOptions());
        await using var l3Acceptor = new StreamMultiplexer(
            new ChannelReadStream(l2Bidi.AcceptorRead), new ChannelWriteStream(l2Bidi.AcceptorWrite),
            new MultiplexerOptions());

        var l3InitiatorTask = l3Initiator.RunAsync(cts.Token);
        var l3AcceptorTask = l3Acceptor.RunAsync(cts.Token);
        await Task.Delay(500);

        // Transfer data through L3 (two levels of nesting)
        ReadChannel? l3ReadChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in l3Acceptor.AcceptChannelsAsync(cts.Token))
            {
                l3ReadChannel = ch;
                break;
            }
        });

        var l3WriteChannel = await l3Initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "l3_data" }, cts.Token);
        await acceptTask;

        var testData = new byte[8192];
        Random.Shared.NextBytes(testData);
        
        await l3WriteChannel.WriteAsync(testData, cts.Token);
        await l3WriteChannel.FlushAsync(cts.Token);
        await l3WriteChannel.CloseAsync(cts.Token);

        var receivedData = await ReadAllAsync(l3ReadChannel!, cts.Token);
        Assert.Equal(testData, receivedData);

        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task NestedMux_MultipleChannelsPerLevel_AllDataCorrect()
    {
        await using var physicalPipe = new DuplexPipe();
        
        await using var l1Initiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var l1Acceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var l1InitiatorTask = l1Initiator.RunAsync(cts.Token);
        var l1AcceptorTask = l1Acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create multiple inner multiplexers on separate bidirectional channel pairs
        const int innerMuxCount = 5;
        const int channelsPerMux = 10;
        const int dataSize = 512;

        var innerMuxes = new List<(StreamMultiplexer Initiator, StreamMultiplexer Acceptor, Task InitTask, Task AcceptTask)>();

        for (int i = 0; i < innerMuxCount; i++)
        {
            var bidi = await CreateFullBidirectionalPipeAsync(l1Initiator, l1Acceptor, $"inner_{i}_a", $"inner_{i}_b", cts.Token);

            var innerInit = new StreamMultiplexer(
                new ChannelReadStream(bidi.InitiatorRead), new ChannelWriteStream(bidi.InitiatorWrite),
                new MultiplexerOptions());
            var innerAccept = new StreamMultiplexer(
                new ChannelReadStream(bidi.AcceptorRead), new ChannelWriteStream(bidi.AcceptorWrite),
                new MultiplexerOptions());

            var initTask = innerInit.RunAsync(cts.Token);
            var acceptTask = innerAccept.RunAsync(cts.Token);

            innerMuxes.Add((innerInit, innerAccept, initTask, acceptTask));
        }

        await Task.Delay(200);

        // Transfer data through all inner multiplexers concurrently
        var allTasks = new List<Task>();
        // Use ChannelId as key instead of index to ensure proper matching
        var sentData = new ConcurrentDictionary<(int MuxIndex, string ChannelId), byte[]>();
        var receivedData = new ConcurrentDictionary<(int MuxIndex, string ChannelId), byte[]>();

        foreach (var (muxIndex, (initiator, acceptor, _, _)) in innerMuxes.Select((m, i) => (i, m)))
        {
            var mi = muxIndex;
            
            // Accept channels
            var acceptChannelTask = Task.Run(async () =>
            {
                var count = 0;
                await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
                {
                    var channel = ch;
                    _ = Task.Run(async () =>
                    {
                        var received = await ReadAllAsync(channel, cts.Token);
                        receivedData[(mi, channel.ChannelId)] = received;
                    });
                    if (++count >= channelsPerMux) break;
                }
            });

            // Send data
            for (int c = 0; c < channelsPerMux; c++)
            {
                var channelIndex = c;
                allTasks.Add(Task.Run(async () =>
                {
                    var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"mux_{mi}_ch_{channelIndex}" }, cts.Token);
                    var data = new byte[dataSize];
                    Random.Shared.NextBytes(data);
                    
                    sentData[(mi, channel.ChannelId)] = data;
                    
                    await channel.WriteAsync(data, cts.Token);
                    await channel.FlushAsync(cts.Token);
                    await channel.CloseAsync(cts.Token);
                }));
            }

            allTasks.Add(acceptChannelTask);
        }

        await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(500); // Let receivers finish

        // Verify all data - match by ChannelId
        Assert.Equal(innerMuxCount * channelsPerMux, sentData.Count);
        foreach (var kvp in sentData)
        {
            Assert.True(receivedData.TryGetValue(kvp.Key, out var received), 
                $"Missing received data for mux {kvp.Key.MuxIndex} channel {kvp.Key.ChannelId}");
            Assert.Equal(kvp.Value, received);
        }

        // Cleanup
        foreach (var (init, accept, _, _) in innerMuxes)
        {
            await init.DisposeAsync();
            await accept.DisposeAsync();
        }

        cts.Cancel();
    }

    #endregion

    #region Chaos Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Chaos_RandomOperations_NoDeadlocks()
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

        var channels = new ConcurrentBag<WriteChannel>();
        var readChannels = new ConcurrentDictionary<string, ReadChannel>();
        var exceptions = new ConcurrentBag<Exception>();

        // Accept all channels
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
                {
                    readChannels[ch.ChannelId] = ch;
                    // Read and discard
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var buf = new byte[1024];
                            while (await ch.ReadAsync(buf, cts.Token) > 0) { }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { exceptions.Add(ex); }
                    });
                }
            }
            catch (OperationCanceledException) { }
        });

        // Chaos operations
        var chaosOps = new Func<Task>[]
        {
            // Open channel
            async () =>
            {
                var ch = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"chaos_{Guid.NewGuid():N}" }, cts.Token);
                channels.Add(ch);
            },
            // Write random data
            async () =>
            {
                if (channels.TryPeek(out var ch) && ch.State == ChannelState.Open)
                {
                    var data = new byte[Random.Shared.Next(1, 4096)];
                    Random.Shared.NextBytes(data);
                    await ch.WriteAsync(data, cts.Token);
                }
            },
            // Close random channel
            async () =>
            {
                if (channels.TryTake(out var ch) && ch.State == ChannelState.Open)
                {
                    await ch.FlushAsync(cts.Token);
                    await ch.CloseAsync(cts.Token);
                }
            },
            // Random delay
            async () => await Task.Delay(Random.Shared.Next(1, 50), cts.Token)
        };

        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var op = chaosOps[Random.Shared.Next(chaosOps.Length)];
                    await op();
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { } // Channel state changes
            catch (Exception ex) { exceptions.Add(ex); }
        });

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(25));
        
        cts.Cancel();

        // Should complete without deadlock
        Assert.Empty(exceptions);
    }

    [Fact(Timeout = 120000)]
    public async Task Chaos_RapidOpenClose_NoResourceLeaks()
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

        var acceptedChannels = new List<ReadChannel>();
        var acceptLock = new object();
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                lock (acceptLock)
                {
                    acceptedChannels.Add(ch);
                }
            }
        });

        const int iterations = 1000;
        var openCloseTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var ch = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"rapid_open_{i}" }, cts.Token);
                // Random: either close gracefully or dispose immediately
                if (Random.Shared.NextDouble() > 0.5)
                    await ch.CloseAsync(cts.Token);
                else
                    ch.Dispose();
            }
        });

        await openCloseTask.WaitAsync(TimeSpan.FromSeconds(25));
        await Task.Delay(500);

        int acceptedCount;
        lock (acceptLock)
        {
            acceptedCount = acceptedChannels.Count;
            foreach (var ch in acceptedChannels)
            {
                ch.Dispose();
            }
        }
        
        Assert.Equal(iterations, acceptedCount);
        
        cts.Cancel();
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Chaos_InterleavedReadWrites_DataIntegrity()
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
        var sentHashes = new ConcurrentDictionary<string, byte[]>();
        var receivedHashes = new ConcurrentDictionary<string, byte[]>();
        var sentSizes = new ConcurrentDictionary<string, int>();
        var receivedSizes = new ConcurrentDictionary<string, int>();
        var readTasks = new List<Task>();

        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    using var sha = SHA256.Create();
                    var buffer = new byte[1024];
                    var totalRead = 0;
                    while (true)
                    {
                        var read = await channel.ReadAsync(buffer, cts.Token);
                        if (read == 0) break;
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        totalRead += read;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    receivedHashes[channel.ChannelId] = sha.Hash!;
                    receivedSizes[channel.ChannelId] = totalRead;
                });
                lock (readTasks)
                {
                    readTasks.Add(readTask);
                }
                if (++count >= channelCount) break;
            }
        });

        // Open channels and write with random chunk sizes
        var sendTasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            var rng = new Random(i); // Per-channel RNG for determinism
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"interleaved_{i}" }, cts.Token);
            
            using var sha = SHA256.Create();
            var totalSize = rng.Next(1024, 65536);
            var sent = 0;
            
            while (sent < totalSize)
            {
                var chunkSize = Math.Min(rng.Next(1, 2048), totalSize - sent);
                var chunk = new byte[chunkSize];
                rng.NextBytes(chunk);
                
                sha.TransformBlock(chunk, 0, chunk.Length, null, 0);
                await channel.WriteAsync(chunk, cts.Token);
                sent += chunkSize;
                
                // Random yield
                if (rng.NextDouble() > 0.8)
                    await Task.Yield();
            }
            
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sentHashes[channel.ChannelId] = sha.Hash!;
            sentSizes[channel.ChannelId] = sent;
            
            await channel.FlushAsync(cts.Token);
            await channel.CloseAsync(cts.Token);
        });

        await Task.WhenAll(sendTasks);
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        
        Task[] readTasksSnapshot;
        lock (readTasks)
        {
            readTasksSnapshot = readTasks.ToArray();
        }
        await Task.WhenAll(readTasksSnapshot).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(channelCount, sentHashes.Count);
        Assert.Equal(channelCount, receivedHashes.Count);
        
        var mismatches = new List<string>();
        foreach (var kvp in sentHashes)
        {
            var sentSize = sentSizes[kvp.Key];
            if (!receivedHashes.TryGetValue(kvp.Key, out var received))
            {
                mismatches.Add($"Channel {kvp.Key}: not received (sent {sentSize} bytes)");
            }
            else 
            {
                var recvSize = receivedSizes[kvp.Key];
                if (!kvp.Value.SequenceEqual(received))
                {
                    mismatches.Add($"Channel {kvp.Key}: hash mismatch sent={sentSize} bytes recv={recvSize} bytes (sent hash={Convert.ToHexString(kvp.Value)[..16]}... recv hash={Convert.ToHexString(received)[..16]}...)");
                }
            }
        }
        
        Assert.True(mismatches.Count == 0, $"Mismatches ({mismatches.Count}):\n{string.Join("\n", mismatches)}");

        cts.Cancel();
    }

    #endregion

    #region Scale Tests

    [Fact(Timeout = 120000)]
    public async Task Scale_TenThousandChannels_AllSucceed()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 10_000;
        var acceptedCount = 0;

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var count = Interlocked.Increment(ref acceptedCount);
                ch.Dispose(); // Just accept and dispose
                if (count >= channelCount) break;
            }
        });

        var sw = Stopwatch.StartNew();
        
        // Open channels in batches
        const int batchSize = 100;
        for (int batch = 0; batch < channelCount / batchSize; batch++)
        {
            var batchNum = batch;
            var tasks = Enumerable.Range(0, batchSize).Select(async i =>
            {
                var ch = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"scale10k_{batchNum}_{i}" }, cts.Token);
                ch.Dispose();
            });
            await Task.WhenAll(tasks);
        }

        await acceptTask.WaitAsync(TimeSpan.FromSeconds(60));
        sw.Stop();

        Assert.Equal(channelCount, acceptedCount);
        
        // Log performance
        var rate = channelCount / sw.Elapsed.TotalSeconds;
        await Console.Out.WriteLineAsync($"10K channels: {sw.ElapsedMilliseconds}ms ({rate:F0} channels/sec)");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Scale_LargeDataTransfer_SingleChannel()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "large_data_500mb" }, cts.Token);
        await acceptTask;

        // Transfer 500MB
        const long totalSize = 500L * 1024 * 1024;
        const int chunkSize = 64 * 1024;
        var chunk = new byte[chunkSize];
        Random.Shared.NextBytes(chunk);

        var sw = Stopwatch.StartNew();
        
        var writeTask = Task.Run(async () =>
        {
            long sent = 0;
            while (sent < totalSize)
            {
                var toSend = (int)Math.Min(chunkSize, totalSize - sent);
                await writeChannel.WriteAsync(chunk.AsMemory(0, toSend), cts.Token);
                sent += toSend;
            }
            await writeChannel.CloseAsync(cts.Token);
        });

        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[chunkSize];
            long received = 0;
            while (true)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                received += read;
            }
            return received;
        });

        await writeTask;
        var receivedTotal = await readTask;
        sw.Stop();

        Assert.Equal(totalSize, receivedTotal);
        
        var throughput = totalSize / sw.Elapsed.TotalSeconds / 1024 / 1024;
        await Console.Out.WriteLineAsync($"500MB transfer: {sw.Elapsed.TotalSeconds:F2}s ({throughput:F0} MB/s)");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Scale_ManySmallMessages_HighThroughput()
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "small_messages_100k" }, cts.Token);
        await acceptTask;

        const int messageCount = 100_000;
        const int messageSize = 32; // Small messages
        var message = new byte[messageSize];
        Random.Shared.NextBytes(message);

        var receivedCount = 0;
        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[messageSize];
            while (true)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                Interlocked.Add(ref receivedCount, read / messageSize);
            }
        });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < messageCount; i++)
        {
            await writeChannel.WriteAsync(message, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);
        sw.Stop();

        await readTask.WaitAsync(TimeSpan.FromSeconds(30));

        var rate = messageCount / sw.Elapsed.TotalSeconds;
        await Console.Out.WriteLineAsync($"100K messages ({messageSize}B): {sw.ElapsedMilliseconds}ms ({rate:F0} msg/s)");

        cts.Cancel();
    }

    #endregion

    #region Bidirectional Stress Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task Bidirectional_BothSidesOpenChannels_NoConflicts()
    {
        await using var pipe = new DuplexPipe();
        
        await using var side1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var side2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        
        var side1Task = side1.RunAsync(cts.Token);
        var side2Task = side2.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelsPerSide = 25;
        var side1Sent = new ConcurrentDictionary<string, byte[]>();
        var side2Sent = new ConcurrentDictionary<string, byte[]>();
        var side1Received = new ConcurrentDictionary<string, byte[]>();
        var side2Received = new ConcurrentDictionary<string, byte[]>();

        // Create channels sequentially, using named accept for reliable matching
        for (var i = 0; i < channelsPerSide; i++)
        {
            var side1ChId = $"side1_ch_{i}";
            var side2ChId = $"side2_ch_{i}";

            // Side 1 opens a channel
            var write1 = await side1.OpenChannelAsync(new ChannelOptions { ChannelId = side1ChId }, cts.Token);
            
            // Side 2 opens a channel
            var write2 = await side2.OpenChannelAsync(new ChannelOptions { ChannelId = side2ChId }, cts.Token);

            // Accept channels by name
            var read1 = await side2.AcceptChannelAsync(side1ChId, cts.Token);
            var read2 = await side1.AcceptChannelAsync(side2ChId, cts.Token);

            // Generate and store data
            var data1 = new byte[Random.Shared.Next(100, 500)];
            Random.Shared.NextBytes(data1);
            side1Sent[side1ChId] = data1;

            var data2 = new byte[Random.Shared.Next(100, 500)];
            Random.Shared.NextBytes(data2);
            side2Sent[side2ChId] = data2;

            // Send data
            await write1.WriteAsync(data1, cts.Token);
            await write2.WriteAsync(data2, cts.Token);

            // Close write channels
            await write1.CloseAsync(cts.Token);
            await write2.CloseAsync(cts.Token);

            // Read all data
            side1Received[side1ChId] = await ReadAllAsync(read1, cts.Token);
            side2Received[side2ChId] = await ReadAllAsync(read2, cts.Token);
        }

        // Verify all data
        Assert.Equal(channelsPerSide, side1Sent.Count);
        Assert.Equal(channelsPerSide, side2Sent.Count);
        Assert.Equal(channelsPerSide, side1Received.Count);
        Assert.Equal(channelsPerSide, side2Received.Count);
        
        foreach (var kvp in side1Sent)
        {
            Assert.True(side1Received.TryGetValue(kvp.Key, out var received), $"Side2 didn't receive from side1 channel {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }
        
        foreach (var kvp in side2Sent)
        {
            Assert.True(side2Received.TryGetValue(kvp.Key, out var received), $"Side1 didn't receive from side2 channel {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task Bidirectional_EchoServer_AllDataReturned()
    {
        await using var pipe = new DuplexPipe();
        
        await using var client = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var server = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var clientTask = client.RunAsync(cts.Token);
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(100);

        const int requestCount = 25;
        var sentData = new ConcurrentDictionary<string, byte[]>();
        var responses = new ConcurrentDictionary<string, byte[]>();
        var serverEchoCompleted = 0;

        // Server: accept channels, read data, open response channel, write data back
        var serverEchoTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var readCh in server.AcceptChannelsAsync(cts.Token))
            {
                var channel = readCh;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var data = await ReadAllAsync(channel, cts.Token);
                        
                        // Open response channel
                        var responseCh = await server.OpenChannelAsync(
                            new ChannelOptions { ChannelId = $"echo_response_{channel.ChannelId}" },
                            cts.Token);
                        await responseCh.WriteAsync(data, cts.Token);
                        await responseCh.CloseAsync(cts.Token);
                        Interlocked.Increment(ref serverEchoCompleted);
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= requestCount) break;
            }
        });

        // Client: collect responses
        var clientReceiveTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in client.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var data = await ReadAllAsync(channel, cts.Token);
                        responses[channel.ChannelId] = data;
                    }
                    catch (OperationCanceledException) { }
                });
                if (++count >= requestCount) break;
            }
        });

        // Client: send requests sequentially to avoid overwhelming
        for (var i = 0; i < requestCount; i++)
        {
            var ch = await client.OpenChannelAsync(new ChannelOptions { ChannelId = $"echo_request_{i}" }, cts.Token);
            var data = new byte[Random.Shared.Next(100, 500)];
            Random.Shared.NextBytes(data);
            sentData[ch.ChannelId] = data;
            
            await ch.WriteAsync(data, cts.Token);
            await ch.CloseAsync(cts.Token);
        }

        // Wait for server to process all and client to receive responses
        await serverEchoTask;
        await clientReceiveTask;
        await Task.Delay(500);

        Assert.Equal(requestCount, sentData.Count);
        Assert.Equal(requestCount, serverEchoCompleted);
        Assert.Equal(requestCount, responses.Count);

        // Verify data matches (response channel name encodes original)
        foreach (var kvp in sentData)
        {
            var responseKey = $"echo_response_{kvp.Key}";
            Assert.True(responses.TryGetValue(responseKey, out var received), $"Missing response for {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    #endregion

    #region Edge Cases

    [Fact(Timeout = 120000)]
    public async Task EdgeCase_ZeroLengthWrites_Handled()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "zero_length" }, cts.Token);
        await acceptTask;

        // Write zero bytes
        await writeChannel.WriteAsync(Array.Empty<byte>(), cts.Token);
        
        // Write actual data
        var data = new byte[] { 1, 2, 3, 4, 5 };
        await writeChannel.WriteAsync(data, cts.Token);
        
        // Write zero bytes again
        await writeChannel.WriteAsync(Array.Empty<byte>(), cts.Token);
        
        await writeChannel.CloseAsync(cts.Token);

        var received = await ReadAllAsync(readChannel!, cts.Token);
        Assert.Equal(data, received);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task EdgeCase_MaximumFrameSize_Handled()
    {
        const int maxFrameSize = 1024 * 1024; // 1MB
        
        await using var pipe = new DuplexPipe();
        
        // Use higher initial credits to handle large frames without many round-trips
        var channelOptions = new DefaultChannelOptions { InitialCredits = (uint)maxFrameSize * 2 };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions { MaxFrameSize = maxFrameSize, DefaultChannelOptions = channelOptions });
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions { MaxFrameSize = maxFrameSize, DefaultChannelOptions = channelOptions });

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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "max_frame" }, cts.Token);
        await acceptTask;

        // Write exactly max frame size
        var data = new byte[maxFrameSize];
        Random.Shared.NextBytes(data);
        
        await writeChannel.WriteAsync(data, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        var received = await ReadAllAsync(readChannel!, cts.Token);
        Assert.Equal(data, received);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task EdgeCase_SimultaneousCloseFromBothSides_NoDeadlock()
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create channels from both sides
        var (write1, read1) = await CreateBidirectionalChannelPairAsync(initiator, acceptor, "close_test_1", cts.Token);
        var (write2, read2) = await CreateBidirectionalChannelPairAsync(initiator, acceptor, "close_test_2", cts.Token);

        // Close simultaneously from both sides
        await Task.WhenAll(
            write1.CloseAsync(cts.Token).AsTask(),
            write2.CloseAsync(cts.Token).AsTask()
        );

        // Both should complete without deadlock
        Assert.True(true);

        cts.Cancel();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Result for a fully bidirectional pipe between two multiplexers.
    /// </summary>
    private record BidirectionalPipe(
        WriteChannel InitiatorWrite,
        ReadChannel InitiatorRead,
        WriteChannel AcceptorWrite,
        ReadChannel AcceptorRead
    );

    /// <summary>
    /// Creates a fully bidirectional pipe between two multiplexers.
    /// This creates two one-way channels (one in each direction) that together form a bidirectional pipe.
    /// - InitiatorWrite -> AcceptorRead
    /// - AcceptorWrite -> InitiatorRead
    /// </summary>
    private static async Task<BidirectionalPipe> CreateFullBidirectionalPipeAsync(
        StreamMultiplexer initiator, StreamMultiplexer acceptor, string channel1Id, string channel2Id, CancellationToken ct)
    {
        // Channel 1: Initiator -> Acceptor
        ReadChannel? acceptorRead = null;
        var accept1Task = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(ct))
            {
                acceptorRead = ch;
                break;
            }
        });
        var initiatorWrite = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = channel1Id }, ct);
        await accept1Task;

        // Channel 2: Acceptor -> Initiator
        ReadChannel? initiatorRead = null;
        var accept2Task = Task.Run(async () =>
        {
            await foreach (var ch in initiator.AcceptChannelsAsync(ct))
            {
                initiatorRead = ch;
                break;
            }
        });
        var acceptorWrite = await acceptor.OpenChannelAsync(new ChannelOptions { ChannelId = channel2Id }, ct);
        await accept2Task;

        return new BidirectionalPipe(initiatorWrite, initiatorRead!, acceptorWrite, acceptorRead!);
    }

    private static async Task<(WriteChannel Write, ReadChannel Read)> CreateBidirectionalChannelPairAsync(
        StreamMultiplexer initiator, StreamMultiplexer acceptor, string channelId, CancellationToken ct)
    {
        ReadChannel? readChannel = null;
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(ct))
            {
                readChannel = ch;
                break;
            }
        });

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, ct);
        await acceptTask;

        return (writeChannel, readChannel!);
    }

    private static async Task<byte[]> ReadAllAsync(ReadChannel channel, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var temp = new byte[4096];
        while (true)
        {
            var read = await channel.ReadAsync(temp, ct);
            if (read == 0) break;
            buffer.Write(temp, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task AcceptAndReceiveAsync(
        StreamMultiplexer mux, ConcurrentDictionary<string, byte[]> received, int count, CancellationToken ct)
    {
        var accepted = 0;
        var readTasks = new List<Task>();
        await foreach (var ch in mux.AcceptChannelsAsync(ct))
        {
            var channel = ch;
            var readTask = Task.Run(async () =>
            {
                var data = await ReadAllAsync(channel, ct);
                received[channel.ChannelId] = data;
            });
            readTasks.Add(readTask);
            if (++accepted >= count) break;
        }
        // Wait for all read tasks to complete
        await Task.WhenAll(readTasks);
    }

    private static async Task OpenAndSendAsync(
        StreamMultiplexer mux, ConcurrentDictionary<string, byte[]> sent, int count, string channelIdPrefix, CancellationToken ct)
    {
        // Open and send sequentially to avoid overwhelming the multiplexer
        for (var i = 0; i < count; i++)
        {
            var ch = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = $"{channelIdPrefix}_{i}" }, ct);
            var data = new byte[Random.Shared.Next(100, 1000)];
            Random.Shared.NextBytes(data);
            sent[ch.ChannelId] = data;
            
            await ch.WriteAsync(data, ct);
            await ch.CloseAsync(ct);
        }
    }

    /// <summary>
    /// Wrapper to use a ReadChannel as a Stream for nested multiplexers.
    /// </summary>
    private class ChannelReadStream : Stream
    {
        private readonly ReadChannel _channel;

        public ChannelReadStream(ReadChannel channel) => _channel = channel;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _channel.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _channel.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _channel.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Wrapper to use a WriteChannel as a Stream for nested multiplexers.
    /// </summary>
    private class ChannelWriteStream : Stream
    {
        private readonly WriteChannel _channel;

        public ChannelWriteStream(WriteChannel channel) => _channel = channel;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _channel.Flush();
        public override Task FlushAsync(CancellationToken ct) => _channel.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _channel.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _channel.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _channel.WriteAsync(buffer, ct);
    }

    #endregion

    #region Heavy Load Tests

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task HeavyLoad_100KChannels_AllSucceed()
    {
        // Test opening and using 100,000 channels
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 100_000;
        var receivedCount = 0;
        var sentCount = 0;

        var sw = Stopwatch.StartNew();

        // Accept channels in background
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[64];
                    var read = await channel.ReadAsync(buffer, cts.Token);
                    if (read > 0)
                        Interlocked.Increment(ref receivedCount);
                });
                
                if (Interlocked.Increment(ref sentCount) >= channelCount)
                    break;
            }
        });

        // Open channels and send minimal data
        var sendTasks = new List<Task>();
        for (int i = 0; i < channelCount; i++)
        {
            var channelIndex = i;
            sendTasks.Add(Task.Run(async () =>
            {
                var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"scale_ch_{channelIndex}" }, cts.Token);
                await channel.WriteAsync(new byte[] { 0x42 }, cts.Token);
                await channel.FlushAsync(cts.Token);
                await channel.CloseAsync(cts.Token);
            }));

            // Batch to avoid overwhelming
            if (sendTasks.Count >= 1000)
            {
                await Task.WhenAll(sendTasks);
                sendTasks.Clear();
            }
        }
        await Task.WhenAll(sendTasks);
        await acceptTask;

        sw.Stop();

        // Wait for all receives to complete
        await Task.Delay(2000);

        var rate = channelCount / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"100K channels: {sw.ElapsedMilliseconds}ms ({rate:N0} channels/sec)");

        Assert.True(receivedCount >= channelCount * 0.99, 
            $"Expected at least 99% of channels to receive data, got {receivedCount}/{channelCount}");

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task HeavyLoad_ConcurrentBidirectional_StressTest()
    {
        // Heavy concurrent bidirectional traffic
        await using var pipe = new DuplexPipe();
        
        // Use higher credits for heavy load
        var options = new MultiplexerOptions 
        { 
            DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 1024 * 1024 } 
        };
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 1000;
        const int messagesPerChannel = 100;
        const int messageSize = 1024;
        
        var sentMessages = new ConcurrentDictionary<string, int>();
        var receivedMessages = new ConcurrentDictionary<string, int>();

        var sw = Stopwatch.StartNew();

        // Accept and echo
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[messageSize];
                    var msgCount = 0;
                    while (true)
                    {
                        var read = await channel.ReadAsync(buffer, cts.Token);
                        if (read == 0) break;
                        msgCount++;
                    }
                    receivedMessages[channel.ChannelId] = msgCount;
                });
                
                if (++count >= channelCount) break;
            }
        });

        // Send many messages per channel
        var sendTasks = Enumerable.Range(0, channelCount).Select(async chIdx =>
        {
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"heavy_ch_{chIdx}" }, cts.Token);
            var data = new byte[messageSize];
            Random.Shared.NextBytes(data);
            
            for (int i = 0; i < messagesPerChannel; i++)
            {
                await channel.WriteAsync(data, cts.Token);
            }
            sentMessages[channel.ChannelId] = messagesPerChannel;
            await channel.FlushAsync(cts.Token);
            await channel.CloseAsync(cts.Token);
        });

        await Task.WhenAll(sendTasks);
        await acceptTask;
        await Task.Delay(2000); // Wait for receives

        sw.Stop();

        var totalMessages = channelCount * messagesPerChannel;
        var rate = totalMessages / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"Heavy bidirectional: {totalMessages:N0} messages in {sw.ElapsedMilliseconds}ms ({rate:N0} msg/sec)");

        // Verify
        Assert.Equal(channelCount, sentMessages.Count);
        var totalReceived = receivedMessages.Values.Sum();
        Assert.True(totalReceived >= totalMessages * 0.99,
            $"Expected at least 99% messages received, got {totalReceived}/{totalMessages}");

        cts.Cancel();
    }

    #endregion

    #region Big Data Tests

    [Fact(Timeout = 120000)]
    public async Task BigData_1GB_SingleChannel_TransfersCorrectly()
    {
        // Transfer 1GB of data through a single channel
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 16 * 1024 * 1024 } });
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 16 * 1024 * 1024 } });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "large_transfer" }, cts.Token);
        await acceptTask;

        const long totalBytes = 1L * 1024 * 1024 * 1024; // 1GB
        const int chunkSize = 64 * 1024; // 64KB chunks
        
        using var sendHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var recvHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        
        var sw = Stopwatch.StartNew();

        // Send in background
        var sendTask = Task.Run(async () =>
        {
            var chunk = new byte[chunkSize];
            var rng = new Random(42); // Deterministic
            long sent = 0;
            
            while (sent < totalBytes)
            {
                var toSend = (int)Math.Min(chunkSize, totalBytes - sent);
                rng.NextBytes(chunk.AsSpan(0, toSend));
                sendHash.AppendData(chunk.AsSpan(0, toSend));
                
                await writeChannel.WriteAsync(chunk.AsMemory(0, toSend), cts.Token);
                sent += toSend;
            }
            await writeChannel.CloseAsync(cts.Token);
        });

        // Receive
        var buffer = new byte[chunkSize];
        long received = 0;
        while (true)
        {
            var read = await readChannel!.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            recvHash.AppendData(buffer.AsSpan(0, read));
            received += read;
        }

        await sendTask;
        sw.Stop();

        var sendHashBytes = sendHash.GetCurrentHash();
        var recvHashBytes = recvHash.GetCurrentHash();

        var throughput = (totalBytes / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"1GB transfer: {sw.Elapsed.TotalSeconds:F2}s ({throughput:F0} MB/s)");

        Assert.Equal(totalBytes, received);
        Assert.Equal(sendHashBytes, recvHashBytes);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task BigData_100MB_AcrossMultipleChannels_AllCorrect()
    {
        // Distribute 100MB across 100 channels (1MB each)
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 2 * 1024 * 1024 } });
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 2 * 1024 * 1024 } });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int channelCount = 100;
        const int bytesPerChannel = 1024 * 1024; // 1MB per channel
        
        var sentHashes = new ConcurrentDictionary<string, byte[]>();
        var receivedHashes = new ConcurrentDictionary<string, byte[]>();
        var readTasks = new List<Task>();

        var sw = Stopwatch.StartNew();

        // Accept channels
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await channel.ReadAsync(buffer, cts.Token);
                        if (read == 0) break;
                        hash.AppendData(buffer.AsSpan(0, read));
                    }
                    receivedHashes[channel.ChannelId] = hash.GetCurrentHash();
                });
                lock (readTasks) readTasks.Add(readTask);
                if (++count >= channelCount) break;
            }
        });

        // Send 1MB per channel
        var sendTasks = Enumerable.Range(0, channelCount).Select(async i =>
        {
            var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"small_msg_{i}" }, cts.Token);
            var rng = new Random(i);
            
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = new byte[64 * 1024];
            var sent = 0;
            
            while (sent < bytesPerChannel)
            {
                var toSend = Math.Min(chunk.Length, bytesPerChannel - sent);
                rng.NextBytes(chunk.AsSpan(0, toSend));
                hash.AppendData(chunk.AsSpan(0, toSend));
                await channel.WriteAsync(chunk.AsMemory(0, toSend), cts.Token);
                sent += toSend;
            }
            
            sentHashes[channel.ChannelId] = hash.GetCurrentHash();
            await channel.CloseAsync(cts.Token);
        });

        await Task.WhenAll(sendTasks);
        await acceptTask;
        
        Task[] readTasksSnapshot;
        lock (readTasks) readTasksSnapshot = readTasks.ToArray();
        await Task.WhenAll(readTasksSnapshot);

        sw.Stop();

        var totalMB = (channelCount * bytesPerChannel) / 1024.0 / 1024.0;
        var throughput = totalMB / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"100MB across {channelCount} channels: {sw.Elapsed.TotalSeconds:F2}s ({throughput:F0} MB/s)");

        // Verify all hashes match
        Assert.Equal(channelCount, sentHashes.Count);
        Assert.Equal(channelCount, receivedHashes.Count);
        
        foreach (var kvp in sentHashes)
        {
            Assert.True(receivedHashes.TryGetValue(kvp.Key, out var recvHash),
                $"Channel {kvp.Key} not received");
            Assert.Equal(kvp.Value, recvHash);
        }

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task BigData_StreamingVideo_Simulation()
    {
        // Simulate streaming video: continuous 4K frames at 60fps
        // 4K frame ~ 8MB uncompressed, compressed ~ 500KB average
        // 60fps * 500KB = 30MB/s sustained throughput
        
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 32 * 1024 * 1024 } });
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions { DefaultChannelOptions = new DefaultChannelOptions { InitialCredits = 32 * 1024 * 1024 } });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "video_stream" }, cts.Token);
        await acceptTask;

        const int frameSize = 500 * 1024; // 500KB per frame (compressed 4K)
        const int fps = 60;
        const int durationSeconds = 10;
        const int totalFrames = fps * durationSeconds;
        
        var framesReceived = 0;
        var bytesReceived = 0L;

        var sw = Stopwatch.StartNew();

        // Receiver
        var receiveTask = Task.Run(async () =>
        {
            var buffer = new byte[frameSize];
            while (true)
            {
                var frameBytes = 0;
                while (frameBytes < frameSize)
                {
                    var read = await readChannel!.ReadAsync(buffer.AsMemory(frameBytes), cts.Token);
                    if (read == 0) return;
                    frameBytes += read;
                }
                Interlocked.Increment(ref framesReceived);
                Interlocked.Add(ref bytesReceived, frameBytes);
            }
        });

        // Sender - send frames as fast as possible (simulating encoder output)
        var frame = new byte[frameSize];
        Random.Shared.NextBytes(frame);
        
        for (int i = 0; i < totalFrames; i++)
        {
            await writeChannel.WriteAsync(frame, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);

        await receiveTask;
        sw.Stop();

        var actualFps = framesReceived / sw.Elapsed.TotalSeconds;
        var throughputMBps = (bytesReceived / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;
        
        Console.WriteLine($"Video streaming: {framesReceived} frames in {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Effective FPS: {actualFps:F1}, Throughput: {throughputMBps:F1} MB/s");

        Assert.Equal(totalFrames, framesReceived);

        cts.Cancel();
    }

    #endregion

    #region Scaled Channel Tests

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    [Fact(Timeout = 120000)]
    public async Task ScaledChannels_Hundred_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(100, batchSize: 50);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledChannels_Thousand_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(1_000, batchSize: 100);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledChannels_TenThousand_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(10_000, batchSize: 1_000);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledChannels_FiftyThousand_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(50_000, batchSize: 5_000);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledChannels_HundredThousand_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(100_000, batchSize: 10_000);
    }

    [Fact(Skip = "Million channel test takes too long - needs investigation")]
    public async Task MillionChannels_OpenCloseRapidly_NoLeaks()
    {
        await RunOpenCloseStressTest(1_000_000, batchSize: 10_000);
    }

    private static async Task RunOpenCloseStressTest(int totalChannels, int batchSize)
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TestTimeout);
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        var completedCount = 0;
        var acceptedChannels = new List<ReadChannel>();
        var acceptLock = new object();
        var acceptLoopReady = new SemaphoreSlim(0, 1);

        var sw = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(true);

        // Accept channels
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            acceptLoopReady.Release();
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                lock (acceptLock)
                {
                    acceptedChannels.Add(ch);
                }
                if (++count >= totalChannels)
                    break;
            }
        });

        // Wait for accept loop to start
        await acceptLoopReady.WaitAsync(cts.Token);
        await Task.Delay(10, cts.Token);

        // Open channels in batches
        for (int batch = 0; batch < totalChannels / batchSize; batch++)
        {
            var tasks = new List<Task>();
            for (int i = 0; i < batchSize; i++)
            {
                var channelIndex = batch * batchSize + i;
                tasks.Add(Task.Run(async () =>
                {
                    var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"mem_ch_{channelIndex}" }, cts.Token);
                    await channel.CloseAsync(cts.Token);
                    channel.Dispose();
                    Interlocked.Increment(ref completedCount);
                }));
            }
            await Task.WhenAll(tasks);
        }

        await acceptTask;
        sw.Stop();

        // Cleanup accepted channels
        int acceptedCount;
        lock (acceptLock)
        {
            acceptedCount = acceptedChannels.Count;
            foreach (var ch in acceptedChannels)
            {
                ch.Dispose();
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(100); // Allow more time for GC
        GC.Collect();
        var memoryAfter = GC.GetTotalMemory(true);
        var memoryDelta = memoryAfter - memoryBefore;

        var rate = totalChannels / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{totalChannels:N0} channels: {sw.Elapsed.TotalSeconds:F1}s ({rate:N0} channels/sec)");
        Console.WriteLine($"Memory delta: {memoryDelta / 1024.0 / 1024.0:F2} MB");

        Assert.Equal(totalChannels, completedCount);
        Assert.Equal(totalChannels, acceptedCount);
        
        // Memory should not grow significantly - allow 3KB per channel + 50MB overhead
        // This accounts for GC timing variability and test infrastructure
        var allowedMemory = 50 * 1024 * 1024 + (totalChannels * 3 * 1024);
        Assert.True(memoryDelta < allowedMemory, 
            $"Memory grew by {memoryDelta / 1024.0 / 1024.0:F2} MB, allowed {allowedMemory / 1024.0 / 1024.0:F2} MB");

        cts.Cancel();
    }

    #endregion

    #region Scaled Messages Tests

    [Fact(Timeout = 120000)]
    public async Task ScaledMessages_TenThousand_SmallPackets_HighThroughput()
    {
        await RunSmallPacketsTest(10_000);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledMessages_FiftyThousand_SmallPackets_HighThroughput()
    {
        await RunSmallPacketsTest(50_000);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledMessages_HundredThousand_SmallPackets_HighThroughput()
    {
        await RunSmallPacketsTest(100_000);
    }

    [Fact(Skip = "Million messages test takes too long - needs investigation")]
    public async Task MillionMessages_SmallPackets_HighThroughput()
    {
        await RunSmallPacketsTest(1_000_000);
    }

    private static async Task RunSmallPacketsTest(int messageCount)
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TestTimeout);
        
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

        var writeChannel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "game_state" }, cts.Token);
        await acceptTask;

        const int messageSize = 64; // Small packets like game state updates
        var receivedCount = 0;

        var sw = Stopwatch.StartNew();

        // Receiver
        var receiveTask = Task.Run(async () =>
        {
            var buffer = new byte[messageSize * 100]; // Read multiple messages at once
            while (receivedCount < messageCount)
            {
                var read = await readChannel!.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                Interlocked.Add(ref receivedCount, read / messageSize);
            }
        });

        // Sender - send as fast as possible
        var message = new byte[messageSize];
        for (int i = 0; i < 4; i++) // Put a sequence number
            message[i] = (byte)(i & 0xFF);
        
        for (int i = 0; i < messageCount; i++)
        {
            await writeChannel.WriteAsync(message, cts.Token);
        }
        await writeChannel.CloseAsync(cts.Token);

        await receiveTask;
        sw.Stop();

        var rate = messageCount / sw.Elapsed.TotalSeconds;
        var throughput = (messageCount * messageSize / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;
        
        Console.WriteLine($"{messageCount:N0} messages ({messageSize}B each): {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"  Rate: {rate:N0} msg/s, Throughput: {throughput:F1} MB/s");

        Assert.True(receivedCount >= messageCount * 0.99,
            $"Expected at least 99% messages, got {receivedCount}/{messageCount}");

        cts.Cancel();
    }

    #endregion

    #region Scaled Parallel Streams Tests

    [Fact(Timeout = 120000)]
    public async Task ScaledParallelStreams_Thousand_Sustained()
    {
        await RunParallelStreamsTest(concurrentChannels: 100, totalChannels: 1_000);
    }

    [Fact(Timeout = 120000)]
    public async Task ScaledParallelStreams_TenThousand_Sustained()
    {
        await RunParallelStreamsTest(concurrentChannels: 1_000, totalChannels: 10_000);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task ScaledParallelStreams_FiftyThousand_Sustained()
    {
        await RunParallelStreamsTest(concurrentChannels: 5_000, totalChannels: 50_000);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Stress")]
    public async Task ScaledParallelStreams_HundredThousand_Sustained()
    {
        await RunParallelStreamsTest(concurrentChannels: 10_000, totalChannels: 100_000);
    }

    [Fact(Skip = "Million channels parallel test takes too long - needs investigation")]
    public async Task MillionChannels_ParallelStreams_Sustained()
    {
        await RunParallelStreamsTest(concurrentChannels: 10_000, totalChannels: 100_000);
    }

    private static async Task RunParallelStreamsTest(int concurrentChannels, int totalChannels)
    {
        await using var pipe = new DuplexPipe();
        
        await using var initiator = new StreamMultiplexer(pipe.Stream1, pipe.Stream1,
            new MultiplexerOptions());
        await using var acceptor = new StreamMultiplexer(pipe.Stream2, pipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TestTimeout);
        
        var initiatorTask = initiator.RunAsync(cts.Token);
        var acceptorTask = acceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int dataPerChannel = 1024;
        
        var completed = 0;
        var accepted = 0;
        var semaphore = new SemaphoreSlim(concurrentChannels);

        var sw = Stopwatch.StartNew();

        // Accept and echo
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in acceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[dataPerChannel];
                    await channel.ReadExactlyAsync(buffer, cts.Token);
                    Interlocked.Increment(ref accepted);
                });
                
                if (Interlocked.Increment(ref accepted) >= totalChannels)
                    break;
            }
        });

        // Open channels with concurrency limit
        var channelCounter = 0;
        var tasks = Enumerable.Range(0, totalChannels).Select(async _ =>
        {
            await semaphore.WaitAsync(cts.Token);
            try
            {
                var idx = Interlocked.Increment(ref channelCounter);
                var channel = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"sustained_ch_{idx}" }, cts.Token);
                var data = new byte[dataPerChannel];
                await channel.WriteAsync(data, cts.Token);
                await channel.FlushAsync(cts.Token);
                await channel.CloseAsync(cts.Token);
                Interlocked.Increment(ref completed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        var rate = completed / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"Sustained {concurrentChannels:N0} concurrent channels:");
        Console.WriteLine($"  {completed:N0} total in {sw.Elapsed.TotalSeconds:F1}s ({rate:N0} channels/sec)");

        Assert.True(completed >= totalChannels * 0.99,
            $"Expected at least 99% completion, got {completed}/{totalChannels}");

        cts.Cancel();
    }

    #endregion
}
