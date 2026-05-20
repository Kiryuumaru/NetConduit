# `MultiplexerOptions`

Namespace: `NetConduit.Models`.

Session-level configuration. Passed to `StreamMultiplexer.Create` (or to transport factories, which forward it).

```csharp
public sealed record MultiplexerOptions
{
    public required StreamFactoryDelegate StreamFactory { get; init; }
    public Guid?     SessionId                       { get; init; }
    public TimeSpan  PingInterval                    { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan  PingTimeout                     { get; init; } = TimeSpan.FromSeconds(10);
    public int       MaxMissedPings                  { get; init; } = 3;
    public TimeSpan  GoAwayTimeout                   { get; init; } = TimeSpan.FromSeconds(30);
    public int       MaxAutoReconnectAttempts        { get; init; } = -1;                     // -1 = unlimited (default), 0 = no reconnect
    public TimeSpan  AutoReconnectDelay              { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan  MaxAutoReconnectDelay           { get; init; } = TimeSpan.FromSeconds(30);
    public double    AutoReconnectBackoffMultiplier  { get; init; } = 2.0;
    public TimeSpan  ConnectionTimeout               { get; init; } = TimeSpan.FromSeconds(30);
    public DefaultChannelOptions DefaultChannelOptions { get; init; } = new();
}
```

## Property reference

| Property | Default | Meaning |
| --- | --- | --- |
| `StreamFactory` | (required) | Builds a fresh `IStreamPair` on connect and reconnect. |
| `SessionId` | new GUID | Local session identity. Sticky across reconnects. |
| `PingInterval` | 30 s | Time between keepalive pings. |
| `PingTimeout` | 10 s | Time to wait for a `Pong` before counting a missed ping. |
| `MaxMissedPings` | 3 | After this many missed pings, the connection is declared dead. |
| `GoAwayTimeout` | 30 s | How long `GoAwayAsync` waits for channels to drain. |
| `MaxAutoReconnectAttempts` | `-1` | `-1` = **unlimited** retries (default; replay buffer enabled). `0` = no reconnect (terminal on first failure; replay disabled). `>0` = max attempts; further failures throw. |
| `AutoReconnectDelay` | 1 s | Base delay for the first reconnect attempt. |
| `MaxAutoReconnectDelay` | 30 s | Cap for exponential backoff. |
| `AutoReconnectBackoffMultiplier` | 2.0 | Multiplier applied to delay each attempt. |
| `ConnectionTimeout` | 30 s | Per-attempt timeout passed to `StreamFactory`. |
| `DefaultChannelOptions` | new | Defaults used when `ChannelOptions` aren't specified. |

## Reconnect behavior

- The default `MaxAutoReconnectAttempts = -1` reconnects forever with **replay buffer enabled**: open channels survive transport drops; the writer replays its send slab after reconnect and the reader skips already-received bytes so no duplicates are delivered.
- If `MaxAutoReconnectAttempts == 0`, no reconnect is attempted; the first transport failure raises terminal `Disconnected`. The **replay buffer is disabled**. Open channels are torn down with `ChannelCloseReason.TransportFailed`.
- If `MaxAutoReconnectAttempts > 0`, the **replay buffer is enabled** for up to N attempts. After the configured limit is reached, channels close with `ChannelCloseReason.TransportFailed`.

See [Reconnection](../concepts/reconnection.md).

## `DefaultChannelOptions`

```csharp
public sealed class DefaultChannelOptions
{
    public ChannelPriority Priority { get; init; } = ChannelPriority.Normal;
    public int             SlabSize { get; init; } = 1 * 1024 * 1024;
    public TimeSpan        SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
```

Applied by `OpenChannel(string)` extension (no per-channel `ChannelOptions`).

## Validation

- `DefaultChannelOptions.SlabSize` must be between 64 KiB and 64 MiB (`FrameConstants.MinSlabSize` / `MaxSlabSize`). Enforced in `StreamMultiplexer.Create` and again per-channel in `OpenChannel`.
- `StreamFactory` is `required` — omitting it is a compile error.
