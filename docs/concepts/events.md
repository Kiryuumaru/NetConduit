# Events

Both `IStreamMultiplexer` and `IWriteChannel`/`IReadChannel` raise events for major lifecycle moments. All events are raised on internal threads — keep handlers quick or marshal to your own context.

## Multiplexer events

| Event | When |
| --- | --- |
| `Connected` | Transport up. Fires on initial connect and every successful reconnect. |
| `Ready` | First handshake completed. Fires **exactly once** per multiplexer instance. |
| `ChannelOpened` | A local `OpenChannel` was acknowledged by the remote. Args: `ChannelEventArgs`. |
| `ChannelAccepted` | An inbound `Init` arrived and the local `AcceptChannel` completed. Args: `ChannelEventArgs`. |
| `ChannelClosed` | A channel transitioned to `Closed`. Args: `ChannelClosedEventArgs` (with `ChannelId` and any `Exception`). |
| `Error` | A non-fatal protocol or transport error occurred. Args: `ErrorEventArgs`. |
| `Disconnected` | Transport dropped. Args: `DisconnectedEventArgs` (with `DisconnectReason` and any `Exception`). |
| `Reconnecting` | A reconnect attempt is about to start. Args: `ReconnectingEventArgs.Attempt` (1-based). |

## Channel events

Each `IWriteChannel` and `IReadChannel`:

| Event | When |
| --- | --- |
| `Ready` | The channel first becomes `Open`. Fires **once**. |
| `Connected` | The underlying transport is up and this channel is active (initial + every reconnect if replay is enabled). |
| `Disconnected` | The transport dropped. Args: `DisconnectedEventArgs`. |
| `Closed` | The channel transitioned to `Closed`. Args: `ChannelCloseEventArgs` (with `Reason` and any `Exception`). |

## Typical ordering

Successful initial connect:

```
mux.Connected
  ↓
mux.Ready                       (IsReady becomes true)
  ↓ you call OpenChannel("a")
mux.ChannelOpened("a")
ch.Ready                        (per channel)
ch.Connected                    (per channel)
```

Graceful shutdown:

```
GoAwayAsync()
  ↓ remote acks drain
mux.ChannelClosed(...)         (one per channel)
ch.Closed                      (per channel)
mux.Disconnected(GoAwayReceived)
mux.DisposeAsync() finishes
```

Reconnect (`MaxAutoReconnectAttempts != 0` — the default `-1` enables this):

```
mux.Disconnected(TransportError)
ch.Disconnected                 (per open channel)
mux.Reconnecting(Attempt = 1)
... (factory call, handshake) ...
mux.Connected                   (Ready does NOT fire again)
ch.Connected                    (per channel; replay completes silently)
```

## Subscribing safely

Events fire on internal threads:

```csharp
mux.Disconnected += (_, args) =>
{
    // Quick work only — don't await, don't block.
    _logger.LogWarning("disconnected: {Reason}", args.Reason);
};
```

For richer reactions, post the work to your own dispatcher:

```csharp
mux.ChannelAccepted += (_, args) =>
    _taskScheduler.Post(() => HandleAcceptAsync(args.ChannelId));
```

Unsubscribing is unnecessary if the multiplexer is disposed afterwards — disposal clears all subscriptions.
