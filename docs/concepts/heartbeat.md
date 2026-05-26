# Heartbeat

The multiplexer sends keepalive **pings** on a control channel and tears down the connection if pongs stop arriving. This detects half-open TCP, transparent proxies that silently drop idle links, and crashed peers.

## How it works

```
local                                 remote
  |                                     |
  | <wait PingInterval>                 |
  |  ----- Ping (timestamp) ---------->|
  |  <---- Pong (timestamp) -----------|
  |                                     |
  | <wait PingInterval>                 |
  |  ----- Ping --------------------- >|
  |  <---- Pong ------------------- ---|
  |                                     |
  | <wait PingInterval>                 |
  |  ----- Ping --------------------- >|
  | <PingTimeout elapses with no Pong>  |
  | [missed += 1]                       |
  |                                     |
  | <wait PingInterval again>           |
  |  ----- Ping --------------------- >|
  | <PingTimeout elapses with no Pong>  |
  | [missed += 1]                       |
  |                                     |
  | [missed == MaxMissedPings ->        |
  |  disconnect, fire Disconnected]     |
```

The loop awaits `PingInterval` at the top of *every* iteration, including the
ones that follow a missed pong — there is no immediate re-ping on timeout. A
`Pong` resets the missed-ping counter.

## Options

All three live on `MultiplexerOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `PingInterval` | 30 s | Time between successful pings, and also between a missed-ping timeout and the next ping. |
| `PingTimeout` | 10 s | How long to wait for a `Pong` before counting a miss. |
| `MaxMissedPings` | 3 | Consecutive misses that trigger disconnect. |

Worst-case dead-connection detection latency from the last successful pong is
at most `MaxMissedPings * (PingInterval + PingTimeout)` (≈120 s with defaults),
and from the moment the first failed ping is sent it is
`MaxMissedPings * PingTimeout + (MaxMissedPings - 1) * PingInterval` (≈90 s
with defaults). With the defaults `PingInterval` is the dominant term; to get
faster detection, lower `PingInterval` as well as `PingTimeout` and/or
`MaxMissedPings`.

## Tuning

- **Faster failure detection** — lower `PingInterval` *and* `PingTimeout` and/or `MaxMissedPings`. `PingInterval` runs between every ping (including after a miss), so it contributes to detection latency, not just to wire chatter. Be mindful of jittery networks; very low values can produce false positives.
- **Less wire chatter** — raise `PingInterval`. Trades off against detection latency above.
- **No heartbeat at all** — set `PingInterval = TimeSpan.Zero`. The keepalive loop is skipped entirely. Negative values are rejected — pass exactly `TimeSpan.Zero`. Use only if your transport already has its own keepalive (some QUIC stacks, IPC).

## What happens after a missed-ping disconnect

The transport disconnect fires `Disconnected` with `DisconnectReason.TransportError`. If reconnection is configured, the multiplexer immediately tries to reconnect. See [Reconnection](reconnection.md).
