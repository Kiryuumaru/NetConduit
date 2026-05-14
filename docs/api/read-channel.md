# `IReadChannel`

Namespace: `NetConduit.Interfaces`.

The local end of an inbound channel.

```csharp
public interface IReadChannel : IAsyncDisposable, IDisposable
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
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask CloseAsync(CancellationToken ct = default);
    Stream    AsStream();
}
```

## `ReadAsync`

```csharp
int n = await channel.ReadAsync(buffer, ct);
if (n == 0)  // EOF
    return;
```

- Returns the number of bytes read (1..`buffer.Length`), or **0 at EOF** (remote sent `FIN` and all buffered data is consumed).
- May return fewer bytes than the buffer holds — loop or use `Stream.CopyToAsync` via `AsStream()`.
- Throws:
  - `ChannelClosedException` if the channel is closed due to error (`RemoteError`, `TransportFailed`, `MuxDisposed`).
  - `OperationCanceledException` on `ct` cancellation.

## `CloseAsync`

Acknowledges the remote's intent to close (or initiates close locally if not already closed by remote). Subsequent `ReadAsync` calls return 0 immediately.

`DisposeAsync` is equivalent to `CloseAsync` + cleanup.

## `AsStream`

Returns a `Stream` wrapper. `CanRead == true`, `CanWrite == false`, `CanSeek == false`. EOF is reported when `Read` returns 0.

## Backpressure

The remote's writer is paused when our read buffer fills. Reading drains the buffer and `ACK` frames are sent back to credit the remote, allowing further sends. See [Backpressure](../concepts/backpressure.md).

## Events

Same shape as `IWriteChannel`: `Ready`, `Connected`, `Disconnected`, `Closed`.
