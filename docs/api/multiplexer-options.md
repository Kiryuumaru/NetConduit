# MultiplexerOptions

Configuration for creating a StreamMultiplexer. See [Getting Started](../getting-started.md) for basic usage.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StreamFactory` | `StreamFactoryDelegate` | *required* | Factory to create `IStreamPair` connections |
| `SessionId` | `Guid?` | auto-generated | Session identifier for reconnection support |
| `MaxFrameSize` | `int` | 16MB | Maximum payload per frame |
| `PingInterval` | `TimeSpan` | 30s | Heartbeat interval |
| `PingTimeout` | `TimeSpan` | 10s | Max wait for pong response |
| `MaxMissedPings` | `int` | 3 | Missed pings before disconnect |
| `GoAwayTimeout` | `TimeSpan` | 30s | Timeout for graceful shutdown after GOAWAY |
| `GracefulShutdownTimeout` | `TimeSpan` | 5s | Timeout for DisposeAsync pending operations |
| `DefaultChannelOptions` | `DefaultChannelOptions` | (see below) | Defaults for new channels |
| `MaxAutoReconnectAttempts` | `int` | 0 (unlimited) | Max reconnection attempts before giving up |
| `AutoReconnectDelay` | `TimeSpan` | 1s | Initial delay between reconnection attempts |
| `MaxAutoReconnectDelay` | `TimeSpan` | 30s | Maximum delay (exponential backoff cap) |
| `AutoReconnectBackoffMultiplier` | `double` | 2.0 | Multiplier for exponential backoff |
| `ConnectionTimeout` | `TimeSpan` | Infinite | Timeout for each StreamFactory call |
| `HandshakeTimeout` | `TimeSpan` | Infinite | Timeout for handshake after connection |
| `FlushMode` | `FlushMode` | Batched | Frame flushing strategy |
| `FlushInterval` | `TimeSpan` | 1ms | Interval for batched flushing |

All properties are `init`-only. Use the `configure` callback on transport factory methods, or set them in object initializers.

## StreamFactory

The `StreamFactory` delegate creates connections. It returns an `IStreamPair` (separate read/write streams with disposal):

```csharp
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
```

Transport helpers set this automatically:

```csharp
// Transport helpers handle StreamFactory for you
var options = TcpMultiplexer.CreateOptions("localhost", 5000);

// Or configure additional properties
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.PingInterval = TimeSpan.FromSeconds(15);
    o.MaxMissedPings = 2;
});
```

## DefaultChannelOptions

Defaults applied to channels when not overridden in `ChannelOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MinCredits` | `uint` | 64KB | Minimum credit window |
| `MaxCredits` | `uint` | 4MB | Maximum credit window (initial) |
| `SendTimeout` | `TimeSpan` | 30s | Timeout waiting for credits |
| `Priority` | `ChannelPriority` | Normal (128) | Default channel priority |

## Heartbeat Configuration

Heartbeats detect dead connections. See [Reconnection](../concepts/reconnection.md) for recovery behavior.

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.PingInterval = TimeSpan.FromSeconds(15);
    o.PingTimeout = TimeSpan.FromSeconds(5);
    o.MaxMissedPings = 2;
});
// Disconnect after: 15s + 15s + 5s = 35 seconds of silence
```

## Reconnection Configuration

See [Reconnection](../concepts/reconnection.md) for full behavior details.

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.MaxAutoReconnectAttempts = 10;
    o.AutoReconnectDelay = TimeSpan.FromSeconds(2);
    o.MaxAutoReconnectDelay = TimeSpan.FromSeconds(30);
    o.AutoReconnectBackoffMultiplier = 2.0;
});
```

## Flush Modes

```csharp
public enum FlushMode
{
    Immediate,  // Flush after every frame
    Batched,    // Batch frames, flush on interval
    Manual      // Never explicitly flush, rely on stream buffering
}
```

```csharp
// Low latency - flush immediately
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Immediate;
});

// High throughput - batch frames
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Batched;
    o.FlushInterval = TimeSpan.FromMilliseconds(5);
});

// Maximum throughput - manual flush
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Manual;
});
```

## Example Configurations

### Low Latency (Interactive)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Immediate;
    o.PingInterval = TimeSpan.FromSeconds(10);
    o.PingTimeout = TimeSpan.FromSeconds(3);
});
```

### High Throughput (Bulk Transfer)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Batched;
    o.FlushInterval = TimeSpan.FromMilliseconds(10);
    o.MaxFrameSize = 64 * 1024 * 1024;  // 64MB frames
});
```

### Mobile (Unstable Network)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.MaxAutoReconnectAttempts = 20;
    o.AutoReconnectDelay = TimeSpan.FromSeconds(1);
    o.MaxAutoReconnectDelay = TimeSpan.FromMinutes(1);
    o.PingInterval = TimeSpan.FromSeconds(20);
    o.MaxMissedPings = 5;
});
```

### Localhost (IPC-style)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.FlushMode = FlushMode.Immediate;
    o.MaxAutoReconnectAttempts = 1;  // Fast fail
    o.PingInterval = TimeSpan.FromMinutes(5);  // Rare pings
});
```
