# Statistics

NetConduit exposes lightweight counters on every multiplexer and channel. Reads are volatile (no locking required) and cheap enough to poll on a UI timer.

## `MultiplexerStats`

Namespace: `NetConduit.Models`.

```csharp
public sealed class MultiplexerStats
{
    public long     BytesSent            { get; }
    public long     BytesReceived        { get; }
    public int      OpenChannels         { get; }
    public int      TotalChannelsOpened  { get; }
    public int      TotalChannelsClosed  { get; }
    public TimeSpan Uptime               { get; }
}
```

Available via `IStreamMultiplexer.Stats`.

| Counter | Meaning |
| --- | --- |
| `BytesSent` | Total **wire** bytes written by the writer loop, **including** 8-byte frame headers and control-frame traffic (Ping/Pong, GoAway, INIT, FIN). Use this to measure transport throughput. |
| `BytesReceived` | Total **wire** bytes read from the transport, including frame headers and control-frame traffic. |
| `OpenChannels` | Currently open channels (excluding `Opening`/`Closing`). |
| `TotalChannelsOpened` | Count of channels opened during this session. |
| `TotalChannelsClosed` | Count of channels closed during this session. |
| `Uptime` | Elapsed wall time since `Start()` was called. `TimeSpan.Zero` before start. The clock keeps advancing across reconnects (it is not paused while disconnected). |

> **Wire bytes ≠ sum of channel bytes.** `MultiplexerStats.BytesSent` is **not** the sum of every channel's `ChannelStats.BytesSent`. The mux counter includes per-frame headers and control frames that have no associated channel. Expect `mux.Stats.BytesSent > sum(channel.Stats.BytesSent)` under any non-trivial workload. If you need payload-only aggregate throughput, sum the channel counters yourself.

## `ChannelStats`

Namespace: `NetConduit.Models`.

```csharp
public sealed class ChannelStats
{
    public long BytesSent       { get; }
    public long BytesReceived   { get; }
    public long FramesSent      { get; }
    public long FramesReceived  { get; }
}
```

Available via `IWriteChannel.Stats` and `IReadChannel.Stats`.

| Counter | Meaning |
| --- | --- |
| `BytesSent` / `BytesReceived` | **Payload** bytes for this channel only — frame headers and per-frame overhead are **not** included. |
| `FramesSent` / `FramesReceived` | Number of multiplexer frames; payloads may span many frames. |

## Example — periodic status

```csharp
var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
while (await timer.WaitForNextTickAsync())
{
    var s = mux.Stats;
    Console.WriteLine(
        $"up={s.Uptime} channels={s.OpenChannels} sent={s.BytesSent:N0}B recv={s.BytesReceived:N0}B");
}
```
