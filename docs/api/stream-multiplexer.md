# `IStreamMultiplexer` / `StreamMultiplexer`

The core multiplexer interface. A `StreamMultiplexer` wraps an `IStreamPair` and exposes virtual channels.

Namespace: `NetConduit.Interfaces` (interface), `NetConduit` (class).

## Interface

```csharp
public interface IStreamMultiplexer : IAsyncDisposable
{
    // ----- Properties -----
    MultiplexerOptions Options { get; }
    MultiplexerStats   Stats   { get; }

    bool IsReady       { get; }   // true after first successful handshake. Sticky.
    bool IsConnected   { get; }   // transport is currently connected
    bool IsRunning     { get; }   // started and not disposed
    bool IsShuttingDown{ get; }   // graceful shutdown in progress

    Guid SessionId       { get; } // local session identity
    Guid RemoteSessionId { get; } // remote session identity

    IReadOnlyCollection<string> ActiveChannelIds { get; }
    int                         ActiveChannelCount { get; }
    DisconnectReason?           DisconnectReason   { get; }

    // ----- Events -----
    event EventHandler? Ready;
    event EventHandler<ChannelEventArgs>?       ChannelOpened;
    event EventHandler<ChannelEventArgs>?       ChannelAccepted;
    event EventHandler<ChannelClosedEventArgs>? ChannelClosed;
    event EventHandler<ErrorEventArgs>?         Error;
    event EventHandler<DisconnectedEventArgs>?  Disconnected;
    event EventHandler?                         Connected;
    event EventHandler<ReconnectingEventArgs>?  Reconnecting;

    // ----- Lifecycle -----
    void Start();
    Task WaitForReadyAsync(CancellationToken ct = default);
    ValueTask GoAwayAsync(CancellationToken ct = default);
    ValueTask FlushAsync (CancellationToken ct = default);

    // ----- Channels -----
    IWriteChannel  OpenChannel  (ChannelOptions options);
    IReadChannel   AcceptChannel(string channelId);
    IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(CancellationToken ct = default);

    bool TryRegisterChannels(
        ReadOnlySpan<ChannelRegistration> registrations,
        out IReadOnlyDictionary<ChannelRegistration, IChannel> channels);

    IWriteChannel? GetWriteChannel(string channelId);
    IReadChannel?  GetReadChannel (string channelId);
}
```

## Class `StreamMultiplexer`

```csharp
namespace NetConduit;

public sealed class StreamMultiplexer : IStreamMultiplexer
{
    public static StreamMultiplexer Create(MultiplexerOptions options);
}
```

The constructor is private — always use `Create`. Transport packages wrap this with role-specific factories (`TcpMultiplexer.CreateOptions`, `TcpMultiplexer.CreateServerOptions`, etc.).

## Lifecycle methods

| Method | Behavior |
| --- | --- |
| `Start()` | Begins the main loop, handshake, and IO tasks. Throws if already running. Returns immediately; use `WaitForReadyAsync` to await readiness. |
| `WaitForReadyAsync(ct)` | Completes when the first connection's handshake succeeds. Never completes again on reconnects. |
| `GoAwayAsync(ct)` | Sends a `GoAway` control frame, refuses new channels, waits for active ones to drain (up to `MultiplexerOptions.GoAwayTimeout`). |
| `FlushAsync(ct)` | Signals the writer loop to flush all buffered frames to the transport, then completes. |
| `DisposeAsync()` | Stops loops, closes channels, disposes the transport. Raises `Disconnected` with `LocalDispose`. |

## Channel methods

```csharp
IWriteChannel ch = mux.OpenChannel(new ChannelOptions { ChannelId = "data" });
await ch.WaitForReadyAsync();
```

```csharp
IReadChannel ch = mux.AcceptChannel("data");
await ch.WaitForReadyAsync();
```

```csharp
await foreach (var ch in mux.AcceptChannelsAsync(ct))
{
    _ = Task.Run(() => HandleAsync(ch));
}
```

`OpenChannel` allocates a channel index immediately and sends `INIT`. `AcceptChannel` returns a pending channel; it becomes ready when the remote `INIT` arrives (or the multiplexer is already aware of a matching unclaimed inbound channel).

`GetWriteChannel` and `GetReadChannel` return `null` if no channel with that ID currently exists; they don't open new ones.

### Atomic multi-channel registration

`TryRegisterChannels` registers a group of channels as a single all-or-nothing transaction. Either every entry in the batch is committed and no `INIT` frame is buffered for any rolled-back outbound, or the registry is restored to its pre-call state and the method returns `false`.

```csharp
var writeReg = new ChannelRegistration("data>>", ChannelDirection.Outbound);
var readReg  = new ChannelRegistration("data<<", ChannelDirection.Inbound);
ReadOnlySpan<ChannelRegistration> regs = [writeReg, readReg];

if (!mux.TryRegisterChannels(regs, out var channels))
    throw new InvalidOperationException("Channel id already in use.");

var write = (IWriteChannel)channels[writeReg];
var read  = (IReadChannel)channels[readReg];
```

Semantics:

- **Outbound** entries require a vacant id. Any pre-existing write channel, read channel, or pending accept on the same id causes the full batch to roll back and the method returns `false`.
- **Inbound** entries are idempotent, like `AcceptChannel(string)`. An existing read channel or pending accept on the same id is reused and returned in the result dictionary. A pre-existing outbound binding on the same id is still a collision.
- The same `(ChannelId, Direction)` pair may not appear twice in a single batch — that throws `ArgumentException`.
- Invalid input (empty batch, null/oversized channel id, mismatched `Options.ChannelId`, out-of-range `SlabSize`) throws before any commit occurs.
- The multiplexer must be started and not shutting down, otherwise `InvalidOperationException` is thrown.

This primitive is what the composite transit packages (`NetConduit.Transit.DuplexStream`, `NetConduit.Transit.Message`, `NetConduit.Transit.DeltaMessage`) use to allocate their write + read channel pair without leaking a half-committed channel id when the second registration would have failed.

See also: [`StreamMultiplexerExtensions`](extensions.md) for `OpenChannel(string)`, `OpenChannelAsync`, `AcceptChannelAsync`.

## Events

| Event | Fires when |
| --- | --- |
| `Ready` | First handshake succeeds (once). |
| `Connected` | Each time the transport connects (initial + each reconnect). |
| `Disconnected` | Transport disconnects. |
| `Reconnecting` | Each reconnect attempt starts. `Attempt` is 1-based. |
| `ChannelOpened` | A locally opened channel becomes ready. |
| `ChannelAccepted` | A remotely opened channel becomes ready. |
| `ChannelClosed` | Any channel closes. |
| `Error` | Non-fatal error occurred. |

## Thread safety

All methods on `IStreamMultiplexer` are safe to call from any thread. Events are raised on internal IO threads — do not block on them.
