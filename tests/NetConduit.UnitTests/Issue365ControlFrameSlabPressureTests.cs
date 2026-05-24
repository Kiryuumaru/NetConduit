using System.Reflection;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// #365 regression: <see cref="StreamMultiplexer"/>'s control-frame send paths
/// (Pong reply via <c>TrySendControlFrame</c>, <c>SendInitAck</c>, and the
/// GoAway-staging path in <c>GoAwayAsync</c>) must use the non-throwing
/// <c>TryWriteRawFrame</c> (parallel of #291/#336/#355). Pre-fix: the
/// throwing <c>WriteRawFrame</c> variant was reachable from the reader thread
/// (Pong/INIT-ACK) and from the public <c>GoAwayAsync</c> surface; under
/// transient control-slab pressure it would fault the mux or leave it
/// half-shut without a GoAway frame on the wire.
/// </summary>
public sealed class Issue365ControlFrameSlabPressureTests
{
    [Fact]
    public async Task TrySendControlFrame_Pong_UnderSlabPressure_ReturnsFalse_DoesNotThrow()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            PingInterval = TimeSpan.Zero,
        });
        try
        {
            AttachSaturatedControlChannel(mux);

            bool? staged = null;
            var ex = Record.Exception(() => staged = InvokeTrySendControlFrame(mux, FrameFlags.Pong, new byte[8]));
            Assert.Null(ex);
            Assert.False(staged);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendInitAck_UnderSlabPressure_DoesNotThrow()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            PingInterval = TimeSpan.Zero,
        });
        try
        {
            AttachSaturatedControlChannel(mux);

            var ex = Record.Exception(() => InvokeSendInitAck(mux, channelIndex: 7));
            Assert.Null(ex);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task GoAwayAsync_UnderSlabPressure_DoesNotThrow_AndCompletesTeardown()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromMilliseconds(50),
        });
        try
        {
            AttachSaturatedControlChannel(mux);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ex = await Record.ExceptionAsync(() => mux.GoAwayAsync(cts.Token).AsTask());
            Assert.Null(ex);
            Assert.True(mux.IsShuttingDown);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    private static WriteChannel AttachSaturatedControlChannel(StreamMultiplexer mux)
    {
        var owner = (IChannelOwner)mux;
        var control = new WriteChannel(
            channelId: "ctrl",
            channelIndex: ChannelConstants.ControlChannel,
            priority: ChannelPriority.Highest,
            slabSize: FrameConstants.MinSlabSize,
            sendTimeout: TimeSpan.FromSeconds(5),
            owner: owner);
        control.MarkOpen();

        byte[] filler = ControlFrameBuilder.BuildAckFrame(channelIndex: 1, consumedPosition: 0);
        for (int i = 0; i < (FrameConstants.MinSlabSize / filler.Length) + 16; i++)
        {
            control.TryWriteRawFrame(filler);
        }

        byte[] pongProbe = ControlFrameBuilder.BuildControlFrame(FrameFlags.Pong, new byte[8]);
        Assert.False(control.TryWriteRawFrame(pongProbe),
            "Test precondition: control slab must be fully saturated.");

        var connField = typeof(StreamMultiplexer).GetField("_conn",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connField);
        var conn = (MuxConnection)connField!.GetValue(mux)!;
        conn.ControlChannel = control;
        return control;
    }

    private delegate bool TrySendControlFrameDelegate(FrameFlags flags, ReadOnlySpan<byte> payload);

    private static bool InvokeTrySendControlFrame(StreamMultiplexer mux, FrameFlags flags, byte[] payload)
    {
        var method = typeof(StreamMultiplexer).GetMethod("TrySendControlFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var del = (TrySendControlFrameDelegate)method!.CreateDelegate(typeof(TrySendControlFrameDelegate), mux);
        return del(flags, payload);
    }

    private static void InvokeSendInitAck(StreamMultiplexer mux, ushort channelIndex)
    {
        var method = typeof(StreamMultiplexer).GetMethod("SendInitAck",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(mux, [channelIndex]);
    }
}
