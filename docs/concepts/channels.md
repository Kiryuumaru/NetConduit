# Channels

A **channel** is a virtual byte pipe routed over the multiplexer's single transport stream. Each channel has a string ID, a priority, its own buffer, and an independent lifecycle.

Channels are unidirectional:

- `IWriteChannel` — local writes only.
- `IReadChannel` — local reads only.

A "duplex" conversation uses two channels, one each direction. The `DuplexStreamTransit` automates this; see [DuplexStream transit](../transits/duplex-stream.md).

## Open and accept

Both sides know the channel ID in advance. One side opens, the other accepts:

```csharp
// Side A
IWriteChannel writer = mux.OpenChannel("control");
```

```csharp
// Side B
IReadChannel reader = mux.AcceptChannel("control");
```

Order does not matter. `AcceptChannel` returns a pending channel immediately; when the remote `OpenChannel` for the same ID arrives, the pending channel is wired up and becomes `Open`.

Either side may be the opener. There is no client/server asymmetry at the channel level — the asymmetry, if any, lives in your transport (server accepts connections; client connects).

## State machine

```
                 OpenChannel / AcceptChannel
                              |
                              v
                       +-------------+
                       |   Opening   |   (INIT sent, awaiting remote ACK)
                       +-------------+
                              |
                  remote ACK  |
                              v
                       +-------------+
                       |    Open     |   (IsReady = true; data flows)
                       +-------------+
                              |
                 CloseAsync   |   remote FIN / RemoteError / TransportFailed / MuxDisposed
                              v
                       +-------------+
                       |   Closing   |   (FIN sent, draining)
                       +-------------+
                              |
                              v
                       +-------------+
                       |   Closed    |   (no more reads or writes)
                       +-------------+
```

`State` reflects the current node. `IsReady` flips to `true` on `Opening → Open` and stays `true` for the lifetime of the channel.

## Channel IDs

The ID is any non-empty UTF-8 string (up to 1024 bytes encoded). Pick stable, descriptive names: `"chat"`, `"control"`, `"file/42"`. They are not interpreted by NetConduit.

Two reserved suffixes are used by duplex transits — see [DuplexStream transit](../transits/duplex-stream.md):

| Suffix | Meaning |
| --- | --- |
| `>>` | The "outbound" half of a duplex pair |
| `<<` | The "inbound" half of a duplex pair |

If you use duplex transits, do not include `>>` or `<<` in your base IDs.

## Per-channel options

```csharp
var ch = mux.OpenChannel(new ChannelOptions
{
    ChannelId   = "uploads",
    Priority    = ChannelPriority.Low,
    SlabSize    = 4 * 1024 * 1024,   // 4 MiB buffer
    SendTimeout = TimeSpan.FromSeconds(60),
});
```

| Option | Default | Effect |
| --- | --- | --- |
| `ChannelId` | required | The string ID. |
| `Priority` | `Normal` (128) | Higher priorities ship before lower when both are ready to send. See [Priority](priority.md). |
| `SlabSize` | 1 MiB | Per-channel ring buffer in bytes. Larger slab = more in-flight data. See [Backpressure](backpressure.md). |
| `SendTimeout` | 30 s | How long `WriteAsync` will wait for slab space before throwing. |

Defaults come from `MultiplexerOptions.DefaultChannelOptions`. If you pass a single `string` (e.g. `mux.OpenChannel("foo")`) the defaults are used for the rest.

## Writing and reading

```csharp
// Writer
await ch.WriteAsync(buffer, ct);     // Memory<byte>; framed and queued
await ch.CloseAsync(ct);             // FIN; flushes pending data first

// Reader
var buf = new byte[8192];
int n = await ch.ReadAsync(buf, ct); // 0 = EOF (channel closed remotely)
```

A read of 0 bytes means the channel is closed (remote FIN, remote error, or transport drop). Inspect `CloseReason` and `CloseException` for details.

## Channel as `Stream`

```csharp
Stream s = ch.AsStream();
```

`AsStream()` returns a thin wrapper that exposes the channel through the `System.IO.Stream` API. The wrapper is read-only on `IReadChannel` and write-only on `IWriteChannel`. For a bidirectional `Stream`, use [DuplexStream transit](../transits/duplex-stream.md).

## Closing a channel

| Action | Effect |
| --- | --- |
| `await ch.CloseAsync()` | Flushes pending writes, sends FIN, transitions to `Closing` then `Closed`. |
| `ch.Dispose()` / `await ch.DisposeAsync()` | Equivalent to close. Safe to call multiple times. |

After close, `CloseReason` is set:

| `ChannelCloseReason` | When |
| --- | --- |
| `LocalClose` | You called `CloseAsync` / `Dispose`. |
| `RemoteFin` | Peer closed cleanly. |
| `RemoteError` | Peer sent an ERR frame. `CloseException` describes it. |
| `TransportFailed` | Underlying transport dropped and could not be recovered. |
| `MuxDisposed` | The multiplexer was disposed. |

## Channel events

| Event | When |
| --- | --- |
| `Ready` | The channel first becomes `Open`. Fires once. |
| `Connected` | The transport is up and this channel is active (initial + every reconnect, when replay is enabled). |
| `Disconnected` | The transport dropped. |
| `Closed` | The channel transitioned to `Closed`. |
