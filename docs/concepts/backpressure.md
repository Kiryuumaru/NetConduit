# Backpressure

Credit-based flow control prevents fast senders from overwhelming slow receivers. See [Concepts Overview](index.md) for related topics.

## How It Works

```
┌────────────────────────────────────────────────────────────┐
│                                                            │
│  Sender                              Receiver              │
│  ┌──────┐                            ┌──────┐              │
│  │      │  ──── data + credits ───▶  │      │              │
│  │      │                            │      │              │
│  │      │  ◀─── credit grant ─────   │      │              │
│  └──────┘                            └──────┘              │
│                                                            │
│  1. Receiver starts with MaxCredits (e.g., 4MB)            │
│  2. Each send consumes credits by bytes sent               │
│  3. When receiver reads data, it grants credits back       │
│  4. If credits hit 0, sender blocks until granted          │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

## Default Behavior

Out of the box, backpressure works automatically:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

// Writes may block if receiver is slow
await channel.WriteAsync(largeData);  // Might wait for credits
```

## Configuration

### Per-Channel Options

See [ChannelOptions](../api/channel-options.md) for full details.

```csharp
var options = new ChannelOptions
{
    ChannelId = "controlled",
    
    // Credit buffer sizes
    MinCredits = 64 * 1024,        // 64KB minimum
    MaxCredits = 4 * 1024 * 1024,  // 4MB maximum (starts here)
    
    // When to auto-grant credits (50% = grant when half consumed)
    CreditGrantThreshold = 0.5,
    
    // Timeout if credits exhausted
    SendTimeout = TimeSpan.FromSeconds(30)
};

var channel = await mux.OpenChannelAsync(options);
```

### Option Descriptions

| Option | Default | Description |
|--------|---------|-------------|
| `MinCredits` | 64KB | Minimum credit buffer (adapts down to this) |
| `MaxCredits` | 4MB | Maximum credit buffer (starts here) |
| `CreditGrantThreshold` | 0.5 | Auto-grant when this fraction consumed |
| `SendTimeout` | 30s | Timeout waiting for credits |

## Adaptive Credits

Credits adapt between `MinCredits` and `MaxCredits`:

- **Fast receiver**: Credits stay high
- **Slow receiver**: Credits decrease toward minimum
- **Buffer bloat avoided**: Prevents memory buildup

## Monitoring Backpressure

### Channel Stats

```csharp
var stats = channel.Stats;

Console.WriteLine($"Credit starvation events: {stats.CreditStarvationCount}");
Console.WriteLine($"Total wait time: {stats.TotalWaitTimeForCredits}");
Console.WriteLine($"Longest single wait: {stats.LongestWaitForCredits}");
Console.WriteLine($"Currently waiting: {stats.IsWaitingForCredits}");
Console.WriteLine($"Current wait duration: {stats.CurrentWaitDuration}");
```

### Channel Events

```csharp
channel.OnCreditStarvation += () =>
{
    Console.WriteLine($"Channel '{channel.ChannelId}' blocked - no credits");
};

channel.OnCreditRestored += (waitTime) =>
{
    Console.WriteLine($"Credits restored after {waitTime.TotalMilliseconds}ms");
};
```

### Multiplexer Stats

```csharp
var stats = mux.Stats;

Console.WriteLine($"Total starvation events: {stats.TotalCreditStarvationEvents}");
Console.WriteLine($"Channels waiting: {stats.ChannelsCurrentlyWaitingForCredits}");
Console.WriteLine($"Total wait time: {stats.TotalCreditWaitTime}");
Console.WriteLine($"System under pressure: {stats.IsExperiencingBackpressure}");
```

## Handling Backpressure

### Timeout Approach

```csharp
var options = new ChannelOptions
{
    ChannelId = "time-sensitive",
    SendTimeout = TimeSpan.FromSeconds(5)  // Short timeout
};

var channel = await mux.OpenChannelAsync(options);

try
{
    await channel.WriteAsync(data);
}
catch (TimeoutException)
{
    // Receiver too slow - drop or queue
    Console.WriteLine("Send timeout - receiver cannot keep up");
}
```

### Non-Blocking Check

```csharp
if (channel.Stats.IsWaitingForCredits)
{
    // Already backpressured - buffer locally or drop
}
else
{
    await channel.WriteAsync(data);
}
```

### Adaptive Producer

```csharp
// Slow down production based on backpressure
var baseDelay = TimeSpan.FromMilliseconds(10);

while (producing)
{
    await channel.WriteAsync(data);
    
    // If experiencing backpressure, slow down
    if (channel.Stats.CreditStarvationCount > lastCount)
    {
        baseDelay *= 2;  // Back off
        lastCount = channel.Stats.CreditStarvationCount;
    }
    else if (baseDelay > TimeSpan.FromMilliseconds(10))
    {
        baseDelay /= 2;  // Speed up
    }
    
    await Task.Delay(baseDelay);
}
```

## Common Scenarios

### Fast Producer, Slow Consumer

```csharp
// Producer generates data faster than consumer can process
// Backpressure naturally throttles producer

var options = new ChannelOptions
{
    ChannelId = "bulk-data",
    MaxCredits = 16 * 1024 * 1024,  // 16MB buffer
    SendTimeout = TimeSpan.FromMinutes(5)  // Long timeout
};
```

### Interactive Low-Latency

```csharp
// Small, frequent messages - keep buffer small

var options = new ChannelOptions
{
    ChannelId = "interactive",
    MinCredits = 4 * 1024,   // 4KB min
    MaxCredits = 64 * 1024,  // 64KB max
    SendTimeout = TimeSpan.FromSeconds(1)  // Short timeout
};
```

### Bulk Transfer

```csharp
// Large file transfer - maximize throughput

var options = new ChannelOptions
{
    ChannelId = "file-transfer",
    MinCredits = 1 * 1024 * 1024,   // 1MB min
    MaxCredits = 32 * 1024 * 1024,  // 32MB max
    SendTimeout = TimeSpan.FromMinutes(10)
};
```

## Tips

**Monitor before tuning:**
```csharp
// Log backpressure events to understand patterns
channel.OnCreditStarvation += () => 
    logger.Info($"Backpressure on {channel.ChannelId}");
```

**Match buffer to message size:**
```csharp
// For 64KB messages, use at least 256KB buffer
var options = new ChannelOptions
{
    MaxCredits = 4 * messageSize
};
```

**Test with slow consumers:**
```csharp
// Simulate slow receiver
await foreach (var channel in mux.AcceptChannelsAsync())
{
    await Task.Delay(100);  // Artificial slowdown
    var n = await channel.ReadAsync(buffer);
}
```
