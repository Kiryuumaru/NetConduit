# Heartbeat

The multiplexer sends keepalive **pings** on a control channel and tears down the connection if pongs stop arriving. This detects half-open TCP, transparent proxies that silently drop idle links, and crashed peers.

## How it works

```
local                                 remote
  |                                     |
  |  ----- Ping (timestamp) ---------->|
  |  <---- Pong (timestamp) -----------|
  |                                     |
  | <wait PingInterval>                 |
  |                                     |
  |  ----- Ping --------------------- >|
  |  <---- Pong ------------------- ---|
  |                                     |
  | <PingTimeout elapses with no Pong>  |
  |                                     |
  | [missed += 1; resend Ping]          |
  |                                     |
  | [missed == MaxMissedPings ->        |
  |  disconnect, fire Disconnected]     |
```

A `Pong` resets the missed-ping counter.

## Options

All three live on `MultiplexerOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `PingInterval` | 30 s | Time between successful pings. |
| `PingTimeout` | 10 s | How long to wait for a `Pong` before counting a miss. |
| `MaxMissedPings` | 3 | Consecutive misses that trigger disconnect. |

Effective dead-connection detection latency is roughly `MaxMissedPings * PingTimeout` (≈30 s with defaults).

## Tuning

- **Faster failure detection** — lower `PingTimeout` and/or `MaxMissedPings`. Be mindful of jittery networks; very low values can produce false positives.
- **Less wire chatter** — raise `PingInterval`.
- **No heartbeat at all** — set `PingInterval = TimeSpan.Zero` (or any non-positive value). The keepalive loop is skipped entirely. Use only if your transport already has its own keepalive (some QUIC stacks, IPC).

## What happens after a missed-ping disconnect

The transport disconnect fires `Disconnected` with `DisconnectReason.TransportError`. If reconnection is configured, the multiplexer immediately tries to reconnect. See [Reconnection](reconnection.md).
