# Events

NetConduit raises events for lifecycle transitions on multiplexers and channels. All `EventArgs` types live in `NetConduit.Events`.

Events are raised on internal IO threads — **do not block** in handlers. Use them to queue work, not to do work.

## `ChannelEventArgs`

```csharp
public sealed class ChannelEventArgs(string channelId) : EventArgs
{
    public string ChannelId { get; }
}
```

Used by `IStreamMultiplexer.ChannelOpened` and `IStreamMultiplexer.ChannelAccepted`.

## `ChannelClosedEventArgs`

```csharp
public sealed class ChannelClosedEventArgs(string channelId, Exception? exception) : EventArgs
{
    public string     ChannelId { get; }
    public Exception? Exception { get; }
}
```

Used by `IStreamMultiplexer.ChannelClosed`.

> **Partial close stream.** This mux-level event fires **only when the remote peer closes a channel via a FIN frame** (i.e. `ChannelCloseReason.RemoteFin`). It does **not** fire for `LocalClose`, `RemoteError`, `TransportFailed`, or `MuxDisposed`. The `Exception` field is reserved and is currently always `null`. For a complete per-channel close stream with reason information, subscribe to the channel-instance `Closed` event documented below.

## `ChannelCloseEventArgs`

```csharp
public sealed class ChannelCloseEventArgs(ChannelCloseReason reason, Exception? exception) : EventArgs
{
    public ChannelCloseReason Reason    { get; }
    public Exception?         Exception { get; }
}
```

Used by `IWriteChannel.Closed` and `IReadChannel.Closed`.

> **Complete close stream.** Every value of `ChannelCloseReason` raises this. The channel id is recoverable from `sender` (the channel instance). Use this — not `ChannelClosedEventArgs` — when you need full close visibility per channel.

## `DisconnectedEventArgs`

```csharp
public sealed class DisconnectedEventArgs(DisconnectReason reason, Exception? exception) : EventArgs
{
    public DisconnectReason Reason    { get; }
    public Exception?       Exception { get; }
}
```

Used by `IStreamMultiplexer.Disconnected`, `IWriteChannel.Disconnected`, `IReadChannel.Disconnected`, and `ITransit.Disconnected`.

## `ReconnectingEventArgs`

```csharp
public sealed class ReconnectingEventArgs(int attempt) : EventArgs
{
    public int Attempt { get; }    // 1-based
}
```

Used by `IStreamMultiplexer.Reconnecting`.

## `ErrorEventArgs`

```csharp
public sealed class ErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; }
}
```

Used by `IStreamMultiplexer.Error`. Lives in `NetConduit.Events`, so qualify if conflicting with `System.IO.ErrorEventArgs`.

## Subscription rules

- Subscribe **before** calling `Start()` to guarantee no missed events (e.g., a very fast `Ready`).
- `Ready` is sticky: it fires exactly once. Subscribing after it already fired never invokes the handler.
- `Connected` / `Disconnected` fire once per transport transition (including reconnects).
- `ChannelOpened` / `ChannelAccepted` fire when a channel becomes ready, not when `INIT` is sent.
