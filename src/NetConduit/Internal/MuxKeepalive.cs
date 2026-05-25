using System.Buffers.Binary;
using NetConduit.Enums;

namespace NetConduit.Internal;

/// <summary>
/// Keepalive subsystem for <see cref="StreamMultiplexer"/>. Owns the periodic
/// PING send loop and PONG wait, signalling missed pings to the multiplexer's
/// main loop via an <see cref="IOException"/> when the configured budget is
/// exhausted. The reader thread completes a pending pong by exchanging
/// <see cref="MuxConnection.PendingPong"/> back to <c>null</c> and signalling
/// the captured task source (see <c>ProcessControlFrame</c>).
/// </summary>
internal sealed class MuxKeepalive(
    MuxConnection conn,
    TimeSpan pingInterval,
    TimeSpan pingTimeout,
    int maxMissedPings)
{
    /// <summary>
    /// Runs the keepalive loop until <paramref name="ct"/> is cancelled or the
    /// missed-ping budget is exhausted. Throws <see cref="IOException"/> when
    /// the cumulative missed-ping count reaches <c>maxMissedPings</c>.
    /// </summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        int missedPings = 0;
        long pingToken = 0;
        byte[] pingPayload = new byte[8];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pingInterval, ct);

                // Monotonically increasing per-ping correlation token. Used by the
                // Pong handler to discard stale pongs (#293) — a counter is collision
                // free even when two pings would otherwise share Environment.TickCount64.
                pingToken++;
                var pending = new PendingPong(
                    pingToken,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                Interlocked.Exchange(ref conn.PendingPong, pending);

                BinaryPrimitives.WriteInt64BigEndian(pingPayload, pingToken);
                if (!TrySendPing(pingPayload))
                {
                    // #366: Slab pressure is transient under normal load, but if
                    // the writer is stuck on a half-open transport the slab never
                    // drains and no pong can ever arrive. Treat a consecutive run
                    // of TrySendPing failures the same as missed pongs so the mux
                    // still observes the dead peer and tears down. Recovery on a
                    // successful TrySendPing+pong resets the counter, so brief
                    // slab pressure on a healthy link does not bring the mux down.
                    Interlocked.CompareExchange(ref conn.PendingPong, null, pending);
                    missedPings++;
                    if (missedPings >= maxMissedPings)
                    {
                        throw new IOException(
                            $"Keepalive failed: {missedPings} consecutive control-slab failures (slab pressure on half-open transport)");
                    }
                    continue;
                }

                if (await WaitForPongAsync(pending, ct))
                {
                    missedPings = 0;
                    continue;
                }

                missedPings++;
                if (missedPings >= maxMissedPings)
                {
                    throw new IOException(
                        $"Keepalive timeout: {missedPings} missed pings (timeout: {pingTimeout})");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private bool TrySendPing(ReadOnlySpan<byte> payload)
    {
        // ControlChannel may be torn down concurrently with shutdown — treat as
        // a benign skip (the cancellation token will fire on the next Delay).
        // The throwing WriteRawFrame variant must not be used here: under
        // sustained control-slab pressure from coalesced position ACKs the
        // slab can transiently lack room for the ping frame, and an exception
        // out of the keepalive loop tears the mux down even though the wire is
        // healthy (#355 — parallel of #291/#336 not previously applied to the
        // PING path).
        return conn.ControlChannel?.TryWriteRawFrame(
            ControlFrameBuilder.BuildControlFrame(FrameFlags.Ping, payload)) ?? false;
    }

    private async Task<bool> WaitForPongAsync(PendingPong pending, CancellationToken ct)
    {
        // #397: Use a linked CTS we can cancel when pong wins so the Task.Delay
        // timer is released immediately instead of running until pingTimeout.
        // Without this, every successful ping/pong cycle on a long-lived
        // healthy connection leaks one Task.Delay timer per interval; over
        // hours/days the orphan timers accumulate into measurable allocation
        // and timer-wheel pressure. Awaiting the cancelled Task and swallowing
        // OCE prevents the cancellation from surfacing as an unobserved task
        // exception.
        if (pingTimeout <= TimeSpan.Zero)
        {
            await pending.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task timeout = Task.Delay(pingTimeout, timeoutCts.Token);
        Task completed = await Task.WhenAny(pending.Tcs.Task, timeout).ConfigureAwait(false);

        if (completed == pending.Tcs.Task)
        {
            timeoutCts.Cancel();
            try { await timeout.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected after cancel */ }
            return true;
        }

        ct.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref conn.PendingPong, null, pending);
        return false;
    }
}
