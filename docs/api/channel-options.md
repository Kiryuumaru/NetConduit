# ChannelOptions

Configuration for opening individual channels.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Priority` | `byte` | 128 | Frame scheduling priority (0-255) |
| `InitialCredits` | `int` | 64KB | Initial flow control credits |
| `MaxCredits` | `int` | 256KB | Maximum credits to accumulate |
| `CreditReplenishThreshold` | `float` | 0.25 | Replenish when credits drop below % |
| `WriteTimeout` | `TimeSpan?` | null | Timeout for blocked writes |

## Priority

Higher values get more bandwidth:

```csharp
// Video stream - highest priority
var videoChannel = await mux.OpenChannelAsync("video", new ChannelOptions
{
    Priority = 255  // Maximum
});

// File download - lowest priority
var fileChannel = await mux.OpenChannelAsync("download", new ChannelOptions
{
    Priority = 1  // Minimum
});

// Default priority
var regularChannel = await mux.OpenChannelAsync("chat", new ChannelOptions
{
    Priority = 128  // Default
});
```

## Flow Control

Credits control backpressure:

```csharp
// Small buffer - memory constrained
var options = new ChannelOptions
{
    InitialCredits = 16 * 1024,      // 16KB
    MaxCredits = 64 * 1024           // 64KB
};

// Large buffer - high throughput
var options = new ChannelOptions
{
    InitialCredits = 256 * 1024,     // 256KB
    MaxCredits = 1024 * 1024         // 1MB
};

// Replenish threshold
var options = new ChannelOptions
{
    MaxCredits = 256 * 1024,
    CreditReplenishThreshold = 0.5f  // Replenish at 50% consumed
};
```

## Write Timeout

```csharp
// Timeout blocked writes
var options = new ChannelOptions
{
    WriteTimeout = TimeSpan.FromSeconds(10)
};

try
{
    await channel.WriteAsync(data);
}
catch (TimeoutException)
{
    // Write blocked for > 10 seconds
}

// No timeout (default) - wait indefinitely
var options = new ChannelOptions
{
    WriteTimeout = null
};
```

## Example Configurations

### Low Latency Interactive

```csharp
new ChannelOptions
{
    Priority = 200,
    InitialCredits = 32 * 1024,  // Small buffer
    MaxCredits = 64 * 1024,
    WriteTimeout = TimeSpan.FromSeconds(5)
}
```

### Bulk Transfer

```csharp
new ChannelOptions
{
    Priority = 50,
    InitialCredits = 512 * 1024,  // 512KB
    MaxCredits = 2 * 1024 * 1024, // 2MB
    WriteTimeout = TimeSpan.FromMinutes(1)
}
```

### Background Sync

```csharp
new ChannelOptions
{
    Priority = 1,  // Lowest
    InitialCredits = 64 * 1024,
    MaxCredits = 128 * 1024,
    WriteTimeout = null  // Wait forever
}
```

## Usage

```csharp
// Open channel with options
var channel = await mux.OpenChannelAsync("data", new ChannelOptions
{
    Priority = 200,
    InitialCredits = 128 * 1024
});

// Accept with options
var channel = await mux.AcceptChannelAsync("data", new ChannelOptions
{
    Priority = 200,
    InitialCredits = 128 * 1024
});
```
