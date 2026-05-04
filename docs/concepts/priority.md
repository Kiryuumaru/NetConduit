# Priority

Channel priority determines the order in which frames are sent when multiple channels have data ready. See [Concepts Overview](index.md) for related concepts.

## Priority Levels

| Level | Value | Use Case |
|-------|-------|----------|
| `Highest` | 255 | Control messages, heartbeats |
| `High` | 192 | Interactive UI, user input |
| `Normal` | 128 | Default for all channels |
| `Low` | 64 | Background transfers |
| `Lowest` | 0 | Bulk data, logs |

## Usage

```csharp
// Set priority when opening a channel
var channel = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "control",
    Priority = ChannelPriority.High
});

// Default priority channel
var dataChannel = mux.OpenChannel("bulk-data");  // Normal priority
```

## Behavior

When multiple channels have frames queued for sending:
- Higher priority frames are sent first
- Same-priority channels are served in round-robin order
- Priority only affects sending order — it does not affect receive order

## Default Priority

Set a default priority for all channels via [MultiplexerOptions](../api/multiplexer-options.md):

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,
    DefaultChannelOptions = new DefaultChannelOptions
    {
        Priority = ChannelPriority.Low  // All channels default to Low
    }
};
```

Individual channels can still override:

```csharp
var important = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "alerts",
    Priority = ChannelPriority.Highest
});
```
