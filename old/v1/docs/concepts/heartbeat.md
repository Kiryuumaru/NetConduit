# Heartbeat (Ping/Pong)

The multiplexer sends periodic ping frames to detect dead connections and measure round-trip time.

## How It Works

```
Local                           Remote
  │                               │
  │──── Ping (timestamp) ────────>│
  │                               │
  │<─── Pong (timestamp) ────────│
  │                               │
  │   RTT = now - timestamp       │
  │   MissedPings = 0             │
```

Every `PingInterval`, the multiplexer sends a ping frame. If no pong is received within `PingTimeout`, it counts as a missed ping. After `MaxMissedPings` consecutive misses, the multiplexer initiates a GoAway and disconnects.

## Configuration

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    PingInterval = TimeSpan.FromSeconds(30),  // How often to ping (default: 30s)
    PingTimeout = TimeSpan.FromSeconds(10),   // How long to wait for pong (default: 10s)
    MaxMissedPings = 3                        // Consecutive misses before disconnect (default: 3)
};
```

| Option | Default | Description |
|--------|---------|-------------|
| `PingInterval` | 30 seconds | Time between ping frames |
| `PingTimeout` | 10 seconds | Maximum wait for a pong reply |
| `MaxMissedPings` | 3 | Consecutive missed pings before GoAway + disconnect |

With defaults, a dead connection is detected in at most **30 + 10 = 40 seconds** for the first miss, and full disconnect after **3 × 40 = 120 seconds** worst case.

## Round-Trip Time

Each successful pong updates the `LastPingRtt` statistic:

```csharp
Console.WriteLine($"RTT: {mux.Stats.LastPingRtt.TotalMilliseconds}ms");
Console.WriteLine($"Missed pings: {mux.Stats.MissedPings}");
```

RTT is measured as the time between sending the ping and receiving the matching pong.

## Missed Ping Behavior

When pings are missed:

1. **First missed ping:** `MissedPings` increments to 1
2. **Subsequent misses:** `MissedPings` keeps incrementing
3. **Pong received:** `MissedPings` resets to 0 and RTT is updated
4. **MaxMissedPings reached:** Multiplexer sends GoAway and disconnects

If auto-reconnection is configured, the multiplexer will attempt to reconnect after disconnection.

## Tuning Examples

### Low-Latency (detect failures fast)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 9000) with
{
    PingInterval = TimeSpan.FromSeconds(5),
    PingTimeout = TimeSpan.FromSeconds(3),
    MaxMissedPings = 2
};
// Detects dead connection in ~16 seconds
```

### Mobile/Unreliable Networks (tolerate lag)

```csharp
var options = TcpMultiplexer.CreateOptions("server.example.com", 9000) with
{
    PingInterval = TimeSpan.FromSeconds(60),
    PingTimeout = TimeSpan.FromSeconds(30),
    MaxMissedPings = 5
};
// Tolerates up to ~7.5 minutes of missed pings
```

### Localhost (minimal overhead)

```csharp
var options = IpcMultiplexer.CreateOptions("my-service") with
{
    PingInterval = TimeSpan.FromMinutes(5),
    PingTimeout = TimeSpan.FromSeconds(5),
    MaxMissedPings = 2
};
```

## Monitoring

Use [Statistics](../api/statistics.md) to monitor heartbeat health:

```csharp
var stats = mux.Stats;

// Connection health check
bool isHealthy = stats.MissedPings == 0;
bool isDegraded = stats.MissedPings > 0 && stats.MissedPings < mux.Options.MaxMissedPings;
bool isUnresponsive = stats.MissedPings >= mux.Options.MaxMissedPings;

Console.WriteLine($"RTT: {stats.LastPingRtt.TotalMilliseconds:F1}ms");
Console.WriteLine($"Missed: {stats.MissedPings}/{mux.Options.MaxMissedPings}");
```

## See Also

- [MultiplexerOptions](../api/multiplexer-options.md) — Ping configuration
- [Statistics](../api/statistics.md) — RTT and missed ping metrics
- [Graceful Shutdown](graceful-shutdown.md) — What happens when max pings are missed
- [Reconnection](reconnection.md) — Recovery after heartbeat-triggered disconnect
