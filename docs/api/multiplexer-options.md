# MultiplexerOptions

Configuration for creating a StreamMultiplexer. See [Getting Started](../getting-started.md) for basic usage.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StreamFactory` | `Func<CancellationToken, Task<(Stream, Stream)>>` | *required* | Factory to create stream pairs |
| `MaxFrameSize` | `int` | 16MB | Maximum payload per frame |
| `PingInterval` | `TimeSpan` | 30s | Heartbeat interval |
| `PingTimeout` | `TimeSpan` | 10s | Max wait for pong response |
| `MaxMissedPings` | `int` | 3 | Missed pings before disconnect |
| `EnableReconnection` | `bool` | true | Enable automatic reconnection |
| `ReconnectTimeout` | `TimeSpan` | 60s | Max time to attempt reconnection |
| `ReconnectDelay` | `TimeSpan` | 1s | Delay between reconnect attempts |
| `ReconnectBufferSize` | `int` | 1MB | Buffer for pending data during disconnect |
| `GracefulShutdownTimeout` | `TimeSpan` | 5s | Timeout for graceful shutdown |
| `FlushMode` | `FlushMode` | Batched | Frame flushing strategy |
| `FlushInterval` | `TimeSpan` | 1ms | Interval for batched flushing |

## StreamFactory

The stream factory is called to establish connections:

```csharp
// Simple: Same stream for read/write
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var client = new TcpClient();
        await client.ConnectAsync("localhost", 5000, ct);
        var stream = client.GetStream();
        return (stream, stream);  // (readStream, writeStream)
    }
};

// Split streams for read/write
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var readPipe = new Pipe();
        var writePipe = new Pipe();
        return (readPipe.Reader.AsStream(), writePipe.Writer.AsStream());
    }
};
```

## Heartbeat Configuration

Heartbeats detect dead connections. See [Reconnection](../concepts/reconnection.md) for recovery behavior.

```csharp
var options = new MultiplexerOptions
{
    PingInterval = TimeSpan.FromSeconds(15),  // Ping every 15s
    PingTimeout = TimeSpan.FromSeconds(5),    // Wait 5s for pong
    MaxMissedPings = 2                        // Disconnect after 2 missed
};
// Disconnect after: 15s + 15s + 5s = 35 seconds of silence
```

## Reconnection Configuration

See [Reconnection](../concepts/reconnection.md) for full behavior details.

```csharp
var options = new MultiplexerOptions
{
    EnableReconnection = true,
    ReconnectTimeout = TimeSpan.FromMinutes(2),  // Try for 2 minutes
    ReconnectDelay = TimeSpan.FromSeconds(2),    // Wait 2s between attempts
    ReconnectBufferSize = 4 * 1024 * 1024        // 4MB buffer
};
```

## Flush Modes

```csharp
public enum FlushMode
{
    Immediate,  // Flush after every frame
    Batched     // Batch frames, flush on interval
}
```

```csharp
// Low latency - flush immediately
var options = new MultiplexerOptions
{
    FlushMode = FlushMode.Immediate
};

// High throughput - batch frames
var options = new MultiplexerOptions
{
    FlushMode = FlushMode.Batched,
    FlushInterval = TimeSpan.FromMilliseconds(5)
};
```

## Example Configurations

### Low Latency (Interactive)

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = streamFactory,
    FlushMode = FlushMode.Immediate,
    PingInterval = TimeSpan.FromSeconds(10),
    PingTimeout = TimeSpan.FromSeconds(3)
};
```

### High Throughput (Bulk Transfer)

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = streamFactory,
    FlushMode = FlushMode.Batched,
    FlushInterval = TimeSpan.FromMilliseconds(10),
    MaxFrameSize = 64 * 1024 * 1024  // 64MB frames
};
```

### Mobile (Unstable Network)

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = streamFactory,
    EnableReconnection = true,
    ReconnectTimeout = TimeSpan.FromMinutes(5),
    ReconnectBufferSize = 8 * 1024 * 1024,  // 8MB
    PingInterval = TimeSpan.FromSeconds(20),
    MaxMissedPings = 5
};
```

### Localhost (IPC-style)

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = streamFactory,
    FlushMode = FlushMode.Immediate,
    EnableReconnection = false,  // Fast fail
    PingInterval = TimeSpan.FromMinutes(5)  // Rare pings
};
```
