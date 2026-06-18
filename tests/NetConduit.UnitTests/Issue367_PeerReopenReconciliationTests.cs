namespace NetConduit.UnitTests;

/// <summary>
/// Regression for #367 — when the peer closes-and-reopens the same
/// <c>ChannelId</c> before the local consumer has disposed the closed
/// <see cref="IReadChannel"/>, the local mux must reconcile the registry
/// slot atomically with the inbound INIT (eviction at the conflict point
/// in <c>HandleInitFrame</c>) instead of throwing
/// <c>MultiplexerException(ChannelExists)</c> and faulting the reader
/// into an infinite reconnect-and-refault loop.
///
/// The fix is intentionally scoped to the peer-reopen moment. The two
/// orthogonal contracts MUST remain intact:
///   1. The original consumer's reference to the closed channel keeps
///      working for buffered post-FIN drain and close-reason inspection.
///   2. <c>AcceptChannel(id)</c> still returns the still-registered
///      closed channel until the local consumer disposes it, so a late
///      drain caller can pick it up — the eviction only fires when the
///      peer actually reopens with a new INIT.
/// The freshly-created replacement channel is delivered through the
/// generic <c>AcceptChannelsAsync</c> stream rather than displacing the
/// id-bound lookup.
/// </summary>
public sealed class Issue367_PeerReopenReconciliationTests
{
    [Fact]
    public async Task PeerReopensChannelId_BeforeLocalReadChannelIsDisposed_DoesNotFaultReader()
    {
        var duplex = new DuplexMemoryStream();
        await using var muxA = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        await using var muxB = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        muxA.Start();
        muxB.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            muxA.WaitForReadyAsync(cts.Token),
            muxB.WaitForReadyAsync(cts.Token));

        // Start a background pump on the generic accept stream so the peer's
        // reopen INIT has a destination to deliver rc2 into. This mirrors a
        // realistic application that accepts inbound channels through both
        // a named (AcceptChannel(id)) and a generic (AcceptChannelsAsync())
        // path — the legacy bug faulted the reader before any accept
        // mechanism on this side could pick up the reopened channel.
        var genericAccepts = new System.Collections.Concurrent.ConcurrentQueue<IReadChannel>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in muxB.AcceptChannelsAsync(ct: cts.Token))
                    genericAccepts.Enqueue(ch);
            }
            catch (OperationCanceledException) { }
        });

        var rc1Task = muxB.AcceptChannelAsync("X", cts.Token);
        var wc1 = muxA.OpenChannel(new ChannelOptions { ChannelId = "X" });
        var rc1 = await rc1Task;
        var payload1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await wc1.WriteAsync(payload1, cts.Token);
        var read1 = new byte[payload1.Length];
        await ReadExactAsync(rc1, read1, cts.Token);
        Assert.Equal(payload1, read1);

        var rc1Closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rc1.Closed += (_, _) => rc1Closed.TrySetResult();
        await wc1.CloseAsync(cts.Token);
        await rc1Closed.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.Equal(ChannelCloseReason.RemoteFin, rc1.CloseReason);

        var muxBDisconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        muxB.Disconnected += (_, e) => muxBDisconnected.TrySetResult(e);

        // The local consumer intentionally has not disposed rc1 — it
        // remains in the registry holding the slot for ChannelId "X" so
        // the consumer can still inspect the close reason and drain any
        // buffered post-FIN bytes. Per #367 the mux MUST nevertheless
        // permit the peer to immediately reopen "X" without faulting:
        // the legacy behaviour was a ChannelExists throw → reader fault →
        // infinite reconnect-and-refault loop on every INIT replay.
        var wc2 = muxA.OpenChannel(new ChannelOptions { ChannelId = "X" });
        var payload2 = new byte[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0x10, 0x11 };
        await wc2.WriteAsync(payload2, cts.Token);

        // The replacement channel is delivered through the generic
        // accept stream because the named AcceptChannel("X") lookup
        // still resolves to the (intentionally retained) closed rc1.
        IReadChannel? rc2 = null;
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(2).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (genericAccepts.TryDequeue(out rc2)) break;
            await Task.Delay(10, cts.Token);
        }
        Assert.NotNull(rc2);
        Assert.NotSame(rc1, rc2);
        Assert.Equal("X", rc2!.ChannelId);

        var read2 = new byte[payload2.Length];
        await ReadExactAsync(rc2, read2, cts.Token);
        Assert.Equal(payload2, read2);

        Assert.True(muxA.IsConnected);
        Assert.True(muxB.IsConnected);
        Assert.False(muxBDisconnected.Task.IsCompleted, "muxB must not have disconnected during the reopen.");
    }

    [Fact]
    public async Task ClosedReadChannel_RemainsAcceptableForDrainUntilConsumerDisposes()
    {
        // Companion invariant: the eviction in HandleInitFrame must NOT
        // make the Closed channel disappear from AcceptChannel before
        // the peer actually reopens — that would break the documented
        // "drain buffered post-FIN bytes after Closed" contract for a
        // late drain caller.
        var duplex = new DuplexMemoryStream();
        await using var muxA = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        await using var muxB = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        muxA.Start();
        muxB.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            muxA.WaitForReadyAsync(cts.Token),
            muxB.WaitForReadyAsync(cts.Token));

        var rc1Task = muxB.AcceptChannelAsync("X", cts.Token);
        var wc1 = muxA.OpenChannel(new ChannelOptions { ChannelId = "X" });
        var rc1 = await rc1Task;

        var rc1Closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rc1.Closed += (_, _) => rc1Closed.TrySetResult();
        await wc1.CloseAsync(cts.Token);
        await rc1Closed.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.Equal(ChannelState.Closed, rc1.State);

        // Without a peer reopen, AcceptChannel returns the still-registered
        // rc1 for drain.
        var rc1Again = muxB.AcceptChannel("X");
        Assert.Same(rc1, rc1Again);
    }

    private static async Task ReadExactAsync(IReadChannel channel, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await channel.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
