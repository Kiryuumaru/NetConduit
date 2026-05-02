# Errors and Exceptions

NetConduit uses typed exceptions and error codes to communicate failures precisely.

## Exception Types

### MultiplexerException

Thrown for protocol-level and multiplexer-level errors:

```csharp
public class MultiplexerException : Exception
{
    public ErrorCode ErrorCode { get; }
}
```

```csharp
try
{
    await mux.OpenChannelAsync("data");
}
catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.ChannelExists)
{
    Console.WriteLine($"Channel already exists: {ex.Message}");
}
```

### ChannelClosedException

Thrown when reading from or writing to a closed channel:

```csharp
public class ChannelClosedException : Exception
{
    public string ChannelId { get; }
    public ChannelCloseReason CloseReason { get; }
}
```

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

## ErrorCode

Protocol-level error codes sent in error frames:

```csharp
public enum ErrorCode : ushort
{
    None             = 0x0000,
    UnknownChannel   = 0x0001,
    ChannelExists    = 0x0002,
    ProtocolError    = 0x0003,
    FlowControlError = 0x0004,
    Timeout          = 0x0005,
    Internal         = 0x0006,
    Refused          = 0x0007,
    Cancel           = 0x0008,
    SessionMismatch  = 0x0009
}
```

| Code | When |
|------|------|
| `None` | No error |
| `UnknownChannel` | Frame received for a channel that doesn't exist |
| `ChannelExists` | Attempt to open a channel with an ID already in use |
| `ProtocolError` | Invalid frame sequence or format |
| `FlowControlError` | Writer exceeded granted credits |
| `Timeout` | Operation timed out (handshake, ping, send) |
| `Internal` | Unexpected internal error |
| `Refused` | Channel open was refused by the remote side |
| `Cancel` | Operation was cancelled |
| `SessionMismatch` | Reconnection session ID doesn't match |

## ChannelCloseReason

Why a channel was closed:

```csharp
public enum ChannelCloseReason
{
    LocalClose,
    RemoteFin,
    RemoteError,
    TransportFailed,
    MuxDisposed
}
```

| Reason | Meaning |
|--------|---------|
| `LocalClose` | You called `CloseAsync()` or `DisposeAsync()` |
| `RemoteFin` | Remote side gracefully closed its channel |
| `RemoteError` | Remote side sent an error frame for this channel |
| `TransportFailed` | The underlying transport connection failed |
| `MuxDisposed` | The multiplexer was disposed while the channel was open |

## DisconnectReason

Why the multiplexer disconnected:

```csharp
public enum DisconnectReason
{
    GoAwayReceived,
    TransportError,
    LocalDispose
}
```

| Reason | Meaning |
|--------|---------|
| `GoAwayReceived` | Remote side sent a GoAway frame (graceful shutdown) |
| `TransportError` | Underlying transport failed (network error, timeout) |
| `LocalDispose` | You called `DisposeAsync()` |

## Error Handling Patterns

### Catch Specific Errors

```csharp
try
{
    var channel = await mux.OpenChannelAsync("data");
    await channel.WriteAsync(payload);
    await channel.CloseAsync();
}
catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.ChannelExists)
{
    // Channel ID already in use — use a different ID
}
catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.Refused)
{
    // Remote side refused the channel — handle rejection
}
catch (ChannelClosedException ex) when (ex.CloseReason == ChannelCloseReason.TransportFailed)
{
    // Connection lost during write — wait for reconnection or abort
}
catch (ChannelClosedException ex)
{
    // Channel closed for another reason
}
```

### Detect Timeouts

Write operations throw when `SendTimeout` expires waiting for credits:

```csharp
try
{
    await channel.WriteAsync(largePayload, cancellationToken);
}
catch (TimeoutException)
{
    Console.WriteLine("Receiver is not consuming data fast enough");
}
```

### Handle Disconnection via Events

For connection-level errors, prefer events over try/catch:

```csharp
mux.OnDisconnected += (reason, ex) =>
{
    switch (reason)
    {
        case DisconnectReason.GoAwayReceived:
            Console.WriteLine("Server is shutting down");
            break;
        case DisconnectReason.TransportError:
            Console.WriteLine($"Network error: {ex?.Message}");
            break;
        case DisconnectReason.LocalDispose:
            break; // Expected
    }
};
```

### Channel Close Handling

```csharp
channel.OnClosed += (reason, ex) =>
{
    switch (reason)
    {
        case ChannelCloseReason.RemoteFin:
            // Normal — remote is done sending
            break;
        case ChannelCloseReason.RemoteError:
            Console.WriteLine($"Remote error on {channel.ChannelId}: {ex?.Message}");
            break;
        case ChannelCloseReason.TransportFailed:
            Console.WriteLine("Connection lost");
            break;
    }
};
```

## See Also

- [Events](../concepts/events.md) — Event handling patterns
- [Reconnection](../concepts/reconnection.md) — Automatic recovery from transport errors
- [StreamMultiplexer](stream-multiplexer.md) — Multiplexer lifecycle and properties
