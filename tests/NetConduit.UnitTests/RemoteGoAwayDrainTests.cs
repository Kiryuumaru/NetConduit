using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for issue #165: a remote <c>Ctrl/GoAway</c> must mirror the
/// drain semantics of the local <see cref="StreamMultiplexer.GoAwayAsync"/>
/// path, otherwise frames already stamped into channel slabs by
/// <c>WriteAsync</c> are silently dropped when the receiver of GoAway tears
/// down its writer loop mid-flush.
/// </summary>
public sealed class RemoteGoAwayDrainTests
{
    [Fact]
    public async Task RemoteGoAway_DrainsLocalSlabFramesBeforeTeardown()
    {
        var duplex = new DuplexMemoryStream();

        // Slow the client's outbound writes so the writer loop cannot drain
        // its slabs in a single iteration. Without the fix, _cts.Cancel() in
        // HandleRemoteGoAway aborts the writer with stamped-but-unsent frames
        // still in the slab.
        var clientPair = new SlowingStreamPair(duplex.SideA, perWriteDelay: TimeSpan.FromMilliseconds(20));

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(clientPair),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(5),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            GoAwayTimeout = TimeSpan.FromSeconds(5),
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeCh = client.OpenChannel("drain");
        var readCh = await server.AcceptChannelAsync("drain", cts.Token);
        await writeCh.WaitForReadyAsync(cts.Token);
        await readCh.WaitForReadyAsync(cts.Token);

        const int frameCount = 40;
        const int frameLen = 64;
        var payload = new byte[frameLen];
        for (int i = 0; i < frameCount; i++)
        {
            payload[0] = (byte)i;
            await writeCh.WriteAsync(payload, cts.Token);
        }

        // Server initiates graceful shutdown. Don't await — we want to observe
        // the local-drain behavior on the client (the side *receiving* GoAway)
        // while the server is still in its own drain loop.
        var serverGoAwayTask = server.GoAwayAsync(cts.Token).AsTask();

        // Read the full payload from the server side. With the bug, the
        // client's writer is cancelled mid-flush after the GoAway frame
        // arrives and the server reaches EOF before the full payload has
        // been pumped onto the wire.
        var received = new byte[frameCount * frameLen];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int n = await readCh.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(received.Length, totalRead);
        for (int i = 0; i < frameCount; i++)
        {
            Assert.Equal((byte)i, received[i * frameLen]);
        }

        try { await serverGoAwayTask; } catch { /* drain timeout is acceptable */ }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    /// <summary>
    /// Wraps an <see cref="IStreamPair"/> and inserts a fixed delay before
    /// each underlying <c>Write</c>/<c>WriteAsync</c> call. Used to keep the
    /// multiplexer's writer loop busy long enough for stamped-but-unsent
    /// frames to accumulate in channel slabs.
    /// </summary>
    private sealed class SlowingStreamPair(IStreamPair inner, TimeSpan perWriteDelay) : IStreamPair
    {
        public Stream ReadStream { get; } = inner.ReadStream;
        public Stream WriteStream { get; } = new SlowingWriteStream(inner.WriteStream, perWriteDelay);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SlowingWriteStream(Stream inner, TimeSpan perWriteDelay) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(perWriteDelay);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Thread.Sleep(perWriteDelay);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(perWriteDelay, ct).ConfigureAwait(false);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
