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
        // Independent budget for consecutive cycles where the control slab
        // cannot fit a PING (#366). The #355 fix introduced TrySendPing +
        // continue to absorb transient control-slab pressure from coalesced
        // ACKs, but the assumption "pressure is transient because the writer
        // will drain" is false on a half-open transport where the writer
        // thread is itself blocked on transport.WriteStream.Write. In that
        // scenario the slab stays full forever, every PING attempt returns
        // false, missedPings never increments, and the mux never tears down —
        // silently disabling the liveness check exactly when it is needed.
        // Treat sustained inability to stage a PING as evidence of writer
        // stall and fire the same IOException missed-pong saturation would.
        int slabPressureCycles = 0;
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
                    // Control slab is under pressure. The ping was NOT placed on the
                    // wire, so it would be wrong to count this as a missed ping or to
                    // wait for a pong that can never arrive. Clear the just-installed
                    // pending (so the dangling TCS does not pin allocation) and back
                    // off until the next interval. See #355.
                    Interlocked.CompareExchange(ref conn.PendingPong, null, pending);

                    // Bound the number of consecutive cycles tolerated under
                    // slab pressure. The budget mirrors maxMissedPings so the
                    // half-open detection window matches the documented
                    // keepalive contract: by N * pingInterval the mux tears
                    // down whether the failure mode is "PONG never arrives"
                    // or "PING cannot even be staged" (#366).
                    slabPressureCycles++;
                    if (slabPressureCycles >= maxMissedPings)
                    {
                        throw new IOException(
                            $"Keepalive timeout: {slabPressureCycles} consecutive cycles unable to stage PING due to control-slab pressure (interval: {pingInterval}). " +
                            "The writer thread is likely blocked on the underlying transport — assuming half-open connection.");
                    }
                    continue;
                }

                // PING staged successfully — reset the pressure counter so a
                // burst of transient pressure (which #355 must still tolerate)
                // does not aggregate across recovered cycles.
                slabPressureCycles = 0;

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
        Task timeout = pingTimeout > TimeSpan.Zero
            ? Task.Delay(pingTimeout, ct)
            : Task.CompletedTask;

        Task completed = await Task.WhenAny(pending.Tcs.Task, timeout);
        if (completed == pending.Tcs.Task)
            return true;

        ct.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref conn.PendingPong, null, pending);
        return false;
    }
}
