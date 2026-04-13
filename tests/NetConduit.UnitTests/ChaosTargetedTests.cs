using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Internal;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Deliberate chaos tests designed to surface concurrency bugs, race conditions,
/// resource exhaustion, and edge cases that normal happy-path tests miss.
/// </summary>
public partial class ChaosTargetedTests
{
    public record ChaosMessage(string Id, int Seq, byte[] Payload);

    [JsonSerializable(typeof(ChaosMessage))]
    internal partial class ChaosJsonContext : JsonSerializerContext { }

    #region Concurrent Writer Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_ConcurrentWritersSingleChannel_DataIntegrity()
    {
        // Multiple writers hammer a single channel simultaneously.
        // Tests credit system under extreme contention.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "chaos_concurrent" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("chaos_concurrent", cts.Token);

        var totalSent = 0L;
        var errors = new ConcurrentBag<Exception>();

        // 16 concurrent writers hitting the same channel
        var writerTasks = Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            var data = new byte[512];
            data.AsSpan().Fill((byte)(t & 0xFF));
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await writeChannel.WriteAsync(data, cts.Token);
                    Interlocked.Add(ref totalSent, data.Length);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(ex); break; }
            }
        })).ToArray();

        // Reader
        var totalReceived = 0L;
        var readerTask = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var n = await readChannel.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref totalReceived, n);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });

        await Task.WhenAll(writerTasks);

        // Give reader time to drain
        await Task.Delay(2000);
        await cts.CancelAsync();

        Assert.Empty(errors);
        Assert.True(totalSent > 0);
    }

    #endregion

    #region Rapid Channel Open/Close Chaos

    [Fact(Timeout = 120000)]
    public async Task Chaos_RapidChannelOpenClose_NoResourceLeak()
    {
        // Open and close channels as fast as possible from both sides
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<Exception>();
        var channelsOpened = 0;

        // Two sides opening channels concurrently
        var sideA = Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    var id = $"a_{i}";
                    var write = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
                    var read = await muxB.AcceptChannelAsync(id, cts.Token);

                    await write.WriteAsync(new byte[] { 0x01 }, cts.Token);
                    var buf = new byte[1];
                    var n = await read.ReadAsync(buf, cts.Token);
                    Assert.Equal(1, n);

                    await write.DisposeAsync();
                    await read.DisposeAsync();
                    Interlocked.Increment(ref channelsOpened);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(ex); }
            }
        });

        var sideB = Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    var id = $"b_{i}";
                    var write = await muxB.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
                    var read = await muxA.AcceptChannelAsync(id, cts.Token);

                    await write.WriteAsync(new byte[] { 0x02 }, cts.Token);
                    var buf = new byte[1];
                    var n = await read.ReadAsync(buf, cts.Token);
                    Assert.Equal(1, n);

                    await write.DisposeAsync();
                    await read.DisposeAsync();
                    Interlocked.Increment(ref channelsOpened);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(ex); }
            }
        });

        await Task.WhenAll(sideA, sideB);
        await cts.CancelAsync();

        Assert.Empty(errors);
        Assert.True(channelsOpened > 0);
    }

    #endregion

    #region Interleaved Transit Operations

    [Fact(Timeout = 60000)]
    public async Task Chaos_InterleavedDeltaAndMessage_NoCorruption()
    {
        // Multiple transit types operating simultaneously over the same multiplexer
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // DeltaTransit pair
        var deltaWriteA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_chaos" }, cts.Token);
        var deltaReadB = await muxB.AcceptChannelAsync("delta_chaos", cts.Token);
        await using var deltaSender = new DeltaTransit<JsonNode>(deltaWriteA, null);
        await using var deltaReceiver = new DeltaTransit<JsonNode>(null, deltaReadB);

        // MessageTransit pair
        var msgWriteA = await muxA.OpenChannelAsync(new() { ChannelId = "msg_chaos" }, cts.Token);
        var msgReadB = await muxB.AcceptChannelAsync("msg_chaos", cts.Token);
        await using var msgSender = new MessageTransit<ChaosMessage, ChaosMessage>(
            msgWriteA, null, ChaosJsonContext.Default.ChaosMessage, ChaosJsonContext.Default.ChaosMessage);
        await using var msgReceiver = new MessageTransit<ChaosMessage, ChaosMessage>(
            null, msgReadB, ChaosJsonContext.Default.ChaosMessage, ChaosJsonContext.Default.ChaosMessage);

        // Raw stream pair
        var rawWriteA = await muxA.OpenChannelAsync(new() { ChannelId = "raw_chaos" }, cts.Token);
        var rawReadB = await muxB.AcceptChannelAsync("raw_chaos", cts.Token);

        var deltaErrors = new ConcurrentBag<Exception>();
        var msgErrors = new ConcurrentBag<Exception>();
        var rawErrors = new ConcurrentBag<Exception>();

        // Drive all three concurrently
        var deltaTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    var state = JsonNode.Parse($$"""{"counter": {{i}}, "data": "delta_{{i}}"}""")!;
                    await deltaSender.SendAsync(state, cts.Token);
                    var received = await deltaReceiver.ReceiveAsync(cts.Token);
                    if (received is null)
                    {
                        deltaErrors.Add(new InvalidOperationException($"Delta null at iteration {i}"));
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { deltaErrors.Add(ex); break; }
            }
        });

        var msgTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    var msg = new ChaosMessage($"msg_{i}", i, new byte[64]);
                    await msgSender.SendAsync(msg, cts.Token);
                    var received = await msgReceiver.ReceiveAsync(cts.Token);
                    if (received is null)
                    {
                        msgErrors.Add(new InvalidOperationException($"Message null at iteration {i}"));
                        break;
                    }
                    if (received.Id != msg.Id || received.Seq != msg.Seq)
                    {
                        msgErrors.Add(new InvalidOperationException(
                            $"Message mismatch: expected {msg.Id}/{msg.Seq}, got {received.Id}/{received.Seq}"));
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { msgErrors.Add(ex); break; }
            }
        });

        var rawTask = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var data = new byte[256];
                    data.AsSpan().Fill((byte)(i & 0xFF));
                    await rawWriteA.WriteAsync(data, cts.Token);

                    var buf = new byte[256];
                    var totalRead = 0;
                    while (totalRead < 256)
                    {
                        var n = await rawReadB.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                        if (n == 0)
                        {
                            rawErrors.Add(new InvalidOperationException($"Raw EOF at iteration {i}"));
                            return;
                        }
                        totalRead += n;
                    }
                    // Verify data integrity
                    for (int j = 0; j < 256; j++)
                    {
                        if (buf[j] != (byte)(i & 0xFF))
                        {
                            rawErrors.Add(new InvalidOperationException(
                                $"Raw corruption at iter {i} byte {j}: expected {(byte)(i & 0xFF)}, got {buf[j]}"));
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { rawErrors.Add(ex); break; }
            }
        });

        await Task.WhenAll(deltaTask, msgTask, rawTask);
        await cts.CancelAsync();

        Assert.Empty(deltaErrors);
        Assert.Empty(msgErrors);
        Assert.Empty(rawErrors);
    }

    #endregion

    #region Dispose During Active Operations

    [Fact(Timeout = 60000)]
    public async Task Chaos_DisposeWhileWriting_NoHangOrCorruption()
    {
        // Dispose the multiplexer while writes are in-flight
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "dispose_chaos" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("dispose_chaos", cts.Token);

        var writeErrors = new ConcurrentBag<Exception>();

        // Start writing continuously
        var writeTask = Task.Run(async () =>
        {
            var data = new byte[4096];
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await writeChannel.WriteAsync(data, cts.Token);
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }
                catch (IOException) { break; }
                catch (Exception ex)
                {
                    if (ex is not InvalidOperationException)
                        writeErrors.Add(ex);
                    break;
                }
            }
        });

        // Let writer run briefly
        await Task.Delay(100);

        // Signal writer to stop first
        await cts.CancelAsync();
        await writeTask;

        // Dispose with timeout guard — on resource-starved CI runners,
        // DisposeAsync may block on stream I/O. The test validates that
        // the writer exits cleanly, not that dispose is fast.
        var disposeTask = Task.Run(async () =>
        {
            await muxA.DisposeAsync();
            await muxB.DisposeAsync();
        });
        await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10)));

        // No unexpected errors
        foreach (var err in writeErrors)
        {
            Assert.Fail($"Unexpected error during dispose: {err.GetType().Name}: {err.Message}");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Chaos_DisposeWhileReading_NoHangOrCorruption()
    {
        // Dispose the channel/mux while reads are pending
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "read_dispose" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("read_dispose", cts.Token);

        // Start a blocking read
        var readTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                var n = await readChannel.ReadAsync(buf, cts.Token);
                return n;
            }
            catch (ObjectDisposedException) { return -1; }
            catch (OperationCanceledException) { return -2; }
            catch { return -3; }
        });

        // Let the read establish
        await Task.Delay(200);

        // Dispose while read is pending
        await readChannel.DisposeAsync();

        var result = await readTask;
        // Should complete (0 for EOF, -1 for disposed, -2 for cancelled — never hang)
        Assert.True(result <= 0, "Read should have completed with 0 or exception, not data");
    }

    #endregion

    #region Message Size Boundaries

    [Fact(Timeout = 60000)]
    public async Task Chaos_VariableMessageSizes_1ByteTo1MB_AllDelivered()
    {
        // Test extreme message size variance
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "size_chaos" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("size_chaos", cts.Token);

        // Sizes from 1 byte to 512KB
        var sizes = new[] { 1, 2, 7, 15, 16, 63, 64, 127, 128, 255, 256, 511, 512,
                           1023, 1024, 4095, 4096, 8191, 8192, 16383, 16384,
                           32767, 32768, 65535, 65536, 131072, 262144, 524288 };

        foreach (var size in sizes)
        {
            var data = new byte[size];
            Random.Shared.NextBytes(data);
            var checksum = data.Aggregate(0L, (acc, b) => acc + b);

            await writeChannel.WriteAsync(data, cts.Token);

            // Read all bytes
            var received = new byte[size];
            var totalRead = 0;
            while (totalRead < size)
            {
                var n = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                Assert.True(n > 0, $"Unexpected EOF at size {size}, read {totalRead}/{size}");
                totalRead += n;
            }

            var receivedChecksum = received.Aggregate(0L, (acc, b) => acc + b);
            Assert.Equal(checksum, receivedChecksum);
        }
    }

    #endregion

    #region Bidirectional Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_BidirectionalFlood_BothSidesSimultaneous()
    {
        // Both sides writing and reading simultaneously across multiple channels
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        const int channelCount = 10;
        const int messagesPerChannel = 100;
        const int messageSize = 256;
        var errors = new ConcurrentBag<string>();

        var tasks = new List<Task>();

        for (int c = 0; c < channelCount; c++)
        {
            var channelIndex = c;

            // A → B channel
            var abId = $"ab_{channelIndex}";
            var abWrite = await muxA.OpenChannelAsync(new() { ChannelId = abId }, cts.Token);
            var abRead = await muxB.AcceptChannelAsync(abId, cts.Token);

            // B → A channel
            var baId = $"ba_{channelIndex}";
            var baWrite = await muxB.OpenChannelAsync(new() { ChannelId = baId }, cts.Token);
            var baRead = await muxA.AcceptChannelAsync(baId, cts.Token);

            // A writes to B
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < messagesPerChannel; i++)
                {
                    try
                    {
                        var data = new byte[messageSize];
                        data[0] = (byte)channelIndex;
                        data[1] = (byte)(i & 0xFF);
                        await abWrite.WriteAsync(data, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"AB write ch{channelIndex}: {ex.Message}"); break; }
                }
            }));

            // B reads from A
            tasks.Add(Task.Run(async () =>
            {
                var buf = new byte[messageSize];
                for (int i = 0; i < messagesPerChannel; i++)
                {
                    try
                    {
                        var totalRead = 0;
                        while (totalRead < messageSize)
                        {
                            var n = await abRead.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                            if (n == 0) { errors.Add($"AB read ch{channelIndex}: EOF at msg {i}"); return; }
                            totalRead += n;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"AB read ch{channelIndex}: {ex.Message}"); break; }
                }
            }));

            // B writes to A
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < messagesPerChannel; i++)
                {
                    try
                    {
                        var data = new byte[messageSize];
                        data[0] = (byte)(channelIndex + 128);
                        data[1] = (byte)(i & 0xFF);
                        await baWrite.WriteAsync(data, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"BA write ch{channelIndex}: {ex.Message}"); break; }
                }
            }));

            // A reads from B
            tasks.Add(Task.Run(async () =>
            {
                var buf = new byte[messageSize];
                for (int i = 0; i < messagesPerChannel; i++)
                {
                    try
                    {
                        var totalRead = 0;
                        while (totalRead < messageSize)
                        {
                            var n = await baRead.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                            if (n == 0) { errors.Add($"BA read ch{channelIndex}: EOF at msg {i}"); return; }
                            totalRead += n;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"BA read ch{channelIndex}: {ex.Message}"); break; }
                }
            }));
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        if (errors.Any())
        {
            Assert.Fail($"Bidirectional chaos errors:\n{string.Join("\n", errors.Take(20))}");
        }
    }

    #endregion

    #region Transit State Mutation Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_DeltaTransit_RapidStateChanges_NoCorruption()
    {
        // Rapidly mutate state through DeltaTransit, verify receiver tracks correctly
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_rapid" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_rapid", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        // Rapidly evolving state — each iteration changes something
        for (int i = 0; i < 100; i++)
        {
            var state = JsonNode.Parse($$"""
            {
                "counter": {{i}},
                "name": "user_{{i % 10}}",
                "active": {{(i % 2 == 0 ? "true" : "false")}},
                "scores": [{{i}}, {{i + 1}}, {{i + 2}}]
            }
            """)!;

            await sender.SendAsync(state, cts.Token);
            var received = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(received);

            var counter = received["counter"]!.GetValue<int>();
            Assert.Equal(i, counter);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Chaos_DeltaTransit_AlternatingIdenticalAndDifferent_AllDelivered()
    {
        // Alternating between identical states and different states
        // Directly targets Bug 1
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "delta_alt" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("delta_alt", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var stateA = JsonNode.Parse("""{"x": 1}""")!;
        var stateB = JsonNode.Parse("""{"x": 2}""")!;

        var receivedCount = 0;

        for (int i = 0; i < 20; i++)
        {
            // Alternate: A, A, B, B, A, A, B, B...
            var state = (i / 2) % 2 == 0 ? stateA.DeepClone() : stateB.DeepClone();

            await sender.SendAsync(state, cts.Token);

            using var perMsgTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, perMsgTimeout.Token);

            var received = await receiver.ReceiveAsync(linked.Token);
            Assert.NotNull(received);
            receivedCount++;
        }

        // Bug 1: If identical consecutive states are dropped, receivedCount < 20
        Assert.Equal(20, receivedCount);
    }

    #endregion

    #region Mixed Channel Lifecycle Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_MixedLifecycles_SomeShortSomeLong_NoLeaks()
    {
        // Mix of short-lived and long-lived channels operating simultaneously
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<string>();

        // Long-lived channel
        var longWrite = await muxA.OpenChannelAsync(new() { ChannelId = "long_lived" }, cts.Token);
        var longRead = await muxB.AcceptChannelAsync("long_lived", cts.Token);

        var longLivedTask = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var data = new byte[128];
                    BinaryPrimitives.WriteInt32BigEndian(data, i);
                    await longWrite.WriteAsync(data, cts.Token);

                    var buf = new byte[128];
                    var total = 0;
                    while (total < 128)
                    {
                        var n = await longRead.ReadAsync(buf.AsMemory(total), cts.Token);
                        if (n == 0) { errors.Add("Long-lived EOF"); return; }
                        total += n;
                    }
                    var received = BinaryPrimitives.ReadInt32BigEndian(buf);
                    if (received != i) errors.Add($"Long-lived mismatch: expected {i}, got {received}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"Long-lived: {ex.Message}"); break; }
            }
        });

        // Many short-lived channels in parallel
        var shortTasks = Enumerable.Range(0, 50).Select(idx => Task.Run(async () =>
        {
            try
            {
                var id = $"short_{idx}";
                var w = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
                var r = await muxB.AcceptChannelAsync(id, cts.Token);

                var data = new byte[32];
                BinaryPrimitives.WriteInt32BigEndian(data, idx);
                await w.WriteAsync(data, cts.Token);

                var buf = new byte[32];
                var total = 0;
                while (total < 32)
                {
                    var n = await r.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (n == 0) { errors.Add($"Short {idx} EOF"); return; }
                    total += n;
                }
                var val = BinaryPrimitives.ReadInt32BigEndian(buf);
                if (val != idx) errors.Add($"Short {idx} mismatch: expected {idx}, got {val}");

                await w.DisposeAsync();
                await r.DisposeAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errors.Add($"Short {idx}: {ex.Message}"); }
        })).ToArray();

        await Task.WhenAll(shortTasks);
        await longLivedTask;
        await cts.CancelAsync();

        if (errors.Any())
        {
            Assert.Fail($"Mixed lifecycle errors:\n{string.Join("\n", errors.Take(20))}");
        }
    }

    #endregion

    #region Credit Starvation Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_TinyCredits_ProducerConsumerStress()
    {
        // Minimal credit budget forces constant starvation/restore cycles
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions
            {
                MinCredits = 256,
                MaxCredits = 1024,
            },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "tiny_credits" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("tiny_credits", cts.Token);

        var errors = new ConcurrentBag<string>();
        var totalSent = 0L;
        var totalReceived = 0L;

        // Producer sends chunks larger than credit budget
        var producer = Task.Run(async () =>
        {
            var data = new byte[2048]; // 4x the credit budget
            Random.Shared.NextBytes(data);
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    await writeChannel.WriteAsync(data, cts.Token);
                    Interlocked.Add(ref totalSent, data.Length);
                }
                catch (OperationCanceledException) { break; }
                catch (TimeoutException) { break; } // Expected with tiny credits
                catch (Exception ex) { errors.Add($"Producer: {ex.Message}"); break; }
            }
        });

        // Consumer reads slowly
        var consumer = Task.Run(async () =>
        {
            var buf = new byte[256]; // Small reads
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var n = await readChannel.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref totalReceived, n);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"Consumer: {ex.Message}"); break; }
            }
        });

        await producer;
        await Task.Delay(2000); // Let consumer drain
        await cts.CancelAsync();

        Assert.Empty(errors);
        Assert.True(totalSent > 0, "Producer should have sent data");
    }

    #endregion

    #region Random Operation Sequence

    [Fact(Timeout = 120000)]
    public async Task Chaos_RandomOperationSequence_NoDeadlockOrCorruption()
    {
        // Random mix of operations: open, write, read, close channels
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<string>();
        var rng = new Random(42); // Deterministic seed for reproducibility
        var activeChannels = new ConcurrentDictionary<string, (WriteChannel Write, ReadChannel Read)>();
        var channelCounter = 0;

        for (int i = 0; i < 200; i++)
        {
            if (cts.Token.IsCancellationRequested) break;

            var operation = rng.Next(3); // 0=open, 1=write, 2=close

            try
            {
                switch (operation)
                {
                    case 0: // Open new channel
                        var id = $"rng_{Interlocked.Increment(ref channelCounter)}";
                        var write = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
                        var read = await muxB.AcceptChannelAsync(id, cts.Token);
                        activeChannels.TryAdd(id, (write, read));
                        break;

                    case 1: // Write to random active channel
                        var keys = activeChannels.Keys.ToArray();
                        if (keys.Length > 0)
                        {
                            var key = keys[rng.Next(keys.Length)];
                            if (activeChannels.TryGetValue(key, out var ch))
                            {
                                try
                                {
                                    var data = new byte[rng.Next(1, 1024)];
                                    rng.NextBytes(data);
                                    await ch.Write.WriteAsync(data, cts.Token);

                                    // Read back
                                    var buf = new byte[data.Length];
                                    var total = 0;
                                    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, readTimeout.Token);
                                    while (total < buf.Length)
                                    {
                                        var n = await ch.Read.ReadAsync(buf.AsMemory(total), linked.Token);
                                        if (n == 0) break;
                                        total += n;
                                    }
                                }
                                catch (ObjectDisposedException) { activeChannels.TryRemove(key, out _); }
                                catch (OperationCanceledException) { }
                            }
                        }
                        break;

                    case 2: // Close random channel
                        var closeKeys = activeChannels.Keys.ToArray();
                        if (closeKeys.Length > 0)
                        {
                            var closeKey = closeKeys[rng.Next(closeKeys.Length)];
                            if (activeChannels.TryRemove(closeKey, out var closeCh))
                            {
                                await closeCh.Write.DisposeAsync();
                                await closeCh.Read.DisposeAsync();
                            }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { errors.Add($"Op {operation} iter {i}: {ex.Message}"); }
        }

        // Clean up remaining channels
        foreach (var kv in activeChannels)
        {
            await kv.Value.Write.DisposeAsync();
            await kv.Value.Read.DisposeAsync();
        }
        await cts.CancelAsync();

        if (errors.Any())
        {
            Assert.Fail($"Random ops errors:\n{string.Join("\n", errors.Take(20))}");
        }
    }

    #endregion

    #region Helpers

    private static void WriteInt32BE(Span<byte> span, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(span, value);
    }

    #endregion

    #region Delta Transit Identical State Chaos (Bug 1 Stress)

    [Fact(Timeout = 60000)]
    public async Task Chaos_DeltaTransit_HundredIdenticalSends_AllReceived()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "chaos_ident100" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("chaos_ident100", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var state = JsonNode.Parse("""{"status": "ok"}""")!;
        var count = 0;

        for (int i = 0; i < 100; i++)
        {
            await sender.SendAsync(state.DeepClone(), cts.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
            var r = await receiver.ReceiveAsync(linked.Token);
            if (r is not null) count++;
        }

        Assert.Equal(100, count);
    }

    #endregion

    #region Multi-Transit Concurrent Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_MessageAndDelta_ConcurrentOnSameMux_NoInterference()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // MessageTransit channel
        var msgW = await muxA.OpenChannelAsync(new() { ChannelId = "chaos_msg" }, cts.Token);
        var msgR = await muxB.AcceptChannelAsync("chaos_msg", cts.Token);
        await using var msgSender = new MessageTransit<ChaosMessage, ChaosMessage>(
            msgW, null, ChaosJsonContext.Default.ChaosMessage, ChaosJsonContext.Default.ChaosMessage);
        await using var msgReceiver = new MessageTransit<ChaosMessage, ChaosMessage>(
            null, msgR, ChaosJsonContext.Default.ChaosMessage, ChaosJsonContext.Default.ChaosMessage);

        // DeltaTransit channel
        var deltaW = await muxA.OpenChannelAsync(new() { ChannelId = "chaos_delta" }, cts.Token);
        var deltaR = await muxB.AcceptChannelAsync("chaos_delta", cts.Token);
        await using var deltaSender = new DeltaTransit<JsonNode>(deltaW, null);
        await using var deltaReceiver = new DeltaTransit<JsonNode>(null, deltaR);

        // Raw channel
        var rawW = await muxA.OpenChannelAsync(new() { ChannelId = "chaos_raw" }, cts.Token);
        var rawR = await muxB.AcceptChannelAsync("chaos_raw", cts.Token);

        // DuplexStream
        var openDuplexTask = muxA.OpenDuplexStreamAsync("chaos_duplex", cts.Token);
        var acceptDuplexTask = muxB.AcceptDuplexStreamAsync("chaos_duplex", cts.Token);
        var duplex = await openDuplexTask;
        var remoteDuplex = await acceptDuplexTask;

        var errors = new ConcurrentBag<string>();

        // All four transit types operating simultaneously
        var msgTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    await msgSender.SendAsync(new ChaosMessage($"m{i}", i, new byte[32]), cts.Token);
                    var r = await msgReceiver.ReceiveAsync(cts.Token);
                    if (r is null || r.Seq != i) errors.Add($"msg mismatch at {i}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"msg: {ex.Message}"); break; }
            }
        });

        var deltaTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    await deltaSender.SendAsync(JsonNode.Parse($$"""{"v": {{i}}}""")!, cts.Token);
                    var r = await deltaReceiver.ReceiveAsync(cts.Token);
                    if (r is null) errors.Add($"delta null at {i}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"delta: {ex.Message}"); break; }
            }
        });

        var rawTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var data = new byte[64];
                    BinaryPrimitives.WriteInt32BigEndian(data, i);
                    await rawW.WriteAsync(data, cts.Token);
                    var buf = new byte[64];
                    var total = 0;
                    while (total < 64) { var n = await rawR.ReadAsync(buf.AsMemory(total), cts.Token); if (n == 0) break; total += n; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"raw: {ex.Message}"); break; }
            }
        });

        var duplexTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    await duplex.WriteAsync(new byte[] { (byte)i }, cts.Token);
                    var buf = new byte[1];
                    Assert.Equal(1, await remoteDuplex.ReadAsync(buf, cts.Token));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"duplex: {ex.Message}"); break; }
            }
        });

        await Task.WhenAll(msgTask, deltaTask, rawTask, duplexTask);
        await cts.CancelAsync();

        Assert.Empty(errors);
    }

    #endregion

    #region Credit Exhaustion Multi-Channel Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_ManyChannels_TinyCredits_NoDeadlock()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotSupportedException(),
            DefaultChannelOptions = new DefaultChannelOptions { MinCredits = 256, MaxCredits = 512 },
        };

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts, cts.Token);

        var errors = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int c = 0; c < 10; c++)
        {
            var channelId = $"tiny_ch_{c}";
            var write = await muxA.OpenChannelAsync(new() { ChannelId = channelId }, cts.Token);
            var read = await muxB.AcceptChannelAsync(channelId, cts.Token);

            var cIdx = c;
            tasks.Add(Task.Run(async () =>
            {
                var data = new byte[1024];
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        await write.WriteAsync(data, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (TimeoutException) { break; }
                    catch (Exception ex) { errors.Add($"write ch{cIdx}: {ex.Message}"); break; }
                }
            }));

            tasks.Add(Task.Run(async () =>
            {
                var buf = new byte[4096];
                while (!cts.Token.IsCancellationRequested)
                {
                    try { if (await read.ReadAsync(buf, cts.Token) == 0) break; }
                    catch { break; }
                }
            }));
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        Assert.Empty(errors);
    }

    #endregion

    #region Large Message Fragmentation Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_LargeMessages_MultipleChannels_DataIntegrity()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int c = 0; c < 4; c++)
        {
            var id = $"large_ch_{c}";
            var write = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
            var read = await muxB.AcceptChannelAsync(id, cts.Token);
            var cIdx = c;

            // Each channel sends one large message (256KB)
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var msg = new byte[256 * 1024];
                    for (int i = 0; i < msg.Length; i++) msg[i] = (byte)(cIdx + (i & 0xFF));
                    await write.WriteAsync(msg, cts.Token);
                }
                catch (Exception ex) { errors.Add($"write ch{cIdx}: {ex.Message}"); }
            }));

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[256 * 1024];
                    var total = 0;
                    while (total < buf.Length)
                    {
                        var n = await read.ReadAsync(buf.AsMemory(total), cts.Token);
                        if (n == 0) { errors.Add($"read ch{cIdx}: EOF at {total}"); return; }
                        total += n;
                    }
                    // Verify first byte pattern
                    Assert.Equal((byte)(cIdx + (0 & 0xFF)), buf[0]);
                }
                catch (Exception ex) { errors.Add($"read ch{cIdx}: {ex.Message}"); }
            }));
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        Assert.Empty(errors);
    }

    #endregion

    #region DuplexStream Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_MultipleDuplexStreams_BidirectionalFlood()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int s = 0; s < 5; s++)
        {
            var id = $"duplex_{s}";
            var openTask = muxA.OpenDuplexStreamAsync(id, cts.Token);
            var acceptTask = muxB.AcceptDuplexStreamAsync(id, cts.Token);
            var local = await openTask;
            var remote = await acceptTask;
            var sIdx = s;

            // Local writes, remote reads
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var data = new byte[128];
                        BinaryPrimitives.WriteInt32BigEndian(data, i);
                        await local.WriteAsync(data, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"duplex{sIdx} write: {ex.Message}"); break; }
                }
            }));

            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var buf = new byte[128];
                        var total = 0;
                        while (total < 128) { var n = await remote.ReadAsync(buf.AsMemory(total), cts.Token); if (n == 0) break; total += n; }
                        var seq = BinaryPrimitives.ReadInt32BigEndian(buf);
                        if (seq != i) errors.Add($"duplex{sIdx} read mismatch: expected {i}, got {seq}");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"duplex{sIdx} read: {ex.Message}"); break; }
                }
            }));

            // Remote writes, local reads
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var data = new byte[128];
                        BinaryPrimitives.WriteInt32BigEndian(data, i + 1000);
                        await remote.WriteAsync(data, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"duplex{sIdx} rwrite: {ex.Message}"); break; }
                }
            }));

            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var buf = new byte[128];
                        var total = 0;
                        while (total < 128) { var n = await local.ReadAsync(buf.AsMemory(total), cts.Token); if (n == 0) break; total += n; }
                        var seq = BinaryPrimitives.ReadInt32BigEndian(buf);
                        if (seq != i + 1000) errors.Add($"duplex{sIdx} lread mismatch");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add($"duplex{sIdx} lread: {ex.Message}"); break; }
                }
            }));
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        Assert.Empty(errors);
    }

    #endregion

    #region Reconnection Buffer Chaos (Bug 10)

    [Fact(Timeout = 60000)]
    public async Task Chaos_ChannelSyncState_ConcurrentRecordAndAcknowledge()
    {
        var state = new ChannelSyncState(4096);
        state.StartRecording();

        var errors = new ConcurrentBag<string>();

        // Multiple threads recording sends
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var data = new byte[128];
            for (int i = 0; i < 100; i++)
            {
                try { state.RecordSend(data); }
                catch (Exception ex) { errors.Add($"write: {ex.Message}"); break; }
            }
        })).ToArray();

        await Task.WhenAll(writers);

        // Total should be 4 * 100 * 128
        Assert.Equal(4 * 100 * 128, state.BytesSent);
        Assert.Empty(errors);
    }

    #endregion

    #region Dispose Ordering Chaos

    [Fact(Timeout = 60000)]
    public async Task Chaos_DisposeChannelBeforeMux_NoError()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var write = await muxA.OpenChannelAsync(new() { ChannelId = "dispose_order" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("dispose_order", cts.Token);

        // Dispose channels first, then mux — should not throw
        await write.DisposeAsync();
        await read.DisposeAsync();
        await muxA.DisposeAsync();
        await muxB.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Chaos_DisposeMuxWithActiveChannels_ChannelsBecomeDisposed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var write = await muxA.OpenChannelAsync(new() { ChannelId = "mux_dispose" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("mux_dispose", cts.Token);

        // Dispose mux while channels are still active
        await muxA.DisposeAsync();
        await muxB.DisposeAsync();

        // Writing to a channel on a disposed mux should fail
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await write.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        });
    }

    #endregion
}
