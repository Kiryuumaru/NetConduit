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
| `BytesSent` | Total payload bytes written into channels (does **not** include framing overhead). |
| `BytesReceived` | Total payload bytes delivered out of channels. |
| `OpenChannels` | Currently open channels (excluding `Opening`/`Closing`). |
| `TotalChannelsOpened` | Count of channels opened during this session. |
| `TotalChannelsClosed` | Count of channels closed during this session. |
| `Uptime` | Elapsed wall time since `Start()` was called. `TimeSpan.Zero` before start. |

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
| `BytesSent` / `BytesReceived` | Payload bytes for this channel only. |
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
