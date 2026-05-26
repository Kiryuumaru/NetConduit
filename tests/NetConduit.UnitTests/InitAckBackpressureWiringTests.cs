using System.Diagnostics;
using System.Reflection;
using NetConduit.Constants;
using NetConduit.Internal;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression coverage for the INIT-ACK back-pressure retry wiring.
///
/// The mechanism (<see cref="MuxConnection.PendingInitAcks"/> queue plus the
/// <c>DrainPendingInitAcks</c> method on <see cref="StreamMultiplexer"/>) was
/// introduced to keep peer-initiated channel opens recoverable across
/// transient control-slab pressure: if <c>SendInitAck</c> cannot stage the
/// INIT-ACK reply into the control slab right now, the channel index must be
/// queued so a later loop iteration can retry the reply once the slab drains.
///
/// These tests exercise both halves of the wiring:
///   * <see cref="ReaderLoop_DrainsPreEnqueuedPendingInitAcks"/> proves the
///     reader loop actually invokes the drain (consumer half).
///   * <see cref="HandleInitFrame_EnqueuesIndex_WhenControlSlabSaturated"/>
///     proves the call site enqueues on <c>SendInitAck</c> failure rather
///     than discarding the boolean return value (producer half).
/// </summary>
public sealed class InitAckBackpressureWiringTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    private static MuxConnection GetMuxConnection(StreamMultiplexer mux)
    {
        var field = typeof(StreamMultiplexer).GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("StreamMultiplexer._conn field not found via reflection.");
        return (MuxConnection)(field.GetValue(mux) ?? throw new InvalidOperationException("_conn was null."));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    /// <summary>
    /// Reproduces issue #426 (consumer half).
    ///
    /// Pre-populates <c>_conn.PendingInitAcks</c> with two spurious channel
    /// indices, then triggers a reader-loop iteration on the server by
    /// opening a real channel from the client. Once the server's reader loop
    /// processes that frame, it must drain the queue.
    ///
    /// Pre-fix: <c>DrainPendingInitAcks</c> is defined but has zero callers,
    /// so the queue stays populated forever and the assertion fails.
    ///
    /// Post-fix: the reader loop invokes the drain on every iteration; the
    /// queue empties within the wait window.
    ///
    /// The spurious indices are safe because the peer's frame dispatcher
    /// silently drops ACK frames whose channel index does not match any
    /// registered channel.
    /// </summary>
    [Fact]
    public async Task ReaderLoop_DrainsPreEnqueuedPendingInitAcks()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            var serverConn = GetMuxConnection(server);

            serverConn.PendingInitAcks.Enqueue(9001);
            serverConn.PendingInitAcks.Enqueue(9003);
            Assert.Equal(2, serverConn.PendingInitAcks.Count);

            // Trigger reader-loop iterations on the server by opening a real
            // channel from the client (its INIT and post-handshake traffic
            // both wake the server's reader thread).
            await using var ch = (NetConduit.Interfaces.IWriteChannel)client.OpenChannel(new ChannelOptions
            {
                ChannelId = "drain-trigger",
            });
            await ch.WaitForReadyAsync();

            bool drained = await WaitUntilAsync(
                () => serverConn.PendingInitAcks.IsEmpty,
                TimeSpan.FromSeconds(3));

            Assert.True(drained,
                $"Reader loop must drain PendingInitAcks. Remaining count: {serverConn.PendingInitAcks.Count}.");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Reproduces issue #431 (producer half).
    ///
    /// Wraps the server's transport in a pausable stream pair, completes the
    /// initial handshake, then pauses the server-to-client write direction so
    /// the server's writer task blocks on its underlying <c>WriteAsync</c>.
    /// Saturating the control slab from the test thread no longer races
    /// against the writer's drain, so when the client opens channels the
    /// server's reader calls <c>SendInitAck</c> against a deterministically
    /// full slab and the call site must enqueue the rejected index.
    ///
    /// Pre-fix: the boolean return of <c>SendInitAck</c> is discarded, so
    /// every failure is silently dropped and <c>PendingInitAcks</c> stays
    /// empty — the assertion fails.
    ///
    /// Post-fix: every failed INIT-ACK is enqueued for retry, so the
    /// queue grows to at least one entry per opened channel.
    /// </summary>
    [Fact]
    public async Task HandleInitFrame_EnqueuesIndex_WhenControlSlabSaturated()
    {
        var duplex = new DuplexMemoryStream();
        var pausableServerSide = new PausableStreamPair(duplex.SideB);
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(pausableServerSide),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        try
        {
            var serverConn = GetMuxConnection(server);
            var control = serverConn.ControlChannel
                ?? throw new InvalidOperationException("Server control channel not initialized.");

            // Pause the server's outbound write direction. The server's writer
            // task will block on WriteAsync as soon as the next signal lands,
            // so the control slab cannot drain.
            pausableServerSide.PauseWrites();

            // Trigger one round of drain to ensure the writer is parked inside
            // the paused WriteAsync (it may already be parked from idle).
            byte[] filler = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);
            for (int i = 0; i < 8; i++)
                control.TryWriteRawFrame(filler);
            await Task.Delay(50);

            // Saturate the rest of the slab. With the writer parked these
            // bytes accumulate and stay there.
            int prefill = 0;
            while (control.TryWriteRawFrame(filler) && prefill < 100_000)
                prefill++;
            Assert.False(control.TryWriteRawFrame(filler),
                "Test precondition: server control slab must be saturated before triggering INIT.");

            // Open channels from the client. INIT frames arrive at the server
            // and trigger SendInitAck — which must fail (slab full) and enqueue.
            var opens = new List<IDisposable>();
            for (int i = 0; i < 8; i++)
            {
                opens.Add(client.OpenChannel(new ChannelOptions { ChannelId = $"saturated-open-{i}" }));
            }

            bool enqueued = await WaitUntilAsync(
                () => !serverConn.PendingInitAcks.IsEmpty,
                TimeSpan.FromSeconds(3));

            Assert.True(enqueued,
                $"HandleInitFrame must enqueue the channel index into PendingInitAcks when SendInitAck returns false. Current count: {serverConn.PendingInitAcks.Count}.");

            // Resume writes so DisposeAsync can drain cleanly.
            pausableServerSide.ResumeWrites();

            foreach (var ch in opens)
            {
                if (ch is IAsyncDisposable a) await a.DisposeAsync();
                else ch.Dispose();
            }
        }
        finally
        {
            pausableServerSide.ResumeWrites();
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Wraps an <see cref="IStreamPair"/> with a pausable write stream so
    /// tests can hold the underlying writer parked inside its WriteAsync
    /// while they manipulate slab state from another thread.
    /// </summary>
    private sealed class PausableStreamPair : IStreamPair
    {
        private readonly IStreamPair _inner;
        private readonly PausableWriteStream _write;

        public PausableStreamPair(IStreamPair inner)
        {
            _inner = inner;
            _write = new PausableWriteStream(inner.WriteStream);
        }

        public Stream ReadStream => _inner.ReadStream;
        public Stream WriteStream => _write;

        public void PauseWrites() => _write.Pause();
        public void ResumeWrites() => _write.Resume();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class PausableWriteStream(Stream inner) : Stream
    {
        private readonly ManualResetEventSlim _gate = new(initialState: true);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Pause() => _gate.Reset();
        public void Resume() => _gate.Set();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            while (!_gate.Wait(20, ct)) { }
            await inner.WriteAsync(buffer, ct);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
