using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;
using Xunit;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression tests for: peer-initiated resync (0x02) handling races with concurrent SendAsync.
///
/// Pre-fix, the receive path's `case 0x02` mutated the sender-owned field `_lastSentState`
/// outside `_sendLock`. When that mutation interleaved with an in-flight SendCoreAsync,
/// the trailing `_lastSentState = currentState.DeepClone()` overwrote the cleared state and
/// the resync request was silently lost — the next send was a delta against a base the peer
/// had explicitly told us to discard. This test deterministically reproduces the lost-resync
/// hazard by gating the in-flight WriteAsync, injecting the 0x02 frame while the send is
/// paused, then observing the next outbound frame's type byte.
/// </summary>
public sealed class DeltaMessageTransitResyncRaceTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    [Fact(Timeout = 30000)]
    public async Task PeerResyncDuringInFlightSend_NextSendIsFullState_NotDelta()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            var senderWriteInner = client.OpenChannel("dt-resync-mid");
            var senderReadOnPeer = await server.AcceptChannelAsync("dt-resync-mid");
            var peerWrite = server.OpenChannel("dt-resync-mid-back");
            var senderRead = await client.AcceptChannelAsync("dt-resync-mid-back");

            await Task.WhenAll(
                senderWriteInner.WaitForReadyAsync(),
                senderReadOnPeer.WaitForReadyAsync(),
                peerWrite.WaitForReadyAsync(),
                senderRead.WaitForReadyAsync());

            using var gate = new ManualResetEventSlim(initialState: true);
            var senderWrite = new GatedWriteChannel(senderWriteInner, gate);

            var sender = new DeltaMessageTransit<JsonObject>(senderWrite, senderRead);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Step 1: full state s1 establishes _lastSentState on the sender.
            await sender.SendAsync(new JsonObject { ["v"] = 1 }, cts.Token);
            var frame1 = await ReadFrameAsync(senderReadOnPeer, cts.Token);
            Assert.Equal(0x00, frame1[0]); // Full

            // Step 2: close gate, kick off send s2 (would be a delta).
            gate.Reset();
            var s2Task = sender.SendAsync(new JsonObject { ["v"] = 2 }, cts.Token).AsTask();

            // Wait for the send to enter the gated WriteAsync — proving the send is mid-flight.
            await senderWrite.GateBlockedTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Step 3: peer writes a 0x02 resync frame; sender's receive path consumes it
            // while s2 is still parked inside SendCoreAsync's await on WriteAsync.
            await WriteResyncFrameAsync(peerWrite, cts.Token);
            await sender.ReceiveAsync(cts.Token); // returns default after consuming 0x02

            // Step 4: open gate, let s2 finish. Pre-fix, the receive path's
            // `_lastSentState = null` is now overwritten by SendCoreAsync's trailing
            // `_lastSentState = currentState.DeepClone()` — losing the resync request.
            gate.Set();
            await s2Task;
            var frame2 = await ReadFrameAsync(senderReadOnPeer, cts.Token);
            Assert.Equal(0x01, frame2[0]); // Delta (s2 was already mid-send when the resync arrived)

            // Step 5: send s3. Pre-fix sends Delta from s2 (RESYNC LOST → peer cannot apply).
            // Post-fix, ConsumeResyncRequest sees the flag at the top of SendCore and clears
            // _lastSentState, so s3 is sent as a Full state and the resync is honored.
            await sender.SendAsync(new JsonObject { ["v"] = 3 }, cts.Token);
            var frame3 = await ReadFrameAsync(senderReadOnPeer, cts.Token);
            Assert.Equal(0x00, frame3[0]);

            await sender.DisposeAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task WriteResyncFrameAsync(IWriteChannel ch, CancellationToken ct)
    {
        var buf = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), 1);
        buf[4] = 0x02;
        await ch.WriteAsync(buf, ct);
    }

    private static async Task<byte[]> ReadFrameAsync(IReadChannel ch, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(ch, lenBuf, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var payload = new byte[len];
        await ReadExactAsync(ch, payload, ct);
        return payload;
    }

    private static async Task ReadExactAsync(IReadChannel ch, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await ch.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new InvalidOperationException("channel closed");
            read += n;
        }
    }

    /// <summary>
    /// Decorator that delegates all IWriteChannel/IChannel surface to an inner real channel,
    /// but pauses WriteAsync at a manual-reset gate so the test can inject events at a
    /// deterministic point in the send pipeline.
    /// </summary>
    private sealed class GatedWriteChannel(IWriteChannel inner, ManualResetEventSlim gate) : IWriteChannel
    {
        private readonly TaskCompletionSource _gateBlockedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task GateBlockedTask => _gateBlockedTcs.Task;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (!gate.IsSet)
            {
                _gateBlockedTcs.TrySetResult();
                await Task.Run(() => gate.Wait(ct), ct);
            }
            await inner.WriteAsync(data, ct);
        }

        public string ChannelId => inner.ChannelId;
        public ChannelState State => inner.State;
        public bool IsReady => inner.IsReady;
        public bool IsConnected => inner.IsConnected;
        public ChannelPriority Priority => inner.Priority;
        public ChannelStats Stats => inner.Stats;
        public ChannelCloseReason? CloseReason => inner.CloseReason;
        public Exception? CloseException => inner.CloseException;

        public event EventHandler? Ready
        {
            add => inner.Ready += value;
            remove => inner.Ready -= value;
        }
        public event EventHandler? Connected
        {
            add => inner.Connected += value;
            remove => inner.Connected -= value;
        }
        public event EventHandler<DisconnectedEventArgs>? Disconnected
        {
            add => inner.Disconnected += value;
            remove => inner.Disconnected -= value;
        }
        public event EventHandler<ChannelCloseEventArgs>? Closed
        {
            add => inner.Closed += value;
            remove => inner.Closed -= value;
        }

        public Task WaitForReadyAsync(CancellationToken ct = default) => inner.WaitForReadyAsync(ct);
        public ValueTask CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
        public Stream AsStream() => inner.AsStream();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Dispose() => inner.Dispose();
    }
}
