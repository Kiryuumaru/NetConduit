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
