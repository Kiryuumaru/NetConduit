# Flush Modes

Controls when the multiplexer sends buffered frames to the transport.

## Overview

Frames are written to an internal buffer. The flush mode determines when that buffer is flushed to the underlying stream.

```csharp
public enum FlushMode
{
    Immediate,  // Flush after every frame
    Batched,    // Flush periodically (default)
    Manual      // You control when to flush
}
```

## Batched (Default)

Frames are batched and flushed at a regular interval:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    FlushMode = FlushMode.Batched,       // Default
    FlushInterval = TimeSpan.FromMilliseconds(1) // Default: 1ms
};
```

Multiple frames written within the interval are combined into a single transport write. This is the best balance of throughput and latency for most workloads.

### When to Use

- General-purpose applications
- Mixed message sizes and rates
- When you want good performance without tuning

## Immediate

Every frame is flushed to the transport immediately:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    FlushMode = FlushMode.Immediate
};
```

Lower latency but higher overhead — each frame triggers a separate transport write.

### When to Use

- Real-time applications where every millisecond matters
- Low message rates where batching adds unnecessary latency
- Interactive protocols (game input, cursor position)

## Manual

No automatic flushing. You call `Flush()` explicitly:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    FlushMode = FlushMode.Manual
};

var mux = StreamMultiplexer.Create(options);

// ... write frames to channels ...

// Flush when you're ready
mux.Flush();
```

`Flush()` throws `InvalidOperationException` if called in Batched or Immediate mode.

### When to Use

- High-throughput batch processing where you control the send loop
- Custom frame grouping (send N frames, then flush)
- Game server tick-based updates (write all state, flush once per tick)

### Pattern: Game Server Tick

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    FlushMode = FlushMode.Manual
};
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

while (!cancellationToken.IsCancellationRequested)
{
    // Compute game state
    var state = ComputeGameState();

    // Write all updates (buffered, not sent yet)
    foreach (var (channelId, data) in state.PlayerUpdates)
    {
        var channel = mux.GetWriteChannel(channelId);
        if (channel is not null)
            await channel.WriteAsync(data);
    }

    // Send everything in one batch
    mux.Flush();

    await Task.Delay(tickInterval, cancellationToken);
}
```

## Comparison

| Mode | Latency | Throughput | Control |
|------|---------|------------|---------|
| `Immediate` | Lowest | Lowest | None needed |
| `Batched` | Low (~1ms default) | High | None needed |
| `Manual` | You decide | Highest | Call `Flush()` |

## FlushInterval

Only applies to `Batched` mode. Controls the maximum time frames sit in the buffer:

```csharp
// Flush every 5ms — higher throughput, slightly more latency
FlushInterval = TimeSpan.FromMilliseconds(5)

// Flush every 100μs — lower latency, more flushes
FlushInterval = TimeSpan.FromMilliseconds(0.1)
```

Default is 1 millisecond, which provides sub-millisecond effective latency for most workloads.

## See Also

- [MultiplexerOptions](../api/multiplexer-options.md) — FlushMode and FlushInterval configuration
- [StreamMultiplexer](../api/stream-multiplexer.md) — `Flush()` method reference
