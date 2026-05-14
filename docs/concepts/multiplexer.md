# Multiplexer

`StreamMultiplexer` is the central object. It owns one transport stream and exposes the channel API on top of it.

## Create and start

```csharp
var options = new MultiplexerOptions { StreamFactory = MyFactory };
await using var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();
```

- `Create(options)` — constructs the multiplexer. Does not touch the network.
- `Start()` — kicks off the connect loop, handshake, and read/write/keepalive tasks. Returns immediately.
- `WaitForReadyAsync(ct)` — completes when the first handshake succeeds.

You can open or accept channels before `WaitForReadyAsync` returns. They sit in `Opening` state until the handshake completes and the remote ACKs each channel.

## Ready vs. connected

The multiplexer has two booleans:

- `IsReady` — `true` after the first handshake succeeds. Stays `true` forever, even across reconnects.
- `IsConnected` — `true` while the underlying transport is up. Flips to `false` on disconnect, back to `true` on reconnect.

`Ready` (the event) fires exactly once. `Connected` fires on every successful (re)connect; `Disconnected` fires on every drop.

## Sessions

Each multiplexer has a `SessionId` (`Guid`) and learns its peer's `RemoteSessionId` during the handshake. `SessionId` survives reconnection (so the peer can recognize you on resume). You can pin it via `MultiplexerOptions.SessionId`; otherwise one is generated.

## Opening and accepting channels

```csharp
IWriteChannel out = mux.OpenChannel("requests");
IReadChannel  in  = mux.AcceptChannel("requests");
```

Both return immediately in `Opening` state. They become `Open` once the peer's matching call completes the rendezvous. Use `await channel.WaitForReadyAsync()` to wait, or use the async shortcuts:

```csharp
await using var out = await mux.OpenChannelAsync("requests");
await using var in  = await mux.AcceptChannelAsync("requests");
```

`AcceptChannelsAsync()` returns an `IAsyncEnumerable<IReadChannel>` that yields every inbound channel as the remote opens them:

```csharp
await foreach (var ch in mux.AcceptChannelsAsync(ct))
    _ = HandleAsync(ch);
```

## Shutdown

Two ways to stop:

| Call | Effect |
| --- | --- |
| `await mux.GoAwayAsync(ct)` | Sends a GoAway frame, waits up to `GoAwayTimeout` for in-flight channels to drain, then triggers disconnect. |
| `await mux.DisposeAsync()` | Immediate. Cancels all loops, aborts channels with `ChannelCloseReason.MuxDisposed`, disposes the transport. |

A typical clean shutdown does both:

```csharp
await mux.GoAwayAsync(ct);
// mux is now disposed via `await using`
```

## Properties at a glance

| Property | Meaning |
| --- | --- |
| `Options` | The `MultiplexerOptions` you supplied. |
| `Stats` | A live `MultiplexerStats` snapshot. |
| `IsReady` | First handshake completed. |
| `IsConnected` | Transport up right now. |
| `IsRunning` | `Start()` was called and `DisposeAsync` has not finished. |
| `IsShuttingDown` | A `GoAwayAsync` is in progress. |
| `SessionId` / `RemoteSessionId` | Session identities, valid after handshake. |
| `ActiveChannelIds` / `ActiveChannelCount` | Currently open channels. |
| `DisconnectReason` | Reason for the last disconnect, if any. |

See [API: `StreamMultiplexer`](../api/stream-multiplexer.md) for the complete surface.
