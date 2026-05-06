# Eagerness Flow

## Problem

The mux API is inconsistent in how eagerly it returns control to the caller:

| Operation | Current Behavior |
|-----------|-----------------|
| `Start()` | Returns immediately — reconnect loop handles connectivity in background |
| `OpenChannel(id)` | Returns `WriteChannel` immediately — pretends to be `Open` before remote ACKs |
| `AcceptChannelAsync(id)` | **Blocks** until remote sends INIT frame |

Two problems:
- `AcceptChannelAsync` forces the caller to suspend, breaking the optimistic pattern
- `OpenChannel` fakes readiness — `MarkOpen()` fires before remote even receives the INIT frame

---

## Design: Optimistic, Non-Blocking Channel Lifecycle

Every operation returns a handle immediately. The caller opts in to waiting for progress via explicit signals. Naming is consistent across all three levels:

| Concept | Multiplexer | WriteChannel | ReadChannel |
|---------|-------------|--------------|-------------|
| Wait for ready | `WaitForReadyAsync(ct)` | `WaitForReadyAsync(ct)` | `WaitForReadyAsync(ct)` |
| Ready event | `OnConnected` | `OnConnected` | `OnConnected` |
| Poll readiness | `IsConnected` | `IsConnected` | `IsConnected` |
| Channel event | `OnChannelOpened` (outbound) | — | — |
| Channel event | `OnChannelAccepted` (inbound) | — | — |

---

### New Channel API Surface

```csharp
// Both WriteChannel and ReadChannel share these readiness members:

/// Suspend until channel is confirmed ready (remote ACK for write, remote INIT for read).
Task WaitForReadyAsync(CancellationToken ct = default);

/// Raised when the channel becomes ready.
event Action? OnConnected;

/// Whether the channel is currently in a ready/connected state.
bool IsConnected { get; }
```

---

### Consistent Pattern

| Operation | Returns | State | Readiness Signal |
|-----------|---------|-------|-----------------|
| `Start()` | `void` immediately | Mux running, transport pending | `await mux.WaitForReadyAsync()` / `mux.OnConnected` |
| `OpenChannel(id)` | `WriteChannel` immediately | Channel pending, buffers writes | `await channel.WaitForReadyAsync()` / `channel.OnConnected` |
| `AcceptChannel(id)` | `ReadChannel` immediately | Channel pending, reads block | `await channel.WaitForReadyAsync()` / `channel.OnConnected` |

All three follow the same contract: **return handle → use optimistically → observe readiness if needed**.

---

## Channel States

```
Pending → Open → Closing → Closed
```

- **Pending**: Handle exists, not yet confirmed by remote. Reads/writes block transparently.
- **Open**: Remote confirmed. `OnConnected` fires, `WaitForReadyAsync` completes, `IsConnected` returns `true`.
- **Closing / Closed**: FIN or error received.

### WriteChannel State Transitions

1. `OpenChannel()` → channel in `Pending` state, INIT frame queued in slab
2. Remote sends ACK → channel transitions to `Open`, `OnConnected` fires
3. Writes during `Pending` buffer into slab (existing behavior, no change)

### ReadChannel State Transitions

1. `AcceptChannel()` → channel in `Pending` state, registered in channel registry
2. Remote sends INIT → channel transitions to `Open`, `OnConnected` fires
3. Reads during `Pending` block until data arrives (transparent)

---

## Blocking Behavior on Pending Channels

Both directions block transparently when the channel is in `Pending` state:

| Direction | Pending Behavior | Open Behavior |
|-----------|-----------------|---------------|
| `WriteChannel.WriteAsync` | Buffers into slab, blocks only if slab full | Same |
| `ReadChannel.ReadAsync` | Blocks until channel opens + data arrives | Returns data |

If the channel never opens (mux disposed, timeout, GoAway), blocked operations throw `ChannelClosedException` with reason `TransportFailed` or `MuxDisposed`.

---

## Multiplexer-Level Channel Events

| Event | Fires When |
|-------|------------|
| `OnChannelOpened` | Local side calls `OpenChannel` (outbound channel created) |
| `OnChannelAccepted` | Remote INIT arrives for an accepted channel (inbound channel ready) |
| `OnChannelClosed` | Any channel closes (with reason + exception) |

```csharp
mux.OnChannelOpened += (channelId) => { ... };    // outbound created
mux.OnChannelAccepted += (channelId) => { ... };  // inbound confirmed by remote
mux.OnChannelClosed += (channelId, ex) => { ... };
```

---

## Backward Compatibility (Extension Methods)

Async convenience methods live in a static extension class, not on the interface:

```csharp
public static class StreamMultiplexerExtensions
{
    /// Equivalent to OpenChannel + WaitForReadyAsync
    public static async Task<WriteChannel> OpenChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.OpenChannel(channelId);
        await channel.WaitForReadyAsync(ct);
        return channel;
    }

    /// Equivalent to AcceptChannel + WaitForReadyAsync
    public static async Task<ReadChannel> AcceptChannelAsync(
        this IStreamMultiplexer mux, string channelId, CancellationToken ct = default)
    {
        var channel = mux.AcceptChannel(channelId);
        await channel.WaitForReadyAsync(ct);
        return channel;
    }
}
```

Usage:

```csharp
WriteChannel outbound = await mux.OpenChannelAsync("telemetry", ct);
ReadChannel inbound = await mux.AcceptChannelAsync("commands", ct);
```

---

## Usage Examples

### Optimistic (no awaits until I/O)

```csharp
var mux = StreamMultiplexer.Create(options);
mux.Start();

// Declare all channels up front — no awaits needed
var outbound = mux.OpenChannel("telemetry");
var inbound = mux.AcceptChannel("commands");

// Reads/writes block transparently until channels are ready
await inbound.ReadAsync(buffer, ct);
await outbound.WriteAsync(data, ct);
```

### Explicit readiness (wait before using)

```csharp
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync(ct);

var outbound = mux.OpenChannel("telemetry");
var inbound = mux.AcceptChannel("commands");

await outbound.WaitForReadyAsync(ct);  // remote ACKed our INIT
await inbound.WaitForReadyAsync(ct);   // remote sent INIT

await inbound.ReadAsync(buffer, ct);
await outbound.WriteAsync(data, ct);
```

### Event-driven readiness

```csharp
var mux = StreamMultiplexer.Create(options);
mux.Start();

var outbound = mux.OpenChannel("telemetry");
outbound.OnConnected += () => Console.WriteLine("telemetry channel ready");

var inbound = mux.AcceptChannel("commands");
inbound.OnConnected += () => Console.WriteLine("commands channel ready");
```

---

## Open Questions

1. Should `AcceptChannel` accept `ChannelOptions` (timeout, buffer size) like `OpenChannel` does?
2. Should there be an `AcceptChannels()` (non-async IEnumerable) that yields pending channels as they are declared, or does `AcceptChannelsAsync` remain the only multi-channel accept?
3. Should `WaitForReadyAsync` have a default timeout from `ChannelOptions`, or rely solely on the caller's `CancellationToken`?
4. Should `OnConnected` fire again after a reconnection (channel resumes), or only on first open?
