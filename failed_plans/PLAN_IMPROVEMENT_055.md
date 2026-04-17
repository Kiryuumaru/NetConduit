# Plan 055: Set TCP Socket Buffer Sizes to 2MB

## Change

Set `SendBufferSize` and `ReceiveBufferSize` to 2MB (2,097,152 bytes) on all `TcpClient` instances in `TcpMultiplexer.cs`.

## Files Modified

- `src/NetConduit.Tcp/TcpMultiplexer.cs`

## Analysis

### Evidence

System TCP buffer defaults (from `sysctl`):
- `net.ipv4.tcp_wmem` = 4096 / **16384** / 4194304 (min/default/max)
- `net.ipv4.tcp_rmem` = 4096 / **131072** / 6291456

The initial TCP send buffer is only **16KB**. For 64KB frames, the sender exhausts the buffer in a single frame write and must wait for the kernel to drain data to the receiver before continuing.

### Why It Should Help

For large transfers (64KB frames typical in bulk throughput):
1. With 16KB send buffer: each 64KB socket write requires ~4 kernel roundtrips to drain the buffer
2. With 2MB send buffer: 64KB fits entirely, completing in one kernel operation
3. For 1ch×1MB (16 × 64KB frames): eliminates ~48 kernel roundtrips (16 frames × 3 extra roundtrips each)

The Go muxes (yamux, smux) face the same 16KB initial buffer but their goroutine + channel architecture amortizes the blocking differently (sendCh buffers up to 64 frames independently of socket buffer).

### Auto-Tuning Consideration

Setting explicit `SO_SNDBUF` disables Linux TCP auto-tuning. For the benchmark (short-lived connections, CPU-pinned), auto-tuning has no time to ramp up from the 16KB initial value. Explicit 2MB is superior. For production long-lived connections, 2MB is a reasonable default for multiplexed traffic.

### Game-Tick Safety

Game-tick messages are 64-256 bytes. These never fill even the smallest socket buffer. No impact on game-tick performance.

### References

- Linux TCP buffer documentation: `man 7 tcp` (tcp_wmem, tcp_rmem)
- Go net.Conn also uses system defaults (no explicit buffer sizing in yamux/smux)
- Neither Go benchmarks nor .NET benchmarks currently configure socket buffers

## Success Criteria

- Bulk throughput ratios improved (NC vs FRP closer to 1.0x for 100KB/1MB scenarios)
- Game-tick ratios maintained (NC vs FRP 1ch×64B > 10x, 50ch×64B within 5% of current)
- All tests pass (532/532)
