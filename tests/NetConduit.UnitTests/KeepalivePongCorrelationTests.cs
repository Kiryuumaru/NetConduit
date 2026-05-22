using System.Buffers.Binary;
using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

// Regression for issue #293: PONG handler must correlate the echoed timestamp
// to the most recent PING. Without correlation, any PONG (including stale
// echoes of a previous PING) satisfies the in-flight wait, masking missed
// pings indefinitely.
public sealed class KeepalivePongCorrelationTests
{
    [Fact]
    public async Task StalePongPayload_DoesNotSatisfyKeepalive_AndMuxDisconnects()
    {
        // Wrap the server-side write stream so every outbound PONG frame has
        // its 8-byte timestamp payload zeroed — simulating a peer that echoes
        // the wrong (stale) timestamp.
        var duplex = new DuplexMemoryStream();
        var mangledServerSide = new PongMangledStreamPair(duplex.SideB);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromMilliseconds(200),
            MaxMissedPings = 2,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(mangledServerSide),
            PingInterval = TimeSpan.FromSeconds(30),
            PingTimeout = TimeSpan.FromSeconds(30),
            MaxMissedPings = 100,
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Wait long enough for at least MaxMissedPings worth of PING/PONG
        // round trips. With correlation, the stale PONG payload never matches
        // the expected timestamp and missed pings accumulate to disconnect.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(client.IsConnected,
            "Client should disconnect when peer pongs with stale timestamps");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private sealed class PongMangledStreamPair(IStreamPair inner) : IStreamPair
    {
        public Stream ReadStream { get; } = inner.ReadStream;
        public Stream WriteStream { get; } = new PongMangledStream(inner.WriteStream);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PongMangledStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            byte[] copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            MangleAllPongFrames(copy);
            inner.Write(copy, 0, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            byte[] copy = buffer.ToArray();
            MangleAllPongFrames(copy);
            return inner.WriteAsync(copy, ct);
        }

        // 8-byte frame header: [Channel:2B BE][Flags:1B][Reserved:1B][Length:4B BE].
        // Walk the buffer frame-by-frame and zero the payload of any PONG (0x06)
        // frame whose declared length is 8 bytes.
        private static void MangleAllPongFrames(Span<byte> buffer)
        {
            const byte PongFlag = 0x06;
            const int HeaderSize = 8;

            int pos = 0;
            while (pos + HeaderSize <= buffer.Length)
            {
                byte flags = buffer[pos + 2];
                uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(pos + 4, 4));
                if (length > int.MaxValue) break;
                int frameEnd = pos + HeaderSize + (int)length;
                if (frameEnd > buffer.Length) break;

                if (flags == PongFlag && length == 8)
                    buffer.Slice(pos + HeaderSize, 8).Clear();

                pos = frameEnd;
            }
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
