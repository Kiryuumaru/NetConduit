using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Connection-attempt subsystem for <see cref="StreamMultiplexer"/>. Owns
/// the retry loop, exponential backoff, per-attempt connection timeout,
/// and the <c>Reconnecting</c>/<c>Error</c> event raising tied to each
/// attempt. Pure orchestration over <see cref="MultiplexerOptions.StreamFactory"/>;
/// holds no connection state of its own.
/// </summary>
internal sealed class MuxConnectRetry(
    MultiplexerOptions options,
    Action<ReconnectingEventArgs> raiseReconnecting,
    Action<Exception> raiseError)
{
    /// <summary>
    /// True when the configured retry budget still permits another
    /// handshake attempt after <paramref name="failedAttempts"/> failures.
    /// Mirrors the pre-extraction semantics: <c>MaxAutoReconnectAttempts</c>
    /// of <c>-1</c> means unlimited.
    /// </summary>
    internal bool HasHandshakeRetryBudget(int failedAttempts)
    {
        int maxAttempts = options.MaxAutoReconnectAttempts;
        return maxAttempts < 0 || failedAttempts < maxAttempts;
    }

    /// <summary>
    /// Runs the connect-with-retry loop until an <see cref="IStreamPair"/>
    /// is produced, the retry budget is exhausted, or <paramref name="ct"/>
    /// is cancelled. Raises the <c>Reconnecting</c> event on every attempt
    /// after the first (and on the first attempt of a reconnect), and the
    /// <c>Error</c> event on every transport-factory failure that is not
    /// the result of <paramref name="ct"/> cancellation.
    /// </summary>
    internal async Task<IStreamPair> ConnectWithRetryAsync(bool isReconnect, CancellationToken ct)
    {
        int maxAttempts = options.MaxAutoReconnectAttempts;
        double delay = options.AutoReconnectDelay.TotalMilliseconds;
        double maxDelay = options.MaxAutoReconnectDelay.TotalMilliseconds;

        // Enforce a non-zero floor so a misconfigured zero AutoReconnectDelay
        // does not spin the retry loop against a permanently-failing transport
        // (#402). The floor is conservative: 10 ms gives the OS scheduler time
        // to make progress on whatever is failing without altering observable
        // behavior for sane configurations.
        const double MinRetryDelayMs = 10.0;
        if (delay < MinRetryDelayMs) delay = MinRetryDelayMs;
        if (maxDelay < delay) maxDelay = delay;

        if (isReconnect && maxAttempts == 0)
            throw new MultiplexerException(ErrorCode.Internal, "Reconnect is disabled.");

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Enforce max attempts. -1 = unlimited, 0 = single attempt (no retry),
            // >0 = at most N total attempts.
            if (maxAttempts > 0 && attempt > maxAttempts)
                throw new MultiplexerException(ErrorCode.Internal,
                    $"Connection failed after {maxAttempts} attempts.");

            // Delay before retry (skip on first attempt of first connect)
            if (attempt > 1 || isReconnect)
            {
                raiseReconnecting(new ReconnectingEventArgs(attempt));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                delay = Math.Min(delay * options.AutoReconnectBackoffMultiplier, maxDelay);
            }

            try
            {
                if (options.ConnectionTimeout > TimeSpan.Zero
                    && options.ConnectionTimeout != Timeout.InfiniteTimeSpan)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(options.ConnectionTimeout);
                    try
                    {
                        return await options.StreamFactory(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Connection timed out after {options.ConnectionTimeout}");
                    }
                }

                return await options.StreamFactory(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                raiseError(ex);

                // Permanently-fatal factory exceptions must short-circuit the
                // retry loop instead of spinning forever (#406). Server-side
                // helpers (Tcp/Udp/Quic/Ipc/WebSocket CreateServerOptions)
                // install one-shot factories that throw on a second invocation;
                // their failure is structural, not transient, and the mux
                // cannot recover by waiting.
                if (IsFatalFactoryException(ex))
                    throw;

                // maxAttempts == 0 means no retry: propagate the first failure immediately
                // whether this is the initial connect or a reconnect after the link died.
                if (maxAttempts == 0)
                    throw;
            }
        }
    }

    // Heuristic classifier for permanently-fatal connect failures. Conservative:
    // only known-fatal patterns short-circuit; anything else stays in the retry
    // loop so honest-peer transient failures still recover. Closes #406.
    private static bool IsFatalFactoryException(Exception ex)
    {
        // User-error exceptions thrown by misconfigured factory args.
        if (ex is ArgumentException) return true;
        // Server-side one-shot helpers across all transports throw
        // InvalidOperationException with this exact phrase once their accept
        // state is consumed. A second invocation can never succeed.
        if (ex is InvalidOperationException && ex.Message.Contains(
                "does not support reconnection", StringComparison.Ordinal))
            return true;
        return false;
    }
}
