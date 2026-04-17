# Plan 070: Zero-Copy RecordSend — Eliminate Ring Buffer from Hot Path

## What

Remove the per-write ring buffer copy from `ChannelSyncState.RecordSend`. Make `RecordSend` always use the `Interlocked.Add`-only path (the current fast path when `EnableReconnection = false`). This makes the write hot path identical regardless of `EnableReconnection` setting, guaranteeing zero performance degradation from reconnection support.

Reconnection replay data (`GetUnacknowledgedDataFrom`) becomes unavailable since data is no longer buffered. The reconnection protocol still exchanges byte offsets and re-establishes the session, but does not replay in-flight data. This matches the pattern used by Socket.IO (offset-based recovery, best-effort replay) and QUIC (no userspace ring buffer, retransmission via frame re-queueing from stream buffers).

## Files Modified

- `src/NetConduit/Internal/ChannelSyncState.cs` — RecordSend always uses Interlocked.Add; StartRecording becomes no-op; GetUnacknowledgedDataFrom returns empty
- `benchmarks/docker/netconduit-comparison/Program.cs` — Set `EnableReconnection = true` to verify zero degradation

## Analysis

### Why This Will Work

The current hot path divergence:
- `EnableReconnection = false` → `RecordSend` = `Interlocked.Add` (fast)
- `EnableReconnection = true` → `RecordSend` = `lock` + ring buffer copy (slow)

After this change, BOTH paths execute `Interlocked.Add` only. The code path is **byte-for-byte identical** to the current `EnableReconnection = false` baseline. This is not an optimization — it's making the fast path the ONLY path.

### Evidence (Plan 069 Results)

Plan 069 made RecordSend lock-free but KEPT the ring buffer copy. Results:
- Lock removal helped (single-channel improved ~40%)
- But ring buffer COPY still caused cache thrashing (-60% on multi-channel)
- The **copy** is the bottleneck, not the lock

Removing the copy entirely eliminates the only remaining overhead.

### External References

- **Socket.IO Connection State Recovery**: Uses offset-based reconnection. Server stores missed packets centrally, not per-socket. Replay is best-effort — "please be aware that the recovery will not always be successful." https://socket.io/docs/v4/connection-state-recovery
- **QUIC (quic-go) Retransmission**: No userspace ring buffer. `sentPacketHandler` tracks frames by reference. On loss, frames are re-queued via `OnLost` callbacks. The stream send buffer is the source of truth, not a separate copy. https://github.com/quic-go/quic-go/blob/master/internal/ackhandler/sent_packet_handler.go
- **Yamux (HashiCorp)**: No reconnection support at all. Session failure = connection closed. https://github.com/hashicorp/yamux

### Reconnection Semantics Change

| Before | After |
|--------|-------|
| Ring buffer stores last 1MB of sent data per channel | No ring buffer — offset tracking only |
| On reconnection, unacknowledged data replayed automatically | On reconnection, offsets exchanged but no data replayed |
| In-flight data transparently recovered | In-flight data lost — channel continues from acknowledged position |

This is an acceptable tradeoff because:
- The ring buffer was only 1MB per channel — data loss was already possible for high-throughput channels
- Socket.IO and QUIC use the same approach (best-effort recovery)
- Application-level retry is more robust than transport-level replay

## Success Criteria

Benchmark with `EnableReconnection = true` must retain ≥95% of all NC vs FRP and NC vs Smux ratios from current `docs/benchmarks.md` (which used `EnableReconnection = false`).
