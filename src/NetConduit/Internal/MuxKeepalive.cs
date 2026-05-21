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
        byte[] pingPayload = new byte[8];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pingInterval, ct);

                var pendingPong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref conn.PendingPong, pendingPong);

                BinaryPrimitives.WriteInt64BigEndian(pingPayload, Environment.TickCount64);
                SendPing(pingPayload);

                if (await WaitForPongAsync(pendingPong, ct))
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

    private void SendPing(ReadOnlySpan<byte> payload)
    {
        // ControlChannel may be torn down concurrently with shutdown — skip silently.
        conn.ControlChannel?.WriteRawFrame(ControlFrameBuilder.BuildControlFrame(FrameFlags.Ping, payload));
    }

    private async Task<bool> WaitForPongAsync(TaskCompletionSource pendingPong, CancellationToken ct)
    {
        Task timeout = pingTimeout > TimeSpan.Zero
            ? Task.Delay(pingTimeout, ct)
            : Task.CompletedTask;

        Task completed = await Task.WhenAny(pendingPong.Task, timeout);
        if (completed == pendingPong.Task)
            return true;

        ct.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref conn.PendingPong, null, pendingPong);
        return false;
    }
}
