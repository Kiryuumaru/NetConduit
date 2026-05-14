# `IWriteChannel`

Namespace: `NetConduit.Interfaces`.

The local end of an outbound channel.

```csharp
public interface IWriteChannel : IAsyncDisposable, IDisposable
{
    string          ChannelId      { get; }
    ChannelState    State          { get; }
    bool            IsReady        { get; }
    bool            IsConnected    { get; }
    ChannelPriority Priority       { get; }
    ChannelStats    Stats          { get; }
    ChannelCloseReason? CloseReason   { get; }
    Exception?          CloseException { get; }

    event EventHandler?                       Ready;
    event EventHandler?                       Connected;
    event EventHandler<DisconnectedEventArgs>? Disconnected;
    event EventHandler<ChannelCloseEventArgs>? Closed;

    Task      WaitForReadyAsync(CancellationToken ct = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask CloseAsync(CancellationToken ct = default);
    Stream    AsStream();
}
```

## Lifecycle

```
Opening  -INIT/ACK->  Open  -FIN->  Closing  -drained->  Closed
```

- `IsReady` becomes true on transition to `Open` and **stays true** for the channel's lifetime.
- `IsConnected` tracks the transport — it flips false on disconnect and true again on reconnect (while the channel is still open).
- `State` transitions:
  - `Opening` — `INIT` sent, no ACK yet.
  - `Open` — confirmed by remote, data flows.
  - `Closing` — local `CloseAsync` called and `FIN` queued; buffered data still draining.
  - `Closed` — transport sent FIN/ERR or close has fully drained.

## `WriteAsync`

```csharp
await channel.WriteAsync(payload, ct);
```

- Synchronous up to the channel's slab capacity. Beyond that it asynchronously waits for slab space (this is the local backpressure).
- Frame size is bounded by `FrameConstants.MaxFramePayloadSize` (16 MiB) per multiplexer frame; larger writes are split automatically.
- Throws:
  - `ChannelClosedException` if the channel is `Closed`.
  - `TimeoutException` if `SendTimeout` elapses while waiting for slab space.
  - `OperationCanceledException` on `ct` cancellation.
- After write returns, bytes are committed to the slab. Actual transmission happens asynchronously on the mux writer loop. Use `IStreamMultiplexer.FlushAsync` to force flush.

## `CloseAsync`

Sends a `FIN` to the remote. Pending bytes already in the slab are flushed first. After `CloseAsync`:
- `State` becomes `Closing`, then `Closed` when drained.
- `WriteAsync` calls throw `ChannelClosedException`.

`DisposeAsync` is equivalent to `CloseAsync` + resource cleanup.

## `AsStream`

Returns a `Stream` wrapper where `Write` calls translate to `WriteAsync`. `CanWrite == true`, `CanRead == false`, `CanSeek == false`. Useful for adapters that want a `Stream` directly without going through `StreamTransit`.

## Events

| Event | Fires when |
| --- | --- |
| `Ready` | Channel becomes `Open`. Once. |
| `Connected` | Transport reconnected and the channel is still alive. |
| `Disconnected` | Transport dropped (channel may resume after reconnect if replay is enabled). |
| `Closed` | Channel transitions to `Closed`. Carries `ChannelCloseReason` and any exception. |
