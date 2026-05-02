# Statistics

Runtime metrics exposed by the multiplexer and channels. Use with [Events](../concepts/events.md) for monitoring.

## Multiplexer Statistics

Access via `mux.Stats` (type: `MultiplexerStats`):

```csharp
var stats = mux.Stats;

Console.WriteLine($"Sent: {stats.BytesSent}");
Console.WriteLine($"Received: {stats.BytesReceived}");
Console.WriteLine($"Active channels: {stats.OpenChannels}");
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `BytesSent` | `long` | Total bytes sent across all channels |
| `BytesReceived` | `long` | Total bytes received across all channels |
| `OpenChannels` | `int` | Currently open channels |
| `TotalChannelsOpened` | `int` | Lifetime channels opened |
| `TotalChannelsClosed` | `int` | Lifetime channels closed |
| `Uptime` | `TimeSpan` | How long the multiplexer has been running |
| `LastPingRtt` | `TimeSpan` | Round-trip time of the last successful ping |
| `MissedPings` | `int` | Consecutive missed pings |
| `TotalCreditStarvationEvents` | `long` | Total backpressure events across all channels |
| `ChannelsCurrentlyWaitingForCredits` | `int` | Channels currently experiencing backpressure |
| `TotalCreditWaitTime` | `TimeSpan` | Total time all channels spent waiting for credits |
| `IsExperiencingBackpressure` | `bool` | Whether any channel is currently waiting for credits |

## Channel Statistics

Access via `channel.Stats` (type: `ChannelStats`):

```csharp
var stats = channel.Stats;

Console.WriteLine($"Sent: {stats.BytesSent}");
Console.WriteLine($"Frames sent: {stats.FramesSent}");
Console.WriteLine($"Starvation events: {stats.CreditStarvationCount}");
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `BytesSent` | `long` | Total bytes sent on this channel |
| `BytesReceived` | `long` | Total bytes received on this channel |
| `FramesSent` | `long` | Total frames sent |
| `FramesReceived` | `long` | Total frames received |
| `CreditsGranted` | `long` | Total credits granted |
| `CreditsConsumed` | `long` | Total credits consumed |
| `OpenDuration` | `TimeSpan` | How long the channel has been open |
| `CreditStarvationCount` | `long` | Number of backpressure events |
| `TotalWaitTimeForCredits` | `TimeSpan` | Total time spent waiting for credits |
| `LongestWaitForCredits` | `TimeSpan` | Longest single credit wait |
| `IsWaitingForCredits` | `bool` | Whether currently waiting for credits |
| `CurrentWaitDuration` | `TimeSpan` | How long the current wait has lasted (zero if not waiting) |

## Example: Monitoring Dashboard

```csharp
async Task MonitorLoop(IStreamMultiplexer mux, CancellationToken ct)
{
    while (mux.IsRunning && !ct.IsCancellationRequested)
    {
        var stats = mux.Stats;

        Console.Clear();
        Console.WriteLine($"=== NetConduit Stats ===");
        Console.WriteLine($"Connected: {mux.IsConnected}");
        Console.WriteLine($"RTT: {stats.LastPingRtt.TotalMilliseconds:F1}ms");
        Console.WriteLine($"Channels: {stats.OpenChannels}");
        Console.WriteLine($"Sent: {FormatBytes(stats.BytesSent)}");
        Console.WriteLine($"Received: {FormatBytes(stats.BytesReceived)}");
        Console.WriteLine($"Backpressure: {stats.IsExperiencingBackpressure}");

        await Task.Delay(1000, ct);
    }
}

string FormatBytes(long bytes)
{
    string[] sizes = ["B", "KB", "MB", "GB"];
    int order = 0;
    double size = bytes;
    while (size >= 1024 && order < sizes.Length - 1)
    {
        order++;
        size /= 1024;
    }
    return $"{size:F1} {sizes[order]}";
}
```

## Example: Performance Logging

```csharp
Timer? _statsTimer;

void StartStatsLogging(IStreamMultiplexer mux)
{
    long lastBytesSent = 0;
    long lastBytesReceived = 0;

    _statsTimer = new Timer(_ =>
    {
        var stats = mux.Stats;

        var sentRate = stats.BytesSent - lastBytesSent;
        var recvRate = stats.BytesReceived - lastBytesReceived;

        _logger.LogInformation(
            "Throughput: TX={TxRate}/s RX={RxRate}/s Channels={Channels}",
            FormatBytes(sentRate),
            FormatBytes(recvRate),
            stats.OpenChannels);

        lastBytesSent = stats.BytesSent;
        lastBytesReceived = stats.BytesReceived;

    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
}
```

## Example: Health Check

```csharp
bool IsHealthy(IStreamMultiplexer mux)
{
    // Check connection
    if (!mux.IsConnected)
        return false;

    var stats = mux.Stats;

    // Check for excessive missed pings
    if (stats.MissedPings > 2)
        return false;

    // Check RTT isn't too high
    if (stats.LastPingRtt > TimeSpan.FromSeconds(5))
        return false;

    return true;
}
```
