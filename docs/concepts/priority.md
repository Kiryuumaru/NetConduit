# Priority

Frame scheduling based on channel importance. Higher priority channels get bandwidth preference.

## How It Works

```
┌──────────────────────────────────────────────┐
│                                              │
│  High Priority    ───┐                       │
│  Medium Priority  ───┼───▶ [Scheduler] ───▶ Wire
│  Low Priority     ───┘                       │
│                                              │
│  Scheduler sends higher priority first       │
│  Lower priority waits when bandwidth limited │
│                                              │
└──────────────────────────────────────────────┘
```

## Priority Levels

| Level | Value | Use Case |
|-------|-------|----------|
| `Highest` | 255 | Control messages, heartbeats |
| `High` | 192 | Interactive, real-time |
| `Normal` | 128 | Default, general data |
| `Low` | 64 | Background, bulk transfers |
| `Lowest` | 0 | Best-effort, can be delayed |

## Setting Priority

```csharp
// Use predefined levels
var controlChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "control",
    Priority = ChannelPriority.Highest
});

var dataChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "data",
    Priority = ChannelPriority.Normal
});

var bulkChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "bulk-upload",
    Priority = ChannelPriority.Low
});

// Or use numeric value (0-255)
var customPriority = await mux.OpenChannelAsync(new()
{
    ChannelId = "custom",
    Priority = 200  // Between High and Highest
});
```

## Priority Values

```csharp
public static class ChannelPriority
{
    public const byte Lowest = 0;
    public const byte Low = 64;
    public const byte Normal = 128;
    public const byte High = 192;
    public const byte Highest = 255;
}
```

## Common Patterns

### Control vs Data Separation

```csharp
// Control channel - always responsive
var control = await mux.OpenChannelAsync(new()
{
    ChannelId = "control",
    Priority = ChannelPriority.Highest
});

// Data channel - can be delayed
var data = await mux.OpenChannelAsync(new()
{
    ChannelId = "data",
    Priority = ChannelPriority.Normal
});

// Control never blocked by data traffic
```

### Video Game Example

```csharp
// Input events - highest priority (instant response)
var inputChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "input",
    Priority = ChannelPriority.Highest
});

// Game state updates - high priority
var stateChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "state",
    Priority = ChannelPriority.High
});

// Chat messages - normal priority
var chatChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "chat",
    Priority = ChannelPriority.Normal
});

// Asset downloads - low priority (background)
var downloadChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "assets",
    Priority = ChannelPriority.Low
});
```

### File Server Example

```csharp
// Commands - instant response
var cmdChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "fs-commands",
    Priority = ChannelPriority.Highest
});

// Directory listings - responsive
var listChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "fs-listing",
    Priority = ChannelPriority.High
});

// File transfers - background
var fileChannel = await mux.OpenChannelAsync(new()
{
    ChannelId = "fs-transfer",
    Priority = ChannelPriority.Low
});
```

## Priority Behavior

### Fair Scheduling

Higher priority doesn't mean exclusive access:

- High priority frames sent first when multiple are queued
- Lower priority still makes progress (starvation-free)
- Works best under congestion/bandwidth pressure

### No Effect at Low Load

When bandwidth is ample:
- All priorities get instant send
- Priority matters only under pressure

### Priority is Local

Priority affects:
- This multiplexer's send order
- Frames waiting in send queue

Priority does NOT affect:
- Remote side's processing order
- Network-level QoS

## Tips

**Reserve highest for control:**
```csharp
// Keep Highest for critical control
// Use High for "important" application data
```

**Don't over-prioritize:**
```csharp
// Bad - everything is high priority (defeats the purpose)
Priority = ChannelPriority.Highest  // on all channels

// Good - clear priority hierarchy
control:  Highest
interactive: High
background: Low
```

**Test under load:**
```csharp
// Priority effects are visible under bandwidth pressure
// Test with concurrent bulk transfers
```

**Combine with backpressure:**
```csharp
// Low priority + small credit buffer = background transfer
var background = await mux.OpenChannelAsync(new()
{
    ChannelId = "background",
    Priority = ChannelPriority.Low,
    MaxCredits = 64 * 1024  // Small buffer
});
```
