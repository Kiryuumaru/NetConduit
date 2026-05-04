# MultiplexerOptions

Configuration for a multiplexer session. See [StreamMultiplexer](stream-multiplexer.md) for usage.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StreamFactory` | `StreamFactoryDelegate` | (required) | Factory that creates transport stream pairs |
| `SessionId` | `Guid?` | auto-generated | Session identity |
| `DefaultSlabSize` | `int` | 1,048,576 (1 MB) | Default slab size per channel |
| `PingInterval` | `TimeSpan` | 30s | Interval between keepalive pings |
| `PingTimeout` | `TimeSpan` | 10s | Time to wait for pong reply |
| `MaxMissedPings` | `int` | 3 | Missed pings before disconnect |
| `GoAwayTimeout` | `TimeSpan` | 30s | Time to wait during graceful shutdown |
| `MaxAutoReconnectAttempts` | `int` | 0 (unlimited) | Max reconnect attempts |
| `AutoReconnectDelay` | `TimeSpan` | 1s | Base delay between reconnect attempts |
| `MaxAutoReconnectDelay` | `TimeSpan` | 30s | Maximum reconnect delay |
| `AutoReconnectBackoffMultiplier` | `double` | 2.0 | Backoff multiplier |
| `ConnectionTimeout` | `TimeSpan` | 30s | Timeout for StreamFactory calls |
| `DefaultChannelOptions` | `DefaultChannelOptions` | (defaults) | Default options for new channels |

## StreamFactory

The `StreamFactory` delegate is called to create new transport connections:

```csharp
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
```

It's called:
- Once at startup for the initial connection
- On each reconnection attempt (if reconnection is configured)

Example:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("localhost", 5000, ct);
        return new StreamPair(tcp.GetStream(), tcp);
    }
};
```

## DefaultChannelOptions

Default options applied to channels that don't specify their own:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Priority` | `ChannelPriority` | `Normal` | Default channel priority |
| `SlabSize` | `int` | 1,048,576 (1 MB) | Default slab size |
| `SendTimeout` | `TimeSpan` | 30s | Default send timeout |

## Transport Helpers

Rather than constructing `MultiplexerOptions` directly, use transport-specific helpers:

```csharp
// TCP
var options = TcpMultiplexer.CreateOptions("localhost", 5000);

// WebSocket
var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/mux");

// UDP
var options = UdpMultiplexer.CreateOptions("localhost", 5000);

// IPC
var options = IpcMultiplexer.CreateOptions("my-app");

// QUIC
var options = QuicMultiplexer.CreateOptions("localhost", 5000);
```

See [Transports](../transports/index.md) for details.

## Reconnection Example

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("server.example.com", 5000, ct);
        return new StreamPair(tcp.GetStream(), tcp);
    },
    MaxAutoReconnectAttempts = 0,           // Unlimited
    AutoReconnectDelay = TimeSpan.FromSeconds(2),
    MaxAutoReconnectDelay = TimeSpan.FromSeconds(60),
    AutoReconnectBackoffMultiplier = 2.0
};
```
