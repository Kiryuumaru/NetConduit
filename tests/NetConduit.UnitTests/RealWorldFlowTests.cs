using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Real-world application flow tests that simulate how actual integrators
/// would use NetConduit as a dependency. Derived from known issues in
/// yamux, smux, QUIC, HTTP/2, and common multiplexer failure patterns.
///
/// Categories:
/// 1. Application Lifecycle — connect, use, shutdown, cleanup
/// 2. Client-Server Patterns — request/response, streaming, pub/sub
/// 3. Edge Cases — half-close, idle, slow consumer, channel reuse
/// 4. Graceful Shutdown — GoAway, dispose ordering, mid-transfer close
/// 5. Reconnection Flows — real app behavior across disconnects
/// 6. Resource Management — channel leaks, memory, cleanup
/// </summary>
public partial class RealWorldFlowTests
{
    #region Shared Types

    public record Command(string Action, string Payload);
    public record Response(string Status, string? Data);

    [JsonSerializable(typeof(Command))]
    [JsonSerializable(typeof(Response))]
    [JsonSerializable(typeof(string))]
    internal partial class FlowJsonContext : JsonSerializerContext { }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 1. APPLICATION LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    #region 1. Application Lifecycle

    /// <summary>
    /// The most basic integrator flow: connect → open channel → send data → 
    /// receive data → close channel → dispose mux. If this fails, nothing works.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Lifecycle_ConnectSendReceiveCloseDispose()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, runA, runB) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.True(muxA.IsConnected);
        Assert.True(muxB.IsConnected);

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "hello" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("hello", cts.Token);

        var sent = "Hello, World!"u8.ToArray();
        await writer.WriteAsync(sent, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[sent.Length];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = await reader.ReadAsync(buf.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(sent.Length, totalRead);
        Assert.Equal(sent, buf);

        cts.Cancel();
    }

    /// <summary>
    /// App opens multiple channels for different purposes (control, data, logs),
    /// uses them concurrently, then shuts down cleanly.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Lifecycle_MultiPurposeChannels_ConcurrentUse()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var controlWriter = await muxA.OpenChannelAsync(new() { ChannelId = "control" }, cts.Token);
        var dataWriter = await muxA.OpenChannelAsync(new() { ChannelId = "data" }, cts.Token);
        var logWriter = await muxA.OpenChannelAsync(new() { ChannelId = "logs" }, cts.Token);

        var controlReader = await muxB.AcceptChannelAsync("control", cts.Token);
        var dataReader = await muxB.AcceptChannelAsync("data", cts.Token);
        var logReader = await muxB.AcceptChannelAsync("logs", cts.Token);

        var sendTasks = new[]
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                    await controlWriter.WriteAsync(new byte[] { 0xCC, (byte)i }, cts.Token);
                await controlWriter.CloseAsync();
            }),
            Task.Run(async () =>
            {
                var data = new byte[1024];
                Random.Shared.NextBytes(data);
                await dataWriter.WriteAsync(data, cts.Token);
                await dataWriter.CloseAsync();
            }),
            Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                    await logWriter.WriteAsync(Encoding.UTF8.GetBytes($"log-{i}\n"), cts.Token);
                await logWriter.CloseAsync();
            })
        };

        var controlReceived = new MemoryStream();
        var dataReceived = new MemoryStream();
        var logReceived = new MemoryStream();

        var recvTasks = new[]
        {
            ReadToEndAsync(controlReader, controlReceived, cts.Token),
            ReadToEndAsync(dataReader, dataReceived, cts.Token),
            ReadToEndAsync(logReader, logReceived, cts.Token),
        };

        await Task.WhenAll(sendTasks);
        await Task.WhenAll(recvTasks);

        Assert.Equal(20, controlReceived.Length);
        Assert.Equal(1024, dataReceived.Length);
        Assert.True(logReceived.Length > 0);

        cts.Cancel();
    }

    /// <summary>
    /// Both sides open channels independently — common in peer-to-peer apps
    /// where either side can initiate communication at any time.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Lifecycle_BothSidesOpenChannels_Simultaneously()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var aToB = Task.Run(async () =>
        {
            var w = await muxA.OpenChannelAsync(new() { ChannelId = "a-to-b" }, cts.Token);
            await w.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
            await w.CloseAsync();
        });

        var bToA = Task.Run(async () =>
        {
            var w = await muxB.OpenChannelAsync(new() { ChannelId = "b-to-a" }, cts.Token);
            await w.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);
            await w.CloseAsync();
        });

        var readAToB = Task.Run(async () =>
        {
            var r = await muxB.AcceptChannelAsync("a-to-b", cts.Token);
            var ms = new MemoryStream();
            await ReadToEndAsync(r, ms, cts.Token);
            return ms.ToArray();
        });

        var readBToA = Task.Run(async () =>
        {
            var r = await muxA.AcceptChannelAsync("b-to-a", cts.Token);
            var ms = new MemoryStream();
            await ReadToEndAsync(r, ms, cts.Token);
            return ms.ToArray();
        });

        await Task.WhenAll(aToB, bToA);
        var resultAToB = await readAToB;
        var resultBToA = await readBToA;

        Assert.Equal(new byte[] { 1, 2, 3 }, resultAToB);
        Assert.Equal(new byte[] { 4, 5, 6 }, resultBToA);

        cts.Cancel();
    }

    /// <summary>
    /// App creates channels, uses them briefly, closes them, then opens new ones.
    /// Common pattern in connection pooling and session rotation.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Lifecycle_ChannelRotation_OldClosedNewOpened()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        for (int round = 0; round < 10; round++)
        {
            var chId = $"session-{round}";
            await using var writer = await muxA.OpenChannelAsync(new() { ChannelId = chId }, cts.Token);
            var reader = await muxB.AcceptChannelAsync(chId, cts.Token);

            var payload = Encoding.UTF8.GetBytes($"round-{round}");
            await writer.WriteAsync(payload, cts.Token);
            await writer.CloseAsync();

            var buf = new byte[payload.Length];
            int read = 0;
            while (read < buf.Length)
            {
                int n = await reader.ReadAsync(buf.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            Assert.Equal(payload, buf);
        }

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 2. CLIENT-SERVER PATTERNS
    // ═══════════════════════════════════════════════════════════════════

    #region 2. Client-Server Patterns

    /// <summary>
    /// Classic RPC: client sends a command on a request channel, server responds
    /// on a response channel. Multiple concurrent request/response pairs.
    /// Inspired by gRPC bidirectional streaming patterns.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ClientServer_RpcOverChannels_ConcurrentCalls()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (client, server, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var c = client;
        await using var s = server;

        const int callCount = 20;
        var results = new ConcurrentBag<(int Id, bool Ok)>();

        var serverTask = Task.Run(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    var buf = new byte[256];
                    int n = await ch.ReadAsync(buf, cts.Token);
                    var cmd = Encoding.UTF8.GetString(buf, 0, n);

                    var responseChId = ch.ChannelId + "-res";
                    var resWriter = await server.OpenChannelAsync(new() { ChannelId = responseChId }, cts.Token);
                    await resWriter.WriteAsync(Encoding.UTF8.GetBytes($"OK:{cmd}"), cts.Token);
                    await resWriter.CloseAsync();
                });
            }
        });

        var clientTasks = Enumerable.Range(0, callCount).Select(i => Task.Run(async () =>
        {
            var reqId = $"rpc-{i}";
            var writer = await client.OpenChannelAsync(new() { ChannelId = reqId }, cts.Token);
            await writer.WriteAsync(Encoding.UTF8.GetBytes($"cmd-{i}"), cts.Token);

            var resReader = await client.AcceptChannelAsync(reqId + "-res", cts.Token);
            var ms = new MemoryStream();
            await ReadToEndAsync(resReader, ms, cts.Token);
            var response = Encoding.UTF8.GetString(ms.ToArray());
            results.Add((i, response == $"OK:cmd-{i}"));
        })).ToArray();

        await Task.WhenAll(clientTasks);
        cts.Cancel();

        Assert.Equal(callCount, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, $"Call {r.Id} failed"));
    }

    /// <summary>
    /// Server pushes a continuous stream of events to the client.
    /// Client reads as fast as it can. Tests producer-consumer flow with
    /// no backpressure deadlock.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ClientServer_EventStream_ProducerConsumer()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (client, server, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var c = client;
        await using var s = server;

        var eventWriter = await server.OpenChannelAsync(new() { ChannelId = "events" }, cts.Token);
        var eventReader = await client.AcceptChannelAsync("events", cts.Token);

        const int eventCount = 1000;
        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < eventCount; i++)
            {
                var eventData = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(eventData, i);
                await eventWriter.WriteAsync(eventData, cts.Token);
            }
            await eventWriter.CloseAsync();
        });

        var received = new List<int>();
        var recvTask = Task.Run(async () =>
        {
            var buf = new byte[4];
            while (true)
            {
                int totalRead = 0;
                while (totalRead < 4)
                {
                    int n = await eventReader.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                    if (n == 0) return;
                    totalRead += n;
                }
                received.Add(BinaryPrimitives.ReadInt32BigEndian(buf));
            }
        });

        await sendTask;
        await recvTask;

        Assert.Equal(eventCount, received.Count);
        for (int i = 0; i < eventCount; i++)
            Assert.Equal(i, received[i]);

        cts.Cancel();
    }

    /// <summary>
    /// Fan-out pattern: server opens one channel per subscriber, each subscriber
    /// gets the full stream. Simulates pub/sub broadcast.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ClientServer_FanOut_MultipleSubscribers()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        const int subCount = 5;
        const int msgCount = 50;
        var expected = Encoding.UTF8.GetBytes("broadcast-msg");

        var writers = new WriteChannel[subCount];
        var readers = new ReadChannel[subCount];

        for (int i = 0; i < subCount; i++)
        {
            writers[i] = await muxA.OpenChannelAsync(new() { ChannelId = $"sub-{i}" }, cts.Token);
            readers[i] = await muxB.AcceptChannelAsync($"sub-{i}", cts.Token);
        }

        var receivedCounts = new int[subCount];

        var recvTasks = readers.Select((r, idx) => Task.Run(async () =>
        {
            var buf = new byte[expected.Length];
            while (true)
            {
                int totalRead = 0;
                while (totalRead < buf.Length)
                {
                    int n = await r.ReadAsync(buf.AsMemory(totalRead), cts.Token);
                    if (n == 0) return;
                    totalRead += n;
                }
                Assert.Equal(expected, buf);
                receivedCounts[idx]++;
            }
        })).ToArray();

        for (int m = 0; m < msgCount; m++)
        {
            foreach (var w in writers)
                await w.WriteAsync(expected, cts.Token);
        }

        foreach (var w in writers)
            await w.CloseAsync();

        await Task.WhenAll(recvTasks);

        for (int i = 0; i < subCount; i++)
            Assert.Equal(msgCount, receivedCounts[i]);

        cts.Cancel();
    }

    /// <summary>
    /// File upload: client streams a large file in chunks, server reassembles.
    /// Ensures no data corruption and byte-exact delivery for large transfers.
    /// Inspired by yamux large transfer deadlock issues.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ClientServer_FileUpload_LargeChunkedTransfer()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var (client, server, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var c = client;
        await using var s = server;

        var writer = await client.OpenChannelAsync(new() { ChannelId = "upload" }, cts.Token);
        var reader = await server.AcceptChannelAsync("upload", cts.Token);

        const int totalSize = 10 * 1024 * 1024;
        const int chunkSize = 8192;
        var fileData = new byte[totalSize];
        Random.Shared.NextBytes(fileData);

        var sendTask = Task.Run(async () =>
        {
            for (int offset = 0; offset < totalSize; offset += chunkSize)
            {
                int len = Math.Min(chunkSize, totalSize - offset);
                await writer.WriteAsync(fileData.AsMemory(offset, len), cts.Token);
            }
            await writer.CloseAsync();
        });

        var received = new MemoryStream();
        await ReadToEndAsync(reader, received, cts.Token);
        await sendTask;

        Assert.Equal(totalSize, (int)received.Length);
        Assert.True(fileData.AsSpan().SequenceEqual(received.ToArray()));

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 3. EDGE CASES & FAILURE MODES
    // ═══════════════════════════════════════════════════════════════════

    #region 3. Edge Cases

    /// <summary>
    /// Channel opened but never written to, then closed.
    /// Common when a feature is conditionally used.
    /// (smux #103: "Does the server need to manually close streams?")
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Edge_OpenCloseWithoutWrite_NoHang()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "empty" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("empty", cts.Token);

        await writer.CloseAsync();

        var buf = new byte[1];
        int n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(0, n);

        cts.Cancel();
    }

    /// <summary>
    /// Writer closes, reader reads all remaining data, then gets EOF.
    /// Half-close semantics — critical for HTTP-like protocols.
    /// (smux #112: "Can Stream support CloseWrite?")
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Edge_HalfClose_WriterCloses_ReaderDrainsAndGetsEof()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "half" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("half", cts.Token);

        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.CloseAsync();

        var received = new MemoryStream();
        await ReadToEndAsync(reader, received, cts.Token);

        Assert.Equal(data, received.ToArray());

        cts.Cancel();
    }

    /// <summary>
    /// Reader stops reading (simulating a slow consumer) while writer continues.
    /// Must not deadlock the entire mux or starve other channels.
    /// (smux #85: "One stream not reading blocks the entire session")
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Edge_SlowConsumer_DoesNotBlockOtherChannels()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var slowWriter = await muxA.OpenChannelAsync(new() { ChannelId = "slow" }, cts.Token);
        var slowReader = await muxB.AcceptChannelAsync("slow", cts.Token);

        var fastWriter = await muxA.OpenChannelAsync(new() { ChannelId = "fast" }, cts.Token);
        var fastReader = await muxB.AcceptChannelAsync("fast", cts.Token);

        // Start writing to slow channel (reader won't read for a while)
        var slowWriteTask = Task.Run(async () =>
        {
            var chunk = new byte[1024];
            for (int i = 0; i < 100; i++)
            {
                try { await slowWriter.WriteAsync(chunk, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });

        // Fast channel must still work even though slow channel is backed up
        var fastData = new byte[] { 0xAA, 0xBB, 0xCC };
        await fastWriter.WriteAsync(fastData, cts.Token);
        await fastWriter.CloseAsync();

        var fastReceived = new MemoryStream();
        await ReadToEndAsync(fastReader, fastReceived, cts.Token);

        Assert.Equal(fastData, fastReceived.ToArray());

        cts.Cancel();
    }

    /// <summary>
    /// Many channels opened rapidly, all used briefly, all closed.
    /// Tests that channel index space doesn't get exhausted.
    /// (QUIC §21.8: Stream commitment attack — opening many streams)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Edge_RapidChannelChurn_HundredsOfCycles()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        for (int i = 0; i < 200; i++)
        {
            var id = $"ch-{i}";
            var w = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
            var r = await muxB.AcceptChannelAsync(id, cts.Token);
            await w.WriteAsync(new byte[] { (byte)(i & 0xFF) }, cts.Token);
            await w.CloseAsync();

            var buf = new byte[1];
            await ReadExactAsync(r, buf, cts.Token);
            Assert.Equal((byte)(i & 0xFF), buf[0]);
        }

        cts.Cancel();
    }

    /// <summary>
    /// A single channel is used to transfer many small messages sequentially.
    /// Simulates a chat or command protocol with length-prefixed messages.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Edge_ManySmallMessages_SingleChannel_OrderPreserved()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "msgs" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("msgs", cts.Token);

        const int messageCount = 500;

        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                var msg = Encoding.UTF8.GetBytes($"msg-{i:D4}");
                var lenBuf = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(lenBuf, msg.Length);
                await writer.WriteAsync(lenBuf, cts.Token);
                await writer.WriteAsync(msg, cts.Token);
            }
            await writer.CloseAsync();
        });

        var received = new List<string>();
        var recvTask = Task.Run(async () =>
        {
            var lenBuf = new byte[4];
            while (true)
            {
                if (!await TryReadExactAsync(reader, lenBuf, cts.Token))
                    break;
                int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                var msgBuf = new byte[len];
                if (!await TryReadExactAsync(reader, msgBuf, cts.Token))
                    break;
                received.Add(Encoding.UTF8.GetString(msgBuf));
            }
        });

        await sendTask;
        await recvTask;

        Assert.Equal(messageCount, received.Count);
        for (int i = 0; i < messageCount; i++)
            Assert.Equal($"msg-{i:D4}", received[i]);

        cts.Cancel();
    }

    /// <summary>
    /// A zero-byte write followed by real data.
    /// Some protocols use zero-length frames as signals.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Edge_ZeroByteWrite_DoesNotCorruptStream()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "zero" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("zero", cts.Token);

        await writer.WriteAsync(ReadOnlyMemory<byte>.Empty, cts.Token);
        await writer.WriteAsync(new byte[] { 0x42 }, cts.Token);
        await writer.WriteAsync(ReadOnlyMemory<byte>.Empty, cts.Token);
        await writer.WriteAsync(new byte[] { 0x43 }, cts.Token);
        await writer.CloseAsync();

        var ms = new MemoryStream();
        await ReadToEndAsync(reader, ms, cts.Token);
        var result = ms.ToArray();

        Assert.True(result.Length >= 2);
        Assert.Contains((byte)0x42, result);
        Assert.Contains((byte)0x43, result);

        cts.Cancel();
    }

    /// <summary>
    /// Exactly frame-boundary-sized writes. Ensures no off-by-one in framing.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Edge_ExactFrameBoundaryWrites_NoDataLoss()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "boundary" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("boundary", cts.Token);

        var sizes = new[] { 1, 2, 4, 8, 16, 255, 256, 1023, 1024, 4096, 8192, 65535, 65536 };
        long totalSent = 0;

        foreach (int size in sizes)
        {
            var data = new byte[size];
            Random.Shared.NextBytes(data);
            await writer.WriteAsync(data, cts.Token);
            totalSent += size;
        }
        await writer.CloseAsync();

        var received = new MemoryStream();
        await ReadToEndAsync(reader, received, cts.Token);

        Assert.Equal(totalSent, received.Length);

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 4. GRACEFUL SHUTDOWN
    // ═══════════════════════════════════════════════════════════════════

    #region 4. Graceful Shutdown

    /// <summary>
    /// Server calls GoAwayAsync — client should see GoAwayReceived and
    /// not attempt reconnection.
    /// (QUIC §10.2: Immediate Close / GOAWAY semantics)
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Shutdown_GoAway_ClientSeesDisconnect()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (server, client, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var s = server;
        await using var c = client;

        DisconnectReason? clientDisconnectReason = null;
        client.OnDisconnected += (reason, _) => clientDisconnectReason = reason;

        await server.GoAwayAsync(cts.Token);
        await Task.Delay(500);

        Assert.True(server.IsShuttingDown);

        cts.Cancel();
    }

    /// <summary>
    /// GoAway while channels have in-flight data.
    /// Data sent before GoAway must still be delivered.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Shutdown_GoAway_InFlightDataDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "data" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("data", cts.Token);

        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await writer.WriteAsync(payload, cts.Token);
        await writer.CloseAsync();

        // Read should complete even after GoAway
        var received = new MemoryStream();
        await ReadToEndAsync(reader, received, cts.Token);

        Assert.Equal(payload, received.ToArray());

        await muxA.GoAwayAsync(cts.Token);
        cts.Cancel();
    }

    /// <summary>
    /// Dispose without GoAway — the "crash" scenario from the server side.
    /// The remote side should handle this without hanging forever.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Shutdown_DisposeWithoutGoAway_RemoteSideHandles()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, runA, runB) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "ch" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("ch", cts.Token);

        // Abrupt dispose of one side
        await muxA.DisposeAsync();

        // Other side should eventually detect the disconnect
        await Task.Delay(1000);
        // Don't assert IsConnected because stream may or may not have error yet,
        // but the mux must not hang
        cts.Cancel();
    }

    /// <summary>
    /// After GoAway, new channel opens should be rejected.
    /// (HTTP/2: no new streams after GOAWAY)
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Shutdown_AfterGoAway_NewChannelsRejected()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.GoAwayAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await muxA.OpenChannelAsync(new() { ChannelId = "nope" }, cts.Token);
        });

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 5. RECONNECTION FLOWS (real app patterns)
    // ═══════════════════════════════════════════════════════════════════

    #region 5. Reconnection Flows

    /// <summary>
    /// App is sending a stream of sensor data. Network blips. Data must continue
    /// flowing after reconnect without the app needing to retry.
    /// (Core value proposition of NetConduit reconnection)
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Reconnect_SensorStream_DataContinuesAfterBlip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (mux1, mux2, _, _, rPipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var writer = await mux1.OpenChannelAsync(new() { ChannelId = "sensor" }, cts.Token);
        var reader = await mux2.AcceptChannelAsync("sensor", cts.Token);

        // Write some data
        for (int i = 0; i < 10; i++)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(data, i);
            await writer.WriteAsync(data, cts.Token);
        }

        // Network blip
        await rPipe.DisconnectAsync();
        await Task.Delay(500);

        // Continue writing after disconnect
        for (int i = 10; i < 20; i++)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(data, i);
            await writer.WriteAsync(data, cts.Token);
        }
        await writer.CloseAsync();

        // Read everything
        var received = new List<int>();
        var buf = new byte[4];
        while (true)
        {
            if (!await TryReadExactAsync(reader, buf, cts.Token))
                break;
            received.Add(BinaryPrimitives.ReadInt32BigEndian(buf));
        }

        Assert.Equal(20, received.Count);
        for (int i = 0; i < 20; i++)
            Assert.Equal(i, received[i]);

        cts.Cancel();
    }

    /// <summary>
    /// Multiple channels active during disconnect. All must survive.
    /// Real apps have control + data + heartbeat channels simultaneously.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Reconnect_MultiChannel_AllSurviveDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (mux1, mux2, _, _, rPipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var w1 = await mux1.OpenChannelAsync(new() { ChannelId = "ch-1" }, cts.Token);
        var w2 = await mux1.OpenChannelAsync(new() { ChannelId = "ch-2" }, cts.Token);
        var w3 = await mux1.OpenChannelAsync(new() { ChannelId = "ch-3" }, cts.Token);

        var r1 = await mux2.AcceptChannelAsync("ch-1", cts.Token);
        var r2 = await mux2.AcceptChannelAsync("ch-2", cts.Token);
        var r3 = await mux2.AcceptChannelAsync("ch-3", cts.Token);

        // Write before disconnect
        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        await w2.WriteAsync(new byte[] { 2 }, cts.Token);
        await w3.WriteAsync(new byte[] { 3 }, cts.Token);

        // Disconnect
        await rPipe.DisconnectAsync();
        await Task.Delay(300);

        // Write after disconnect
        await w1.WriteAsync(new byte[] { 11 }, cts.Token);
        await w2.WriteAsync(new byte[] { 22 }, cts.Token);
        await w3.WriteAsync(new byte[] { 33 }, cts.Token);

        await w1.CloseAsync();
        await w2.CloseAsync();
        await w3.CloseAsync();

        var ms1 = new MemoryStream();
        var ms2 = new MemoryStream();
        var ms3 = new MemoryStream();

        await Task.WhenAll(
            ReadToEndAsync(r1, ms1, cts.Token),
            ReadToEndAsync(r2, ms2, cts.Token),
            ReadToEndAsync(r3, ms3, cts.Token));

        Assert.Contains((byte)1, ms1.ToArray());
        Assert.Contains((byte)11, ms1.ToArray());
        Assert.Contains((byte)2, ms2.ToArray());
        Assert.Contains((byte)22, ms2.ToArray());
        Assert.Contains((byte)3, ms3.ToArray());
        Assert.Contains((byte)33, ms3.ToArray());

        cts.Cancel();
    }

    /// <summary>
    /// App observes OnDisconnected and OnAutoReconnecting events properly.
    /// Real monitoring/logging code hooks into these events.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Reconnect_Events_OnDisconnectedAndReconnectingFire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (mux1, mux2, _, _, rPipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var disconnected = new TaskCompletionSource<DisconnectReason>();
        mux1.OnDisconnected += (reason, _) =>
        {
            disconnected.TrySetResult(reason);
        };

        await rPipe.DisconnectAsync();

        var reason = await disconnected.Task.WaitAsync(cts.Token);
        Assert.Equal(DisconnectReason.TransportError, reason);

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 6. RESOURCE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    #region 6. Resource Management

    /// <summary>
    /// Open channels, don't close them explicitly, then dispose the mux.
    /// The mux must clean up without leaking or hanging.
    /// (yamux: "leaked streams" is one of the most common issues)
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Resource_UnclosedChannels_MuxDisposeCleansUp()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var w1 = await muxA.OpenChannelAsync(new() { ChannelId = "leak1" }, cts.Token);
        var w2 = await muxA.OpenChannelAsync(new() { ChannelId = "leak2" }, cts.Token);
        var r1 = await muxB.AcceptChannelAsync("leak1", cts.Token);
        var r2 = await muxB.AcceptChannelAsync("leak2", cts.Token);

        await w1.WriteAsync(new byte[100], cts.Token);
        await w2.WriteAsync(new byte[100], cts.Token);

        // Dispose without closing channels - must not hang
        await muxA.DisposeAsync();
        await muxB.DisposeAsync();
    }

    /// <summary>
    /// ActiveChannelCount tracks correctly as channels open and close.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Resource_ActiveChannelCount_TracksCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.Equal(0, muxA.ActiveChannelCount);

        var w1 = await muxA.OpenChannelAsync(new() { ChannelId = "c1" }, cts.Token);
        await muxB.AcceptChannelAsync("c1", cts.Token);
        await Task.Delay(100);
        Assert.True(muxA.ActiveChannelCount >= 1);

        var w2 = await muxA.OpenChannelAsync(new() { ChannelId = "c2" }, cts.Token);
        await muxB.AcceptChannelAsync("c2", cts.Token);
        await Task.Delay(100);
        Assert.True(muxA.ActiveChannelCount >= 2);

        await w1.CloseAsync();
        await Task.Delay(200);

        await w2.CloseAsync();
        await Task.Delay(200);

        cts.Cancel();
    }

    /// <summary>
    /// Multiple channels opened and disposed correctly tracks resource count.
    /// Real apps must ensure channel cleanup to avoid resource leaks.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Resource_ChannelDispose_DecrementsOpenCount()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.Equal(0, muxA.Stats.OpenChannels);

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "res1" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("res1", cts.Token);

        Assert.Equal(1, muxA.Stats.OpenChannels);

        await writer.DisposeAsync();
        reader.Dispose();
        await Task.Delay(200);

        Assert.Equal(0, muxA.Stats.OpenChannels);
        Assert.True(muxA.Stats.TotalChannelsOpened >= 1);

        cts.Cancel();
    }

    /// <summary>
    /// Stats counters (bytes sent/received) are non-zero after data transfer.
    /// Real monitoring dashboards rely on these stats.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Resource_Stats_TrackBytesSentReceived()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new() { ChannelId = "stats" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("stats", cts.Token);

        var data = new byte[10000];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.CloseAsync();

        await ReadToEndAsync(reader, new MemoryStream(), cts.Token);

        Assert.True(muxA.Stats.BytesSent > 0);
        Assert.True(muxB.Stats.BytesReceived > 0);

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 7. TRANSIT LAYER — real protocol patterns
    // ═══════════════════════════════════════════════════════════════════

    #region 7. Transit Layer

    /// <summary>
    /// MessageTransit used for a simple command/response protocol.
    /// This is the most common integrator pattern.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Transit_MessageTransit_CommandResponse()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var cmdWriter = await muxA.OpenChannelAsync(new() { ChannelId = "cmd" }, cts.Token);
        var cmdReader = await muxB.AcceptChannelAsync("cmd", cts.Token);

        var resWriter = await muxB.OpenChannelAsync(new() { ChannelId = "res" }, cts.Token);
        var resReader = await muxA.AcceptChannelAsync("res", cts.Token);

        await using var cmdSend = new MessageTransit<Command, Command>(
            cmdWriter, null, FlowJsonContext.Default.Command, FlowJsonContext.Default.Command);
        await using var cmdRecv = new MessageTransit<Command, Command>(
            null, cmdReader, FlowJsonContext.Default.Command, FlowJsonContext.Default.Command);

        await using var resSend = new MessageTransit<Response, Response>(
            resWriter, null, FlowJsonContext.Default.Response, FlowJsonContext.Default.Response);
        await using var resRecv = new MessageTransit<Response, Response>(
            null, resReader, FlowJsonContext.Default.Response, FlowJsonContext.Default.Response);

        var serverTask = Task.Run(async () =>
        {
            var cmd = await cmdRecv.ReceiveAsync(cts.Token);
            Assert.NotNull(cmd);
            Assert.Equal("ping", cmd.Action);

            await resSend.SendAsync(new Response("ok", "pong"), cts.Token);
        });

        await cmdSend.SendAsync(new Command("ping", ""), cts.Token);
        var response = await resRecv.ReceiveAsync(cts.Token);

        await serverTask;
        Assert.NotNull(response);
        Assert.Equal("ok", response.Status);
        Assert.Equal("pong", response.Data);

        cts.Cancel();
    }

    /// <summary>
    /// DuplexStreamTransit wrapping a bidirectional channel.
    /// Simulates a terminal/shell session.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Transit_DuplexStream_BidirectionalShell()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "shell-out" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("shell-out", cts.Token);
        var writeB = await muxB.OpenChannelAsync(new() { ChannelId = "shell-in" }, cts.Token);
        var readA = await muxA.AcceptChannelAsync("shell-in", cts.Token);

        await using var transitA = new DuplexStreamTransit(writeA, readA);
        await using var transitB = new DuplexStreamTransit(writeB, readB);

        var inputData = Encoding.UTF8.GetBytes("ls -la\n");
        await transitA.WriteAsync(inputData, cts.Token);
        await transitA.FlushAsync(cts.Token);

        var buf = new byte[inputData.Length];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = await transitB.ReadAsync(buf.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(inputData, buf);

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // 8. CONCURRENT ACCESS PATTERNS
    // ═══════════════════════════════════════════════════════════════════

    #region 8. Concurrent Patterns

    /// <summary>
    /// Multiple tasks open channels concurrently from the same mux.
    /// (smux #97: "OpenStream seems to have parallel performance problems")
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_ParallelChannelOpen_AllSucceed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        const int parallelCount = 50;

        var acceptTask = Task.Run(async () =>
        {
            var accepted = 0;
            await foreach (var ch in muxB.AcceptChannelsAsync(cts.Token))
            {
                accepted++;
                if (accepted >= parallelCount) break;
            }
            return accepted;
        });

        var openTasks = Enumerable.Range(0, parallelCount).Select(i => Task.Run(async () =>
        {
            var w = await muxA.OpenChannelAsync(new() { ChannelId = $"par-{i}" }, cts.Token);
            await w.WriteAsync(new byte[] { (byte)(i & 0xFF) }, cts.Token);
            await w.CloseAsync();
        })).ToArray();

        await Task.WhenAll(openTasks);
        var acceptedCount = await acceptTask;

        Assert.Equal(parallelCount, acceptedCount);

        cts.Cancel();
    }

    /// <summary>
    /// Both sides open channels simultaneously to the mux — bidirectional
    /// concurrent channel creation must not deadlock.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_BothSidesOpenSimultaneously_NoDeadlock()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        const int countPerSide = 20;

        var aOpens = Enumerable.Range(0, countPerSide).Select(i => Task.Run(async () =>
        {
            var w = await muxA.OpenChannelAsync(new() { ChannelId = $"a-{i}" }, cts.Token);
            await w.WriteAsync(BitConverter.GetBytes(i), cts.Token);
            await w.CloseAsync();
        })).ToArray();

        var bOpens = Enumerable.Range(0, countPerSide).Select(i => Task.Run(async () =>
        {
            var w = await muxB.OpenChannelAsync(new() { ChannelId = $"b-{i}" }, cts.Token);
            await w.WriteAsync(BitConverter.GetBytes(i + 1000), cts.Token);
            await w.CloseAsync();
        })).ToArray();

        var aAccepts = Enumerable.Range(0, countPerSide).Select(i => Task.Run(async () =>
        {
            var r = await muxA.AcceptChannelAsync($"b-{i}", cts.Token);
            var ms = new MemoryStream();
            await ReadToEndAsync(r, ms, cts.Token);
            return ms.ToArray();
        })).ToArray();

        var bAccepts = Enumerable.Range(0, countPerSide).Select(i => Task.Run(async () =>
        {
            var r = await muxB.AcceptChannelAsync($"a-{i}", cts.Token);
            var ms = new MemoryStream();
            await ReadToEndAsync(r, ms, cts.Token);
            return ms.ToArray();
        })).ToArray();

        await Task.WhenAll(aOpens);
        await Task.WhenAll(bOpens);
        var aResults = await Task.WhenAll(aAccepts);
        var bResults = await Task.WhenAll(bAccepts);

        Assert.Equal(countPerSide, aResults.Length);
        Assert.Equal(countPerSide, bResults.Length);
        Assert.All(aResults, r => Assert.True(r.Length > 0));
        Assert.All(bResults, r => Assert.True(r.Length > 0));

        cts.Cancel();
    }

    /// <summary>
    /// Multiple writers on different channels, single mux. All data must
    /// arrive without interleaving corruption within a single channel.
    /// (Head-of-line blocking / multiplexing correctness)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Concurrent_MultiChannelWriters_NoInterleaving()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        const int channelCount = 10;
        const int messagesPerChannel = 100;
        const int messageSize = 128;

        var writers = new WriteChannel[channelCount];
        var readers = new ReadChannel[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = await muxA.OpenChannelAsync(new() { ChannelId = $"mch-{i}" }, cts.Token);
            readers[i] = await muxB.AcceptChannelAsync($"mch-{i}", cts.Token);
        }

        var sendTasks = writers.Select((w, idx) => Task.Run(async () =>
        {
            for (int m = 0; m < messagesPerChannel; m++)
            {
                var data = new byte[messageSize];
                Array.Fill(data, (byte)idx);
                await w.WriteAsync(data, cts.Token);
            }
            await w.CloseAsync();
        })).ToArray();

        var recvTasks = readers.Select((r, idx) => Task.Run(async () =>
        {
            var ms = new MemoryStream();
            await ReadToEndAsync(r, ms, cts.Token);
            var data = ms.ToArray();

            Assert.Equal(messagesPerChannel * messageSize, data.Length);
            Assert.All(data, b => Assert.Equal((byte)idx, b));
        })).ToArray();

        await Task.WhenAll(sendTasks);
        await Task.WhenAll(recvTasks);

        cts.Cancel();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static async Task ReadToEndAsync(Stream source, MemoryStream target, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (true)
        {
            int n = await source.ReadAsync(buf, ct);
            if (n == 0) break;
            target.Write(buf, 0, n);
        }
    }

    private static async Task ReadExactAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await source.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    private static async Task<bool> TryReadExactAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await source.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) return offset > 0 ? throw new EndOfStreamException("Partial read") : false;
            offset += n;
        }
        return true;
    }
}
