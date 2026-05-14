# `StreamMultiplexerExtensions`

Namespace: `NetConduit` (extension class).

Convenience extensions on `IStreamMultiplexer` for the common open/accept patterns.

```csharp
public static class StreamMultiplexerExtensions
{
    public static IWriteChannel OpenChannel(
        this IStreamMultiplexer mux,
        string channelId);

    public static Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken ct = default);

    public static Task<IWriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux,
        ChannelOptions options,
        CancellationToken ct = default);

    public static ValueTask<IReadChannel> AcceptChannelAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken ct = default);
}
```

## Behavior

| Method | Equivalent to |
| --- | --- |
| `OpenChannel(mux, id)` | `mux.OpenChannel(new ChannelOptions { ChannelId = id })` (uses `MultiplexerOptions.DefaultChannelOptions`). |
| `OpenChannelAsync(mux, id, ct)` | `OpenChannel` + `WaitForReadyAsync(ct)`. |
| `OpenChannelAsync(mux, options, ct)` | `OpenChannel(options)` + `WaitForReadyAsync(ct)`. |
| `AcceptChannelAsync(mux, id, ct)` | `AcceptChannel` + `WaitForReadyAsync(ct)`. |

`AcceptChannelAsync` returns `ValueTask<IReadChannel>` because the channel may already be in the accept queue when called.

## Transit extensions

Each transit package adds its own extensions to `IStreamMultiplexer`:

| Method | Package |
| --- | --- |
| `OpenStream` / `AcceptStream` / `AcceptStreamAsync` | `NetConduit.Transit.Stream` |
| `OpenDuplexStream` / `OpenDuplexStreamAsync` / `AcceptDuplexStream` / `AcceptDuplexStreamAsync` | `NetConduit.Transit.DuplexStream` |
| `OpenMessageTransit` / `OpenMessageTransitAsync` / `AcceptMessageTransit` / `AcceptMessageTransitAsync` | `NetConduit.Transit.Message` |
| `OpenDeltaMessageTransit` / `OpenDeltaMessageTransitAsync` / `AcceptDeltaMessageTransit` / `AcceptDeltaMessageTransitAsync` | `NetConduit.Transit.DeltaMessage` |

See [Transits](../transits/index.md).
