# Errors

Namespace: `NetConduit.Exceptions`.

## `ChannelClosedException`

```csharp
public sealed class ChannelClosedException : Exception
{
    public ChannelClosedException(
        string channelId,
        ChannelCloseReason closeReason,
        Exception? innerException = null);

    public string             ChannelId   { get; }
    public ChannelCloseReason CloseReason { get; }
}
```

Thrown by:

- `IWriteChannel.WriteAsync` after the channel has closed.
- `IReadChannel.ReadAsync` when the channel closes due to an error (not normal EOF; EOF returns `0`).
- Transit `SendAsync` / `ReceiveAsync` when their backing channels are closed.

The `CloseReason` indicates **why** the channel closed; the message is `"Channel '{id}' is closed ({reason})."`.

## `MultiplexerException`

```csharp
public sealed class MultiplexerException : Exception
{
    public MultiplexerException(
        ErrorCode errorCode,
        string message,
        Exception? innerException = null);

    public ErrorCode ErrorCode { get; }
}
```

Thrown for protocol-level errors (received `ERR` frames, malformed frames, session mismatches). The `ErrorCode` corresponds to the wire-level code in `NetConduit.Enums.ErrorCode`.

Surfaces as:
- An exception inside `Error` event handler.
- An exception thrown from `Start` if the very first handshake fails fatally.
- The `Exception` on `DisconnectedEventArgs` after a protocol-fatal disconnect.

## Standard exceptions

Other exceptions can surface through NetConduit APIs:

| Exception | Source |
| --- | --- |
| `InvalidOperationException` | `Start()` called twice; `OpenChannel` with duplicate ID; operations before `Start()`. |
| `ArgumentException` / `ArgumentNullException` | Required option missing or invalid (e.g., null `StreamFactory`). |
| `OperationCanceledException` | Cancellation token signaled. |
| `TimeoutException` | `WriteAsync` exceeding `ChannelOptions.SendTimeout`. |
| `IOException` | Underlying transport IO errors (typically wrapped in `MultiplexerException`). |

## Pattern — discriminate close reasons

```csharp
try
{
    await ch.WriteAsync(buffer);
}
catch (ChannelClosedException ex)
{
    switch (ex.CloseReason)
    {
        case ChannelCloseReason.RemoteFin:
            // peer closed gracefully; expected
            break;
        case ChannelCloseReason.TransportFailed:
            // reconnect exhausted; surface to caller
            throw;
        case ChannelCloseReason.MuxDisposed:
            // shutting down
            return;
    }
}
```
