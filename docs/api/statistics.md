# Statistics

Runtime statistics for multiplexers and channels.

## MultiplexerStats

Access via `mux.Stats`:

```csharp
var stats = mux.Stats;
Console.WriteLine($"Bytes sent: {stats.BytesSent}");
Console.WriteLine($"Bytes received: {stats.BytesReceived}");
Console.WriteLine($"Open channels: {stats.OpenChannels}");
Console.WriteLine($"Total opened: {stats.TotalChannelsOpened}");
Console.WriteLine($"Total closed: {stats.TotalChannelsClosed}");
Console.WriteLine($"Uptime: {stats.Uptime}");
```

| Property | Type | Description |
|----------|------|-------------|
| `BytesSent` | `long` | Total bytes sent across all channels |
| `BytesReceived` | `long` | Total bytes received across all channels |
| `OpenChannels` | `int` | Currently open channel count |
| `TotalChannelsOpened` | `int` | Total channels opened since start |
| `TotalChannelsClosed` | `int` | Total channels closed since start |
| `Uptime` | `TimeSpan` | Time since multiplexer started |

## ChannelStats

Access via `channel.Stats`:

```csharp
var stats = channel.Stats;
Console.WriteLine($"Sent: {stats.BytesSent} bytes ({stats.FramesSent} frames)");
Console.WriteLine($"Received: {stats.BytesReceived} bytes ({stats.FramesReceived} frames)");
```

| Property | Type | Description |
|----------|------|-------------|
| `BytesSent` | `long` | Bytes sent on this channel |
| `BytesReceived` | `long` | Bytes received on this channel |
| `FramesSent` | `long` | Frames sent on this channel |
| `FramesReceived` | `long` | Frames received on this channel |
