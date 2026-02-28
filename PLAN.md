# StreamMultiplexer Connection Overhaul Plan

> **⚠️ BREAKING CHANGES:** This overhaul introduces breaking API changes. Backwards compatibility is not a concern—feel free to modify any public API as needed.

## Problem Statement

Currently, `StreamMultiplexer.CreateAsync()` calls `StreamFactory` immediately during construction. If the initial connection fails, an exception is thrown directly with no retry logic. The sophisticated auto-reconnect logic only applies AFTER the multiplexer is running and a transport error occurs mid-session.

This creates an inconsistency: **initial connection failures are not resilient, but mid-session failures are.**

---

## Goal

Unify connection handling so that:
1. Creation is synchronous and connection-agnostic
2. All connection logic (initial + reconnection) happens after `Start()` is called
3. Initial connection uses the same retry/backoff logic as reconnection
4. The multiplexer can be created before any network I/O occurs

---

## New API Design

```csharp
// Creation - synchronous, no I/O, no exceptions from network
var mux = StreamMultiplexer.Create(options);  // Static factory method

// Start - kicks off background loop, returns run task immediately
Task runTask = mux.Start();  // Synchronous, just spawns the background task

// Optional: Wait until first successful connection + handshake
await mux.WaitForReadyAsync(cancellationToken);

// Use channels as normal
var channel = await mux.OpenChannelAsync(options);
```

---

## State Machine

```
[Created] ──Start()──► [Connecting] ──success──► [Running]
                            │  ▲                     │
                         failure│              transport error
                            │  │                     │
                            └──┘                     ▼
                       (retry loop)            [Reconnecting] ──success──► [Running]
                            │                        │  ▲
                   max attempts                   failure│
                      exceeded                       │  │
                            │                        └──┘
                            ▼                   (retry loop)
                        [Failed]                     │
                                           max attempts exceeded
                                                     │
                                                     ▼
                                                 [Failed]
```

**States explained:**
- `[Connecting]` = Initial connection phase (StreamFactory + Handshake, with retries)
- `[Reconnecting]` = Recovery phase after transport error (same logic, but `IsReconnecting=true`)
- Both phases use `ConnectWithRetryAsync()` with identical retry/backoff behavior

**Note:** 
- Initial connection failures stay in `[Connecting]` (retry loop with `IsReconnecting=false`)
- Transport errors from `[Running]` transition to `[Reconnecting]` (`IsReconnecting=true`)
- Handshake is part of each connection attempt, not a separate visible state

### State Definitions

| State | `IsRunning` | `IsConnected` | `IsReconnecting` | Description |
|-------|-------------|---------------|------------------|-------------|
| Created | `false` | `false` | `false` | Just constructed, `Start()` not called |
| Connecting | `true` | `false` | `false` | Initial connection in progress (includes handshake) |
| Running | `true` | `true` | `false` | Fully operational, channels can be used |
| Reconnecting | `true` | `false` | `true` | Connection lost, attempting recovery (includes handshake) |
| Failed | `false` | `false` | `false` | Max attempts exceeded, `runTask` faulted |

---

## Internal Flow

### Main Loop (`Start()` → Background Task)

```
Start() called
  │
  └─► Validates not already started
  └─► Sets IsRunning = true
  └─► Spawns MainLoopAsync() as background task
  └─► Returns the task immediately
       │
       ▼
MainLoopAsync():
  │
  └─► ConnectWithRetryAsync()  ◄─── Unified connection logic
       │
       ├─► Loop: attempt = 1, 2, 3, ...
       │    │
       │    ├─► Fire OnAutoReconnecting(attempt, ...)
       │    │
       │    ├─► Call StreamFactory(ct)
       │    │    ├─► Success: got streams
       │    │    └─► Failure: log, wait backoff delay, continue loop
       │    │
       │    ├─► SendHandshakeAsync()
       │    │
       │    ├─► WaitForHandshakeResponseAsync()
       │    │    ├─► Success: handshake complete
       │    │    └─► Failure: dispose streams, continue loop
       │    │
       │    └─► Check max attempts
       │         ├─► Exceeded (and > 0): throw, fire OnAutoReconnectFailed
       │         └─► Not exceeded: continue
       │
       └─► (Success) Set IsConnected = true, signal ready
       │
       └─► Start read/write/ping/flush loops
       │
       └─► On transport error:
            │
            ├─► Set IsConnected = false, IsReconnecting = true
            ├─► Fire OnDisconnected
            ├─► Dispose old streams
            └─► Goto ConnectWithRetryAsync() (same logic)
```

---

## Configuration Options

Existing options that apply to both initial and reconnection:

| Option | Default | Description |
|--------|---------|-------------|
| `StreamFactory` | **required** | Factory delegate to create streams |
| `MaxAutoReconnectAttempts` | `0` | Max attempts (0 = unlimited) |
| `AutoReconnectDelay` | `1s` | Initial delay between attempts |
| `MaxAutoReconnectDelay` | `30s` | Maximum delay (backoff cap) |
| `AutoReconnectBackoffMultiplier` | `2.0` | Exponential backoff multiplier |

### New Options (Confirmed)

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionTimeout` | `Timeout.InfiniteTimeSpan` | Timeout for each StreamFactory call (infinite = rely on user's CancellationToken) |
| `HandshakeTimeout` | `Timeout.InfiniteTimeSpan` | Timeout for handshake completion (infinite = rely on user's CancellationToken) |

---

## Events

| Event | When Fired |
|-------|------------|
| `OnAutoReconnecting` | Before each connection attempt (**including initial** - unified) |
| `OnAutoReconnectFailed` | When max attempts exceeded (initial or reconnection) |
| `OnDisconnected` | When transport error detected, before reconnection starts |
| `OnReconnected` | When connection restored after disconnection |
| `OnReady` | When first successful connection + handshake completes |
| `OnError` | On unrecoverable errors |

**Confirmed:** `OnAutoReconnecting` fires for initial connection too. `AttemptNumber=1` for first attempt.

---

## Edge Cases & Handling

### Connection Phase

| Edge Case | Handling |
|-----------|----------|
| `Start()` called twice | Throw `InvalidOperationException` |
| `StreamFactory` throws | Count as failed attempt, fire event, retry |
| `StreamFactory` returns null | Treat as failure, retry |
| `StreamFactory` hangs | Relies on user's CancellationToken or on `ConnectionTimeout` |
| Handshake timeout | Dispose streams, fire event, retry |
| Handshake protocol error | Dispose streams, retry (assume transient - **confirmed**) |
| `Dispose()` during connecting | Cancel all attempts, clean up, complete runTask |

### Reconnection Phase

| Edge Case | Handling |
|-----------|----------|
| Transport error detected | Start reconnection loop |
| `Dispose()` during reconnecting | Cancel reconnection, clean up |
| Remote sends GOAWAY | Do NOT reconnect (graceful shutdown intended) |
| Ping timeout (no explicit error) | Treat as transport error, trigger reconnect |
| `MaxAutoReconnectAttempts` exceeded | Fire `OnAutoReconnectFailed`, fault runTask |

### Channel Operations (Confirmed: Await Optimistically)

| Operation | Before Ready | During Reconnecting | After Ready |
|-----------|--------------|---------------------|-------------|
| `OpenChannelAsync` | Awaits until ready | Awaits until reconnected | Normal |
| `AcceptChannelAsync` | Awaits until ready | Awaits until reconnected | Normal |
| `WriteChannel.WriteAsync` | Awaits | Buffers (up to limit) | Normal |
| `ReadChannel.ReadAsync` | Awaits | Returns buffered, then awaits | Normal |

**Decision:** Await asynchronously (not thread-blocking). Channels will resume once connection is restored.

> **Note:** "Awaits" = async waiting via `await`, not literal thread blocking. All operations remain non-blocking and async.

---

## New Public Members

### Methods

```csharp
/// <summary>
/// Creates a new multiplexer. No I/O occurs until Start() is called.
/// </summary>
public static StreamMultiplexer Create(MultiplexerOptions options);

/// <summary>
/// Starts the multiplexer background loop. Returns immediately.
/// The returned task completes when the multiplexer shuts down (or fails permanently).
/// </summary>
public Task Start();

/// <summary>
/// Waits until the multiplexer is ready (first successful connection + handshake).
/// </summary>
public Task WaitForReadyAsync(CancellationToken cancellationToken = default);
```

### Properties

```csharp
/// <summary>
/// Current connection attempt number (0 if connected, >0 during connecting/reconnecting).
/// </summary>
public int CurrentConnectionAttempt { get; }
```

---

## Removed/Changed Members

| Member | Change |
|--------|--------|
| `CreateAsync(MultiplexerOptions)` | **Renamed** to `Create()`, no longer async |
| `StartAsync()` | **Renamed** to `Start()`, no longer async |
| Constructor | **Remains private** - use static `Create()` |
| `_readStream`, `_writeStream` | No longer `readonly`, can be replaced |

---

## Migration Guide

### Before

```csharp
var options = new MultiplexerOptions { StreamFactory = factory };
await using var mux = await StreamMultiplexer.CreateAsync(options);
var runTask = await mux.StartAsync();
```

### After

```csharp
var options = new MultiplexerOptions { StreamFactory = factory };
await using var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();  // Optional: wait for first connection
```

---

## Test Updates Required

1. Update tests from `CreateAsync()` to `Create()`
2. Update all tests to use new `Start()` + `WaitForReadyAsync()` pattern
3. Add tests for initial connection retry behavior
4. Add tests for initial connection failure scenarios
5. Add tests for `WaitForReadyAsync()` timeout/cancellation
6. Update `TestMuxHelper` to use new API

---

## Design Decisions (Confirmed)

1. **Channel operations during reconnecting:** ✅ **Await until reconnected**
   - Async waiting (not thread-blocking) for connection to restore
   - Channels will resume automatically once reconnected

2. **Handshake failure classification:** ✅ **Retry all failures**
   - Assume transient (network issues can cause partial frames)
   - No distinction between "protocol mismatch" and other failures

3. **OnAutoReconnecting for initial connection:** ✅ **Yes, unified**
   - Same event fires for initial connection attempts
   - `AttemptNumber` starts at 1 for initial connection

4. **Connection/Handshake timeout:** ✅ **Add options with infinite default**
   - `ConnectionTimeout` and `HandshakeTimeout` options added
   - Default: `Timeout.InfiniteTimeSpan` (rely on user's CancellationToken)
   - Flexible: users can set explicit timeouts if needed

---

## Implementation Phases

### Phase 1: Core Restructure
- [ ] Make `_readStream`/`_writeStream` mutable
- [ ] Keep constructor private, rename `CreateAsync()` to `Create()` (non-async)
- [ ] Implement `Start()` (non-async)
- [ ] Implement `ConnectWithRetryAsync()` unified logic
- [ ] Implement `WaitForReadyAsync()`

### Phase 2: State Management
- [ ] Add `CurrentConnectionAttempt` property
- [ ] Ensure state transitions are correct
- [ ] Add logging for state changes

### Phase 3: Channel Awaiting
- [ ] Implement async await behavior for channel operations during connecting/reconnecting
- [ ] Add ready-gate mechanism (e.g., `TaskCompletionSource` or `SemaphoreSlim`)

### Phase 4: Testing
- [ ] Update `TestMuxHelper`
- [ ] Update all existing tests
- [ ] Add new tests for initial connection scenarios

### Phase 5: Documentation
- [ ] Update XML docs
- [ ] Update README examples
