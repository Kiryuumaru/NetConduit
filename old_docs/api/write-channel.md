# IWriteChannel

Outbound channel interface for writing data to the remote side. Implements `IAsyncDisposable` and `IDisposable`. See [Channels](../concepts/channels.md) for concepts.

## Creating

```csharp
// From multiplexer
var channel = mux.OpenChannel("data");

// With options
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High
});

// Open and wait for ready
var channel = await mux.OpenChannelAsync("data", cancellationToken);
```

## Writing Data

```csharp
// WriteAsync (primary method)
await channel.WriteAsync(data, cancellationToken);

// Wait for channel confirmation first
await channel.WaitForReadyAsync(cancellationToken);
await channel.WriteAsync(data);
```

## Stream Interop

Use `AsStream()` for APIs that require a `Stream`:

```csharp
var stream = channel.AsStream();
using var writer = new StreamWriter(stream, leaveOpen: true);
await writer.WriteLineAsync("Hello!");
```

## Properties

| Property         | Type                  | Description                                                 |
| ---------------- | --------------------- | ----------------------------------------------------------- |
| `ChannelId`      | `string`              | The channel identifier                                      |
| `State`          | `ChannelState`        | Current state: Opening, Open, Closing, Closed               |
| `IsReady`        | `bool`                | Whether channel is confirmed by remote (stays true forever) |
| `IsConnected`    | `bool`                | Whether the underlying transport is active                  |
| `Priority`       | `ChannelPriority`     | Priority level                                              |
| `Stats`          | `ChannelStats`        | Bytes/frames sent                                           |
| `CloseReason`    | `ChannelCloseReason?` | Why the channel was closed                                  |
| `CloseException` | `Exception?`          | Exception that caused closure                               |

## Methods

| Method                                                | Returns     | Description                      |
| ----------------------------------------------------- | ----------- | -------------------------------- |
| `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)` | `ValueTask` | Write data to the channel        |
| `WaitForReadyAsync(CancellationToken)`                | `Task`      | Wait until confirmed by remote   |
| `CloseAsync(CancellationToken)`                       | `ValueTask` | Send FIN frame gracefully        |
| `AsStream()`                                          | `Stream`    | Get a Stream wrapper for interop |
| `DisposeAsync()`                                      | `ValueTask` | Close and dispose the channel    |

## Events

| Event          | Signature                              | Description                                |
| -------------- | -------------------------------------- | ------------------------------------------ |
| `Ready`        | `EventHandler?`                        | Channel confirmed by remote (fires once)   |
| `Connected`    | `EventHandler?`                        | Transport connected (including reconnects) |
| `Disconnected` | `EventHandler<DisconnectedEventArgs>?` | Transport disconnected                     |
| `Closed`       | `EventHandler<ChannelCloseEventArgs>?` | Channel closed                             |

```csharp
channel.Closed += (sender, e) =>
{
    Console.WriteLine($"Write channel closed: {e.Reason}");
};
```

## Disposing

```csharp
// Explicit
await channel.DisposeAsync();

// await using
await using var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

Disposing a write channel sends a FIN to the remote side, signaling end-of-stream.
