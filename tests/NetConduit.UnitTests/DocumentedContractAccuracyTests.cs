using System.Buffers.Binary;
using System.Diagnostics;
using NetConduit.Enums;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for public-surface documentation claims that must remain
/// consistent with the implementation: <see cref="FrameFlags.Ack"/> wire size,
/// <see cref="ReadChannel"/> never visiting <see cref="ChannelState.Closing"/>,
/// and the order of <c>Disconnected</c>/<c>Reconnecting</c> relative to the
/// reconnect back-off wait.
/// </summary>
public sealed class DocumentedContractAccuracyTests
{
    // =====================================================================
    // FrameFlags.Ack: payload is 8-byte big-endian UInt64 (not 4 bytes).
    // Anchors the wire-format claim documented on the public enum member and
    // in docs/concepts/framing-protocol.md, against ControlFrameBuilder /
    // WriteChannel / StreamMultiplexer reader.
    // =====================================================================

    [Fact]
    public void AckFrame_PayloadIsEightByteBigEndianUInt64()
    {
        const ushort channelIndex = 7;
        const ulong consumedPosition = 0x0102_0304_0506_0708UL;

        byte[] frame = ControlFrameBuilder.BuildAckFrame(channelIndex, consumedPosition);

        Assert.Equal(FrameHeader.Size + 8, frame.Length);

        var header = FrameHeader.Parse(frame);
        Assert.Equal(channelIndex, header.ChannelIndex);
        Assert.Equal(FrameFlags.Ack, header.Flags);
        Assert.Equal(8, header.PayloadLength);

        ulong decoded = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(FrameHeader.Size, 8));
        Assert.Equal(consumedPosition, decoded);
    }

    // =====================================================================
    // ReadChannel never enters Closing state on any close reason. The docs
    // previously showed a unified Open->Closing->Closed diagram covering both
    // channel directions, but ReadChannel.SetClosed jumps straight to Closed
    // from every entry point (local CloseAsync, RemoteFin, RemoteError,
    // TransportFailed, MuxDisposed). A consumer that gates on State == Closing
    // for a read channel is gating on a state that can never hold.
    // =====================================================================

    [Theory]
    [InlineData(ChannelCloseReason.LocalClose)]
    [InlineData(ChannelCloseReason.RemoteFin)]
    [InlineData(ChannelCloseReason.RemoteError)]
    [InlineData(ChannelCloseReason.TransportFailed)]
    [InlineData(ChannelCloseReason.MuxDisposed)]
    public void ReadChannel_SetClosed_GoesDirectlyToClosed_WithoutVisitingClosing(ChannelCloseReason reason)
    {
        var channel = new ReadChannel("doc-lockin", 11, ChannelPriority.Normal, 64 * 1024);
        channel.MarkOpen();
        Assert.Equal(ChannelState.Open, channel.State);

        channel.SetClosed(reason);

        Assert.NotEqual(ChannelState.Closing, channel.State);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public async Task ReadChannel_CloseAsync_GoesDirectlyToClosed_WithoutVisitingClosing()
    {
        var channel = new ReadChannel("doc-lockin", 12, ChannelPriority.Normal, 64 * 1024);
        channel.MarkOpen();
        Assert.Equal(ChannelState.Open, channel.State);

        await channel.CloseAsync();

        Assert.NotEqual(ChannelState.Closing, channel.State);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    // =====================================================================
    // Reconnect: the Reconnecting event fires BEFORE the back-off wait, not
    // after it. The docs previously diagrammed Disconnected -> wait ->
    // Reconnecting, which would put a multi-second gap between Disconnected
    // and Reconnecting. The actual order is Disconnected -> Reconnecting ->
    // wait -> factory call.
    // =====================================================================

    [Fact]
    public async Task Reconnecting_FiresBeforeBackoffWait_NotAfter()
    {
        await using var factory = new ReconnectableTransportFactory();

        TimeSpan backoff = TimeSpan.FromSeconds(2);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => factory.CreateSideA(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = backoff,
            MaxAutoReconnectDelay = backoff,
            AutoReconnectBackoffMultiplier = 1.0,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = ct => factory.CreateSideB(ct),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = backoff,
            MaxAutoReconnectDelay = backoff,
            AutoReconnectBackoffMultiplier = 1.0,
        });

        long disconnectedAtTicks = 0;
        long reconnectingAtTicks = 0;
        var reconnectingFired = new TaskCompletionSource();

        client.Disconnected += (_, _) =>
        {
            Interlocked.CompareExchange(ref disconnectedAtTicks, Stopwatch.GetTimestamp(), 0);
        };
        client.Reconnecting += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref reconnectingAtTicks, Stopwatch.GetTimestamp(), 0) == 0)
                reconnectingFired.TrySetResult();
        };

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        factory.KillCurrentTransport();

        await reconnectingFired.Task.WaitAsync(cts.Token);

        long disconnectedSnapshot = Interlocked.Read(ref disconnectedAtTicks);
        long reconnectingSnapshot = Interlocked.Read(ref reconnectingAtTicks);
        Assert.NotEqual(0, disconnectedSnapshot);
        Assert.NotEqual(0, reconnectingSnapshot);

        var gap = Stopwatch.GetElapsedTime(disconnectedSnapshot, reconnectingSnapshot);

        // Reconnecting must arrive well within the back-off window; the doc-implied
        // ordering would put it at >= backoff. Allow a generous slice (half the
        // back-off) to absorb scheduling jitter on slow CI.
        Assert.True(gap < TimeSpan.FromMilliseconds(backoff.TotalMilliseconds / 2),
            $"Reconnecting fired {gap.TotalMilliseconds:F0} ms after Disconnected; " +
            $"expected < {backoff.TotalMilliseconds / 2:F0} ms (Reconnecting must precede the back-off wait, not follow it).");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
