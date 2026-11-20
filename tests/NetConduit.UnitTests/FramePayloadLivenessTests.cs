using System.Buffers.Binary;
using System.Collections.Concurrent;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Liveness guards for the framing receive path: a peer that declares a
/// frame length and then stalls or drips bytes must terminate the session
/// within the configured per-frame deadline instead of pinning the reader
/// loop (and its buffer) indefinitely, while honest full-rate traffic and
/// over-cap fast-reject behavior are preserved.
/// </summary>
[Collection("Sequential")]
public sealed class FramePayloadLivenessTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromSeconds(2);

    private static MultiplexerOptions LivenessOptions(IStreamPair pair) => new()
    {
        StreamFactory = _ => Task.FromResult<IStreamPair>(pair),
        PingInterval = TimeSpan.Zero,
        MaxAutoReconnectAttempts = 0,
        FrameReadTimeout = ShortDeadline,
    };

    [Fact(Timeout = 30000)]
    public async Task DeclaredLargePayload_TrickledBytes_TerminatesSessionWithinBound()
    {
        var pair = new ScriptedStreamPair();
        pair.Feed(BuildPeerHandshake());

        var mux = StreamMultiplexer.Create(LivenessOptions(pair));
        var errors = new ConcurrentQueue<Exception>();
        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        mux.Error += (_, e) => errors.Enqueue(e.Exception);
        mux.Disconnected += (_, e) => disconnected.TrySetResult(e);

        mux.Start();
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await mux.WaitForReadyAsync(readyCts.Token);

        // Declare 256 KiB (past the 64 KiB inline fast path), deliver a small
        // prefix plus a slow drip that must NOT reset the per-frame deadline.
        const int declared = 256 * 1024;
        pair.Feed(BuildDataHeader(channelIndex: 7, payloadLength: declared));
        pair.Feed(new byte[128]);

        using var dripCts = new CancellationTokenSource();
        var drip = Task.Run(async () =>
        {
            while (!dripCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), dripCts.Token);
                pair.Feed(new byte[] { 0xAA });
            }
        }, CancellationToken.None);

        try
        {
            var args = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(DisconnectReason.TransportError, args.Reason);

            var timeout = await WaitForErrorAsync(errors, ErrorCode.Timeout, TimeSpan.FromSeconds(5));
            Assert.NotNull(timeout);
        }
        finally
        {
            dripCts.Cancel();
            try { await drip; }
            catch (OperationCanceledException) { }
            await mux.DisposeAsync();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StalledHeader_TerminatesSessionWithinBound()
    {
        var pair = new ScriptedStreamPair();
        pair.Feed(BuildPeerHandshake());

        var mux = StreamMultiplexer.Create(LivenessOptions(pair));
        var errors = new ConcurrentQueue<Exception>();
        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        mux.Error += (_, e) => errors.Enqueue(e.Exception);
        mux.Disconnected += (_, e) => disconnected.TrySetResult(e);

        mux.Start();
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await mux.WaitForReadyAsync(readyCts.Token);

        // Partial 8-byte header, then silence: the header read itself stalls.
        var header = BuildDataHeader(channelIndex: 7, payloadLength: 1024);
        pair.Feed(header[..4]);

        try
        {
            var args = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(DisconnectReason.TransportError, args.Reason);

            var timeout = await WaitForErrorAsync(errors, ErrorCode.Timeout, TimeSpan.FromSeconds(5));
            Assert.NotNull(timeout);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task OversizedHeader_RejectsPromptlyWithProtocolError()
    {
        var pair = new ScriptedStreamPair();
        pair.Feed(BuildPeerHandshake());

        var mux = StreamMultiplexer.Create(LivenessOptions(pair));
        var errors = new ConcurrentQueue<Exception>();
        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        mux.Error += (_, e) => errors.Enqueue(e.Exception);
        mux.Disconnected += (_, e) => disconnected.TrySetResult(e);

        mux.Start();
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await mux.WaitForReadyAsync(readyCts.Token);

        // Over-cap length must fail at header parse without awaiting payload.
        // Awaited with a short ceiling: a correct rejection lands in
        // milliseconds with ProtocolError, never the liveness Timeout.
        pair.Feed(BuildDataHeader(channelIndex: 7, payloadLength: FrameConstants.MaxFramePayloadSize + 1));

        try
        {
            var args = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DisconnectReason.TransportError, args.Reason);

            var protocolError = await WaitForErrorAsync(errors, ErrorCode.ProtocolError, TimeSpan.FromSeconds(5));
            Assert.NotNull(protocolError);
            Assert.Contains("exceeds maximum", protocolError.Message);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact(Timeout = 60000)]
    public async Task FullRatePayload_CompletesUnderShortDeadline()
    {
        var duplex = new DuplexMemoryStream();
        await using var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
            FrameReadTimeout = ShortDeadline,
        });
        await using var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
            FrameReadTimeout = ShortDeadline,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writer = client.OpenChannel("full-rate");
        var reader = await server.AcceptChannelAsync("full-rate", cts.Token);

        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.DisposeAsync();

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
            ms.Write(buf, 0, read);

        Assert.Equal(data.Length, ms.Length);
        Assert.True(data.AsSpan().SequenceEqual(ms.ToArray()));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task ZeroFrameReadTimeout_DisablesHandshakeDeadline()
    {
        // Zero must disable the handshake read deadline exactly like
        // InfiniteTimeSpan: peer handshake bytes arriving after a delay
        // still complete. Pre-fix the handshake path only exempted
        // InfiniteTimeSpan, so CancelAfter(Zero) fired instantly and the
        // handshake faulted with a timeout before any byte arrived.
        var peerSessionId = Guid.NewGuid();
        byte[] frame = new byte[FrameHeader.Size + 20];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, 20);
        peerSessionId.TryWriteBytes(frame.AsSpan(FrameHeader.Size, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(FrameHeader.Size + 16, 4),
            (uint)FrameConstants.DefaultSlabSize);

        var transport = new DelayedReadStreamPair(frame, TimeSpan.FromMilliseconds(500));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await MuxHandshake.PerformInitialAsync(
            transport,
            Guid.NewGuid(),
            FrameConstants.DefaultSlabSize,
            cts.Token,
            TimeSpan.Zero);

        Assert.Equal(peerSessionId, result.RemoteSessionId);
        Assert.Equal(FrameConstants.DefaultSlabSize, result.PeerMaxRecvPayload);
    }

    [Fact(Timeout = 60000)]
    public async Task HandshakeLargeDeclaredPayload_TrickledBytes_CompletesExactly()
    {
        // Covers the handshake incremental-growth path: a reconnect frame
        // declaring a large position vector (past the 512B initial capacity)
        // arrives in small segments, and the reader must reassemble exactly
        // the declared bytes without ever allocating the declared length up
        // front. Pre-fix the handshake allocated the full declared length
        // before a single payload byte arrived.
        const int channelCount = 50_000; // 23B header + 500_000B positions
        var remoteSessionId = Guid.NewGuid();
        byte[] frame = BuildReconnectFrame(remoteSessionId, channelCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new SegmentedReadStreamPair(frame, segmentSize: 1024);

        List<ChannelReplayPosition> applied = null!;
        var result = await MuxHandshake.PerformReconnectAsync(
            transport,
            Guid.NewGuid(),
            remoteSessionId,
            FrameConstants.DefaultSlabSize,
            Array.Empty<ChannelReplayPosition>(),
            positions => applied = new List<ChannelReplayPosition>(positions),
            cts.Token,
            Timeout.InfiniteTimeSpan);

        Assert.Equal(FrameConstants.DefaultSlabSize, result.PeerMaxRecvPayload);
        Assert.NotNull(applied);
        Assert.Equal(channelCount, applied.Count);
        for (int i = 0; i < channelCount; i++)
        {
            Assert.Equal((ushort)(i + 1), applied[i].ChannelIndex);
            Assert.Equal((long)(i * 8), applied[i].FrameBytesReceived);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task HandshakeFrame_ReassemblesSegmentedBytes()
    {
        // Honest-handshake regression through the growth path: a standard
        // initial frame split into header/payload segments completes with
        // the exact session id and max-recv payload.
        var peerSessionId = Guid.NewGuid();
        byte[] frame = new byte[FrameHeader.Size + 20];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, 20);
        peerSessionId.TryWriteBytes(frame.AsSpan(FrameHeader.Size, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(FrameHeader.Size + 16, 4),
            (uint)FrameConstants.DefaultSlabSize);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new SegmentedReadStreamPair(frame, segmentSize: 4);

        var result = await MuxHandshake.PerformInitialAsync(
            transport,
            Guid.NewGuid(),
            FrameConstants.DefaultSlabSize,
            cts.Token,
            Timeout.InfiniteTimeSpan);

        Assert.Equal(peerSessionId, result.RemoteSessionId);
        Assert.Equal(FrameConstants.DefaultSlabSize, result.PeerMaxRecvPayload);
    }

    [Fact]
    public void Create_NegativeFrameReadTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                FrameReadTimeout = TimeSpan.FromSeconds(-5),
            }));

        Assert.Contains("FrameReadTimeout", ex.Message);
    }

    [Fact]
    public void Create_TooLargeFrameReadTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                FrameReadTimeout = TimeSpan.FromDays(30),
            }));

        Assert.Contains("FrameReadTimeout", ex.Message);
    }

    [Fact]
    public void Create_InfiniteFrameReadTimeout_Accepted()
    {
        var ex = Record.Exception(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                FrameReadTimeout = Timeout.InfiniteTimeSpan,
            }));

        Assert.Null(ex);
    }

    [Fact]
    public void Create_ZeroFrameReadTimeout_Accepted()
    {
        var ex = Record.Exception(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                FrameReadTimeout = TimeSpan.Zero,
            }));

        Assert.Null(ex);
    }

    [Fact]
    public void Create_NegativeFrameMinReadRate_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                FrameMinReadRateBytesPerSecond = -1,
            }));

        Assert.Contains("FrameMinReadRateBytesPerSecond", ex.Message);
    }

    [Fact]
    public void Create_ZeroAndPositiveFrameMinReadRate_Accepted()
    {
        foreach (var rate in new[] { 0, 1024 })
        {
            var ex = Record.Exception(() =>
                StreamMultiplexer.Create(new MultiplexerOptions
                {
                    StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
                    FrameMinReadRateBytesPerSecond = rate,
                }));

            Assert.Null(ex);
        }
    }

    [Fact]
    public void FrameLivenessOptions_Defaults_PreserveDocumentedValues()
    {
        var options = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
        };

        Assert.Equal(TimeSpan.FromSeconds(30), options.FrameReadTimeout);
        Assert.Equal(0, options.FrameMinReadRateBytesPerSecond);
    }

    private static async Task<MultiplexerException?> WaitForErrorAsync(
        ConcurrentQueue<Exception> errors,
        ErrorCode code,
        TimeSpan ceiling)
    {
        var deadline = DateTimeOffset.UtcNow + ceiling;
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var error in errors)
            {
                if (error is MultiplexerException muxEx && muxEx.ErrorCode == code)
                    return muxEx;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        return null;
    }

    private static byte[] BuildPeerHandshake()
    {
        byte[] frame = new byte[FrameHeader.Size + 20];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, 20);
        Guid.NewGuid().TryWriteBytes(frame.AsSpan(FrameHeader.Size, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(FrameHeader.Size + 16, 4),
            (uint)FrameConstants.DefaultSlabSize);
        return frame;
    }

    private static byte[] BuildDataHeader(ushort channelIndex, int payloadLength)
    {
        byte[] header = new byte[FrameHeader.Size];
        FrameHeader.WriteTo(header, channelIndex, FrameFlags.Data, payloadLength);
        return header;
    }

    private static byte[] BuildReconnectFrame(Guid remoteSessionId, int channelCount)
    {
        int payloadLength = MuxHandshake.ReconnectHeaderLength + channelCount * MuxHandshake.ReconnectChannelEntrySize;
        byte[] payload = new byte[payloadLength];
        payload[0] = CtrlSubtype.Reconnect;
        remoteSessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            payload.AsSpan(17, 4),
            (uint)FrameConstants.DefaultSlabSize);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(21, 2), (ushort)channelCount);
        int offset = MuxHandshake.ReconnectHeaderLength;
        for (int i = 0; i < channelCount; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)(i + 1));
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset + 2, 8), (ulong)(i * 8));
            offset += MuxHandshake.ReconnectChannelEntrySize;
        }

        byte[] frame = new byte[FrameHeader.Size + payload.Length];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, payload.Length);
        payload.CopyTo(frame.AsSpan(FrameHeader.Size));
        return frame;
    }

    /// <summary>
    /// Test double replaying one frame in fixed-size segments, exercising
    /// reassembly across many small reads without any timing element.
    /// </summary>
    private sealed class SegmentedReadStreamPair : IStreamPair
    {
        public Stream ReadStream { get; }
        public Stream WriteStream { get; } = new MemoryStream();

        public SegmentedReadStreamPair(byte[] frame, int segmentSize)
        {
            ReadStream = new SegmentedReadStream(frame, segmentSize);
        }

        public ValueTask DisposeAsync()
        {
            ReadStream.Dispose();
            WriteStream.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class SegmentedReadStream(byte[] frame, int segmentSize) : Stream
        {
            private int _pos;

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (_pos >= frame.Length)
                    return new ValueTask<int>(0);
                var take = Math.Min(Math.Min(buffer.Length, segmentSize), frame.Length - _pos);
                frame.AsSpan(_pos, take).CopyTo(buffer.Span);
                _pos += take;
                return new ValueTask<int>(take);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Test double replaying one handshake frame after a fixed delay, so a
    /// disabled deadline lets slow-but-honest handshake bytes through while
    /// an active instant deadline would fault first.
    /// </summary>
    private sealed class DelayedReadStreamPair : IStreamPair
    {
        public Stream ReadStream { get; }
        public Stream WriteStream { get; } = new MemoryStream();

        public DelayedReadStreamPair(byte[] frame, TimeSpan delay)
        {
            ReadStream = new DelayedReadStream(frame, delay);
        }

        public ValueTask DisposeAsync()
        {
            ReadStream.Dispose();
            WriteStream.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class DelayedReadStream(byte[] frame, TimeSpan delay) : Stream
        {
            private bool _delayed;
            private int _pos;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (!_delayed)
                {
                    _delayed = true;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                var take = Math.Min(buffer.Length, frame.Length - _pos);
                frame.AsSpan(_pos, take).CopyTo(buffer.Span);
                _pos += take;
                return take;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Test double: a transport pair whose read side is fed explicitly by the
    /// test and whose write side sinks. ReadAsync returns available bytes or
    /// parks until <see cref="Feed"/> or cancellation, mirroring the
    /// BufferedReadChannel idiom from the overmax-drain tests.
    /// </summary>
    private sealed class ScriptedStreamPair : IStreamPair
    {
        private readonly ScriptedReadStream _read = new();

        public Stream ReadStream => _read;
        public Stream WriteStream { get; } = new MemoryStream();

        public void Feed(byte[] data) => _read.Feed(data);

        public ValueTask DisposeAsync()
        {
            _read.Dispose();
            WriteStream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedReadStream : Stream
    {
        private readonly object _lock = new();
        private readonly List<byte> _bytes = new();
        private int _pos;
        private TaskCompletionSource _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Feed(byte[] data)
        {
            lock (_lock)
            {
                _bytes.AddRange(data);
                _dataAvailable.TrySetResult();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                Task wait;
                lock (_lock)
                {
                    var available = _bytes.Count - _pos;
                    if (available > 0)
                    {
                        var take = Math.Min(buffer.Length, available);
                        for (var i = 0; i < take; i++)
                            buffer.Span[i] = _bytes[_pos + i];
                        _pos += take;
                        return take;
                    }
                    if (_dataAvailable.Task.IsCompleted)
                        _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _dataAvailable.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
