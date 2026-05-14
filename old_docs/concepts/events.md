# Events

Multiplexer and channel lifecycle events for monitoring connection state. See [Concepts Overview](index.md) for related concepts.

## Multiplexer Events

| Event             | Signature                               | When                                       |
| ----------------- | --------------------------------------- | ------------------------------------------ |
| `Ready`           | `EventHandler?`                         | First handshake complete (fires once)      |
| `Connected`       | `EventHandler?`                         | Transport connected (initial or reconnect) |
| `Disconnected`    | `EventHandler<DisconnectedEventArgs>?`  | Transport disconnected                     |
| `Reconnecting`    | `EventHandler<ReconnectingEventArgs>?`  | Reconnection attempt starting              |
| `ChannelOpened`   | `EventHandler<ChannelEventArgs>?`       | Outbound channel opened locally            |
| `ChannelAccepted` | `EventHandler<ChannelEventArgs>?`       | Inbound channel confirmed by remote        |
| `ChannelClosed`   | `EventHandler<ChannelClosedEventArgs>?` | Channel closed                             |
| `Error`           | `EventHandler<ErrorEventArgs>?`         | Error occurred                             |

## Usage

```csharp
var mux = StreamMultiplexer.Create(options);

mux.Ready += (sender, e) =>
    Console.WriteLine("Multiplexer ready!");

mux.Connected += (sender, e) =>
    Console.WriteLine("Connected!");

mux.Disconnected += (sender, e) =>
    Console.WriteLine($"Disconnected: {e.Reason} ({e.Exception?.Message})");

mux.Reconnecting += (sender, e) =>
    Console.WriteLine($"Reconnecting... attempt {e.Attempt}");

mux.ChannelOpened += (sender, e) =>
    Console.WriteLine($"Channel opened: {e.ChannelId}");

mux.ChannelAccepted += (sender, e) =>
    Console.WriteLine($"Channel accepted: {e.ChannelId}");

mux.ChannelClosed += (sender, e) =>
    Console.WriteLine($"Channel closed: {e.ChannelId}");

mux.Error += (sender, e) =>
    Console.WriteLine($"Error: {e.Exception.Message}");

mux.Start();
```

## Event Args Types

| Type                     | Properties                                              |
| ------------------------ | ------------------------------------------------------- |
| `ChannelEventArgs`       | `ChannelId` (string)                                    |
| `ChannelClosedEventArgs` | `ChannelId` (string), `Exception` (Exception?)          |
| `ChannelCloseEventArgs`  | `Reason` (ChannelCloseReason), `Exception` (Exception?) |
| `DisconnectedEventArgs`  | `Reason` (DisconnectReason), `Exception` (Exception?)   |
| `ErrorEventArgs`         | `Exception` (Exception)                                 |
| `ReconnectingEventArgs`  | `Attempt` (int)                                         |

## Event Ordering

Events fire in this order during the multiplexer lifecycle:

```
Start()
  → Connected
  → Ready (once, never again)
  → ChannelOpened (per channel)
  → ChannelAccepted (per channel)
  → ChannelClosed (per channel)
  → Disconnected
  → Reconnecting (if reconnection enabled)
  → Connected (reconnected)
  → ...
  → Disconnected (final)
```

## Channel Events

Individual channels have their own events:

| Event          | Signature                              | When                                       |
| -------------- | -------------------------------------- | ------------------------------------------ |
| `Ready`        | `EventHandler?`                        | Channel confirmed by remote (fires once)   |
| `Connected`    | `EventHandler?`                        | Transport connected (including reconnects) |
| `Disconnected` | `EventHandler<DisconnectedEventArgs>?` | Transport disconnected                     |
| `Closed`       | `EventHandler<ChannelCloseEventArgs>?` | Channel closed                             |

```csharp
var channel = mux.OpenChannel("data");
channel.Closed += (sender, e) =>
{
    Console.WriteLine($"Channel closed: {e.Reason}");
};
```

See [Channels](channels.md) for close reasons.

## Disconnect Reasons

| Reason           | Description                             |
| ---------------- | --------------------------------------- |
| `GoAwayReceived` | Remote side initiated graceful shutdown |
| `TransportError` | Underlying transport failed             |
| `LocalDispose`   | Local `DisposeAsync` was called         |

## Thread Safety

Events fire on background threads. Use synchronization when updating shared state:

```csharp
var connected = false;

mux.Connected += (sender, e) => Volatile.Write(ref connected, true);
mux.Disconnected += (sender, e) => Volatile.Write(ref connected, false);
```
