namespace NetConduit.Internal;

/// <summary>
/// Container for the multiplexer's loop task handles and session identities.
/// <para>
/// This type is the first step of a staged restructure (see
/// <c>investigate/arch-002-monolithic-streammultiplexer/README.md</c>). In its
/// current form it is a dumb field bag: a single instance is constructed with
/// <see cref="StreamMultiplexer"/> and lives for the multiplexer's whole
/// lifetime. Follow-up PRs will move transport/loop-cancellation state in and
/// graduate it into a true per-connection object that is swapped on reconnect.
/// Until then, treat it as a private extension of <see cref="StreamMultiplexer"/>
/// — no invariants are enforced here, the multiplexer remains the sole
/// orchestrator of these fields.
/// </para>
/// </summary>
internal sealed class MuxConnection
{
    /// <summary>The multiplexer's local session identity. Assigned once at construction.</summary>
    public Guid SessionId;

    /// <summary>The remote peer's session identity. Assigned during handshake.</summary>
    public Guid RemoteSessionId;

    /// <summary>The main lifecycle loop task. Set by <see cref="StreamMultiplexer.Start"/>.</summary>
    public Task? MainLoopTask;

    /// <summary>The writer loop task. Recreated on each (re)connect.</summary>
    public Task? WriterTask;

    /// <summary>The reader loop task. Recreated on each (re)connect.</summary>
    public Task? ReaderTask;

    /// <summary>The flusher loop task. Recreated on each (re)connect.</summary>
    public Task? FlusherTask;

    /// <summary>The keepalive loop task. Null when keepalive is disabled.</summary>
    public Task? KeepaliveTask;
}
