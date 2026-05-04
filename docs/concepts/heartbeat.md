# Heartbeat

Keep-alive ping/pong mechanism to detect dead connections. See [Concepts Overview](index.md) for related concepts.

## How It Works

The multiplexer periodically sends Ping frames. If the remote side doesn't respond with Pong within the timeout, the connection is considered dead:

```
Local                           Remote
  │                                │
  │──── Ping ─────────────────────▶│
  │◀─── Pong ─────────────────────│
  │                                │
  │   [PingInterval passes]        │
  │                                │
  │──── Ping ─────────────────────▶│
  │                                │
  │   [PingTimeout passes, no Pong]│
  │                                │
  │   Connection declared dead     │
```

## Configuration

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,
    PingInterval = TimeSpan.FromSeconds(30),  // Time between pings (default: 30s)
    PingTimeout = TimeSpan.FromSeconds(10),   // Time to wait for pong (default: 10s)
    MaxMissedPings = 3                        // Missed pings before disconnect (default: 3)
};
```

## Default Behavior

With defaults:
- Ping sent every 30 seconds
- If pong not received within 10 seconds, it counts as a miss
- After 3 consecutive misses, the connection is declared dead
- Total detection time: ~120 seconds worst case (30s interval × 3 misses + 10s timeout)

## Disabling Heartbeat

Set `PingInterval` to `Timeout.InfiniteTimeSpan`:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,
    PingInterval = Timeout.InfiniteTimeSpan
};
```

## Detection vs Reconnection

Heartbeat only **detects** dead connections. What happens next depends on reconnection configuration:

- If `MaxAutoReconnectAttempts > 0` → triggers reconnection
- If no reconnection configured → fires `OnDisconnected` and stops
