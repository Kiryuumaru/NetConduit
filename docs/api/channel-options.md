# `ChannelOptions`

Namespace: `NetConduit.Models`.

Per-channel configuration passed to `IStreamMultiplexer.OpenChannel`.

```csharp
public sealed class ChannelOptions
{
    public required string          ChannelId   { get; init; }
    public ChannelPriority          Priority    { get; init; } = ChannelPriority.Normal;
    public int                      SlabSize    { get; init; } = 1 * 1024 * 1024;        // 1 MiB
    public TimeSpan                 SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
```

## Property reference

| Property | Default | Meaning |
| --- | --- | --- |
| `ChannelId` | (required) | UTF-8 string identifying this channel. Must be unique on the mux. Length <= 1024 bytes. Must not contain `>>` or `<<` if used by [transits](../transits/index.md). |
| `Priority` | `Normal` (128) | Ordering hint for the writer loop when multiple channels are ready. See [Priority](../concepts/priority.md). |
| `SlabSize` | 1 MiB | Bytes pre-allocated for outbound framing on this channel. Larger slabs allow more in-flight data before backpressure. |
| `SendTimeout` | 30 s | Maximum time `WriteAsync` will wait for slab space before throwing `TimeoutException`. |

## Example

```csharp
var ch = mux.OpenChannel(new ChannelOptions
{
    ChannelId = "video",
    Priority  = ChannelPriority.High,
    SlabSize  = 4 * 1024 * 1024,
    SendTimeout = TimeSpan.FromSeconds(5),
});
```

## Defaults from the mux

`OpenChannel(string)` (the extension method) uses `MultiplexerOptions.DefaultChannelOptions` for `Priority`, `SlabSize`, and `SendTimeout`.

## Validation

- `SlabSize` must be between 64 KiB and 64 MiB.
- `ChannelId` must be non-empty and <= 1024 bytes when UTF-8 encoded.
- Duplicate IDs throw `InvalidOperationException` at `OpenChannel` time.
