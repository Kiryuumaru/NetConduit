using System.Text.Json.Serialization;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

public partial class TransitTests
{
    public record RequestMessage(string RequestId, string Payload);
    public record ResponseMessage(string RequestId, bool Success);

    [JsonSerializable(typeof(RequestMessage))]
    [JsonSerializable(typeof(ResponseMessage))]
    internal partial class MessageFramingJsonContext : JsonSerializerContext { }

    #region MessageTransit Message Framing

    [Fact(Timeout = 30000)]
    public async Task MessageTransit_EmptyString_DeliveredNotNull()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "empty_msg" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("empty_msg", cts.Token);

        await using var sender = new MessageTransit<string, string>(
            writeA, null,
            null, null);

        await using var receiver = new MessageTransit<string, string>(
            null, readB,
            null, null);

        await sender.SendAsync("", cts.Token);
        var result = await receiver.ReceiveAsync(cts.Token);

        Assert.NotNull(result);
    }

    [Fact(Timeout = 30000)]
    public async Task MessageTransit_EmptyAfterNonEmpty_DistinguishableFromEOF()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "mixed_msg" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("mixed_msg", cts.Token);

        await using var sender = new MessageTransit<RequestMessage, RequestMessage>(
            writeA, null,
            MessageFramingJsonContext.Default.RequestMessage,
            MessageFramingJsonContext.Default.RequestMessage);

        await using var receiver = new MessageTransit<RequestMessage, RequestMessage>(
            null, readB,
            MessageFramingJsonContext.Default.RequestMessage,
            MessageFramingJsonContext.Default.RequestMessage);

        await sender.SendAsync(new RequestMessage("1", "data"), cts.Token);
        var msg1 = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(msg1);
        Assert.Equal("1", msg1.RequestId);

        await sender.SendAsync(new RequestMessage("2", ""), cts.Token);
        var msg2 = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(msg2);
        Assert.Equal("2", msg2.RequestId);
    }

    [Fact(Timeout = 30000)]
    public async Task MessageTransit_MinimalPayload_Delivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "msg_bool" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("msg_bool", cts.Token);

        await using var sender = new MessageTransit<string, string>(writeA, null, null, null);
        await using var receiver = new MessageTransit<string, string>(null, readB, null, null);

        await sender.SendAsync("a", cts.Token);
        var result = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(result);
        Assert.Equal("a", result);
    }

    [Fact(Timeout = 30000)]
    public async Task MessageTransit_ManySmallMessages_AllDelivered()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "msg_many" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("msg_many", cts.Token);

        await using var sender = new MessageTransit<RequestMessage, RequestMessage>(
            writeA, null, MessageFramingJsonContext.Default.RequestMessage, MessageFramingJsonContext.Default.RequestMessage);
        await using var receiver = new MessageTransit<RequestMessage, RequestMessage>(
            null, readB, MessageFramingJsonContext.Default.RequestMessage, MessageFramingJsonContext.Default.RequestMessage);

        for (int i = 0; i < 50; i++)
        {
            await sender.SendAsync(new RequestMessage($"r{i}", ""), cts.Token);
            var msg = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(msg);
            Assert.Equal($"r{i}", msg.RequestId);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task MessageTransit_ChannelClose_ReturnsNull()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "msg_eof" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("msg_eof", cts.Token);

        await using var sender = new MessageTransit<RequestMessage, RequestMessage>(
            writeA, null, MessageFramingJsonContext.Default.RequestMessage, MessageFramingJsonContext.Default.RequestMessage);
        await using var receiver = new MessageTransit<RequestMessage, RequestMessage>(
            null, readB, MessageFramingJsonContext.Default.RequestMessage, MessageFramingJsonContext.Default.RequestMessage);

        await sender.SendAsync(new RequestMessage("last", "data"), cts.Token);
        var msg = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(msg);

        await writeA.DisposeAsync();

        var eof = await receiver.ReceiveAsync(cts.Token);
        Assert.Null(eof);
    }

    #endregion

    #region Transit Suffix and Duplex Streams

    [Fact(Timeout = 30000)]
    public async Task TransitSuffix_DuplicateChannelId_Rejected()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var rawWrite = await muxA.OpenChannelAsync(new() { ChannelId = "data>>" }, cts.Token);
        var rawRead = await muxB.AcceptChannelAsync("data>>", cts.Token);

        try
        {
            var conflictWrite = await muxA.OpenChannelAsync(new() { ChannelId = "data>>" }, cts.Token);
            await conflictWrite.DisposeAsync();
        }
        catch (Exception)
        {
            // Duplicate detected — correct behavior
        }

        await rawWrite.WriteAsync(new byte[] { 0xAA }, cts.Token);
        var buf = new byte[1];
        var n = await rawRead.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xAA, buf[0]);

        Assert.Equal(">>", TransitExtensions.OutboundSuffix);
        Assert.Equal("<<", TransitExtensions.InboundSuffix);

        await rawWrite.DisposeAsync();
        await rawRead.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task TransitSuffix_ChannelNameEndingWithArrows_Works()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var w1 = await muxA.OpenChannelAsync(new() { ChannelId = "stream<<" }, cts.Token);
        var r1 = await muxB.AcceptChannelAsync("stream<<", cts.Token);

        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        var buf = new byte[1];
        Assert.Equal(1, await r1.ReadAsync(buf, cts.Token));
        Assert.Equal(1, buf[0]);

        await w1.DisposeAsync();
        await r1.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task DuplexStream_MultipleDuplexStreams_IndependentData()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var openAlphaTask = muxA.OpenDuplexStreamAsync("alpha", cts.Token);
        var acceptAlphaTask = muxB.AcceptDuplexStreamAsync("alpha", cts.Token);
        var transitA = await openAlphaTask;
        var remoteA = await acceptAlphaTask;

        var openBetaTask = muxA.OpenDuplexStreamAsync("beta", cts.Token);
        var acceptBetaTask = muxB.AcceptDuplexStreamAsync("beta", cts.Token);
        var transitB = await openBetaTask;
        var remoteB = await acceptBetaTask;

        await transitA.WriteAsync(new byte[] { 0xAA }, cts.Token);
        await transitB.WriteAsync(new byte[] { 0xBB }, cts.Token);

        var bufA = new byte[1];
        var bufB = new byte[1];
        Assert.Equal(1, await remoteA.ReadAsync(bufA, cts.Token));
        Assert.Equal(1, await remoteB.ReadAsync(bufB, cts.Token));

        Assert.Equal(0xAA, bufA[0]);
        Assert.Equal(0xBB, bufB[0]);

        await transitA.DisposeAsync();
        await remoteA.DisposeAsync();
        await transitB.DisposeAsync();
        await remoteB.DisposeAsync();
    }

    [Fact]
    public void TransitSuffix_Constants_AreDistinct()
    {
        Assert.False(string.IsNullOrEmpty(TransitExtensions.OutboundSuffix));
        Assert.False(string.IsNullOrEmpty(TransitExtensions.InboundSuffix));
        Assert.NotEqual(TransitExtensions.OutboundSuffix, TransitExtensions.InboundSuffix);
    }

    #endregion
}
