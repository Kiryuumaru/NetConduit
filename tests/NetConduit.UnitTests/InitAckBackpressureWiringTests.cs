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
    /// Saturates the server's control-channel slab from outside the mux so
    /// that <c>TryWriteRawFrame</c> (and therefore <c>SendInitAck</c>) cannot
    /// stage a fresh frame. A keeper task re-fills the slab whenever the
    /// writer drains a slot, holding it at saturation across the test window.
    /// The client then opens a real channel; its INIT is processed by the
    /// server's reader thread, which calls <c>SendInitAck</c>, which fails.
    ///
    /// Pre-fix: the boolean return of <c>SendInitAck</c> is discarded, so
    /// the failure is silently dropped and <c>PendingInitAcks</c> stays
    /// empty — the assertion fails.
    ///
    /// Post-fix: the call site enqueues <c>header.ChannelIndex</c> when
    /// <c>SendInitAck</c> returns false, so the queue grows to at least 1.
    /// </summary>
    [Fact]
    public async Task HandleInitFrame_EnqueuesIndex_WhenControlSlabSaturated()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var keeperCts = new CancellationTokenSource();
        try
        {
            var serverConn = GetMuxConnection(server);
            var control = serverConn.ControlChannel
                ?? throw new InvalidOperationException("Server control channel not initialized.");

            // Pre-saturate, then keep the slab full with a background filler.
            // 16-byte ACK frames are the smallest control payload, so the
            // saturation persists in tight memory.
            byte[] filler = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);
            int prefill = 0;
            while (control.TryWriteRawFrame(filler) && prefill < 100_000)
                prefill++;

            // Precondition: the slab must be full so the next attempt is
            // guaranteed to fail.
            Assert.False(control.TryWriteRawFrame(filler),
                "Test precondition: server control slab must be saturated before triggering INIT.");

            var keeperToken = keeperCts.Token;
            var keeper = Task.Run(() =>
            {
                while (!keeperToken.IsCancellationRequested)
                {
                    if (!control.TryWriteRawFrame(filler))
                        Thread.SpinWait(50);
                }
            }, keeperToken);

            // Open a fresh channel from the client; its INIT lands on the
            // server while the slab is saturated.
            await using var ch = (NetConduit.Interfaces.IWriteChannel)client.OpenChannel(new ChannelOptions
            {
                ChannelId = "saturated-open",
            });

            bool enqueued = await WaitUntilAsync(
                () => !serverConn.PendingInitAcks.IsEmpty,
                TimeSpan.FromSeconds(3));

            Assert.True(enqueued,
                "HandleInitFrame must enqueue the channel index into PendingInitAcks when SendInitAck returns false.");

            keeperCts.Cancel();
            try { await keeper; } catch (OperationCanceledException) { }
        }
        finally
        {
            keeperCts.Cancel();
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
