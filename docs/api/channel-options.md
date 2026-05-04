# ChannelOptions

Per-channel configuration. See [Channels](../concepts/channels.md) for concepts.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ChannelId` | `string` | (required) | Unique channel identifier |
| `Priority` | `ChannelPriority` | `Normal` | Channel priority level |
| `SlabSize` | `int` | 1,048,576 (1 MB) | Slab size in bytes |
| `SendTimeout` | `TimeSpan` | 30s | Timeout for sends blocked on backpressure |

## Usage

```csharp
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "high-priority-control",
    Priority = ChannelPriority.Highest,
    SlabSize = 256 * 1024,                    // 256KB (small, control messages)
    SendTimeout = TimeSpan.FromSeconds(5)      // Fail fast
});
```

## Priority Levels

| Level | Value | Use Case |
|-------|-------|----------|
| `Highest` | 255 | Control, heartbeats |
| `High` | 192 | Interactive, user input |
| `Normal` | 128 | Default |
| `Low` | 64 | Background |
| `Lowest` | 0 | Bulk data |

See [Priority](../concepts/priority.md) for details.

## SlabSize

The slab is a pre-allocated buffer for the channel. Larger slabs allow more data in-flight before backpressure kicks in. Smaller slabs reduce memory usage per channel.

See [Backpressure](../concepts/backpressure.md) for flow control details.

## SendTimeout

Time to wait when the credit window is exhausted before throwing `TimeoutException`:

```csharp
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "data",
    SendTimeout = TimeSpan.FromSeconds(60)  // Wait longer for slow receivers
});
```
