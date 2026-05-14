# Priority

When several channels have frames ready to send, the writer thread picks one. **Higher priority wins.**

## The five levels

`ChannelPriority` is a `byte` enum:

| Name | Value |
| --- | --- |
| `Lowest` | 0 |
| `Low` | 64 |
| `Normal` | 128 (default) |
| `High` | 192 |
| `Highest` | 255 |

The values are deliberately spread out — you can cast any `byte` to `ChannelPriority` for finer control if you really need it:

```csharp
var ch = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "uploads",
    Priority  = (ChannelPriority)32,    // between Lowest and Low
});
```

## What it affects

Priority controls **selection order**, not bandwidth allocation. With one `Highest` and one `Normal` channel both backed up:

- `Highest`'s frames go out as long as it has data and slab space.
- `Normal`'s frames go out when `Highest` is idle (no data ready, or backpressured).

Priority does not preempt a frame in flight — once an 8-byte header and its payload are being written, they finish.

## Suggested mapping

| Use case | Priority |
| --- | --- |
| Latency-critical control / heartbeats / input | `High` or `Highest` |
| Real-time state sync (gameplay, dashboards) | `High` |
| Interactive chat, RPC requests | `Normal` |
| Background telemetry, logs | `Low` |
| Bulk uploads, file transfer | `Low` or `Lowest` |

The multiplexer's own control frames (pings, ACKs, channel INIT/FIN) are sent on an internal control channel and are not subject to your priority choices.

## Default priority

`ChannelPriority.Normal` (128) is the default for `ChannelOptions` and for `MultiplexerOptions.DefaultChannelOptions.Priority`.

If you change the default for an entire multiplexer:

```csharp
new MultiplexerOptions
{
    StreamFactory = …,
    DefaultChannelOptions = new() { Priority = ChannelPriority.Low },
}
```

…then `mux.OpenChannel("foo")` (without explicit options) gets `Low`.
