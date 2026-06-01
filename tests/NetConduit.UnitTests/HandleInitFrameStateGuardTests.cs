using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for the structural fix that closes the TOCTOU window
/// in <see cref="StreamMultiplexer.HandleInitFrame"/>'s pending-accept
/// adoption path (issue #428) and the symmetric shutdown guard requested
/// by issue #434.
///
/// HandleInitFrame reads a pending channel's State under
/// <c>_registry.AcceptLock</c>. ReadChannel.SetClosed historically ran
/// under its own instance lock only, so a consumer disposing a pending
/// channel could land between the dispatcher's lock-free State read and
/// its commit, leaving a Closed instance registered (slab returned to
/// ArrayPool, frame writes faulting). The fix routes pending-accept
/// disposal through <c>IChannelOwner.CompletePendingAcceptCancel</c> so
/// the close runs under the same AcceptLock the dispatcher holds.
/// </summary>
public sealed class HandleInitFrameStateGuardTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PendingAcceptReadChannel_DisposeAsync_RunsCloseUnderOwnerAcceptLock()
    {
        var owner = new RecordingOwner();
        var channel = new ReadChannel("pending-id", channelIndex: 0, ChannelPriority.Normal, 64 * 1024, owner);

        await channel.DisposeAsync();

        Assert.Equal(1, owner.CompletePendingAcceptCancelCalls);
        Assert.Equal("pending-id", owner.LastCompletePendingAcceptCancelChannelId);
        Assert.True(owner.CloseActionRanInsideCompletePendingAcceptCancel,
            "ReadChannel.DisposeAsync must invoke the SetClosed action from within CompletePendingAcceptCancel so it runs under the owner's AcceptLock.");
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public void PendingAcceptReadChannel_Dispose_RunsCloseUnderOwnerAcceptLock()
    {
        var owner = new RecordingOwner();
        var channel = new ReadChannel("pending-id", channelIndex: 0, ChannelPriority.Normal, 64 * 1024, owner);

        channel.Dispose();

        Assert.Equal(1, owner.CompletePendingAcceptCancelCalls);
        Assert.True(owner.CloseActionRanInsideCompletePendingAcceptCancel);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public async Task WiredReadChannel_DisposeAsync_DoesNotRouteThroughCompletePendingAcceptCancel()
    {
        // Once a channel has been wired (channelIndex != 0), the TOCTOU
        // window is closed and routing through CompletePendingAcceptCancel
        // would be unnecessary work on a registry slot keyed by channel id
        // that no longer applies.
        var owner = new RecordingOwner();
        var channel = new ReadChannel("wired", channelIndex: 7, ChannelPriority.Normal, 64 * 1024, owner);

        await channel.DisposeAsync();

        Assert.Equal(0, owner.CompletePendingAcceptCancelCalls);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public async Task PendingAcceptDispose_RacedWithPeerInit_NeverAdoptsDisposedInstance()
    {
        const int iterations = 100;

        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        using var acceptCts = new CancellationTokenSource(TestTimeout);
        var acceptedFresh = new HashSet<IReadChannel>(ReferenceEqualityComparer.Instance);
        var acceptLock = new object();

        var acceptPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in server.AcceptChannelsAsync(ct: acceptCts.Token))
                {
                    lock (acceptLock) acceptedFresh.Add(ch);
                }
            }
            catch (OperationCanceledException) { }
        });

        for (int i = 0; i < iterations; i++)
        {
            string id = $"race-{i}";
            var pending = (ReadChannel)server.AcceptChannel(id);

            var openTask = Task.Run(() => client.OpenChannel(id));
            await pending.DisposeAsync();
            var writer = await openTask;

            // Structural invariant: the disposed pending must NEVER surface
            // through the default accept stream. If it did, HandleInitFrame
            // had adopted a Closed instance under the bug.
            lock (acceptLock)
            {
                Assert.DoesNotContain(pending, acceptedFresh);
            }

            await writer.DisposeAsync();
        }

        acceptCts.Cancel();
        try { await acceptPump; } catch (OperationCanceledException) { }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private sealed class RecordingOwner : IChannelOwner
    {
        public int CompletePendingAcceptCancelCalls;
        public string? LastCompletePendingAcceptCancelChannelId;
        public bool CloseActionRanInsideCompletePendingAcceptCancel;

        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelOpened(string channelId) { }
        public void NotifyChannelCompleted(uint channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public bool SendAck(uint channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;

        public void CompletePendingAcceptCancel(string channelId, Action closeAction)
        {
            CompletePendingAcceptCancelCalls++;
            LastCompletePendingAcceptCancelChannelId = channelId;
            CloseActionRanInsideCompletePendingAcceptCancel = true;
            closeAction();
        }
    }
}
