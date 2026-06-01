namespace NetConduit.UnitTests;

using System.Buffers.Binary;
using System.Text;
using NetConduit.Internal;

[Collection("Sequential")]
public sealed class ReadChannelFrameValidationTests
{
    private const ushort UserChannelIndex = 1;
    private const ushort UnknownUserChannelIndex = 123;

    [Fact]
    public async Task DataFrameForUnknownChannel_RaisesUnknownChannel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendUserFrameAsync(UnknownUserChannelIndex, FrameFlags.Data, Encoding.ASCII.GetBytes("abc"), cts.Token);

        await AssertMuxErrorAsync(errorTask, ErrorCode.UnknownChannel, cts.Token);
    }

    [Fact]
    public async Task UserChannelPingFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("user-ping", cts.Token);
        var errorTask = context.CaptureNextError();
        byte[] payload = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(payload, 0x1122334455667788L);

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Ping, payload, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ZeroLengthDataFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("zero-data", cts.Token);
        var readTask = accepted.ReadAsync(new byte[16], cts.Token).AsTask();
        var errorTask = context.CaptureNextError();

        Assert.False(readTask.IsCompleted);

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Data, ReadOnlyMemory<byte>.Empty, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task FinFrameWithPayload_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("fin-extra", cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Fin, Encoding.UTF8.GetBytes("extra"), cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ShortErrFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("err-short", cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Err, ReadOnlyMemory<byte>.Empty, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ErrFrameWithReservedNoneCode_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("err-none", cts.Token);
        var errorTask = context.CaptureNextError();
        byte[] payload = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)ErrorCode.None);

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Err, payload, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ValidErrFrame_MakesReadAsyncThrowChannelClosedException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        await using var accepted = await context.OpenAcceptedChannelAsync("remote-error", cts.Token);
        byte[] payload = BuildErrPayload(ErrorCode.ProtocolError, "boom");

        await context.SendUserFrameAsync(UserChannelIndex, FrameFlags.Err, payload, cts.Token);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await accepted.ReadAsync(new byte[1], cts.Token));
        Assert.Equal(ChannelCloseReason.RemoteError, closed.CloseReason);
        Assert.Equal(ChannelCloseReason.RemoteError, accepted.CloseReason);
        var remoteError = Assert.IsType<MultiplexerException>(accepted.CloseException);
        Assert.Equal(ErrorCode.ProtocolError, remoteError.ErrorCode);
        Assert.Contains("boom", remoteError.Message, StringComparison.Ordinal);
    }

    private static async Task AssertMuxErrorAsync(Task<Exception> errorTask, ErrorCode errorCode, CancellationToken ct)
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), ct);
        var completed = await Task.WhenAny(errorTask, timeoutTask);

        Assert.Same(errorTask, completed);
        var exception = await errorTask;
        var muxError = Assert.IsType<MultiplexerException>(exception);
        Assert.Equal(errorCode, muxError.ErrorCode);
    }

    private static byte[] BuildErrPayload(ErrorCode code, string message)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] payload = new byte[sizeof(ushort) + messageBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)code);
        messageBytes.CopyTo(payload.AsSpan(sizeof(ushort)));
        return payload;
    }

    private static async Task AssertProtocolErrorAsync(Task<Exception> errorTask, CancellationToken ct)
    {
        await AssertMuxErrorAsync(errorTask, ErrorCode.ProtocolError, ct);
    }

    private sealed class RawMuxContext(StreamMultiplexer server, IStreamPair rawPeer, DuplexMemoryStream duplex) : IAsyncDisposable
    {
        public static async Task<RawMuxContext> CreateAsync(CancellationToken ct)
        {
            var duplex = new DuplexMemoryStream();
            var server = StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult(duplex.SideB),
                PingInterval = TimeSpan.Zero,
                MaxAutoReconnectAttempts = 0,
            });

            server.Start();
            await CompleteHandshakeAsync(duplex.SideA, server, ct);
            return new RawMuxContext(server, duplex.SideA, duplex);
        }

        public Task<Exception> CaptureNextError()
        {
            var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Error += (_, args) => error.TrySetResult(args.Exception);
            return error.Task;
        }

        public async Task<IReadChannel> OpenAcceptedChannelAsync(string channelId, CancellationToken ct)
        {
            var acceptTask = server.AcceptChannelAsync(channelId, ct);
            await SendUserFrameAsync(UserChannelIndex, FrameFlags.Init, Encoding.UTF8.GetBytes(channelId), ct);
            var accepted = await acceptTask;
            await accepted.WaitForReadyAsync(ct);
            return accepted;
        }

        public async ValueTask SendUserFrameAsync(ushort channelIndex, FrameFlags flags, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            byte[] frame = new byte[FrameHeader.Size + payload.Length];
            FrameHeader.WriteTo(frame, channelIndex, flags, payload.Length);
            payload.CopyTo(frame.AsMemory(FrameHeader.Size));
            await rawPeer.WriteStream.WriteAsync(frame, ct);
            await rawPeer.WriteStream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync();
            await ((IAsyncDisposable)duplex).DisposeAsync();
        }

        private static async Task CompleteHandshakeAsync(IStreamPair rawPeer, StreamMultiplexer server, CancellationToken ct)
        {
            byte[] serverHandshake = new byte[FrameHeader.Size + 20];
            await rawPeer.ReadStream.ReadExactlyAsync(serverHandshake, ct);

            byte[] clientHandshake = new byte[FrameHeader.Size + 20];
            FrameHeader.WriteTo(clientHandshake, ChannelConstants.ControlChannel, FrameFlags.Ctrl, payloadLength: 20);
            Guid.NewGuid().TryWriteBytes(clientHandshake.AsSpan(FrameHeader.Size, 16));
            BinaryPrimitives.WriteUInt32BigEndian(
                clientHandshake.AsSpan(FrameHeader.Size + 16, sizeof(uint)),
                (uint)FrameConstants.DefaultSlabSize);

            await rawPeer.WriteStream.WriteAsync(clientHandshake, ct);
            await rawPeer.WriteStream.FlushAsync(ct);
            await server.WaitForReadyAsync(ct);
        }
    }
}
