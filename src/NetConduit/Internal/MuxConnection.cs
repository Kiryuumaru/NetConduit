using System.Collections.Concurrent;
using NetConduit.Constants;
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

    // Maximum frame payload the remote will accept on any inbound channel,
    // negotiated during the handshake (#180). Defaults to the historical
    // protocol assumption (1 MiB) so callers that touch this field before
    // the handshake completes — or transports that omit the negotiation
    // entirely — still produce traffic the legacy peer can receive.
    public int PeerMaxRecvPayload = FrameConstants.DefaultSlabSize;

    public Task? MainLoopTask;
    public Task? WriterTask;
    public Task? ReaderTask;
    public Task? FlusherTask;
    public Task? KeepaliveTask;

    public IStreamPair? Transport;
    public CancellationTokenSource? LoopCts;
    public WriteChannel? ControlChannel;
    public PendingPong? PendingPong;

    // Channel indices whose peer-initiated INIT was accepted but whose INIT-ACK
    // reply could not be staged into the control slab (transient back-pressure).
    // Drained on every reader-loop iteration. Bounded by peer's outstanding open
    // budget so unbounded growth is impossible. Closes #365/#377/#404 — the
    // reader thread can no longer throw InvalidOperationException("Slab full")
    // when the control slab is transiently full.
    public readonly ConcurrentQueue<ushort> PendingInitAcks = new();
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
