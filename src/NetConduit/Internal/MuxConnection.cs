namespace NetConduit.Internal;

// Container for per-connection state owned by StreamMultiplexer: loop task
// handles and session identities. Field assignment is orchestrated
// exclusively by StreamMultiplexer; no invariants are enforced here.
internal sealed class MuxConnection
{
    public Guid SessionId;
    public Guid RemoteSessionId;

    public Task? MainLoopTask;
    public Task? WriterTask;
    public Task? ReaderTask;
    public Task? FlusherTask;
    public Task? KeepaliveTask;
}
