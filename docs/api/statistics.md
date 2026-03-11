# Statistics

Runtime metrics exposed by the multiplexer. Use with [Events](../concepts/events.md) for monitoring.

## Accessing Statistics

```csharp
var stats = mux.Statistics;

Console.WriteLine($"Sent: {stats.BytesSent}");
Console.WriteLine($"Received: {stats.BytesReceived}");
Console.WriteLine($"Active channels: {stats.ActiveChannelCount}");
```

## Properties

### Byte Counters

| Property | Type | Description |
|----------|------|-------------|
| `BytesSent` | `long` | Total bytes sent |
| `BytesReceived` | `long` | Total bytes received |

### Frame Counters

| Property | Type | Description |
|----------|------|-------------|
| `FramesSent` | `long` | Total frames sent |
| `FramesReceived` | `long` | Total frames received |

### Channel Metrics

| Property | Type | Description |
|----------|------|-------------|
| `ActiveChannelCount` | `int` | Currently open channels |
| `TotalChannelsOpened` | `long` | Lifetime channels opened |
| `TotalChannelsClosed` | `long` | Lifetime channels closed |

### Connection Metrics

| Property | Type | Description |
|----------|------|-------------|
| `ConnectionState` | `ConnectionState` | Current state |
| `ConnectedTime` | `TimeSpan` | Duration connected |
| `DisconnectedTime` | `TimeSpan` | Duration disconnected |
| `ReconnectionAttempts` | `int` | Current reconnection attempts |
| `TotalReconnections` | `long` | Lifetime reconnections |

### Heartbeat Metrics

| Property | Type | Description |
|----------|------|-------------|
| `LastPingSent` | `DateTimeOffset` | When last ping sent |
| `LastPongReceived` | `DateTimeOffset` | When last pong received |
| `RoundTripTime` | `TimeSpan` | Latest RTT measurement |
| `AverageRoundTripTime` | `TimeSpan` | Smoothed RTT average |

## ConnectionState Enum

```csharp
public enum ConnectionState
{
    Connecting,     // Establishing connection
    Connected,      // Fully connected
    Disconnected,   // Lost connection
    Reconnecting,   // Attempting reconnection
    Closed          // Permanently closed
}
```

## Example: Monitoring Dashboard

```csharp
async Task MonitorLoop(IStreamMultiplexer mux)
{
    while (!mux.IsClosed)
    {
        var stats = mux.Statistics;
        
        Console.Clear();
        Console.WriteLine($"=== NetConduit Stats ===");
        Console.WriteLine($"State: {stats.ConnectionState}");
        Console.WriteLine($"RTT: {stats.RoundTripTime.TotalMilliseconds:F1}ms");
        Console.WriteLine($"Channels: {stats.ActiveChannelCount}");
        Console.WriteLine($"Sent: {FormatBytes(stats.BytesSent)}");
        Console.WriteLine($"Received: {FormatBytes(stats.BytesReceived)}");
        Console.WriteLine($"Reconnections: {stats.TotalReconnections}");
        
        await Task.Delay(1000);
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
        var stats = mux.Statistics;
        
        var sentRate = stats.BytesSent - lastBytesSent;
        var recvRate = stats.BytesReceived - lastBytesReceived;
        
        _logger.LogInformation(
            "Throughput: TX={TxRate}/s RX={RxRate}/s Channels={Channels}",
            FormatBytes(sentRate),
            FormatBytes(recvRate),
            stats.ActiveChannelCount);
        
        lastBytesSent = stats.BytesSent;
        lastBytesReceived = stats.BytesReceived;
        
    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
}
```

## Example: Health Check

```csharp
bool IsHealthy(IStreamMultiplexer mux)
{
    var stats = mux.Statistics;
    
    // Check connection state
    if (stats.ConnectionState != ConnectionState.Connected)
        return false;
    
    // Check for stale connection (no recent pong)
    var staleness = DateTimeOffset.UtcNow - stats.LastPongReceived;
    if (staleness > TimeSpan.FromMinutes(1))
        return false;
    
    // Check RTT isn't too high
    if (stats.AverageRoundTripTime > TimeSpan.FromSeconds(5))
        return false;
    
    return true;
}
```
