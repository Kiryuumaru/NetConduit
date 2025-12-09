# Data Integrity Plan: Credit-Based Selective Repeat with Frame Integrity

## Overview

This document outlines the implementation plan for adding frame-level integrity checking and reliable delivery to NetConduit's multiplexer protocol. The design adds CRC32 checksums, per-channel sequence numbers, and NACK/resend logic.

## Status

**Phase 1: COMPLETED ✅** - Basic ARQ with frame-count-based resend buffer
**Phase 2: COMPLETED ✅** - Byte-based resend buffer aligned with MaxCredits (guaranteed safe)
**Phase 3: COMPLETED ✅** - Timeout-based retransmit for unreliable transports (UDP)

---

## Phase 3: Timeout-Based Retransmit for Unreliable Transports ✅

### Problem

The existing NACK-based ARQ relies on CRC failure detection to trigger retransmission. This works for **corruption** but not for **packet loss** (silent drops), which is common in UDP and other unreliable transports.

### Solution

Add timeout-based retransmission that proactively resends frames when no acknowledgment is received within a configurable timeout period.

### Configuration Options (MultiplexerOptions)

| Option | Default | Description |
|--------|---------|-------------|
| `EnableTimeoutRetransmit` | `false` | Enable timeout-based retransmission (auto-enabled for UDP) |
| `RetransmitTimeout` | `200ms` | Time to wait before retransmitting an unacknowledged frame |
| `RetransmitCheckInterval` | `50ms` | How often to check for frames needing retransmission |
| `MaxRetransmitAttempts` | `10` | Max retries before closing channel with error |

### Implementation Details

#### 1. ResendEntry Struct (WriteChannel)

```csharp
internal struct ResendEntry
{
    public byte[] Data;
    public long SentAtTicks;      // Environment.TickCount64 when sent
    public int RetransmitCount;   // Number of retransmit attempts
}
```

#### 2. Centralized Retransmit Loop (StreamMultiplexer)

```csharp
private async Task RetransmitLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(_options.RetransmitCheckInterval, ct);
        
        foreach (var channel in _writeChannelsByIndex.Values)
        {
            var shouldClose = await channel.CheckAndRetransmitAsync(ct);
            if (shouldClose)
            {
                channel.SetError(ErrorCode.Timeout, "Max retransmit attempts exceeded");
            }
        }
    }
}
```

#### 3. Per-Channel Timeout Check (WriteChannel)

```csharp
internal async ValueTask<bool> CheckAndRetransmitAsync(CancellationToken ct)
{
    foreach (var (seq, entry) in _resendBuffer)
    {
        if (TimedOut(entry) && entry.RetransmitCount < _maxRetransmitAttempts)
        {
            // Update timestamp and count, then retransmit
            await _multiplexer.SendDataFrameAsync(ChannelIndex, seq, entry.Data, Priority, ct);
            Stats.IncrementRetransmissions();
        }
        else if (entry.RetransmitCount >= _maxRetransmitAttempts)
        {
            return true; // Signal to close channel
        }
    }
    return false;
}
```

### UDP Auto-Enable

`UdpMultiplexer` automatically sets `EnableTimeoutRetransmit = true` when creating multiplexers, ensuring UDP connections have proper loss recovery without manual configuration.

### Performance Considerations

1. **Centralized timer**: Single timer loop for all channels (vs per-channel timers) reduces overhead
2. **Configurable intervals**: Adjust `RetransmitCheckInterval` for latency vs CPU tradeoff
3. **Exponential backoff**: RetransmitCount enables future backoff strategies
4. **Lock-free checks**: Uses `ConcurrentDictionary` iteration without blocking writes

### Files Modified

| File | Changes |
|------|---------|
| `MultiplexerOptions.cs` | Added 4 timeout-retransmit config options |
| `WriteChannel.cs` | `ResendEntry` struct, `CheckAndRetransmitAsync` method, timestamp tracking |
| `StreamMultiplexer.cs` | `RetransmitLoopAsync` task, started in `RunAsync` when enabled |
| `UdpMultiplexer.cs` | Auto-enables timeout retransmit for UDP connections |
| `DataIntegrityTests.cs` | Tests for timeout-based recovery under packet drops |

---

## Phase 2: Byte-Based Resend Buffer (Guaranteed Safety) ✅

### Problem with Current Implementation

Current resend buffer uses **frame count** (default 1024 frames), but credits use **bytes** (default 4MB):

```
MaxCredits = 4MB (bytes)
ResendBufferSize = 1024 (frames) ← different unit!

Worst case: 4MB ÷ 1-byte frames = 4M frames needed
            But buffer only holds 1024 → data loss risk
```

### Solution: Align Units

Change resend buffer from **frame count** to **bytes**, and derive size from `MaxCredits`:

```
MaxCredits = 4MB (bytes)
ResendBuffer = 4MB (bytes) ← same unit, auto-derived from MaxCredits

Guarantee: Every byte in-flight is ALWAYS in the resend buffer
           Every frame can ALWAYS be retransmitted on NACK
           IMPOSSIBLE to overflow buffer
```

### Why This Is Guaranteed Safe

```
SENDER                              RECEIVER
   │                                    │
   │ ── Send 1MB frame ──────────────►  │  Resend buffer: 1MB used
   │    (credits: 4MB → 3MB)            │
   │                                    │
   │ ── Send 2MB frame ──────────────►  │  Resend buffer: 3MB used  
   │    (credits: 3MB → 1MB)            │
   │                                    │
   │ ── Send 1MB frame ──────────────►  │  Resend buffer: 4MB used (FULL)
   │    (credits: 1MB → 0MB)            │
   │                                    │
   │ ── [BLOCKED - no credits] ──       │  Can't send more, can't overflow!
   │                                    │
   │ ◄── CREDIT_GRANT (ack_seq=2) ────  │  Receiver confirms frames 0,1,2
   │    (credits: 0MB → 4MB)            │
   │                                    │
   │    Resend buffer: frees 4MB → 0MB  │  Buffer cleared, ready for more
```

### Implementation Changes

#### 1. Remove `ResendBufferSize` from `MultiplexerOptions.cs`

```csharp
// REMOVE this option - no longer needed
// public int ResendBufferSize { get; init; } = 1024;
```

#### 2. Update `WriteChannel.cs`

```csharp
// BEFORE (frame count):
private readonly int _maxResendBufferSize;  // frames
private readonly ConcurrentDictionary<uint, byte[]> _resendBuffer;

// AFTER (byte count, derived from MaxCredits):
private readonly long _maxResendBufferBytes;  // bytes = MaxCredits
private readonly ConcurrentDictionary<uint, byte[]> _resendBuffer;
private long _resendBufferBytes;  // current bytes in buffer
```

Constructor change:
```csharp
// BEFORE:
_maxResendBufferSize = multiplexer.Options.ResendBufferSize;

// AFTER:
_maxResendBufferBytes = options.MaxCredits;  // Auto-aligned!
_resendBufferBytes = 0;
```

Buffer management change:
```csharp
// BEFORE:
private void StoreInResendBuffer(uint seq, byte[] data)
{
    _resendBuffer[seq] = data;
    while (_resendBuffer.Count > _maxResendBufferSize) { /* evict oldest */ }
}

// AFTER:
private void StoreInResendBuffer(uint seq, byte[] data)
{
    _resendBuffer[seq] = data;
    Interlocked.Add(ref _resendBufferBytes, data.Length);
    // No eviction needed - credit system guarantees we won't exceed MaxCredits!
}
```

ACK handling change:
```csharp
internal void HandleAckSeq(uint ackSeq)
{
    foreach (var kvp in _resendBuffer)
    {
        if (IsSeqAcked(kvp.Key, ackSeq))
        {
            if (_resendBuffer.TryRemove(kvp.Key, out var data))
            {
                Interlocked.Add(ref _resendBufferBytes, -data.Length);
            }
        }
    }
    _ackedSeq = ackSeq;
}
```

#### 3. Update `README.md`

Remove `ResendBufferSize` from options table - users don't need to configure it.

#### 4. Update Tests

Verify that:
- No buffer overflow under any frame size pattern
- All tests still pass without `ResendBufferSize` config

### Benefits

| Aspect | Before (Phase 1) | After (Phase 2) |
|--------|------------------|-----------------|
| Config options | 2 (`MaxCredits` + `ResendBufferSize`) | 1 (`MaxCredits` only) |
| User calculation | Required | None |
| Risk of misconfiguration | Yes | No |
| Guaranteed retransmit | Only if sized correctly | **Always** |
| Memory usage | Can under/over allocate | Matches credits exactly |

### Files to Modify

| File | Changes |
|------|---------|
| `src/NetConduit/Models/MultiplexerOptions.cs` | Remove `ResendBufferSize` property |
| `src/NetConduit/WriteChannel.cs` | Change from frame-count to byte-count buffer |
| `README.md` | Remove `ResendBufferSize` from options table |
| Tests | Remove `ResendBufferSize` configuration |

---

## Wire Format (Implemented ✅)

### Frame Header (17 bytes, was 9 bytes)

```
┌─────────────────┬───────────┬─────────────┬──────────────┬─────────────┐
│ channel_index   │   flags   │     seq     │    length    │    crc32    │
│    (4B BE)      │   (1B)    │   (4B BE)   │   (4B BE)    │    (4B)     │
└─────────────────┴───────────┴─────────────┴──────────────┴─────────────┘
```

- **channel_index**: 4 bytes, big-endian - Channel identifier
- **flags**: 1 byte - Frame type flags (DATA=0x01, CONTROL=0x02, etc.)
- **seq**: 4 bytes, big-endian - Per-channel sequence number (wraps at 2³²)
- **length**: 4 bytes, big-endian - Payload length
- **crc32**: 4 bytes - CRC32 of entire frame (header fields + payload, with crc32 field zeroed during computation)

### NACK Control Frame

```
Control Frame Payload:
┌──────────────┬─────────────────┬─────────────┐
│   subtype    │  channel_index  │     seq     │
│  (1B = 0x09) │     (4B BE)     │   (4B BE)   │
└──────────────┴─────────────────┴─────────────┘
```

### Extended CREDIT_GRANT Payload

```
┌─────────────────┬─────────────┬─────────────┐
│  channel_index  │   credits   │   ack_seq   │
│    (4B BE)      │   (4B BE)   │   (4B BE)   │
└─────────────────┴─────────────┴─────────────┘
```

- **ack_seq**: Highest contiguous sequence number successfully received (enables sender to free resend buffer)

## Behavior (Implemented ✅)

### Sender (WriteChannel)

1. **Sequence Assignment**: Assign monotonically increasing `seq` to each DATA frame on this channel
2. **CRC Computation**: Compute CRC32 over `[channel_index, flags, seq, length, payload]` (crc32 field = 0 during computation)
3. **Resend Buffer**: Hold sent frames in buffer until acknowledged via `ack_seq` in CREDIT_GRANT
4. **NACK Handling**: On receiving NACK(channel, seq), retransmit the frame from resend buffer
5. **Buffer Management**: When `ack_seq` received, free all frames with seq ≤ ack_seq

### Receiver (ReadChannel)

1. **CRC Validation**: Verify CRC32 on arrival; if mismatch, send NACK(channel, seq) and drop frame
2. **Sequence Validation**: Check if seq is expected (next in order)
3. **Reorder Buffer**: If seq > expected, buffer the frame (out-of-order)
4. **Duplicate Detection**: If seq < expected, drop (duplicate/late retransmit)
5. **Delivery**: Deliver frames in order to application; update expected_seq
6. **CREDIT_GRANT**: Include `ack_seq` = highest contiguous seq delivered

### StreamMultiplexer

1. **NACK Dispatch**: Route incoming NACK to appropriate WriteChannel for retransmission
2. **CREDIT_GRANT Parsing**: Extract `ack_seq` and forward to WriteChannel for buffer cleanup

## Algorithm: Credit-Based Selective Repeat

This implementation leverages the existing credit-based flow control:

1. **Credits = Window Size**: Sender can have up to `credit` unacknowledged bytes in flight
2. **Resend Buffer = MaxCredits**: Buffer sized to hold all in-flight data (guaranteed retransmit)
3. **Selective ACK**: CREDIT_GRANT with ack_seq acknowledges all frames up to that seq
4. **Selective Retransmit**: NACK requests specific frame retransmission (not Go-Back-N)
5. **No Timeout Retransmit**: Rely on receiver NACK for corruption; TCP/transport handles loss

## Constants

```csharp
public const int FrameHeaderSize = 17;  // was 9
// ResendBufferSize auto-derived from MaxCredits - no separate config needed
public const int ReorderBufferTimeout = 5000;  // ms, for stuck out-of-order frames
```

## Success Criteria

1. ✅ Corruption test passes (corrupted frames trigger NACK → retransmit → data intact)
2. ✅ All existing tests continue to pass
3. ✅ Benchmarks show acceptable overhead
4. ✅ No deadlocks under credit exhaustion + NACK storms
5. **Phase 2**: No data loss possible under any frame size pattern (guaranteed by design)
