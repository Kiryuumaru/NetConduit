# Plan 069: Lock-Free Reconnection Recording with ArrayPool

## What

Remove the `EnableReconnection` option entirely. Make reconnection always-on by eliminating the performance bottleneck in `ChannelSyncState.RecordSend()`.

Three changes:
1. **Remove lock from `RecordSend()`** — safe because `WriteChannel._writeActive` already serializes all writes per channel, and ring buffer readers (`Acknowledge`, `GetUnacknowledgedDataFrom`) only run during reconnection when writes are paused
2. **Use `ArrayPool<byte>.Shared` for ring buffer** — avoids 1MB LOH allocation per channel that triggers Gen 2 GC
3. **Pre-allocate ring buffer in `StartRecording()`** — moves allocation out of the `RecordSend()` hot path
4. **Remove `EnableReconnection` option** — always call `StartRecording()` in `SetOpen()`

## Files Modified

| File | Change |
|------|--------|
| `src/NetConduit/Internal/ChannelSyncState.cs` | Remove lock from RecordSend, ArrayPool ring buffer, pre-allocate in StartRecording |
| `src/NetConduit/Models/MultiplexerOptions.cs` | Remove `EnableReconnection` property |
| `src/NetConduit/WriteChannel.cs` | Remove `if (EnableReconnection)` guard in SetOpen |
| `src/NetConduit/StreamMultiplexer.cs` | Remove all `EnableReconnection` checks |
| `benchmarks/docker/netconduit-comparison/Program.cs` | Remove `EnableReconnection = false` from CreateClientOptions |

## Analysis

### Why the lock is unnecessary

`RecordSend()` is called from `WriteChannel.WriteAsync()` which acquires `_writeActive` spinlock (line 145). This guarantees single-threaded access to `RecordSend` per channel.

The only other accessors of ring buffer state:
- `Acknowledge()` / `SetBytesAcked()` — called only during reconnection (StreamMultiplexer.cs lines 1070, 2106)
- `GetUnacknowledgedDataFrom()` — called only during `ReplayUnacknowledgedDataAsync` (reconnection)

During reconnection, the connection is down and writes are paused. No concurrent access is possible.

### Why ArrayPool eliminates GC pressure

`new byte[1_048_576]` (1MB) goes on the LOH (≥85KB threshold). LOH allocations trigger Gen 2 collection, which collects the entire managed heap. Per Adam Sitnik's benchmarks (https://adamsitnik.com/Array-Pool/):
- `new byte[1_000_000]`: 3,637ns + Gen 2 GC + 100KB LOH pressure
- `ArrayPool<byte>.Shared.Rent(1_000_000)`: ~44ns, 0B allocation

For 100-channel benchmarks, that's 100 × 1MB = 100MB of LOH allocations avoided.

`ArrayPool<byte>.Shared` max array length is 2^20 = 1,048,576 = exactly the default `ReconnectBufferSize`.

### Why pre-allocation helps

Current code lazy-allocates inside `RecordSend()` (hot path). Moving allocation to `StartRecording()` (called once during channel open) removes the branch and allocation from every write.

## References

- Adam Sitnik: "Pooling large arrays with ArrayPool" — https://adamsitnik.com/Array-Pool/
- Steven Giesel: "An asynchronous lock free ring buffer for logging" — https://steven-giesel.com/blogPost/11f0ded8-7119-4cfc-b7cf-317ff73fb671
- .NET ArrayPool source: thread-local cache + shared pool, O(1) rent/return for standard sizes

## Success Criteria

All NC vs FRP and NC vs Smux ratios retain ≥95% of current `docs/benchmarks.md` baselines with reconnection always enabled.
