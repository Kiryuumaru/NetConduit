# Plan 071: Remove EnableReconnection Option — Zero-Overhead Always-On

## What

Remove the `EnableReconnection` option entirely. Make reconnection always-on with zero overhead by changing `ReconnectBufferSize` default from 1MB to 0. When `ReconnectBufferSize = 0`, `StartRecording()` is a no-op (existing guard: `if (_maxBufferSize <= 0) return`), so `RecordSend()` always takes the fast path (`Interlocked.Add` only — no lock, no ring buffer copy).

The ring buffer code remains intact for users who explicitly opt into replay via `ReconnectBufferSize > 0`, but the default path has zero overhead.

## Files Modified

| File | Change |
|------|--------|
| `src/NetConduit/Models/MultiplexerOptions.cs` | Remove `EnableReconnection` property, change `ReconnectBufferSize` default to 0 |
| `src/NetConduit/StreamMultiplexer.cs` | Replace 5 `EnableReconnection` checks: use `!_disposed` or `StreamFactory != null` as applicable; move `_shutdownCts.Cancel()` earlier in DisposeAsync |
| `src/NetConduit/WriteChannel.cs` | Always call `StartRecording()` in SetOpen (remove `EnableReconnection` guard) |
| `src/NetConduit.Tcp/TcpMultiplexer.cs` | Remove `EnableReconnection = false` |
| `src/NetConduit.Quic/QuicMultiplexer.cs` | Remove `EnableReconnection = false` |
| `src/NetConduit.Ipc/IpcMultiplexer.cs` | Remove `EnableReconnection = false` |
| `src/NetConduit.Udp/UdpMultiplexer.cs` | Remove `EnableReconnection = false` |
| `src/NetConduit.WebSocket/WebSocketMultiplexer.cs` | Remove `EnableReconnection = false` |
| `benchmarks/docker/netconduit-comparison/Program.cs` | Remove `EnableReconnection = false` |
| `tests/NetConduit.UnitTests/*.cs` | Remove all `EnableReconnection` settings; remove/update tests for disabled-reconnection behavior; ring buffer tests must set `ReconnectBufferSize > 0` explicitly |
| `samples/**/Program.cs` | Remove all `EnableReconnection` settings |

## Analysis

### Why this works (zero overhead)

`ChannelSyncState.StartRecording()` already has a guard:
```csharp
if (_recording || _maxBufferSize <= 0) return;
```
With `ReconnectBufferSize = 0` (passed as `maxBufferSize`), recording never activates. `RecordSend()` always takes the fast path:
```csharp
if (!_recording) return Interlocked.Add(ref _bytesSent, data.Length) - data.Length;
```
This is identical to the baseline `EnableReconnection = false` code path — zero lock, zero copy.

### Why previous plans failed

- **Plan 069**: Removed the lock from RecordSend but kept the ring buffer copy → cache thrashing from 1MB memcpy on 2-core system caused 70%+ degradation
- **Plan 070**: Removed ring buffer entirely → offset-only RecordSend matched baseline performance, but (a) didn't remove `EnableReconnection` option, (b) benchmarked with `EnableReconnection=false` (same code path as baseline)

### Key insight

The existing code already supports zero-overhead mode when `_maxBufferSize <= 0`. Plan 071 simply makes this the default and removes the `EnableReconnection` toggle.

### Reconnection still works

With `ReconnectBufferSize = 0`:
- Transport reconnection: ✅ (StreamFactory creates new connection, handshake exchanges offsets)
- Channel state preservation: ✅ (channels remain open, byte offsets tracked)
- Ring buffer replay: ❌ (no data buffered, `GetUnacknowledgedDataFrom` returns empty)
- Protocol handling: ✅ (`ReplayUnacknowledgedDataAsync` gracefully skips empty replay)

This is "connection state recovery without replay" — the same model used by Socket.IO when `maxDisconnectionDuration` expires.

### Disposal deadlock fix

Move `_shutdownCts.Cancel()` earlier in `DisposeAsync` (immediately after `_disposed = true`). This cancels the main loop's token immediately, preventing reconnection attempts during disposal.

## References

- Socket.IO Connection State Recovery: https://socket.io/docs/v4/connection-state-recovery — offset-based reconnection with bounded replay buffer
- Existing `ReconnectBufferSize = 0` support in MemoryPressureTests (line 534): "No buffering even with reconnection enabled" — proves the zero-buffer mode is already tested

## Success Criteria

All NC vs FRP and NC vs Smux ratios retain ≥95% of current `docs/benchmarks.md` baselines with reconnection always enabled, `ReconnectBufferSize = 0`.
