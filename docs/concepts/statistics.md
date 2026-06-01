# Statistics

Two stats objects expose live counters. Both are thread-safe to read at any time.

## `MultiplexerStats`

Accessed via `IStreamMultiplexer.Stats`:

| Property | Type | Meaning |
| --- | --- | --- |
| `BytesSent` | `long` | Total wire bytes written by the multiplexer's writer loop, including 12-byte frame headers and control-frame traffic (Ping/Pong, GoAway, INIT, FIN, Ack, Err). |
| `BytesReceived` | `long` | Total wire bytes read by the multiplexer's reader loop, including 12-byte frame headers and control-frame traffic. |
| `OpenChannels` | `int` | Channels currently open. |
| `TotalChannelsOpened` | `int` | Channels opened since `Start()`. |
| `TotalChannelsClosed` | `int` | Channels closed since `Start()`. |
| `Uptime` | `TimeSpan` | Time since `Start()` was called. |

```csharp
var s = mux.Stats;
Console.WriteLine($"{s.BytesSent} bytes out, {s.BytesReceived} bytes in, {s.OpenChannels} channels, up {s.Uptime}");
```

## `ChannelStats`

Accessed via `IWriteChannel.Stats` or `IReadChannel.Stats`:

| Property | Type | Meaning |
| --- | --- | --- |
| `BytesSent` | `long` | Total bytes sent on this channel. |
| `BytesReceived` | `long` | Total bytes received on this channel. |
| `FramesSent` | `long` | Total `Data` frames sent on this channel. |
| `FramesReceived` | `long` | Total `Data` frames received on this channel. |

Each counter is read with a `Volatile.Read` so values are eventually consistent across cores.

## Snapshot

`Stats` returns a live view, not a snapshot — sequential reads on the same property may differ. For a coherent point-in-time view, read all properties into local variables once:

```csharp
var s = mux.Stats;
var snap = (s.BytesSent, s.BytesReceived, s.OpenChannels, s.Uptime);
```
