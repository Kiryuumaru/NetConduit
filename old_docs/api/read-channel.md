# IReadChannel

Inbound channel interface for reading data from the remote side. Implements `IAsyncDisposable` and `IDisposable`. See [Channels](../concepts/channels.md) for concepts.

## Accepting

```csharp
// Accept specific channel by ID (waits until ready)
var channel = await mux.AcceptChannelAsync("data", cancellationToken);

// Accept without waiting
var channel = mux.AcceptChannel("data");
await channel.WaitForReadyAsync(cancellationToken);

// Accept all channels
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    Console.WriteLine($"Got: {channel.ChannelId}");
}
```

## Reading Data

```csharp
// ReadAsync (primary method)
var buffer = new byte[4096];
var bytesRead = await channel.ReadAsync(buffer, cancellationToken);
// Returns 0 when the remote side closes the channel
```

## Stream Interop

Use `AsStream()` for APIs that require a `Stream`:

```csharp
var stream = channel.AsStream();
using var reader = new StreamReader(stream, leaveOpen: true);
var line = await reader.ReadLineAsync();
```

## Read Loop Pattern

```csharp
var buffer = new byte[4096];
int bytesRead;
while ((bytesRead = await channel.ReadAsync(buffer, ct)) > 0)
{
    ProcessData(buffer.AsSpan(0, bytesRead));
}
// Channel is closed when ReadAsync returns 0
```

## Properties

| Property         | Type                  | Description                                                 |
| ---------------- | --------------------- | ----------------------------------------------------------- |
| `ChannelId`      | `string`              | The channel identifier                                      |
| `State`          | `ChannelState`        | Current state: Opening, Open, Closing, Closed               |
| `IsReady`        | `bool`                | Whether channel is confirmed by remote (stays true forever) |
| `IsConnected`    | `bool`                | Whether the underlying transport is active                  |
| `Priority`       | `ChannelPriority`     | Priority level                                              |
| `Stats`          | `ChannelStats`        | Bytes/frames received                                       |
| `CloseReason`    | `ChannelCloseReason?` | Why the channel was closed                                  |
| `CloseException` | `Exception?`          | Exception that caused closure                               |

## Methods

| Method                                       | Returns          | Description                      |
| -------------------------------------------- | ---------------- | -------------------------------- |
| `ReadAsync(Memory<byte>, CancellationToken)` | `ValueTask<int>` | Read data (0 = EOF)              |
| `WaitForReadyAsync(CancellationToken)`       | `Task`           | Wait until confirmed by remote   |
| `CloseAsync(CancellationToken)`              | `ValueTask`      | Gracefully close the channel     |
| `AsStream()`                                 | `Stream`         | Get a Stream wrapper for interop |
| `DisposeAsync()`                             | `ValueTask`      | Close and dispose the channel    |

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
    Console.WriteLine($"Read channel closed: {e.Reason}");
};
```

## Disposing

```csharp
await channel.DisposeAsync();
```

Disposing a read channel signals the remote side that you're no longer interested in the data.
