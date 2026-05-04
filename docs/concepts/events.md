# Events

Multiplexer lifecycle events for monitoring connection state. See [Concepts Overview](index.md) for related concepts.

## Available Events

| Event | Signature | When |
|-------|-----------|------|
| `OnConnected` | `Action` | Transport connected (initial or reconnect) |
| `OnDisconnected` | `Action<DisconnectReason, Exception?>` | Transport disconnected |
| `OnReconnecting` | `Action<int>` | Reconnection attempt starting (parameter = attempt number) |
| `OnChannelOpened` | `Action<string>` | Channel opened (parameter = channel ID) |
| `OnChannelClosed` | `Action<string, Exception?>` | Channel closed |
| `OnError` | `Action<Exception>` | Error occurred |

## Usage

```csharp
var mux = StreamMultiplexer.Create(options);

mux.OnConnected += () =>
    Console.WriteLine("Connected!");

mux.OnDisconnected += (reason, ex) =>
    Console.WriteLine($"Disconnected: {reason} ({ex?.Message})");

mux.OnReconnecting += (attempt) =>
    Console.WriteLine($"Reconnecting... attempt {attempt}");

mux.OnChannelOpened += (channelId) =>
    Console.WriteLine($"Channel opened: {channelId}");

mux.OnChannelClosed += (channelId, ex) =>
    Console.WriteLine($"Channel closed: {channelId}");

mux.OnError += (ex) =>
    Console.WriteLine($"Error: {ex.Message}");

mux.Start();
```

## Event Ordering

Events fire in this order during the multiplexer lifecycle:

```
Start()
  → OnConnected
  → OnChannelOpened (per channel)
  → OnChannelClosed (per channel)
  → OnDisconnected
  → OnReconnecting (if reconnection enabled)
  → OnConnected (reconnected)
  → ...
  → OnDisconnected (final)
```

## Disconnect Reasons

| Reason | Description |
|--------|-------------|
| `GoAwayReceived` | Remote side initiated graceful shutdown |
| `TransportError` | Underlying transport failed |
| `LocalDispose` | Local `DisposeAsync` was called |

## Channel Close Event

Individual channels also have a close event:

```csharp
var channel = mux.OpenChannel("data");
channel.OnClosed += (reason, ex) =>
{
    Console.WriteLine($"Channel closed: {reason}");
};
```

See [Channels](channels.md) for close reasons.

## Thread Safety

Events fire on background threads. Use synchronization when updating shared state:

```csharp
var connected = false;

mux.OnConnected += () => Volatile.Write(ref connected, true);
mux.OnDisconnected += (_, _) => Volatile.Write(ref connected, false);
```
