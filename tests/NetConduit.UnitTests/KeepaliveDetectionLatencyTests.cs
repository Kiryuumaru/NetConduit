using System.Diagnostics;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in tests for the keepalive detection-latency formula documented in
/// <c>docs/concepts/heartbeat.md</c>. The loop in
/// <c>MuxKeepalive.RunAsync</c> awaits <c>Task.Delay(pingInterval)</c> at the
/// TOP of every iteration including after a missed pong, so the actual
/// worst-case detection latency from the last successful pong is
/// <c>MaxMissedPings * (PingInterval + PingTimeout)</c>, NOT
/// <c>MaxMissedPings * PingTimeout</c>. Issue #442.
/// </summary>
public sealed class KeepaliveDetectionLatencyTests
{
    [Fact]
    public async Task DisconnectLatency_FromLastSuccessfulPong_IncludesPingIntervalBetweenMisses()
    {
        // With PingInterval=80ms, PingTimeout=40ms, MaxMissedPings=2 the
        // documented (wrong) formula MaxMissedPings * PingTimeout predicts
        // ~80ms. The actual loop waits PingInterval BEFORE each ping
        // including after a miss, so disconnect takes at least
        // (MaxMissedPings - 1) * PingInterval + MaxMissedPings * PingTimeout
        // = 1 * 80 + 2 * 40 = 160ms from the first failed ping, or up to
        // MaxMissedPings * (PingInterval + PingTimeout) = 240ms from the
        // moment server-side pong responses stopped. Asserting a floor of
        // 150ms proves PingInterval contributes between misses; a low-bar
        // floor of 80ms would NOT distinguish the two formulas.
        var pingInterval = TimeSpan.FromMilliseconds(80);
        var pingTimeout = TimeSpan.FromMilliseconds(40);
        const int maxMissedPings = 2;

        var duplex = new DuplexMemoryStream();
        var sideAWithTap = new PongDroppingPair(duplex.SideA);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(sideAWithTap),
            PingInterval = pingInterval,
            PingTimeout = pingTimeout,
            MaxMissedPings = maxMissedPings,
            MaxAutoReconnectAttempts = 0,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.FromSeconds(60),
            PingTimeout = TimeSpan.FromSeconds(60),
            MaxAutoReconnectAttempts = 0,
        });

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        client.Start();
        server.Start();

        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            client.WaitForReadyAsync(readyCts.Token),
            server.WaitForReadyAsync(readyCts.Token));

        // Start the clock the moment the connection is healthy. From here on,
        // every server pong is dropped, so the keepalive loop will count
        // missed pings until the budget is exhausted.
        var stopwatch = Stopwatch.StartNew();
        sideAWithTap.DropPongs = true;

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        long elapsedMs = stopwatch.ElapsedMilliseconds;

        // Floor: > 1 * PingInterval + 2 * PingTimeout = 160ms minus small
        // slack for the in-flight ping at the moment we started dropping
        // (it may already be mid-Delay). 120ms is a safe lower bound that
        // still discriminates against the doc's old (wrong) formula
        // (~80ms ceiling) by a comfortable margin.
        Assert.True(
            elapsedMs >= 120,
            $"Disconnect happened in {elapsedMs}ms; expected >= 120ms to confirm PingInterval contributes between missed pings (doc formula MaxMissedPings * PingTimeout = {maxMissedPings * pingTimeout.TotalMilliseconds}ms would predict ~80ms). Loop in MuxKeepalive must wait PingInterval at the top of every iter.");

        // Ceiling: 2 * (PingInterval + PingTimeout) = 240ms plus generous
        // CI slack. A regression that re-pings immediately on timeout would
        // disconnect well under this.
        Assert.True(
            elapsedMs <= 2000,
            $"Disconnect took {elapsedMs}ms; expected <= 2000ms (formula MaxMissedPings * (PingInterval + PingTimeout) = 240ms with CI slack).");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    /// <summary>
    /// Wraps a stream pair so the read side can drop server→client traffic
    /// on command. Used to simulate a peer that stops responding to pings
    /// without tearing the transport down — exercises the keepalive-timeout
    /// path, not the transport-error path.
    /// </summary>
    private sealed class PongDroppingPair(IStreamPair inner) : IStreamPair
    {
        private readonly DroppableReadStream _read = new(inner.ReadStream);

        public Stream ReadStream => _read;
        public Stream WriteStream => inner.WriteStream;
        public bool DropPongs { set => _read.Drop = value; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class DroppableReadStream(Stream inner) : Stream
        {
            public volatile bool Drop;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (true)
                {
                    int n = inner.Read(buffer, offset, count);
                    if (!Drop || n <= 0) return n;
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (!Drop || n <= 0) return n;
                }
            }

            public override int Read(Span<byte> buffer)
            {
                while (true)
                {
                    int n = inner.Read(buffer);
                    if (!Drop || n <= 0) return n;
                }
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
