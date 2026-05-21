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
    public TaskCompletionSource? PendingPong;
}
