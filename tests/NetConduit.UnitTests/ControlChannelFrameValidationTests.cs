namespace NetConduit.UnitTests;

using System.Buffers.Binary;
using System.Text;
using NetConduit.Internal;

[Collection("Sequential")]
public sealed class ControlChannelFrameValidationTests
{
    [Fact]
    public async Task ControlChannelDataFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendControlFrameAsync(FrameFlags.Data, Encoding.ASCII.GetBytes("abc"), cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ControlChannelInitFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendControlFrameAsync(FrameFlags.Init, Encoding.UTF8.GetBytes("control-init"), cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ControlChannelAckFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();
        byte[] payload = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(payload, 0);

        await context.SendControlFrameAsync(FrameFlags.Ack, payload, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ControlChannelFinFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();

        await context.SendControlFrameAsync(FrameFlags.Fin, ReadOnlyMemory<byte>.Empty, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    [Fact]
    public async Task ControlChannelErrFrame_RaisesProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = await RawMuxContext.CreateAsync(cts.Token);
        var errorTask = context.CaptureNextError();
        byte[] payload = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)ErrorCode.ProtocolError);

        await context.SendControlFrameAsync(FrameFlags.Err, payload, cts.Token);

        await AssertProtocolErrorAsync(errorTask, cts.Token);
    }

    private static async Task AssertProtocolErrorAsync(Task<Exception> errorTask, CancellationToken ct)
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), ct);
        var completed = await Task.WhenAny(errorTask, timeoutTask);

        Assert.Same(errorTask, completed);
        var exception = await errorTask;
        var protocolError = Assert.IsType<MultiplexerException>(exception);
        Assert.Equal(ErrorCode.ProtocolError, protocolError.ErrorCode);
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

        public async ValueTask SendControlFrameAsync(FrameFlags flags, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            byte[] frame = new byte[FrameHeader.Size + payload.Length];
            FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, flags, payload.Length);
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
