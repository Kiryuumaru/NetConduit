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
# Errors

Exceptions and error codes in NetConduit.

## Exceptions

### MultiplexerException

Thrown for protocol-level errors:

```csharp
try
{
    mux.Start();
    await mux.WaitForReadyAsync();
}
catch (MultiplexerException ex)
{
    Console.WriteLine($"Error: {ex.ErrorCode} - {ex.Message}");
}
```

| Property    | Type        | Description                |
| ----------- | ----------- | -------------------------- |
| `ErrorCode` | `ErrorCode` | Specific error code        |
| `Message`   | `string`    | Human-readable description |

### ChannelClosedException

Thrown when operating on a closed channel:

```csharp
try
{
    await channel.WriteAsync(data);
}
catch (ChannelClosedException ex)
{
    Console.WriteLine($"Channel {ex.ChannelId} closed: {ex.CloseReason}");
}
```

| Property      | Type                 | Description                 |
| ------------- | -------------------- | --------------------------- |
| `ChannelId`   | `string`             | The channel that was closed |
| `CloseReason` | `ChannelCloseReason` | Why it was closed           |

## Error Codes

| Code               | Hex    | Description                              |
| ------------------ | ------ | ---------------------------------------- |
| `None`             | 0x0000 | No error                                 |
| `UnknownChannel`   | 0x0001 | Channel ID not recognized                |
| `ChannelExists`    | 0x0002 | Channel ID already in use                |
| `ProtocolError`    | 0x0003 | Wire protocol violation                  |
| `FlowControlError` | 0x0004 | Credit window violation                  |
| `Timeout`          | 0x0005 | Operation timed out                      |
| `Internal`         | 0x0006 | Internal error                           |
| `Refused`          | 0x0007 | Connection refused                       |
| `Cancel`           | 0x0008 | Operation cancelled                      |
| `SessionMismatch`  | 0x0009 | Session ID doesn't match on reconnection |

## Channel Close Reasons

| Reason            | Description                       |
| ----------------- | --------------------------------- |
| `LocalClose`      | You disposed the channel          |
| `RemoteFin`       | Remote side closed gracefully     |
| `RemoteError`     | Remote side sent an error frame   |
| `TransportFailed` | Underlying transport disconnected |
| `MuxDisposed`     | Multiplexer was disposed          |

## Disconnect Reasons

| Reason           | Description                             |
| ---------------- | --------------------------------------- |
| `GoAwayReceived` | Remote side initiated graceful shutdown |
| `TransportError` | Underlying transport failed             |
| `LocalDispose`   | Local `DisposeAsync` was called         |

## Common Patterns

### Handling channel closure gracefully

```csharp
try
{
    await foreach (var data in ReadAllAsync(channel, ct))
    {
        Process(data);
    }
}
catch (ChannelClosedException ex) when (ex.CloseReason == ChannelCloseReason.RemoteFin)
{
    // Normal closure - remote side is done sending
}
catch (ChannelClosedException ex) when (ex.CloseReason == ChannelCloseReason.TransportFailed)
{
    // Transport died - may reconnect automatically
}
```

### Handling multiplexer errors

```csharp
mux.Error += (sender, e) =>
{
    if (e.Exception is MultiplexerException mex)
        logger.LogError("Mux error {Code}: {Message}", mex.ErrorCode, mex.Message);
    else
        logger.LogError(e.Exception, "Unexpected error");
};
```
