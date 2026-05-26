using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for shutdown teardown ordering bugs:
///
/// - #391: <see cref="IChannel.Disconnected"/> is documented as "raised each
///   time the channel's underlying transport disconnects", but
///   <c>DisposeAsync</c>/<c>GoAwayAsync</c>/remote-GoAway only fire
///   <see cref="IChannel.Closed"/>; the channel-level Disconnected event is
///   never observed on the shutdown path. The contract is violated and the
///   behaviour is inconsistent with the natural transport-fault path.
///
/// - #368: <c>DisposeAsync</c> returns channel slabs to
///   <see cref="System.Buffers.ArrayPool{T}"/> via
///   <c>_registry.AbortAllChannels(MuxDisposed)</c> BEFORE awaiting the
///   writer task. The writer thread can be mid <c>writeStream.Write(frames.Span)</c>
///   where <c>frames</c> points into the just-released slab → use-after-free.
///
/// Both bugs share the same root cause: shutdown paths conflate the
/// "signal channels Disconnected" step with the "release slab resources"
/// step, and do both before the writer loop has actually exited. The natural
/// transport-fault path inside <c>MainLoopAsync</c> already does this
/// correctly (MarkDisconnected → <c>WaitForLoopsAsync</c> → eventual
/// AbortAllChannels); shutdown paths must mirror the same sequence.
/// </summary>
public sealed class ShutdownTeardownOrderingTests
{
    [Fact]
    public async Task DisposeAsync_RaisesChannelDisconnectedEvent_BeforeClosed()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(5),
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(5),
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeCh = client.OpenChannel("foo");
        var readCh = await server.AcceptChannelAsync("foo", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);
        await readCh.WaitForReadyAsync(cts.Token);

        var disconnectedReasons = new List<DisconnectReason>();
        var closedReasons = new List<ChannelCloseReason>();
        bool disconnectedBeforeClosed = false;
        bool closedSeen = false;

        writeCh.Disconnected += (_, e) =>
        {
            if (!closedSeen) disconnectedBeforeClosed = true;
            disconnectedReasons.Add(e.Reason);
        };
        writeCh.Closed += (_, e) =>
        {
            closedSeen = true;
            closedReasons.Add(e.Reason);
        };

        await client.DisposeAsync();

        Assert.Single(disconnectedReasons);
        Assert.Equal(DisconnectReason.LocalDispose, disconnectedReasons[0]);
        Assert.True(disconnectedBeforeClosed, "Disconnected event must fire before Closed event on shutdown.");
        Assert.Single(closedReasons);
        Assert.Equal(ChannelCloseReason.MuxDisposed, closedReasons[0]);

        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
    }

    [Fact]
    public async Task GoAwayAsync_RaisesChannelDisconnectedEvent()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeCh = client.OpenChannel("bar");
        var readCh = await server.AcceptChannelAsync("bar", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);
        await readCh.WaitForReadyAsync(cts.Token);

        var disconnectedReasons = new List<DisconnectReason>();
        writeCh.Disconnected += (_, e) => disconnectedReasons.Add(e.Reason);

        await client.GoAwayAsync(cts.Token);

        Assert.Single(disconnectedReasons);
        Assert.Equal(DisconnectReason.LocalDispose, disconnectedReasons[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WaitsForWriterBeforeReleasingChannelSlabs()
    {
        // #368 use-after-free: the writer thread can be inside
        // writeStream.Write(frames.Span) where frames points into a channel
        // slab. If DisposeAsync's AbortAllChannels(MuxDisposed) runs before
        // the writer exits, the slab is returned to ArrayPool while the
        // writer is still touching it.
        //
        // The Closed event is raised inside SetClosed, which is the same
        // place TryReturnSlab is called. So Closed firing is a precise
        // proxy for "slab returned". We block the writer inside Write,
        // start DisposeAsync, and assert that Closed has NOT fired while
        // the writer is blocked. Releasing the gate lets the writer exit
        // and DisposeAsync proceeds to release slabs.

        var duplex = new DuplexMemoryStream();
        var gate = new ManualResetEventSlim(initialState: false);
        var entered = new ManualResetEventSlim(initialState: false);

        var blockingSide = new BlockingWriteStreamPair(duplex.SideA, gate, entered);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(blockingSide),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(200),
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Handshake completes before the first user write triggers blocking,
        // because BlockingWriteStreamPair only blocks AFTER `Arm()` is called.
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeCh = client.OpenChannel("blocked");
        var readCh = await server.AcceptChannelAsync("blocked", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);
        await readCh.WaitForReadyAsync(cts.Token);

        // Arm the blocking gate. The next writer-thread Write will block on `gate`.
        blockingSide.Arm();

        var payload = new byte[64];
        await writeCh.WriteAsync(payload, cts.Token);

        // Wait until the writer thread is actually parked inside Write.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Writer thread did not enter blocking Write.");

        bool closedFired = false;
        writeCh.Closed += (_, _) => closedFired = true;

        var disposeTask = client.DisposeAsync().AsTask();

        // Give DisposeAsync a real chance to incorrectly release the slab.
        // With the bug, AbortAllChannels runs synchronously and Closed fires
        // immediately. With the fix, Closed cannot fire until the writer
        // exits (which it cannot do while blocked in Write).
        for (int i = 0; i < 25 && !closedFired; i++)
            await Task.Delay(20);

        Assert.False(closedFired,
            "Closed event fired while the writer was still blocked in writeStream.Write — slab would be returned to ArrayPool while writer is touching it (#368).");
        Assert.False(disposeTask.IsCompleted,
            "DisposeAsync completed while the writer was still blocked — it cannot have awaited the writer task.");

        // Release the writer; DisposeAsync should now proceed.
        gate.Set();

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(closedFired, "Closed event must fire once DisposeAsync completes.");

        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
        gate.Dispose();
        entered.Dispose();
    }

    /// <summary>
    /// Wraps an <see cref="IStreamPair"/> and, once armed, blocks the writer
    /// thread inside the next <c>Write</c>/<c>WriteAsync</c> call on a shared
    /// <see cref="ManualResetEventSlim"/>. Used to model a slow/wedged
    /// transport on the shutdown path.
    /// </summary>
    private sealed class BlockingWriteStreamPair : IStreamPair
    {
        private readonly IStreamPair _inner;
        private int _armed;

        public BlockingWriteStreamPair(IStreamPair inner, ManualResetEventSlim gate, ManualResetEventSlim entered)
        {
            _inner = inner;
            ReadStream = inner.ReadStream;
            WriteStream = new BlockingWriteStream(inner.WriteStream, gate, entered, () => Volatile.Read(ref _armed) != 0);
        }

        public Stream ReadStream { get; }
        public Stream WriteStream { get; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class BlockingWriteStream(Stream inner, ManualResetEventSlim gate, ManualResetEventSlim entered, Func<bool> isArmed) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockIfArmed();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockIfArmed();
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            BlockIfArmed();
            await inner.WriteAsync(buffer, ct);
        }

        private void BlockIfArmed()
        {
            if (!isArmed()) return;
            entered.Set();
            gate.Wait();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
