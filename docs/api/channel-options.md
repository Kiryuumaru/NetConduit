# ChannelOptions

Configuration for opening individual [channels](../concepts/channels.md).

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ChannelId` | `string` | *(required)* | Unique channel identifier (max 1024 UTF-8 bytes) |
| `Priority` | `ChannelPriority` | `Normal` (128) | Frame scheduling priority (0-255 enum) |
| `MinCredits` | `uint` | 64KB | Minimum credits — adaptive windowing floor |
| `MaxCredits` | `uint` | 4MB | Maximum credits — window starts here on open |
| `SendTimeout` | `TimeSpan` | 30s | Timeout for writes waiting for credits |

Adaptive flow control starts each channel at `MaxCredits` and shrinks the window toward `MinCredits` after idle periods. See [Backpressure](../concepts/backpressure.md) for details.

## Priority

Higher values get more bandwidth. See [Priority](../concepts/priority.md) for details.

```csharp
// Video stream - highest priority
var videoChannel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "video",
    Priority = ChannelPriority.Highest
});

// File download - lowest priority
var fileChannel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "download",
    Priority = ChannelPriority.Lowest
});

// Default priority
var regularChannel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "chat",
    Priority = ChannelPriority.Normal
});
```

## Flow Control

Credits control [backpressure](../concepts/backpressure.md). The window starts at `MaxCredits` on channel open and adapts automatically — shrinking after idle periods, growing back under active use. Credits never drop below `MinCredits` or exceed `MaxCredits`.

```csharp
// Small buffer - memory constrained
var options = new ChannelOptions
{
    ChannelId = "telemetry",
    MinCredits = 16 * 1024,          // 16KB floor
    MaxCredits = 64 * 1024           // 64KB ceiling (starts here)
};

// Large buffer - high throughput
var options = new ChannelOptions
{
    ChannelId = "bulk-data",
    MinCredits = 128 * 1024,         // 128KB floor
    MaxCredits = 8 * 1024 * 1024     // 8MB ceiling (starts here)
};
```

## Send Timeout

```csharp
// Short timeout for interactive channels
var options = new ChannelOptions
{
    ChannelId = "interactive",
    SendTimeout = TimeSpan.FromSeconds(5)
};

try
{
    await channel.WriteAsync(data);
}
catch (TimeoutException)
{
    // Write blocked for > 5 seconds waiting for credits
}

// Wait forever (explicit)
var options = new ChannelOptions
{
    ChannelId = "background",
    SendTimeout = Timeout.InfiniteTimeSpan
};
```

## Example Configurations

### Low Latency Interactive

```csharp
new ChannelOptions
{
    ChannelId = "control",
    Priority = ChannelPriority.High,
    MinCredits = 16 * 1024,      // 16KB floor
    MaxCredits = 64 * 1024,      // 64KB ceiling
    SendTimeout = TimeSpan.FromSeconds(5)
}
```

### Bulk Transfer

```csharp
new ChannelOptions
{
    ChannelId = "file-transfer",
    Priority = ChannelPriority.Low,
    MinCredits = 256 * 1024,         // 256KB floor
    MaxCredits = 8 * 1024 * 1024,    // 8MB ceiling
    SendTimeout = TimeSpan.FromMinutes(1)
}
```

### Background Sync

```csharp
new ChannelOptions
{
    ChannelId = "sync",
    Priority = ChannelPriority.Lowest,
    MinCredits = 64 * 1024,          // 64KB floor
    MaxCredits = 128 * 1024,         // 128KB ceiling
    SendTimeout = Timeout.InfiniteTimeSpan
}
```

## Usage

```csharp
// Open channel with options
var channel = await mux.OpenChannelAsync(new ChannelOptions
{
    ChannelId = "data",
    Priority = ChannelPriority.High,
    MaxCredits = 8 * 1024 * 1024
});

// Accept channel by ID
var channel = await mux.AcceptChannelAsync("data");
```
