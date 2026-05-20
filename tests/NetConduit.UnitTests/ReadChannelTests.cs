using NetConduit.Internal;

namespace NetConduit.UnitTests;

public sealed class ReadChannelTests
{
    private static ReadChannel CreateChannel(int slabSize = 64 * 1024)
    {
        var channel = new ReadChannel(
            channelId: "test",
            channelIndex: 1,
            priority: ChannelPriority.Normal,
            slabSize: slabSize);
        channel.MarkOpen();
        return channel;
    }

    [Fact]
    public async Task ReadAsync_ReturnsBufferedData()
    {
        var channel = CreateChannel();
        byte[] payload = [10, 20, 30, 40, 50];

        channel.ReceivePayload(FrameFlags.Data, payload);

        byte[] buffer = new byte[10];
        int read = await channel.ReadAsync(buffer);

        Assert.Equal(5, read);
        Assert.Equal(payload, buffer[..5]);
    }

    [Fact]
    public async Task ReadAsync_DirectDelivery_WhenReaderWaiting()
    {
        var channel = CreateChannel();
        byte[] payload = [1, 2, 3];

        // Start read before data arrives — this blocks
        byte[] buffer = new byte[10];
        var readTask = channel.ReadAsync(buffer).AsTask();

        // Data arrives — should complete the blocked read
        channel.ReceivePayload(FrameFlags.Data, payload);

        int read = await readTask;
        Assert.Equal(3, read);
        Assert.Equal(payload, buffer[..3]);
    }

    [Fact]
    public async Task ReadAsync_MultipleReceives_BuffersInSlab()
    {
        var channel = CreateChannel();
        channel.ReceivePayload(FrameFlags.Data, [1, 2, 3]);
        channel.ReceivePayload(FrameFlags.Data, [4, 5, 6]);

        byte[] buffer = new byte[10];
        int read = await channel.ReadAsync(buffer);

        Assert.Equal(6, read);
        Assert.Equal((byte[]) [1, 2, 3, 4, 5, 6], buffer[..6]);
    }

    [Fact]
    public async Task ReadAsync_PartialRead_LeavesRemainderInSlab()
    {
        var channel = CreateChannel();
        channel.ReceivePayload(FrameFlags.Data, [1, 2, 3, 4, 5]);

        byte[] small = new byte[3];
        int read1 = await channel.ReadAsync(small);
        Assert.Equal(3, read1);
        Assert.Equal((byte[]) [1, 2, 3], small);

        byte[] rest = new byte[10];
        int read2 = await channel.ReadAsync(rest);
        Assert.Equal(2, read2);
        Assert.Equal((byte[]) [4, 5], rest[..2]);
    }

    [Fact]
    public async Task ReadAsync_ReturnZero_OnClosedChannelWithNoData()
    {
        var channel = CreateChannel();
        channel.SetClosed(ChannelCloseReason.RemoteFin);

        byte[] buffer = new byte[10];
        int read = await channel.ReadAsync(buffer);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ReadAsync_ReturnsRemainingData_BeforeEOF()
    {
        var channel = CreateChannel();
        channel.ReceivePayload(FrameFlags.Data, [1, 2, 3]);
        channel.SetClosed(ChannelCloseReason.RemoteFin);

        byte[] buffer = new byte[10];
        int read1 = await channel.ReadAsync(buffer);
        Assert.Equal(3, read1);

        int read2 = await channel.ReadAsync(buffer);
        Assert.Equal(0, read2);
    }

    [Fact]
    public async Task ReadAsync_EmptyBuffer_ReturnsZero()
    {
        var channel = CreateChannel();
        int read = await channel.ReadAsync(Memory<byte>.Empty);
        Assert.Equal(0, read);
    }

    [Fact]
    public void ReceivePayload_Fin_ClosesChannel()
    {
        var channel = CreateChannel();
        ChannelCloseReason? reason = null;
        channel.Closed += (_, e) => reason = e.Reason;

        channel.ReceivePayload(FrameFlags.Fin, ReadOnlySpan<byte>.Empty);

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.RemoteFin, reason);
    }

    [Fact]
    public void ReceivePayload_Err_ClosesChannel()
    {
        var channel = CreateChannel();
        channel.ReceivePayload(FrameFlags.Err, ReadOnlySpan<byte>.Empty);

        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.RemoteError, channel.CloseReason);
    }

    [Fact]
    public async Task ReadAsync_BlockedRead_CompletesWithZeroOnClose()
    {
        var channel = CreateChannel();

        byte[] buffer = new byte[10];
        var readTask = channel.ReadAsync(buffer).AsTask();

        // Close wakes the blocked reader with EOF
        channel.SetClosed(ChannelCloseReason.MuxDisposed);

        int read = await readTask;
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task DirectDelivery_BuffersOverflowInSlab()
    {
        var channel = CreateChannel();

        // Start a read with a small buffer
        byte[] smallBuf = new byte[3];
        var readTask = channel.ReadAsync(smallBuf).AsTask();

        // Deliver more data than the buffer can hold
        channel.ReceivePayload(FrameFlags.Data, [1, 2, 3, 4, 5]);

        int read = await readTask;
        Assert.Equal(3, read);
        Assert.Equal((byte[]) [1, 2, 3], smallBuf);

        // Remaining 2 bytes should be buffered in slab
        byte[] rest = new byte[10];
        int read2 = await channel.ReadAsync(rest);
        Assert.Equal(2, read2);
        Assert.Equal((byte[]) [4, 5], rest[..2]);
    }

    [Fact]
    public void Stats_TrackBytesAndFramesReceived()
    {
        var channel = CreateChannel();
        channel.ReceivePayload(FrameFlags.Data, new byte[50]);
        channel.ReceivePayload(FrameFlags.Data, new byte[30]);

        Assert.Equal(2, Volatile.Read(ref channel.Stats._framesReceived));
    }

    [Fact]
    public async Task CloseAsync_SetsClosedState()
    {
        var channel = CreateChannel();
        await channel.CloseAsync();
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public async Task ReadAsync_WithCancellation_Throws()
    {
        var channel = CreateChannel();
        using var cts = new CancellationTokenSource();

        byte[] buffer = new byte[10];
        var readTask = channel.ReadAsync(buffer, cts.Token).AsTask();

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task ReadAsync_UnregistersCancellationCallback_OnSuccess()
    {
        var channel = CreateChannel();
        using var cts = new CancellationTokenSource();

        byte[] buffer = new byte[10];
        var readTask = channel.ReadAsync(buffer, cts.Token).AsTask();
        channel.ReceivePayload(FrameFlags.Data, [1, 2, 3]);
        await readTask;

        var field = typeof(ReadChannel).GetField(
            "_readCancelReg",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var reg = (CancellationTokenRegistration)field!.GetValue(channel)!;
        Assert.Equal(default, reg);
    }

    [Fact]
    public async Task ReadAsync_UnregistersCancellationCallback_OnClose()
    {
        var channel = CreateChannel();
        using var cts = new CancellationTokenSource();

        byte[] buffer = new byte[10];
        var readTask = channel.ReadAsync(buffer, cts.Token).AsTask();
        channel.SetClosed(ChannelCloseReason.LocalClose);
        await readTask;

        var field = typeof(ReadChannel).GetField(
            "_readCancelReg",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var reg = (CancellationTokenRegistration)field!.GetValue(channel)!;
        Assert.Equal(default, reg);
    }

    [Fact]
    public async Task ReadAsync_DoesNotLeakRegistrations_OnSharedLongLivedToken()
    {
        // Leaked registrations keep ReadChannels alive on the CTS. With the
        // fix, completed reads remove their callback so the channel is
        // collectable even while the CTS is still alive.
        using var cts = new CancellationTokenSource();
        var refs = new List<WeakReference>();

        await Populate(refs, cts);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int alive = refs.Count(r => r.IsAlive);
        Assert.True(alive == 0, $"Expected all ReadChannels to be collectable; {alive}/{refs.Count} still alive (registration leak).");

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static async Task Populate(List<WeakReference> refs, CancellationTokenSource cts)
        {
            for (int i = 0; i < 200; i++)
            {
                var channel = CreateChannel();
                byte[] buffer = new byte[4];
                var task = channel.ReadAsync(buffer, cts.Token).AsTask();
                channel.ReceivePayload(FrameFlags.Data, [(byte)i]);
                await task;
                refs.Add(new WeakReference(channel));
            }
        }
    }
}
