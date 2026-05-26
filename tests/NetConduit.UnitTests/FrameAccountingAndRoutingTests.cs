using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in tests for the documented behavior of frame counting
/// (<c>docs/concepts/statistics.md</c>) and per-channel frame routing
/// (<c>docs/concepts/priority.md</c>). Both docs previously claimed that
/// per-channel control frames (INIT, FIN, ACK) traverse the multiplexer's
/// internal <c>__control__</c> channel and are excluded from byte counters.
/// In reality only Ping/Pong/Ctrl traverse the control channel; INIT/FIN/ACK
/// ride the data channel's own slab, are subject to its priority, and are
/// counted in <c>MultiplexerStats.BytesSent</c> / <c>BytesReceived</c>.
/// Issues #445, #447.
/// </summary>
public sealed class FrameAccountingAndRoutingTests
{
    [Fact]
    public async Task BytesSent_IncludesEightByteHeaders_AndPerChannelInitFrame()
    {
        var duplex = new DuplexMemoryStream();

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        long baselineSent = client.Stats.BytesSent;
        long baselineRecv = server.Stats.BytesReceived;

        var wch = client.OpenChannel("X");
        var rch = await server.AcceptChannelAsync("X", cts.Token);
        await wch.WaitForReadyAsync(cts.Token);
        await rch.WaitForReadyAsync(cts.Token);

        var payload = new byte[1000];
        await wch.WriteAsync(payload, cts.Token);

        // Drain at the receiver to make sure all wire bytes have flowed.
        var received = new byte[1000];
        int total = 0;
        while (total < received.Length)
        {
            int n = await rch.ReadAsync(received.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(1000, total);

        long sentDelta = client.Stats.BytesSent - baselineSent;
        long recvDelta = server.Stats.BytesReceived - baselineRecv;

        // Expected wire bytes for an open-then-write-1000 sequence on a single
        // channel: 1 INIT frame (8B header + 1B "X" payload = 9B) + 1 Data
        // frame (8B header + 1000B payload = 1008B) = 1017B. If statistics.md
        // were still claiming "control frames excluded" the delta would be
        // ~1000B; the >= 1017 lower bound discriminates that case while
        // tolerating any additional handshake-domain ACK bytes generated
        // by the receiver back-flow on the same counters.
        Assert.True(
            sentDelta >= 1017,
            $"BytesSent delta = {sentDelta}; expected >= 1017 (1000 user payload + 8B Data header + 9B INIT frame). MultiplexerStats must include per-frame 8B headers AND per-channel INIT/FIN/ACK frames.");
        Assert.True(
            recvDelta >= 1017,
            $"BytesReceived delta = {recvDelta}; expected >= 1017. The reader loop must count every inbound frame including INIT.");

        await client.DisposeAsync();
        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
    }

    [Fact]
    public async Task FinFrame_RidesPerDataChannelIndex_NotControlChannel()
    {
        // Tap the client→server byte stream so we can parse the wire frames
        // emitted by the writer loop. The priority doc previously claimed
        // INIT/FIN/ACK travel on the __control__ channel (ChannelIndex=0);
        // in reality they're embedded in the data channel's slab and the
        // frame header carries the data channel's index. This test captures
        // every byte the writer emits, parses the frame stream, and asserts
        // at least one FIN frame appeared with ChannelIndex != ControlChannel.
        var duplex = new DuplexMemoryStream();
        var capturedSide = new CapturingWritePair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(capturedSide),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var wch = client.OpenChannel("priority-fin");
        var rch = await server.AcceptChannelAsync("priority-fin", cts.Token);
        await wch.WaitForReadyAsync(cts.Token);
        await rch.WaitForReadyAsync(cts.Token);

        // Closing the write channel emits a FIN. The doc claim under test:
        // does that FIN carry the data channel's index (correct) or the
        // control channel's index 0 (what priority.md previously implied)?
        await wch.DisposeAsync();

        // Poll briefly for the FIN to appear on the wire — the writer loop
        // may flush a tick after Dispose returns.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        IReadOnlyList<ushort> finFrames;
        do
        {
            finFrames = capturedSide.GetFramesOfType(FrameFlags.Fin);
            if (finFrames.Count > 0) break;
            await Task.Delay(20, cts.Token);
        } while (DateTime.UtcNow < deadline);

        Assert.NotEmpty(finFrames);

        foreach (ushort channelIndex in finFrames)
        {
            Assert.NotEqual(ChannelConstants.ControlChannel, channelIndex);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
        await ((IAsyncDisposable)duplex).DisposeAsync();
    }

    /// <summary>
    /// Wraps a stream pair's write side with a tap that parses every byte
    /// emitted by the multiplexer's writer loop into <see cref="FrameHeader"/>
    /// records, so tests can assert on the on-the-wire routing of specific
    /// frame types.
    /// </summary>
    private sealed class CapturingWritePair(IStreamPair inner) : IStreamPair
    {
        private readonly CapturingWriteStream _write = new(inner.WriteStream);

        public Stream ReadStream => inner.ReadStream;
        public Stream WriteStream => _write;
        public IReadOnlyList<ushort> GetFramesOfType(FrameFlags flags) => _write.GetFramesOfType(flags);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class CapturingWriteStream(Stream inner) : Stream
        {
            private readonly List<byte> _buffer = [];
            private readonly List<(FrameFlags Flags, ushort ChannelIndex)> _frames = [];
            private int _parsePos;

            public IReadOnlyList<ushort> GetFramesOfType(FrameFlags flags)
            {
                lock (_buffer)
                {
                    var result = new List<ushort>();
                    foreach (var f in _frames)
                    {
                        if (f.Flags == flags) result.Add(f.ChannelIndex);
                    }
                    return result;
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_buffer)
                {
                    _buffer.AddRange(buffer.AsSpan(offset, count).ToArray());
                    ParseAvailable();
                }
                inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                lock (_buffer)
                {
                    _buffer.AddRange(buffer.ToArray());
                    ParseAvailable();
                }
                inner.Write(buffer);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                lock (_buffer)
                {
                    _buffer.AddRange(buffer.ToArray());
                    ParseAvailable();
                }
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

            private void ParseAvailable()
            {
                while (_parsePos + FrameHeader.Size <= _buffer.Count)
                {
                    byte[] hdr = [.. _buffer.GetRange(_parsePos, FrameHeader.Size)];
                    ushort channelIndex = BinaryPrimitives.ReadUInt16BigEndian(hdr);
                    var flags = (FrameFlags)hdr[2];
                    uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4));
                    int frameSize = FrameHeader.Size + (int)payloadLen;
                    if (_parsePos + frameSize > _buffer.Count) break;
                    _frames.Add((flags, channelIndex));
                    _parsePos += frameSize;
                }
            }
        }
    }
}
