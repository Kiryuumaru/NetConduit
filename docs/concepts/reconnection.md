# Reconnection

If the transport drops, the multiplexer can rebuild the connection and resume traffic on existing channels. Whether and how this happens depends on three options.

## Behavior summary

| `MaxAutoReconnectAttempts` | Behavior |
| --- | --- |
| `-1` (default) | Unlimited reconnect attempts. **Replay buffer enabled**: open channels survive disconnects; sent bytes are replayed after the reconnect handshake and the reader skips duplicates. |
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

When `MaxAutoReconnectAttempts != 0`, each open write channel keeps its entire slab of sent data. On reconnect:

1. Both peers exchange `Ctrl/Reconnect` frames carrying their session IDs.
2. If the session IDs match, each side replays all data from its write channels.
3. The receiving side skips any bytes it already received before the disconnect—your `ReadAsync` sees only new data that wasn't delivered before the break.

The skip is transparent: your `ReadAsync` and `WriteAsync` calls just continue as if nothing happened. If data was partially delivered before the disconnect, only the undelivered portion appears after reconnect.

Channels that were not yet `Open` at the moment of disconnect are reopened on the new connection.

## When reconnect won't succeed

`Reconnecting` keeps firing until either:

- A reconnect succeeds → state is restored.
- `MaxAutoReconnectAttempts` is exceeded → the multiplexer gives up and disposes the channels with `ChannelCloseReason.TransportFailed`.
- `SessionMismatch` is returned by the peer (a different multiplexer is now answering) → reconnect fails, channels fail.

## Server-side reconnection

The provided `*Multiplexer.CreateServerOptions(...)` factories accept **one** connection and refuse subsequent calls. For a server that survives client churn, build your own factory that re-accepts from the listener. The pattern is the same for every listener-style transport: keep the listener alive outside the factory, accept one connection per `StreamFactory` invocation, and wrap the resulting transport object as a `StreamPair`.

> Reconnect semantics are inherently per-peer. A re-accepting server factory replaces the previous client with the next one to connect, then drives the multiplexer's reconnect handshake (replay buffers, channel adoption). If the next caller is a *different* peer, the handshake returns `SessionMismatch` and reconnect fails — see [When reconnect won't succeed](#when-reconnect-wont-succeed). The factory itself does not enforce peer identity; application-level authentication is up to you.

### TCP

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

### QUIC

`QuicListener` plays the same role as `TcpListener`. Build the listener with `QuicMultiplexer.ListenAsync`, then re-accept on each factory call:

```csharp
var cert = new X509Certificate2("server.pfx", "password");
var listener = await QuicMultiplexer.ListenAsync(
    new IPEndPoint(IPAddress.Any, 5000),
    cert,
    alpn: "myapp");

var opts = new MultiplexerOptions
{
    StreamFactory = async ct =>
    {
        var connection = await listener.AcceptConnectionAsync(ct);
        var stream = await connection.AcceptInboundStreamAsync(ct);

        // Read the 0x01 handshake marker that the client writes as its first byte.
        var preface = new byte[1];
        _ = await stream.ReadAsync(preface, ct);

        return new StreamPair(stream, stream, owner: connection);
    },
    MaxAutoReconnectAttempts = int.MaxValue,
};
```

The `0x01` handshake marker matches what `QuicMultiplexer.CreateOptions` writes on the client side; keep them in sync.

### WebSocket (`HttpListener`)

`HttpListener` exposes a request stream; the WebSocket itself is produced by `AcceptWebSocketAsync`. Re-accepting means waiting for the next HTTP request and upgrading it on each factory call:

```csharp
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/chat/");
listener.Start();

var opts = new MultiplexerOptions
{
    StreamFactory = async ct =>
    {
        // GetContextAsync does not take a CancellationToken; if you need cancellation,
        // wrap the call or close the listener from another task to unblock it.
        var ctx = await listener.GetContextAsync().WaitAsync(ct);
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            throw new InvalidOperationException("Non-WebSocket request rejected.");
        }

        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);

        // Delegate to the single-shot helper to wrap the accepted WebSocket
        // as an IStreamPair. A new options instance per call gives a fresh
        // single-use factory.
        var innerFactory = WebSocketMultiplexer
            .CreateServerOptions(wsCtx.WebSocket)
            .StreamFactory!;
        return await innerFactory(ct);
    },
    MaxAutoReconnectAttempts = int.MaxValue,
};
```

For ASP.NET Core hosts the accept loop runs inside an endpoint handler, so each request is naturally a new `StreamMultiplexer` instance — the reconnect-factory pattern does not apply. Spawn a fresh multiplexer per WebSocket upgrade instead.

### UDP

`UdpMultiplexer.CreateServerOptions(...)` binds a single UDP socket to one remote peer via the `NC_HELLO` / `NC_HELLO_ACK` handshake. The reliable shim retransmits within a single peer session; surviving a peer change requires tearing down and re-binding the socket, which the provided helper does not expose. If you need server-side reconnect for UDP, dispose the multiplexer on `Disconnected` and create a new one rather than driving it from a custom `StreamFactory`.

### IPC

`IpcMultiplexer.CreateServerOptions(...)` chooses between TCP loopback (Windows) and a Unix domain socket (Linux/macOS) internally, and binds a fresh listener per call. The platform-specific bind/cleanup logic is not exposed publicly, so a hand-written reconnectable IPC factory would have to re-implement it per platform. The simpler pattern is the same as UDP: dispose and recreate the multiplexer on disconnect.
