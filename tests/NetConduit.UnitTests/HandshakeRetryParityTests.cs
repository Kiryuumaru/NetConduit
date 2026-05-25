using NetConduit.Interfaces;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// MainLoopAsync's handshake-retry path must honor the same backoff +
/// <c>Reconnecting</c> telemetry as the connect-retry path. The two paths
/// share one <see cref="MultiplexerOptions"/>; observers cannot distinguish
/// which sub-step failed and must see consistent retry signalling.
/// </summary>
public sealed class HandshakeRetryParityTests
{
    [Fact]
    public async Task HandshakeRetry_FiresReconnectingPerAttempt()
    {
        const int MaxAttempts = 4;
        int connectCount = 0;

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                Interlocked.Increment(ref connectCount);
                return Task.FromResult<IStreamPair>(new EofMidHandshakeStreamPair());
            },
            MaxAutoReconnectAttempts = MaxAttempts,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            AutoReconnectBackoffMultiplier = 1.0,
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(50),
            PingInterval = TimeSpan.Zero,
        };
        await using var client = StreamMultiplexer.Create(opts);

        var attempts = new List<int>();
        client.Reconnecting += (_, e) =>
        {
            lock (attempts) attempts.Add(e.Attempt);
        };

        client.Start();
        await Assert.ThrowsAnyAsync<Exception>(() => client.WaitForReadyAsync());

        // HasHandshakeRetryBudget allows MaxAttempts-1 retries before throwing,
        // so MaxAttempts-1 Reconnecting events must fire (one per retry that
        // actually delays-then-tries-again).
        int expected = MaxAttempts - 1;
        Assert.Equal(expected, attempts.Count);
        for (int i = 0; i < attempts.Count; i++)
        {
            Assert.Equal(i + 1, attempts[i]);
        }
    }

    [Fact]
    public async Task HandshakeRetry_AppliesBackoffMultiplier()
    {
        const int MaxAttempts = 4;
        var attemptTimestamps = new List<DateTime>();

        var opts = new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                lock (attemptTimestamps) attemptTimestamps.Add(DateTime.UtcNow);
                return Task.FromResult<IStreamPair>(new EofMidHandshakeStreamPair());
            },
            MaxAutoReconnectAttempts = MaxAttempts,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(40),
            AutoReconnectBackoffMultiplier = 3.0,
            MaxAutoReconnectDelay = TimeSpan.FromSeconds(5),
            PingInterval = TimeSpan.Zero,
        };
        await using var client = StreamMultiplexer.Create(opts);
        client.Start();
        await Assert.ThrowsAnyAsync<Exception>(() => client.WaitForReadyAsync());

        // attemptTimestamps records every StreamFactory invocation. With
        // MaxAttempts=4 we expect timestamps at: t0 (initial), t1 (retry 1
        // after base delay), t2 (retry 2 after base*3), t3 (retry 3 after
        // base*9). The gap from t1->t2 must exceed t0->t1 because the
        // backoff multiplier widened the delay.
        Assert.True(attemptTimestamps.Count >= 3,
            $"Expected >= 3 connect attempts to measure backoff growth; got {attemptTimestamps.Count}");

        var gap1 = attemptTimestamps[1] - attemptTimestamps[0];
        var gap2 = attemptTimestamps[2] - attemptTimestamps[1];

        // Allow scheduler jitter slack: gap2 must be visibly larger than gap1
        // (base=40ms vs base*3=120ms). 1.5x is well above scheduler noise.
        Assert.True(gap2 > TimeSpan.FromTicks((long)(gap1.Ticks * 1.5)),
            $"Backoff not applied: gap1={gap1.TotalMilliseconds:F1}ms, gap2={gap2.TotalMilliseconds:F1}ms (expected gap2 > 1.5 * gap1).");
    }

    /// <summary>
    /// Stream pair whose ReadStream returns EOF on first read, causing
    /// <c>StreamMultiplexer.ReadExactAsync</c> to raise
    /// <c>HandshakeTransportException</c> wrapping <c>EndOfStreamException</c>.
    /// The WriteStream silently absorbs whatever the handshake writes.
    /// </summary>
    private sealed class EofMidHandshakeStreamPair : IStreamPair
    {
        public Stream ReadStream { get; } = new MemoryStream(Array.Empty<byte>(), writable: false);
        public Stream WriteStream { get; } = new MemoryStream();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
