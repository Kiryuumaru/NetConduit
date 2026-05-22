using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression test for issue #356.
///
/// MainLoopAsync's handshake try-block only caught HandshakeTransportException
/// and OperationCanceledException. Non-transport handshake failures
/// (MultiplexerException for SessionMismatch / ProtocolError / Internal, etc.)
/// on the reconnect path leaked the freshly-connected transport because
/// _conn.Transport was never assigned for the reconnect attempt, so the outer
/// DisposeAsync safety net could not reach it.
///
/// This test drives the client to reconnect against a *different* mux instance
/// (different SessionId). The reconnect handshake throws
/// MultiplexerException(SessionMismatch) and we assert the freshly-connected
/// reconnect transport is disposed.
/// </summary>
public sealed class Issue356MainLoopTransportLeakTests
{
    [Fact]
    public async Task Reconnect_NonTransportHandshakeFailure_DisposesFreshlyConnectedTransport()
    {
        // Phase 1: client connects to serverA via duplex1
        var duplex1 = new DuplexMemoryStream();
        var killableClient1 = new KillableStreamPair(duplex1.SideA);
        var killableServer1 = new KillableStreamPair(duplex1.SideB);

        // Phase 2: client reconnect attempt goes through duplex2 to serverB.
        // serverB is a fresh mux with a different SessionId, so the reconnect
        // handshake throws MultiplexerException(SessionMismatch).
        var duplex2 = new DuplexMemoryStream();
        var trackedClient2 = new DisposeCountingStreamPair(duplex2.SideA);

        int clientConnectCount = 0;

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                int n = Interlocked.Increment(ref clientConnectCount);
                return n switch
                {
                    1 => Task.FromResult<IStreamPair>(killableClient1),
                    2 => Task.FromResult<IStreamPair>(trackedClient2),
                    _ => throw new InvalidOperationException(
                        $"Unexpected client connect attempt {n}; client should terminate after the failed reconnect."),
                };
            },
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 5,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });

        var serverA = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(killableServer1),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        var serverB = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex2.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        serverA.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Task.WhenAll(
            client.WaitForReadyAsync(cts.Token),
            serverA.WaitForReadyAsync(cts.Token));

        // Open a channel on the client. When the reconnect handshake terminally
        // fails, MainLoopAsync's outer catch calls AbortAllChannels with reason
        // TransportFailed, which fires this channel's Closed event. We use it as
        // the synchronization signal that MainLoopAsync has fully unwound —
        // including running our new transport-dispose catch clause.
        var ch = client.OpenChannel("probe");
        var terminallyAborted = new TaskCompletionSource<ChannelCloseReason>();
        ch.Closed += (_, e) =>
        {
            if (e.Reason == ChannelCloseReason.TransportFailed)
                terminallyAborted.TrySetResult(e.Reason);
        };

        // Start serverB so its initial handshake on duplex2 is ready to send
        // when the client's reconnect transport connects to it.
        serverB.Start();

        // Kill phase-1 transport. Client retries; second connect returns the
        // tracked duplex2 pair → reconnect handshake sends Reconnect frame;
        // serverB has not seen the session before so it sends an Initial frame
        // whose session id does NOT match the client's expectedRemoteSessionId
        // → MultiplexerException(SessionMismatch). MainLoopAsync terminates.
        killableClient1.Kill();
        killableServer1.Kill();

        await terminallyAborted.Task.WaitAsync(cts.Token);

        // The freshly-connected reconnect transport must have been disposed.
        // Without the fix, this assertion fails (DisposeCount == 0) because the
        // local `transport` reference went out of scope when the
        // MultiplexerException propagated past the only handshake catch clause.
        Assert.True(
            trackedClient2.DisposeCount > 0,
            $"Reconnect transport leaked: expected DisposeCount > 0, got {trackedClient2.DisposeCount} (#356).");

        await client.DisposeAsync();
        await serverA.DisposeAsync();
        await serverB.DisposeAsync();
    }

    /// <summary>
    /// IStreamPair wrapper that counts DisposeAsync invocations on itself.
    /// Delegates streams directly to the inner pair — only the pair's
    /// DisposeAsync is what the leak path skips.
    /// </summary>
    private sealed class DisposeCountingStreamPair : IStreamPair
    {
        private readonly IStreamPair _inner;
        private int _disposeCount;

        public DisposeCountingStreamPair(IStreamPair inner)
        {
            _inner = inner;
            ReadStream = inner.ReadStream;
            WriteStream = inner.WriteStream;
        }

        public Stream ReadStream { get; }
        public Stream WriteStream { get; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await _inner.DisposeAsync();
        }
    }
}
