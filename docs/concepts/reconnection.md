# Reconnection

If the transport drops, the multiplexer can rebuild the connection and resume traffic on existing channels. Whether and how this happens depends on three options.

## Behavior summary

| `MaxAutoReconnectAttempts` | Behavior |
| --- | --- |
| `-1` (default) | Unlimited reconnect attempts. **Replay buffer enabled**: open channels survive disconnects; unacked bytes are replayed after the reconnect handshake. |
| `0` | No reconnect. The first transport failure raises terminal `Disconnected`. **No replay buffer** is allocated for new channels; open channels are torn down with `ChannelCloseReason.TransportFailed`. |
| `> 0` | Up to N reconnect attempts. **Replay buffer enabled**. After the limit is reached, channels close with `ChannelCloseReason.TransportFailed`. |

Set `MaxAutoReconnectAttempts = 0` to opt out of reconnect/replay (e.g. for short-lived or test multiplexers).

```csharp
new MultiplexerOptions
{
    StreamFactory                  = TcpMultiplexer.CreateOptions("relay.example.com", 5000).StreamFactory,
    MaxAutoReconnectAttempts       = 5,
    AutoReconnectDelay             = TimeSpan.FromSeconds(2),
    MaxAutoReconnectDelay          = TimeSpan.FromSeconds(30),
    AutoReconnectBackoffMultiplier = 2.0,
}
```

## Backoff

Reconnect delay starts at `AutoReconnectDelay` and is multiplied by `AutoReconnectBackoffMultiplier` after every failed attempt, capped at `MaxAutoReconnectDelay`.

With the defaults (1 s → ×2 → cap 30 s), the sequence is **1, 2, 4, 8, 16, 30, 30, …** seconds.

| Option | Default |
| --- | --- |
| `AutoReconnectDelay` | 1 s |
| `MaxAutoReconnectDelay` | 30 s |
| `AutoReconnectBackoffMultiplier` | 2.0 |

`ConnectionTimeout` (default 30 s) caps a single connect attempt.

## Events during reconnect

```
Disconnected (DisconnectReason.TransportError)
   |
   | wait AutoReconnectDelay
   v
Reconnecting (Attempt = 1)
   |
   | factory call (within ConnectionTimeout)
   v
Connected     (transport up; handshake done)
```

`Reconnecting` fires with a 1-based attempt number. `Connected` (the event) fires on every successful reconnect; `Ready` does **not** fire again (it's once-per-multiplexer).

Per-channel events mirror this: each open channel fires `Disconnected` then `Connected` as it survives the transport drop.

## What "replay" means

When `MaxAutoReconnectAttempts > 0`, each open channel keeps a tail of recently sent bytes in its slab. On reconnect:

1. Both peers exchange `Ctrl/Reconnect` frames carrying their last-acked positions per channel.
2. Each side replays anything the other side hasn't acked.
3. Channels resume from the exact byte where they left off — your `ReadAsync` and `WriteAsync` calls just continue.

Channels that were not yet `Open` at the moment of disconnect are reopened on the new connection.

## When reconnect won't succeed

`Reconnecting` keeps firing until either:

- A reconnect succeeds → state is restored.
- `MaxAutoReconnectAttempts` is exceeded → the multiplexer gives up and disposes the channels with `ChannelCloseReason.TransportFailed`.
- `SessionMismatch` is returned by the peer (a different multiplexer is now answering) → reconnect fails, channels fail.

## Server-side reconnection

The provided `*Multiplexer.CreateServerOptions(...)` factories accept **one** connection and refuse subsequent calls. For a server that survives client churn, build your own factory that re-accepts from the listener:

```csharp
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

var opts = new MultiplexerOptions
{
    StreamFactory = async ct =>
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        client.NoDelay = true;
        return new StreamPair(client.GetStream(), client);
    },
    MaxAutoReconnectAttempts = int.MaxValue,
};
```

The [Scoreboard sample](../../samples/ScoreboardSample/README.md) does this and persists state across player reconnects.
