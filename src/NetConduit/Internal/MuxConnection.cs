using NetConduit.Interfaces;

namespace NetConduit.Internal;

// Container for per-connection state owned by StreamMultiplexer: loop task
// handles, session identities, the active transport, the per-session
// cancellation source, the control write channel, and the pending-pong
// completion source. Field assignment is orchestrated exclusively by
// StreamMultiplexer; no invariants are enforced here.
internal sealed class MuxConnection
{
    public Guid SessionId;
    public Guid RemoteSessionId;

    public Task? MainLoopTask;
    public Task? WriterTask;
    public Task? ReaderTask;
    public Task? FlusherTask;
    public Task? KeepaliveTask;

    public IStreamPair? Transport;
    public CancellationTokenSource? LoopCts;
    public WriteChannel? ControlChannel;
    public PendingPong? PendingPong;
}

// Pairs a keepalive ping's outstanding TaskCompletionSource with the 8-byte
// correlation token written into the ping payload. The Pong handler must
// only complete the TCS when the echoed token matches; otherwise a late
// pong from a previous (timed-out) ping would satisfy the next ping's TCS
// and mask real liveness failures (issue #293).
internal sealed class PendingPong(long expectedToken, TaskCompletionSource tcs)
{
    public long ExpectedToken { get; } = expectedToken;
    public TaskCompletionSource Tcs { get; } = tcs;
}
