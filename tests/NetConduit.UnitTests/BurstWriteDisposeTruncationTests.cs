using System.Buffers.Binary;
using System.Diagnostics;
using Xunit.Abstractions;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for dispose-after-burst-write data truncation:
/// DisposeAsync must not drop the slab before pending frames and FIN drain.
/// Also locks in end-to-end peer-window flow control for non-replay sessions:
/// a burst larger than the peer's receive window must stall the writer until
/// the peer's reader consumes (and ACKs) — otherwise the peer's read slab
/// overflows and the channel faults with ProtocolError.
/// </summary>
public sealed class BurstWriteDisposeTruncationTests
{
    private readonly ITestOutputHelper _output;

    public BurstWriteDisposeTruncationTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync(
        bool replayEnabled = true, int slabSize = FrameConstants.DefaultSlabSize)
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            MaxAutoReconnectAttempts = replayEnabled ? -1 : 0,
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize },
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            MaxAutoReconnectAttempts = replayEnabled ? -1 : 0,
            DefaultChannelOptions = new DefaultChannelOptions { SlabSize = slabSize },
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    private static byte[] BuildMessage(long seq, int payloadSize)
    {
        var msg = new byte[payloadSize];
        BinaryPrimitives.WriteInt64LittleEndian(msg, seq);
        return msg;
    }

    [Fact]
    public async Task BurstWrite_1000x256_ConcurrentReader_DisposeAsync_AllMessagesArrive()
    {
        // Verifies DisposeAsync does not truncate data when a concurrent
        // reader drains the data as it arrives. The reader slab is 64KB
        // and can only buffer ~256 messages at once; a concurrent reader
        // is required for workloads exceeding one slab window.
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream" });
        var reader = server.AcceptChannel("stream");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        var received = new List<long>();
        long lastSeq = -1;

        // Concurrent reader drains messages as they arrive
        var readTask = Task.Run(async () =>
        {
            byte[] buf = new byte[256];
            while (true)
            {
                int n = await reader.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                long seq = BinaryPrimitives.ReadInt64LittleEndian(buf);
                lock (received) received.Add(seq);
                Interlocked.Exchange(ref lastSeq, seq);
            }
        });

        // 1,000 writes × 256 bytes = 256 KB
        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();
        await readTask;

        Assert.Equal(1000, received.Count);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BurstWrite_256x256_DisposeAsync_AllMessagesArrive()
    {
        // Exact boundary case: 256 × 256 = 65,536 = 64 KiB
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream256" });
        var reader = server.AcceptChannel("stream256");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        for (int i = 0; i < 256; i++)
        {
            var msg = new byte[256];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        var received = new List<long>();
        byte[] buf = new byte[65536];
        int offset = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(offset, buf.Length - offset), cts.Token);
            if (n == 0) break;
            offset += n;
        }

        int msgSize = 256;
        int totalMessages = offset / msgSize;
        for (int i = 0; i < totalMessages; i++)
        {
            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * msgSize, 8));
            received.Add(seq);
        }

        Assert.Equal(256, received.Count);
        for (int i = 0; i < 256; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task BurstWrite_1000x128_DisposeAsync_AllMessagesArrive()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream128" });
        var reader = server.AcceptChannel("stream128");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        int msgSize = 128;
        for (int i = 0; i < 1000; i++)
        {
            var msg = new byte[msgSize];
            BinaryPrimitives.WriteInt64LittleEndian(msg, i);
            await writer.WriteAsync(msg, cts.Token);
        }

        await writer.DisposeAsync();

        var received = new List<long>();
        byte[] buf = new byte[200_000];
        int offset = 0;
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(offset, buf.Length - offset), cts.Token);
            if (n == 0) break;
            offset += n;
        }

        int totalMessages = offset / msgSize;
        for (int i = 0; i < totalMessages; i++)
        {
            long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * msgSize, 8));
            received.Add(seq);
        }

        Assert.Equal(1000, received.Count);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task NonReplay_5000x256_1MB_SequentialDrain_NoDataLoss()
    {
        // 5000 x 256B = 1.28 MiB total across a 1 MiB peer window. In non-replay
        // mode the writer must stall at the window boundary until the peer's
        // reader consumes and ACKs; without flow control the peer's read slab
        // overflows and the channel faults with ProtocolError (truncated data).
        // The reader only starts draining once the burst has had time to either
        // park at the window (flow control working) or complete unfettered and
        // overflow the peer (flow control broken).
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream-nr" });
        var reader = server.AcceptChannel("stream-nr");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        const int count = 5000;
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), cts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        await Task.Delay(2000, cts.Token);

        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];

        // Phase 1: drain while the burst is still running. On the fixed path
        // this releases the writer parked at the window boundary (its ACKs).
        while (!writerTask.IsCompleted)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        await writerTask;

        // Phase 2: graceful close so the reader can reach EOF via the FIN.
        if (writerFault is null)
            await writer.DisposeAsync();

        // Phase 3: drain the tail (in-flight frames plus the FIN/EOF).
        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task NonReplay_64KB_1000x256_SlowReader_AllArrive()
    {
        // Reader drains from the start but throttled (1 ms per 256B frame). The
        // 64 KiB peer window holds only ~256 frames, so a consumer draining at
        // ~1 frame/ms cannot keep up with the unfettered buggy burst — the peer
        // slab overflows. On the fixed path the writer parks at the window and
        // resumes as ACKs arrive; every frame still arrives in order.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false, slabSize: 64 * 1024);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "stream-slow",
            SlabSize = 64 * 1024,
        });
        var reader = server.AcceptChannel("stream-slow");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        const int count = 1000;
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), cts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];

        // Phase 1: drain (throttled) while the burst is still running.
        while (!writerTask.IsCompleted)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
            await Task.Delay(1, cts.Token);
        }

        await writerTask;

        if (writerFault is null)
            await writer.DisposeAsync();

        // Phase 2: drain the tail (in-flight frames plus the FIN/EOF).
        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task NonReplay_64KB_ExactBoundary_256x256_AllArrive()
    {
        // 256 x 256B = 65,536 = exactly the 64 KiB peer window. The burst must
        // not fault the peer even when it exactly fills the receive slab; the
        // writer parks at the boundary and the reader drains the full payload.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false, slabSize: 64 * 1024);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "stream-boundary",
            SlabSize = 64 * 1024,
        });
        var reader = server.AcceptChannel("stream-boundary");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        const int count = 256;
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), cts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        await Task.Delay(2000, cts.Token);

        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];

        while (!writerTask.IsCompleted)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        await writerTask;

        if (writerFault is null)
            await writer.DisposeAsync();

        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task NonReplay_InitPlusBurst_4500x256_1MB_AllArrive()
    {
        // INIT frame + 4500 x 256B = ~1.19 MiB across a 1 MiB window. The INIT
        // bytes are processed inline by the peer and must not count against the
        // window, yet the data burst must still stall at the boundary.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream-init" });
        var reader = server.AcceptChannel("stream-init");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        const int count = 4500;
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), cts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        await Task.Delay(2000, cts.Token);

        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];

        while (!writerTask.IsCompleted)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        await writerTask;

        if (writerFault is null)
            await writer.DisposeAsync();

        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReplayMode_5000x256_1MB_SlowReader_AllArrive()
    {
        // Replay mode already enforces the peer window (MarkSent does not
        // auto-ack), so this must pass today — a guard that the flow-control
        // contract holds end-to-end for the default configuration.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: true);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream-replay" });
        var reader = server.AcceptChannel("stream-replay");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        const int count = 5000;
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), cts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];

        while (!writerTask.IsCompleted)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
            await Task.Delay(1, cts.Token);
        }

        await writerTask;

        if (writerFault is null)
            await writer.DisposeAsync();

        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf, cts.Token);
            }
            catch (ChannelClosedException ex)
            {
                readerFault = ex;
                break;
            }
            if (n == 0) break;
            received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
        }

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task NonReplay_64KB_1000x256_NoConsumer_WriteParksAtPeerWindow()
    {
        // With no consumer at all, the writer must park at the peer window
        // (64 KiB -> ~247 frames) instead of overrunning the peer's read slab.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false, slabSize: 64 * 1024);
        using var cts = new CancellationTokenSource(TestTimeout);

        var writer = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "stream-noconsumer",
            SlabSize = 64 * 1024,
        });
        var reader = server.AcceptChannel("stream-noconsumer");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        int completed = 0;
        Task? parkedWrite = null;

        for (int i = 0; i < 1000 && parkedWrite is null; i++)
        {
            Task write = writer.WriteAsync(BuildMessage(i, 256), cts.Token).AsTask();
            Task winner = await Task.WhenAny(write, Task.Delay(1000, cts.Token));
            if (winner != write)
            {
                parkedWrite = write;
                break;
            }
            try
            {
                await write;
            }
            catch (ChannelClosedException)
            {
                // Transport faulted mid-burst (peer slab overflowed) — the
                // writer neither parked nor completed; handled by the asserts.
                break;
            }
            completed++;
        }

        // ~247 frames fit in the 64 KiB window; the 248th must park.
        Assert.True(parkedWrite is not null,
            "Expected WriteAsync to park at the 64 KiB peer window with no consumer; "
            + $"it completed {completed} writes without stalling — peer flow control is not enforced.");
        Assert.InRange(completed, 100, 300);

        // Tear down so the parked writer unwinds with ChannelClosedException.
        await client.DisposeAsync();
        await server.DisposeAsync();
        try
        {
            await parkedWrite.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ChannelClosedException)
        {
            // Expected: abort wakes the parked writer with ChannelClosedException.
        }
    }

    [Fact]
    public async Task DisposeAfterBlockedWrites_NoTruncation_NonReplay()
    {
        // A burst that exceeds the peer window, drained only after the window
        // is exhausted, then a graceful DisposeAsync — every message must still
        // arrive and EOF must follow the FIN (no truncation on dispose).
        // Each phase runs under its own budget: a single shared timeout made
        // the next failure anonymous, surfacing wherever the reader happened
        // to be parked instead of naming the slow phase.
        var (client, server) = await CreateReadyPairAsync(replayEnabled: false);

        var writer = client.OpenChannel(new ChannelOptions { ChannelId = "stream-dispose" });
        var reader = server.AcceptChannel("stream-dispose");
        using (var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await Task.WhenAll(writer.WaitForReadyAsync(readyCts.Token), reader.WaitForReadyAsync(readyCts.Token));
        }

        const int count = 5000;
        var stopwatch = Stopwatch.StartNew();
        using var burstCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Exception? writerFault = null;
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                    await writer.WriteAsync(BuildMessage(i, 256), burstCts.Token);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        // Wait up to 5 s for the writer to park at the 1 MiB peer window
        // instead of burning a fixed delay: the stall proves the premise
        // of this test, and the poll exits early if the writer ever
        // completes without parking (surfaced by the assert below).
        // The deadline stays well under the 30 s SendTimeout: no reader
        // drains during this window, so no ACK arrives and the parked writer
        // would fault with TimeoutException if held here too long.
        var received = new List<long>();
        Exception? readerFault = null;
        byte[] buf = new byte[256];
        var parkDeadline = TimeSpan.FromSeconds(5);
        while (!writerTask.IsCompleted && stopwatch.Elapsed < parkDeadline)
            await Task.Delay(50, CancellationToken.None);
        Assert.False(writerTask.IsCompleted,
            $"Writer should park at the 1 MiB peer window; it finished {count} burst writes without stalling.");
        var parkElapsed = stopwatch.Elapsed;
        _output.WriteLine($"Phase park: elapsed={parkElapsed} received={received.Count} writerCompleted={writerTask.IsCompleted}");

        using (var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (!writerTask.IsCompleted)
            {
                int n;
                try
                {
                    n = await reader.ReadAsync(buf, drainCts.Token);
                }
                catch (ChannelClosedException ex)
                {
                    readerFault = ex;
                    break;
                }
                if (n == 0) break;
                received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
            }
        }

        var drainElapsed = stopwatch.Elapsed;
        _output.WriteLine($"Phase drain: elapsed={drainElapsed} (park took {parkElapsed}) received={received.Count}/{count} writerFault={writerFault is not null}");

        await writerTask;

        // Dispose must not drop pending data: all writes completed before FIN.
        // Bounded so a stall here names the dispose phase instead of firing
        // a shared timeout while the tail reader is parked.
        if (writerFault is null)
            await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        var disposeElapsed = stopwatch.Elapsed;
        _output.WriteLine($"Phase dispose: elapsed={disposeElapsed} received={received.Count}/{count}");

        using (var tailCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (true)
            {
                int n;
                try
                {
                    n = await reader.ReadAsync(buf, tailCts.Token);
                }
                catch (ChannelClosedException ex)
                {
                    readerFault = ex;
                    break;
                }
                if (n == 0) break;
                received.Add(BinaryPrimitives.ReadInt64LittleEndian(buf));
            }
        }

        stopwatch.Stop();
        _output.WriteLine($"Phase tail: total={stopwatch.Elapsed} received={received.Count}/{count}");

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
