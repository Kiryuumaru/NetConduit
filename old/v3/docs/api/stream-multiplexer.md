# IStreamMultiplexer

The core interface that multiplexes multiple channels over a single bidirectional stream. Extends `IAsyncDisposable`. The concrete implementation is created via `StreamMultiplexer.Create()`.

## Creating a Multiplexer

Use the static `Create` factory method with [MultiplexerOptions](multiplexer-options.md):

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000);
var mux = StreamMultiplexer.Create(options);
```

Transport helpers create the options for you — see [Transports](../transports/index.md).

## Lifecycle

A multiplexer goes through these stages:

```
Create → Start → Ready → Active → Shutdown/Dispose
```

### Start

`Start` launches the background run loop (handshake, read/write loops):

```csharp
var mux = StreamMultiplexer.Create(options);
mux.Start();
```

`Start` is synchronous and returns `void`. It kicks off background tasks but does not wait for the connection to be ready.

### WaitForReadyAsync

Blocks until the first successful connection and handshake complete:

```csharp
await mux.WaitForReadyAsync(cancellationToken);
// Now safe to open/accept channels
```

### GoAwayAsync

Initiates graceful shutdown — signals the remote side that no new channels will be opened:

```csharp
await mux.GoAwayAsync(cancellationToken);
```

See [Graceful Shutdown](../concepts/graceful-shutdown.md) for the full shutdown protocol.

### FlushAsync

Force an immediate flush of pending writes to the transport:

```csharp
await mux.FlushAsync(cancellationToken);
```

### DisposeAsync

Disposes all channels and the underlying transport:

```csharp
await mux.DisposeAsync();
```

Always dispose with `await using`:

```csharp
await using var mux = StreamMultiplexer.Create(options);
```

## Channel Operations

### Opening Channels

Open an outbound channel (the remote side must accept it):

```csharp
// Simple — default options
var channel = mux.OpenChannel("data");

// Custom options
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "priority-data",
    Priority = ChannelPriority.High,
    SlabSize = 4 * 1024 * 1024
});
```

Returns an [`IWriteChannel`](write-channel.md).

### Accepting Channels

Accept inbound channels opened by the remote side:

```csharp
// Accept a specific channel
var channel = await mux.AcceptChannelAsync("data", cancellationToken);

// Accept all channels
await foreach (var channel in mux.AcceptChannelsAsync(cancellationToken))
{
    _ = HandleChannelAsync(channel);
}
```

Returns [`IReadChannel`](read-channel.md) instances.

### Looking Up Channels

Find existing channels by ID:

```csharp
var writeChannel = mux.GetWriteChannel("data");    // null if not found
var readChannel = mux.GetReadChannel("data");      // null if not found
```

## Properties

| Property             | Type                          | Description                                                      |
| -------------------- | ----------------------------- | ---------------------------------------------------------------- |
| `Options`            | `MultiplexerOptions`          | The configuration                                                |
| `Stats`              | `MultiplexerStats`            | Runtime statistics                                               |
| `IsReady`            | `bool`                        | Whether mux has completed initial handshake (stays true forever) |
| `IsConnected`        | `bool`                        | Whether transport is connected                                   |
| `IsRunning`          | `bool`                        | Whether mux is started and not disposed                          |
| `IsShuttingDown`     | `bool`                        | Whether GoAway is in progress                                    |
| `SessionId`          | `Guid`                        | Local session identity                                           |
| `RemoteSessionId`    | `Guid`                        | Remote peer's session identity                                   |
| `ActiveChannelIds`   | `IReadOnlyCollection<string>` | IDs of all active channels                                       |
| `ActiveChannelCount` | `int`                         | Number of active channels                                        |
| `DisconnectReason`   | `DisconnectReason?`           | Reason for last disconnection                                    |

## Events

| Event             | Signature                               | Description                                |
| ----------------- | --------------------------------------- | ------------------------------------------ |
| `Ready`           | `EventHandler?`                         | First handshake complete (fires once)      |
| `Connected`       | `EventHandler?`                         | Transport connected (initial or reconnect) |
| `Disconnected`    | `EventHandler<DisconnectedEventArgs>?`  | Transport disconnected                     |
| `Reconnecting`    | `EventHandler<ReconnectingEventArgs>?`  | Reconnection attempt starting              |
| `ChannelOpened`   | `EventHandler<ChannelEventArgs>?`       | Outbound channel opened locally            |
| `ChannelAccepted` | `EventHandler<ChannelEventArgs>?`       | Inbound channel confirmed by remote        |
| `ChannelClosed`   | `EventHandler<ChannelClosedEventArgs>?` | Channel closed                             |
| `Error`           | `EventHandler<ErrorEventArgs>?`         | Error occurred                             |

See [Events](../concepts/events.md) for details.
