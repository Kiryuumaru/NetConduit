using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for #293: Pong frame must be correlated to its triggering
/// Ping by the 8-byte payload token. A pong with a non-matching token must NOT
/// satisfy the outstanding ping's TaskCompletionSource — otherwise stale pongs
/// (or forged pongs from a buggy/half-broken peer) silently mask liveness
/// failures and the missed-ping budget never expires.
/// </summary>
public sealed class KeepalivePongCorrelationTests
{
    [Fact]
    public async Task PongWithMismatchedToken_DoesNotResetMissedPings_DisconnectsAfterBudget()
    {
        // Set up a real mux pair, but tap the server→client direction so every
        // Pong frame's 8-byte payload is rewritten to zero. The client's keepalive
        // sends Pings with a non-zero monotonic token, so every echoed pong arrives
        // with the wrong token and must be dropped. After MaxMissedPings the
        // client must surface Disconnected.
        var duplex = new DuplexMemoryStream();
        var sideAWithTap = new PongTokenZeroingPair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(sideAWithTap),
            PingInterval = TimeSpan.FromMilliseconds(80),
            PingTimeout = TimeSpan.FromMilliseconds(40),
            MaxMissedPings = 2,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            // Long-enough interval that the server side's own keepalive does not
            // tear down the connection before the client's missed-ping budget
            // expires (we are testing the client's pong-correlation logic).
            PingInterval = TimeSpan.FromSeconds(60),
            PingTimeout = TimeSpan.FromSeconds(60),
            MaxAutoReconnectAttempts = 0,
        });

        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        // Without the fix, every stale-token pong would still call
        // Interlocked.Exchange(ref PendingPong, null)?.TrySetResult() and
        // reset missedPings to 0 — disconnect would never fire.
        var args = await disconnected.Task.WaitAsync(cts.Token);
        Assert.NotNull(args);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    /// <summary>
    /// Wraps an <see cref="IStreamPair"/> read side with a frame-aware tap that
    /// rewrites the 8-byte payload of every <see cref="FrameFlags.Pong"/> frame
    /// flowing peer→local to all-zero bytes (token = 0). All other frames pass
    /// through unmodified, including the handshake's Ctrl frames.
    /// </summary>
    private sealed class PongTokenZeroingPair(IStreamPair inner) : IStreamPair
    {
        public Stream ReadStream { get; } = new PongMutatingReadStream(inner.ReadStream);
        public Stream WriteStream => inner.WriteStream;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PongMutatingReadStream(Stream inner) : Stream
    {
        // Frame parser state. Mutates only Pong frames' 8-byte payload (sets to 0).
        private readonly byte[] _headerBuf = new byte[FrameHeader.Size];
        private int _headerFilled;
        private int _payloadRemaining;
        private bool _payloadIsPong;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if (n > 0) MutatePongPayload(buffer.AsSpan(offset, n));
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n > 0) MutatePongPayload(buffer.Span[..n]);
            return n;
        }

        // Walks a freshly-read chunk of bytes through a small frame parser and
        // overwrites the payload bytes of any Pong frame in place.
        private void MutatePongPayload(Span<byte> chunk)
        {
            int pos = 0;
            while (pos < chunk.Length)
            {
                if (_payloadRemaining > 0)
                {
                    int take = Math.Min(_payloadRemaining, chunk.Length - pos);
                    if (_payloadIsPong)
                        chunk.Slice(pos, take).Clear();
                    pos += take;
                    _payloadRemaining -= take;
                    if (_payloadRemaining == 0)
                        _payloadIsPong = false;
                    continue;
                }

                // Filling header.
                int needed = FrameHeader.Size - _headerFilled;
                int copy = Math.Min(needed, chunk.Length - pos);
                chunk.Slice(pos, copy).CopyTo(_headerBuf.AsSpan(_headerFilled));
                _headerFilled += copy;
                pos += copy;

                if (_headerFilled == FrameHeader.Size)
                {
                    ushort channel = BinaryPrimitives.ReadUInt16BigEndian(_headerBuf);
                    var flags = (FrameFlags)_headerBuf[2];
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(_headerBuf.AsSpan(4));
                    _payloadRemaining = length;
                    _payloadIsPong = channel == ChannelConstants.ControlChannel
                        && flags == FrameFlags.Pong
                        && length == 8;
                    _headerFilled = 0;
                }
            }
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            if (n > 0) MutatePongPayload(buffer[..n]);
            return n;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task HealthyPongs_KeepConnectionAlive()
    {
        // Positive-control sanity: with token-correct pongs (no tap), the
        // client must not disconnect inside an observation window > several
        // ping intervals.
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.FromMilliseconds(50),
            PingTimeout = TimeSpan.FromMilliseconds(25),
            MaxMissedPings = 3,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.FromMilliseconds(50),
            PingTimeout = TimeSpan.FromMilliseconds(25),
            MaxMissedPings = 3,
            MaxAutoReconnectAttempts = 0,
        });

        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        client.Start();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            server.WaitForReadyAsync(cts.Token));

        var observation = Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token);
        var first = await Task.WhenAny(disconnected.Task, observation);
        Assert.Same(observation, first);
        Assert.True(client.IsConnected);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
