using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class WriteChannelTests
{
    private sealed class TestRouter : IChannelOwner
    {
        public int NotifyCount;
        public WriteChannel? LastNotified;

        public void NotifyReady(WriteChannel channel)
        {
            LastNotified = channel;
            Interlocked.Increment(ref NotifyCount);
        }

        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
        public int PeerMaxRecvPayload => FrameConstants.MaxSlabSize;
    }

    private static WriteChannel CreateChannel(TestRouter? router = null, int slabSize = 64 * 1024)
    {
        router ??= new TestRouter();
        var channel = new WriteChannel(
            channelId: "test",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: TimeSpan.FromSeconds(60),
            owner: router);
        channel.MarkOpen();
        return channel;
    }

    [Fact]
    public async Task WriteAsync_BuildsFrameInSlab()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);
        byte[] payload = [1, 2, 3, 4, 5];

        await channel.WriteAsync(payload);

        var ready = channel.TakeReady();
        Assert.Equal(FrameHeader.Size + 5, ready.Length);

        // Parse the header
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(1, header.ChannelIndex);
        Assert.Equal(FrameFlags.Data, header.Flags);
        Assert.Equal(5, header.PayloadLength);

        // Verify payload
        Assert.True(ready.Span.Slice(FrameHeader.Size, 5).SequenceEqual(payload));
    }

    [Fact]
    public async Task WriteAsync_NotifiesRouter()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);

        await channel.WriteAsync(new byte[10]);

        Assert.Equal(1, router.NotifyCount);
        Assert.Same(channel, router.LastNotified);
    }

    [Fact]
    public async Task WriteAsync_MultipleWrites_AccumulateInSlab()
    {
        var channel = CreateChannel();

        await channel.WriteAsync(new byte[100]);
        await channel.WriteAsync(new byte[200]);

        var ready = channel.TakeReady();
        int expectedSize = (FrameHeader.Size + 100) + (FrameHeader.Size + 200);
        Assert.Equal(expectedSize, ready.Length);
    }

    [Fact]
    public void TakeReady_ReturnsEmpty_WhenNoData()
    {
        var channel = CreateChannel();
        var ready = channel.TakeReady();
        Assert.True(ready.IsEmpty);
    }

    [Fact]
    public async Task MarkSent_AdvancesSentPosition()
    {
        var channel = CreateChannel();
        await channel.WriteAsync(new byte[10]);

        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        var readyAgain = channel.TakeReady();
        Assert.True(readyAgain.IsEmpty);
    }

    [Fact]
    public async Task WriteAsync_ThrowsOnClosedChannel()
    {
        var channel = CreateChannel();
        channel.SetClosed(ChannelCloseReason.LocalClose);

        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await channel.WriteAsync(new byte[10]));
    }

    [Fact]
    public void WriteInitFrame_BuildsCorrectFrame()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);
        byte[] channelIdBytes = "test-channel"u8.ToArray();

        channel.WriteInitFrame(channelIdBytes);

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Init, header.Flags);
        Assert.Equal(channelIdBytes.Length, header.PayloadLength);

        var payload = ready.Span.Slice(FrameHeader.Size, header.PayloadLength).ToArray();
        Assert.Equal(channelIdBytes, payload);
    }

    [Fact]
    public void WriteFinFrame_BuildsHeaderOnly()
    {
        var channel = CreateChannel();

        channel.WriteFinFrame();

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);
        Assert.Equal(0, header.PayloadLength);
        Assert.Equal(FrameHeader.Size, ready.Length);
    }

    [Fact]
    public void WriteAckFrame_Builds8BytePayload()
    {
        var channel = CreateChannel();

        channel.WriteAckFrame(12345);

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Ack, header.Flags);
        Assert.Equal(8, header.PayloadLength);
    }

    [Fact]
    public void WriteAckFrame_RoundTripsPositionAboveUInt32Max()
    {
        // Cumulative ACK positions are 64-bit so that a long-running channel
        // does not deadlock once it has received more than 2 GiB. Encode a
        // value above uint.MaxValue and assert the wire payload preserves it.
        var channel = CreateChannel();
        const ulong position = (ulong)uint.MaxValue + 12345UL;

        channel.WriteAckFrame(position);

        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Ack, header.Flags);
        Assert.Equal(8, header.PayloadLength);

        ulong decoded = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            ready.Span.Slice(FrameHeader.Size, 8));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public async Task OnAck_FreesSlabSpace()
    {
        // Use a small slab to test slab compaction
        var channel = CreateChannel(slabSize: 256);

        // Fill most of the slab
        await channel.WriteAsync(new byte[100]);
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // Acknowledge the data — frees space
        channel.OnAck(ready.Length);

        // Should be able to write again
        await channel.WriteAsync(new byte[100]);
    }

    [Fact]
    public void PrepareReplay_ResetsSentToAcked()
    {
        var channel = CreateChannel();

        channel.WriteInitFrame("ch"u8);
        var ready = channel.TakeReady();
        channel.MarkSent(ready.Length);

        // MarkSent now also advances ackedPos (no replay buffer without reconnection)
        // So PrepareReplay resets sentPos to ackedPos, which equals sentPos — nothing to replay
        channel.PrepareReplay();

        var replayed = channel.TakeReady();
        Assert.True(replayed.IsEmpty);
    }

    [Fact]
    public async Task Stats_TrackBytesSentAndFrames()
    {
        var channel = CreateChannel();

        await channel.WriteAsync(new byte[50]);
        await channel.WriteAsync(new byte[30]);

        Assert.Equal(80, Volatile.Read(ref channel.Stats._bytesSent));
        Assert.Equal(2, Volatile.Read(ref channel.Stats._framesSent));
    }

    [Fact]
    public async Task WriteAsync_ZeroBytes_IsNoop()
    {
        var router = new TestRouter();
        var channel = CreateChannel(router);

        await channel.WriteAsync(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0, router.NotifyCount);
        Assert.True(channel.TakeReady().IsEmpty);
    }

    [Fact]
    public async Task CloseAsync_SendsFinAndTransitionsState()
    {
        var channel = CreateChannel();

        await channel.CloseAsync();

        // CloseAsync queues a FIN frame and moves the channel to Closing.
        // The writer thread drains the FIN and triggers Closing -> Closed
        // via MarkSent (simulated here) per docs/concepts/channels.md.
        Assert.Equal(ChannelState.Closing, channel.State);
        var ready = channel.TakeReady();
        var header = FrameHeader.Parse(ready.Span);
        Assert.Equal(FrameFlags.Fin, header.Flags);

        channel.MarkSent(ready.Length);

        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public void SetClosed_RaisesOnClosedEvent()
    {
        var channel = CreateChannel();
        ChannelCloseReason? capturedReason = null;
        channel.Closed += (_, e) => capturedReason = e.Reason;

        channel.SetClosed(ChannelCloseReason.RemoteFin);

        Assert.Equal(ChannelCloseReason.RemoteFin, capturedReason);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public void Abort_ClosesWithoutFinWhenSlabHasNoSpace()
    {
        var channel = CreateChannel(slabSize: FrameHeader.Size + 4);
        channel.WriteRawFrame(new byte[FrameHeader.Size + 4]);

        channel.Abort(ChannelCloseReason.MuxDisposed);

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, channel.CloseReason);
    }

    [Fact]
    public void SetClosed_MuxDisposed_WithPendingData_ReturnsSlab()
    {
        // Reproduces issue: under MuxDisposed the writer loop is
        // cancelled and will never call MarkSent again. If HasPendingData()
        // gates slab return, the slab is leaked because no other path runs.
        var channel = CreateChannel();
        channel.WriteRawFrame(new byte[FrameHeader.Size + 32]);
        Assert.True(channel.HasPendingData());

        channel.SetClosed(ChannelCloseReason.MuxDisposed);

        Assert.True(SlabWasReturned(channel),
            "Expected slab to be returned to the pool under MuxDisposed even when pending data exists.");
    }

    [Fact]
    public void SetClosed_TransportFailed_WithPendingData_ReturnsSlab()
    {
        // Same structural bug via TryNotifyCompleted: if the writer thread is
        // dead under a terminal transport failure, deferring slab return on
        // HasPendingData leaks the slab.
        var channel = CreateChannel();
        channel.WriteRawFrame(new byte[FrameHeader.Size + 32]);
        Assert.True(channel.HasPendingData());

        channel.SetClosed(ChannelCloseReason.TransportFailed);

        Assert.True(SlabWasReturned(channel),
            "Expected slab to be returned to the pool under TransportFailed even when pending data exists.");
    }

    private static bool SlabWasReturned(WriteChannel channel)
    {
        var field = typeof(WriteChannel).GetField(
            "_slabReturned",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field!.GetValue(channel)! == 1;
    }

    private static WriteChannel CreateChannelWithShortTimeout(int slabSize, TimeSpan sendTimeout)
    {
        var router = new TestRouter();
        var channel = new WriteChannel(
            channelId: "test",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize,
            sendTimeout: sendTimeout,
            owner: router);
        channel.MarkOpen();
        return channel;
    }

    [Fact]
    public async Task WriteAsync_ChannelClosedWhileParked_ThrowsChannelClosedException_PromptlyNotTimeout()
    {
        // Regression for: a writer parked in _spaceAvailable.WaitAsync
        // must unwind with ChannelClosedException promptly when the channel
        // closes, instead of stalling for the full SendTimeout and throwing
        // TimeoutException.
        var sendTimeout = TimeSpan.FromSeconds(10);
        var channel = CreateChannelWithShortTimeout(slabSize: 256, sendTimeout: sendTimeout);

        // Fill the slab so the next write parks.
        int maxPayload = 256 - FrameHeader.Size;
        await channel.WriteAsync(new byte[maxPayload]);

        // Park a second writer.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var blocked = channel.WriteAsync(new byte[1]).AsTask();

        // Give it a moment to actually park in WaitAsync.
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted);

        // Close concurrently.
        channel.SetClosed(ChannelCloseReason.RemoteFin);

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(() => blocked);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Expected ChannelClosedException within ~2s, but write unwound after {sw.ElapsedMilliseconds} ms (SendTimeout was {sendTimeout.TotalSeconds}s).");
        Assert.Equal(ChannelCloseReason.RemoteFin, ex.CloseReason);
    }

    [Fact]
    public async Task WriteAsync_AbortDisposesSemaphore_ConvertedToChannelClosedException()
    {
        // Abort disposes the semaphore. A parked writer whose WaitAsync then
        // throws ObjectDisposedException must surface as ChannelClosedException,
        // not ObjectDisposedException.
        var channel = CreateChannelWithShortTimeout(slabSize: 256, sendTimeout: TimeSpan.FromSeconds(10));

        int maxPayload = 256 - FrameHeader.Size;
        await channel.WriteAsync(new byte[maxPayload]);

        var blocked = channel.WriteAsync(new byte[1]).AsTask();
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted);

        channel.Abort(ChannelCloseReason.TransportFailed);

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(() => blocked);
        Assert.Equal(ChannelCloseReason.TransportFailed, ex.CloseReason);
    }

    [Fact]
    public async Task WriteAsync_AfterClose_StateCheckThrowsChannelClosedException()
    {
        // Sanity: pre-existing top-of-method state check still fires for
        // writes issued after the channel is already Closed.
        var channel = CreateChannelWithShortTimeout(slabSize: 256, sendTimeout: TimeSpan.FromSeconds(60));
        channel.SetClosed(ChannelCloseReason.LocalClose);

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(() => channel.WriteAsync(new byte[1]).AsTask());
        Assert.Equal(ChannelCloseReason.LocalClose, ex.CloseReason);
    }

    [Fact]
    public async Task WriteAsync_ParkedWriter_StateClosedWhileSpaceFreed_ThrowsChannelClosedException()
    {
        // When a parked writer wakes because SetClosed/Abort released the
        // _spaceAvailable semaphore AND the slab has become compactable (e.g.,
        // because the writer task drained and acked the buffer just before
        // close), the writer would otherwise break out of the wait loop into
        // the commit lock with no state recheck. Without the in-commit-lock
        // state guard, the writer silently succeeds and writes a frame into
        // a slab that the close path is about to return to the ArrayPool —
        // corrupting any unrelated component that rents the same buffer.
        var channel = CreateChannelWithShortTimeout(slabSize: 256, sendTimeout: TimeSpan.FromSeconds(10));

        int maxPayload = 256 - FrameHeader.Size;
        await channel.WriteAsync(new byte[maxPayload]);

        var blocked = channel.WriteAsync(new byte[1]).AsTask();
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted);

        // Manually mark all bytes acked WITHOUT releasing the semaphore, so the
        // writer stays parked but the slab is now compactable. When SetClosed
        // releases the semaphore below, the writer wakes, the first-lock
        // TryCompactLocked frees the entire slab, and the writer breaks out
        // of the wait loop straight into the commit lock.
        var ackedField = typeof(WriteChannel).GetField(
            "_ackedPos",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(ackedField);
        ackedField!.SetValue(channel, maxPayload + FrameHeader.Size);

        channel.SetClosed(ChannelCloseReason.LocalClose);

        var ex = await Assert.ThrowsAsync<ChannelClosedException>(() => blocked);
        Assert.Equal(ChannelCloseReason.LocalClose, ex.CloseReason);
    }
}
