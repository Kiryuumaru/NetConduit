# NetConduit Reborn — Zero-Copy Multiplexer Design

A ground-up redesign that eliminates every avoidable `memcpy` between user's `WriteAsync`
and the kernel socket buffer. The mux is a **dumb frame router**. Channels own their data.

---

## Design Philosophy

```
"The fastest byte is the one you never copy."
```

**Core principle**: The user writes bytes directly into a channel's pre-allocated slab.
The channel stamps the frame header in front of that data (header space is reserved).
The mux just picks up the ready frame and sends it. **One copy total: user → kernel.**

**Amazon analogy**: Channels are sellers — they do ALL the packing and addressing.
The mux is Amazon's logistics — it just routes packages to the correct destination.
With 1000 channels, 1000 threads can stamp headers concurrently. The mux hot path
is trivial: pick next ready frame, send it, move on.

**Why Go muxes are fast**: Smux/Yamux use a single mutex + single `Write()` call.
No queues, no pipes, no intermediate buffers. Just: lock → write header → write payload → unlock.
~12% overhead over raw TCP.

**Why current NetConduit is slow**: 4-5 copies per frame (user → ArrayPool → chunk queue →
drain → ring buffer → transport). Each copy adds ~20µs per 64KB frame at memory bandwidth.

**Target**: Match or beat Go mux overhead ratio. ≤5% overhead over raw .NET stream for bulk.

---

## Research Findings

### Scatter-Gather in .NET (dotnet/runtime#36951)

| Approach | Linux Performance | Windows Performance |
|----------|-------------------|---------------------|
| Single contiguous buffer | **Fastest** (baseline) | Fastest |
| IList\<ArraySegment\> (BufferList) | 2-3x slower | ~Same as single |
| Sequential small writes | 10-50x slower | 10x slower |

**Conclusion**: On Linux (primary deployment), **scatter-gather is worse than copying into
a single buffer**. The kernel allocates IOVector + GCHandles arrays per call.
Therefore: write everything contiguously, send as single `ReadOnlyMemory<byte>`.

### Why Smux Achieves 2246 MB/s (44% of raw TCP's 5124 MB/s)

1. **8-byte header** — minimal framing overhead
2. **Slab allocator** — 17 sync.Pool buckets, power-of-two sizes, constant-time selection
3. **No per-frame allocation** — reuse buffers without zeroing
4. **Shared receive buffer** — session-level, not per-stream
5. **Single write path** — goroutine holds mutex, writes header+payload, releases

### .NET-Specific Advantages We Can Exploit

| Feature | Benefit |
|---------|---------|
| `Memory<byte>` / `Span<byte>` | Zero-copy slicing of pre-allocated buffers |
| `IMemoryOwner<byte>` | Lifetime-tracked buffer ownership |
| `Socket.SendAsync(ReadOnlyMemory<byte>)` | Direct kernel send from managed memory |
| `PipeWriter.GetMemory()` | Write directly into transport buffer |
| `ReadOnlySequence<byte>` | Zero-copy view of non-contiguous received data |
| `MemoryMarshal.Write` | Write header structs without allocation |
| `stackalloc` | Small scratch buffers without heap alloc |

---

## Architecture: Channels Do Heavy Lifting, Mux Just Routes

```
┌──────────────────────────────────────────────────────────────┐
│                         USER CODE                             │
└──────────────┬───────────────────────────────────┬───────────┘
               │ WriteAsync(data)                   │ ReadAsync(buf)
               ▼                                   ▼
┌──────────────────────────┐       ┌──────────────────────────┐
│       WRITE CHANNEL       │       │       READ CHANNEL        │
│  (THE HEAVY LIFTER)       │       │  (THE HEAVY LIFTER)       │
│                          │       │                          │
│  • Owns send slab        │       │  • Owns receive slab     │
│  • Stamps frame headers  │       │  • Strips frame headers  │
│  • Builds complete frames│       │  • Tracks: received,     │
│  • Tracks: pending,      │       │    consumed, ack-pending  │
│    inflight, acked       │       │  • Generates ACK frames  │
│  • Handles reconnection  │       │  • Handles reconnection  │
│    replay from slab      │       │    (tells sender where   │
│  • Generates credit/ctrl │       │    to resume)            │
│    frames into own slab  │       │  • Direct delivery       │
└──────────────┬───────────┘       └──────────────▲───────────┘
               │ Ready frame (Memory<byte>)        │ Raw payload
               │ (header already stamped)          │ (header stripped)
               ▼                                   │
┌──────────────────────────────────────────────────────────────┐
│                     MULTIPLEXER (ROUTER)                      │
│                                                              │
│  SEND: Pick next ready channel → send its Memory → done     │
│  RECV: Read 8-byte header → route payload to channel         │
│  RECONN: Re-establish transport, tell channels to replay     │
│                                                              │
│  Does NOT: stamp headers, build frames, manage credits,      │
│            buffer data, track positions, handle flow control  │
│                                                              │
│  Only logic: priority ordering of which channel goes next    │
└──────────────┬───────────────────────────────────▲───────────┘
               │ WriteAsync(memory)                 │ ReadAsync
               ▼                                   │
┌──────────────────────────────────────────────────────────────┐
│                        TRANSPORT                             │
│  TCP / UDP / IPC / QUIC / WebSocket                          │
│  Provides Stream or IStreamPair                              │
└──────────────────────────────────────────────────────────────┘
```

### Why Channels Do the Work (Not the Mux)

| Concern | Who Handles | Why |
|---------|-------------|-----|
| Frame header stamping | **Channel** | 1000 channels stamp concurrently on their own threads |
| Flow control / credits | **Channel** | Each channel knows its own slab state |
| Reconnection replay | **Channel** | Each channel replays from its own slab |
| ACK generation | **Channel** | ReadChannel knows what it consumed |
| Backpressure | **Channel** | Slab full = block, no mux involvement |
| Priority decision | **Mux** | Only the mux sees all channels, picks order |
| Transport write | **Mux** | Single writer to avoid contention |
| Transport read + route | **Mux** | Single reader, routes by channel ID |
| Reconnect trigger | **Mux** | Mux owns the transport connection |

**Result**: The mux hot path is ~10 lines of code:
```csharp
while (!ct.IsCancellationRequested)
{
    var channel = GetNextReadyChannel();     // priority queue
    Memory<byte> frame = channel.TakeReady(); // pre-built frame from slab
    await transport.WriteAsync(frame, ct);    // send it
    channel.MarkSent(frame.Length);           // advance position
}
```

All the "thinking" (framing, credits, retransmission) happens in channels, in parallel,
on the user's thread. The mux writer thread does zero computation — just I/O.

---

### Zero-Copy Write Path (Channel Does Everything)

```csharp
// User calls: channel.WriteAsync(userData)
// Channel does ALL of this on the CALLER'S THREAD (concurrent with other channels):

// 1. Reserve space: header (8 bytes) + payload
int frameStart = _writePos;
int headerEnd = frameStart + FrameHeader.Size;
int frameEnd = headerEnd + userData.Length;

// 2. User data lands directly in slab (SINGLE COPY — user buffer → slab)
userData.CopyTo(_slab.AsSpan(headerEnd, userData.Length));

// 3. Channel stamps header IN PLACE (no copy — write struct directly to slab)
//    This is the "packing and addressing" step — channel knows its own ID
FrameHeader.WriteTo(_slab.AsSpan(frameStart), _channelIndex, FrameFlags.Data, userData.Length);

// 4. Advance pending position (atomic) — frame is now COMPLETE and READY
Interlocked.Exchange(ref _pendingPos, frameEnd);

// 5. Signal mux: "I have a ready frame" (just a flag, no data copy)
_router.NotifyReady(this);
```

**Total copies on write path: 1** (user buffer → slab). Header is written in-place.
**Concurrency**: 1000 channels can execute steps 1-4 simultaneously on different threads.
The mux is not involved until step 5 (and step 5 is just setting a flag).

### Zero-Copy Send (Mux Just Routes Pre-Built Frames)

```csharp
// Mux writer thread — the entire hot path:

// 1. Get READY frame from channel's slab (already has header stamped)
//    This is just a Memory<byte> slice — no copy, no processing
Memory<byte> readyFrame = channel.TakeReady();
// Returns: slab.AsMemory(SentPos, PendingPos - SentPos)
// The bytes in this region are COMPLETE frames: [HDR|PAYLOAD][HDR|PAYLOAD]...

// 2. Send directly to transport (kernel copies from pinned slab → NIC)
await transport.WriteAsync(readyFrame, ct);

// 3. Tell channel "these bytes are now inflight"
channel.MarkSent(readyFrame.Length);
```

**Total copies on send: 0** in user-space. The kernel does DMA from the pinned slab.
**Mux does zero computation** — no header inspection, no framing, no byte manipulation.
It's equivalent to: `send(channel.GetPointer(), channel.GetLength())`.

### Comparison with Current NetConduit

| Step | Current (4-5 copies) | Reborn (1 copy) |
|------|---------------------|-----------------|
| User → buffer | ArrayPool.Rent + memcpy | memcpy to slab |
| Header stamp | Write to rented buffer | Write to slab (in-place) |
| Queue | ConcurrentQueue.Enqueue | Just advance WritePos (atomic int) |
| Drain | Dequeue + WriteAsync | Slice slab.AsMemory (zero-copy) |
| Ring buffer | memcpy entire frame | Slab IS the ring buffer |
| Transport | WriteAsync(chunk) | WriteAsync(slabSlice) |

---

## The Slab IS the Reconnection Buffer

**Key insight**: The slab already holds all inflight data (between `AckedPos` and `WritePos`).
On reconnect, replay = just re-send `slab[AckedPos..WritePos]`. No separate ring buffer needed.

```
On disconnect:
  - SentPos resets to AckedPos (inflight data wasn't confirmed)
  - PendingPos resets to AckedPos (re-send everything unacked)

On reconnect:
  - Mux sends slab[AckedPos..WritePos] as replay
  - This is the SAME data, SAME memory — zero additional copies
```

The slab size determines how much data can be "in flight + pending replay."
If the slab fills up (user writes faster than acks come back), the channel
applies backpressure (blocks WriteAsync until AckedPos advances).

---

## Wire Protocol

### Frame Header (8 bytes — matches Smux for minimal overhead)

```
┌──────────┬──────────┬──────────────────────────────┐
│ CHANNEL  │  FLAGS   │          LENGTH              │
│  (2B BE) │  (1B)    │          (4B BE)             │
│  + 1 rsvd│          │                              │
└──────────┴──────────┴──────────────────────────────┘
Total: 8 bytes (1 byte saved vs current 9-byte header)
```

**Why 2-byte channel ID**: Supports 65535 channels. Current NetConduit uses 4 bytes for
channel INDEX (not ID) but typical usage is <1000 channels. 2 bytes is enough.
If needed, a flag can extend to 4 bytes for the rare case.

**Alternative**: Keep 9-byte header for compatibility. The 1 extra byte is negligible
at 64KB payloads. Decision: **keep 8 bytes** for new design (clean break).

### Frame Flags (1 byte)

| Bit | Name | Purpose |
|-----|------|---------|
| 0 | DATA | Regular data frame |
| 1 | INIT | Open channel (payload = channel name UTF-8) |
| 2 | FIN | Close channel gracefully |
| 3 | ACK | Acknowledge received bytes (payload = 4B position) |
| 4 | ERR | Error (payload = error code + message) |
| 5 | PING | Keepalive request (payload = 8B timestamp) |
| 6 | PONG | Keepalive response |
| 7 | CTRL | Control subframe (reconnect, go-away, etc.) |

### Channel 0 = Control Channel

Same concept as current. Ping/Pong, Handshake, GoAway, Reconnect all go through channel 0.

---

## Read Path: Direct Delivery + Slab

The mux reader thread reads 8-byte headers from the transport, looks up the channel,
and hands off the payload. The channel decides what to do:

- **Fast path (direct delivery)**: User is already waiting in `ReadAsync` → copy directly
  from transport buffer to user's buffer. 1 copy total.
- **Slow path (slab buffered)**: User hasn't called `ReadAsync` yet → copy to channel's
  receive slab. User reads from slab later. 2 copies total.

```csharp
struct ReceiveSlabState
{
    int ReceivedPos;   // total bytes buffered
    int ConsumedPos;   // bytes read by user
    int AckSentPos;    // position we've told sender about (for credits)
}
```

Credits = `ConsumedPos - AckSentPos` (how much buffer space we freed since last ack).

---

## Multiplexer: The Dumb Router

The mux does **three things only**:

1. **Writer thread**: Pick next ready channel (by priority), send its pre-built frames
2. **Reader thread**: Read 8-byte header, route payload bytes to target channel
3. **Reconnect**: On transport failure, re-establish connection, tell channels to replay

### Send Path (Mux Writer)

```csharp
// Writer thread — entire implementation:
while (!ct.IsCancellationRequested)
{
    await _readySignal.WaitAsync(ct);  // channels signal when they have frames ready

    // Priority-ordered: high-priority channels get sent first
    while (TryGetNextReadyChannel(out var channel))
    {
        // Channel already built the frames (header + payload in its slab)
        // Mux just takes the ready region and sends it. Zero processing.
        Memory<byte> frames = channel.TakeReady();
        if (frames.IsEmpty) continue;

        await transport.WriteAsync(frames, ct);
        channel.MarkSent(frames.Length);
    }

    await transport.FlushAsync(ct);
}
```

### Receive Path (Mux Reader)

```csharp
// Reader thread — read header, route to channel:
while (!ct.IsCancellationRequested)
{
    // 1. Read exactly 8 bytes (frame header)
    await ReadExactAsync(headerBuf, 8, ct);
    var header = FrameHeader.Parse(headerBuf);

    // 2. Route: lookup channel by index
    var channel = _registry[header.ChannelIndex];

    // 3. Hand off payload bytes to channel (channel handles the rest)
    //    Channel decides: direct deliver to user? Buffer in slab? Process as ACK?
    await channel.ReceivePayloadAsync(transport, header, ct);
}
```

The mux reader does NOT interpret the payload. It reads the routing header (8 bytes),
finds the destination channel, and lets the channel read its own payload from the
transport stream. The channel knows whether this is data, an ACK, or a control frame.

### What the Mux Does NOT Do

| NOT mux's job | Who does it |
|---------------|-------------|
| Stamp frame headers | WriteChannel (on user's thread) |
| Parse frame payload | ReadChannel (on its own logic) |
| Manage credits/ACKs | Channel (sender + receiver pair) |
| Buffer data | Channel (in its slab) |
| Track positions | Channel (AckedPos, SentPos, etc.) |
| Replay on reconnect | Channel (re-sends from its slab) |
| Decide frame size/chunking | Channel (knows its own max frame) |

**No locks on the write path** (each channel's slab is independent). The single writer
thread means no transport lock needed — only one thing writes to transport at a time.

**No queues** — channels don't "enqueue frames." They build frames in their slab
and advance `PendingPos`. The writer thread just reads the ready region directly.

---

## Flow Control: Slab-Based Backpressure

### Natural Backpressure

The slab itself IS the flow control mechanism:

- **Sender side**: If `WritePos` catches up to `AckedPos` (slab full), `WriteAsync` blocks.
  This is natural backpressure without a separate credit system.
- **Receiver side**: If receive slab fills up (user not reading), stop reading from transport.
  This applies TCP-level backpressure (receive window closes).

### Explicit Credits (Optional, for Fairness)

For multi-channel fairness, the receiver can send ACK frames with consumed position.
The sender advances `AckedPos` on receipt, freeing slab space.

```
Sender:                          Receiver:
  WritePos advances ─────────►  Receive slab fills
  Slab fills → block            ◄───────── ACK(position)
  AckedPos advances             ConsumedPos advances (user read)
  WritePos can advance again
```

**Key difference from current**: No per-frame credit check. Just: "is there slab space?"
Credits are coarse-grained (acknowledge N bytes at once, not per-frame).

---

## Reconnection: Channel-Owned, Not Mux-Owned

### Current Problem

MuxRingBuffer records ALL bytes (all channels mixed) → memcpy on every drain.
On reconnect, replays the entire ring → receiver must demux again.

### Reborn Approach

Each channel's slab already holds its unacked data. On reconnect:

1. Mux re-establishes transport connection
2. Mux sends handshake with session ID
3. Each channel re-sends its unacked region: `slab[AckedPos..WritePos]`
4. Receiver tells sender: "I last received up to position X" → sender adjusts AckedPos

**Benefits**:
- No separate ring buffer allocation (slab serves dual purpose)
- No memcpy on the hot path (no recording)
- Replay is per-channel (only unacked channels replay, not everything)
- Receiver can skip already-received data frame-by-frame

### Reconnection Protocol

```
CLIENT                              SERVER
  │                                   │
  │──── RECONNECT(sessionId) ────────►│
  │                                   │
  │◄─── RECONNECT_ACK ──────────────│
  │     (per-channel: lastRecvPos)    │
  │                                   │
  │  (client adjusts AckedPos per ch) │
  │                                   │
  │──── REPLAY ch1[acked..write] ───►│
  │──── REPLAY ch2[acked..write] ───►│
  │                                   │
  │◄─── REPLAY ch3[acked..write] ───│  (server replays its sends too)
  │                                   │
  │──── NORMAL TRAFFIC ──────────────►│
```

---

## Memory Layout: Why Pinned Slabs

### GC.AllocateArray with Pinned

```csharp
byte[] slab = GC.AllocateArray<byte>(slabSize, pinned: true);
```

**Why pinned**:
- `Socket.SendAsync(Memory<byte>)` pins the buffer during I/O anyway
- Pre-pinning avoids repeated pin/unpin per write (saves ~50ns per I/O op)
- The slab lives for the channel lifetime — no GC pressure from pinning
- Enables potential future `OVERLAPPED` / io_uring zero-copy send

### Slab Sizing

| Use Case | Suggested Slab Size | Rationale |
|----------|--------------------:|-----------|
| Game-tick (64B msgs) | 64 KB | Holds ~900 frames inflight |
| Bulk transfer (64KB msgs) | 4 MB | Holds ~60 frames inflight |
| High-latency links | 16 MB | More inflight for bandwidth-delay product |

Default: **1 MB** per channel (configurable via `ChannelOptions.SlabSize`).

---

## Channel Implementation

### WriteChannel — The Full Self-Contained Sender

The channel does ALL heavy lifting: framing, header stamping, credit management,
reconnection replay. The mux never touches channel data.

```csharp
public sealed class WriteChannel : IAsyncDisposable
{
    // The slab — owns all memory for this channel's sends
    private readonly byte[] _slab;             // pinned
    private readonly Memory<byte> _slabMemory; // wraps _slab
    private readonly ushort _channelIndex;     // my address (stamped into every frame)

    // Position tracking (all volatile/interlocked)
    private int _ackedPos;    // advanced by ACK frames from receiver
    private int _sentPos;     // advanced by mux writer thread
    private int _pendingPos;  // frames ready for mux to pick up
    private int _writePos;    // where next frame is being assembled

    // Signaling
    private readonly ManualResetValueTaskSourceCore<bool> _spaceAvailable;
    private readonly IFrameRouter _router;  // reference to mux (for signaling only)

    public ValueTask WriteAsync(ReadOnlySpan<byte> data, CancellationToken ct = default)
    {
        // 1. Wait for slab space if needed
        int needed = FrameHeader.Size + data.Length;
        int available = GetFreeSpace();
        if (available < needed)
            return WriteAsyncSlow(data, ct);  // await space

        // 2. BUILD THE COMPLETE FRAME (header + payload) in slab
        //    This happens on the USER'S THREAD — concurrent with other channels
        int frameStart = _writePos;
        FrameHeader.WriteTo(_slab.AsSpan(frameStart), _channelIndex, FrameFlags.Data, data.Length);
        data.CopyTo(_slab.AsSpan(frameStart + FrameHeader.Size));

        // 3. Publish: advance pending position (atomic — frame is now complete)
        int frameEnd = frameStart + FrameHeader.Size + data.Length;
        Interlocked.Exchange(ref _pendingPos, frameEnd);
        _writePos = frameEnd;

        // 4. Signal mux router: "I have ready frames"
        _router.NotifyReady(this);
        return ValueTask.CompletedTask;
    }

    // Channel also builds its OWN control frames (credit grants, ACKs, FIN)
    // These go into the same slab, interleaved with data frames
    internal void WriteAckFrame(uint receivedPosition)
    {
        int frameStart = _writePos;
        FrameHeader.WriteTo(_slab.AsSpan(frameStart), _channelIndex, FrameFlags.Ack, 4);
        BinaryPrimitives.WriteUInt32BigEndian(_slab.AsSpan(frameStart + FrameHeader.Size), receivedPosition);
        int frameEnd = frameStart + FrameHeader.Size + 4;
        Interlocked.Exchange(ref _pendingPos, frameEnd);
        _writePos = frameEnd;
        _router.NotifyReady(this);
    }

    internal void WriteFinFrame()
    {
        int frameStart = _writePos;
        FrameHeader.WriteTo(_slab.AsSpan(frameStart), _channelIndex, FrameFlags.Fin, 0);
        int frameEnd = frameStart + FrameHeader.Size;
        Interlocked.Exchange(ref _pendingPos, frameEnd);
        _writePos = frameEnd;
        _router.NotifyReady(this);
    }

    // Called by mux writer thread (no lock needed — single consumer of ready frames)
    internal Memory<byte> TakeReady()
    {
        int pending = Volatile.Read(ref _pendingPos);
        int sent = _sentPos;
        if (pending == sent) return Memory<byte>.Empty;
        return _slabMemory[sent..pending];  // zero-copy slice of complete frames
    }

    internal void MarkSent(int bytes) => _sentPos += bytes;

    // Called when receiver ACKs our data (remote confirmed receipt)
    internal void OnAck(int ackedPosition)
    {
        _ackedPos = ackedPosition;
        _spaceAvailable.SetResult(true);  // wake blocked writer if any
    }

    // Reconnection: just reset SentPos back — slab still has the data
    internal void PrepareReplay()
    {
        _sentPos = _ackedPos;  // re-send everything unconfirmed
        _router.NotifyReady(this);
    }
}
```

### ReadChannel — The Full Self-Contained Receiver

The ReadChannel handles all receive logic: buffering, direct delivery, ACK generation.
The mux just hands it raw payload bytes after reading the routing header.

```csharp
public sealed class ReadChannel : IAsyncDisposable
{
    private readonly byte[] _slab;             // receive buffer
    private readonly Memory<byte> _slabMemory;
    private readonly WriteChannel _ackChannel; // paired sender for ACKs (or shared control channel)

    private int _receivedPos;  // advanced by mux reader thread
    private int _consumedPos;  // advanced by user's ReadAsync
    private int _ackSentPos;   // what we've told the sender

    // Direct delivery (fast path)
    private Memory<byte>? _pendingUserBuffer;
    private ManualResetValueTaskSourceCore<int> _readCompletion;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // Fast path: data already buffered in slab
        int buffered = _receivedPos - _consumedPos;
        if (buffered > 0)
        {
            int toCopy = Math.Min(buffered, buffer.Length);
            _slabMemory.Span.Slice(_consumedPos, toCopy).CopyTo(buffer.Span);
            _consumedPos += toCopy;
            MaybeSendAck();
            return new ValueTask<int>(toCopy);
        }

        // Slow path: register for direct delivery
        _pendingUserBuffer = buffer;
        return new ValueTask<int>(_readCompletion, _readCompletion.Version);
    }

    // Called by mux reader thread — channel decides what to do with the payload
    internal void ReceivePayload(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        switch (flags)
        {
            case FrameFlags.Data:
                if (!TryDirectDeliver(payload))
                    BufferInSlab(payload);
                break;
            case FrameFlags.Ack:
                // ACK frame: tell our paired WriteChannel about confirmed position
                var ackPos = BinaryPrimitives.ReadUInt32BigEndian(payload);
                _pairedWriteChannel.OnAck((int)ackPos);
                break;
            case FrameFlags.Fin:
                MarkClosed();
                break;
        }
    }

    private bool TryDirectDeliver(ReadOnlySpan<byte> payload)
    {
        if (_pendingUserBuffer is { } userBuf)
        {
            int toCopy = Math.Min(payload.Length, userBuf.Length);
            payload[..toCopy].CopyTo(userBuf.Span);
            _pendingUserBuffer = null;
            _consumedPos += toCopy;
            _readCompletion.SetResult(toCopy);
            MaybeSendAck();
            return true;
        }
        return false;
    }

    private void BufferInSlab(ReadOnlySpan<byte> payload)
    {
        payload.CopyTo(_slab.AsSpan(_receivedPos));
        _receivedPos += payload.Length;
    }

    // Channel generates its own ACK frames (written to its paired write slab)
    private void MaybeSendAck()
    {
        int consumed = _consumedPos;
        int lastAck = _ackSentPos;
        int delta = consumed - lastAck;
        // Send ACK when 25%+ of slab capacity has been freed
        if (delta >= _slab.Length / 4)
        {
            _ackChannel.WriteAckFrame((uint)consumed);
            _ackSentPos = consumed;
        }
    }
}
```

---

## Copy Count Summary

### Write Path (User → Network)

| Step | Action | Copies |
|------|--------|--------|
| User calls WriteAsync | `data.CopyTo(slab)` | **1** |
| Mux writer picks up | `slab.AsMemory[sent..pending]` (slice) | **0** |
| Transport sends | Kernel DMA from pinned slab | **0 (user-space)** |

**Total user-space copies: 1**

### Read Path (Network → User)

| Step | Action | Copies |
|------|--------|--------|
| Transport receives | Kernel → PipeReader buffer | **0 (user-space)** |
| Mux dispatches (direct) | `transportBuf.CopyTo(userBuffer)` | **1** |
| Mux dispatches (buffered) | `transportBuf.CopyTo(slab)` then `slab.CopyTo(userBuf)` | **2** |

**Total user-space copies: 1 (fast path), 2 (slow path)**

### vs Current NetConduit

| Path | Current | Reborn |
|------|---------|--------|
| Write | 4-5 copies | 1 copy |
| Read (direct) | 1 copy | 1 copy |
| Read (buffered) | 2 copies | 2 copies |
| Reconnection recording | +1 copy always | 0 (slab is the buffer) |

---

## Feature Parity with all-current-features.md

All 144 features preserved. Key mapping:

### Core Features → Reborn

| Feature | How It Works in Reborn |
|---------|----------------------|
| Stream multiplexing | Same — multiple channels over one connection |
| Transport-agnostic | Same — IStreamPair / StreamFactory |
| 5 Transports (TCP/UDP/IPC/QUIC/WS) | Same — transport layer unchanged |
| Channel priority | Writer thread visits high-priority channels first |
| Flow control | Slab-based: slab full = backpressure. ACK frames advance AckedPos |
| Reconnection | Per-channel slab replay (no separate ring buffer) |
| Ring buffer recording | **Eliminated** — slab IS the replay buffer |
| Heartbeat/Ping | Same — control channel 0 |
| GoAway | Same — control frame |
| Flush modes | Same — Batched/Immediate/Manual |
| Stats | Same — counters on channel + mux |
| Events | Same — OnChannelOpened/Closed/Error/Reconnecting |
| Transits (Message/Delta/Stream) | Same — built on top of channels |
| DeltaTransit | Same — higher-level abstraction unchanged |
| AOT support | Same — JsonTypeInfo overloads |
| Direct delivery | Same optimization — mux reader → user buffer directly |

### Features Simplified in Reborn

| Feature | Current | Reborn |
|---------|---------|--------|
| MuxRingBuffer (16MB) | Separate allocation, memcpy on every drain | Eliminated — channel slab IS replay buffer |
| HotPathProfiler | Volatile checks on hot path | Compile-conditional only |
| Dual write path (Pipe + Chunks) | Two queues, ordering rules | Single slab per channel, no queues |
| FlushLoop + Eager Drain | Dual mechanism, redundant wakeups | Single writer thread, simple signal |
| ArrayPool per frame | Rent/Return per frame | No allocation — write to slab |
| Per-channel Pipe (read side) | Heavyweight Pipe for overflow | Lightweight slab circular buffer |
| Three write methods | WriteFrame/WriteFrameDirect/WriteFrameAsChunk | One path: WriteToSlab |
| Credit system | Complex grant/consume/adaptive | Simple: "slab has space" / "slab full" |
| _writeLock + _streamLock | Two locks | Zero locks (single writer thread + per-channel atomic positions) |

---

## Memory<T> and Span<T> Primer

For understanding how this design works:

### Span\<T\> — Stack-Only View

```csharp
byte[] slab = new byte[1_000_000];

// Span is a POINTER + LENGTH. No allocation. No copy.
Span<byte> header = slab.AsSpan(0, 8);        // points to slab[0..8]
Span<byte> payload = slab.AsSpan(8, 64000);   // points to slab[8..64008]

// Writing to span writes directly to the underlying array
BinaryPrimitives.WriteUInt16BigEndian(header, channelId);  // writes to slab[0..2]
userData.CopyTo(payload);  // copies user data into slab[8..64008]

// The frame is now complete IN the slab. No separate allocation.
```

### Memory\<T\> — Heap-Safe View (for async)

```csharp
byte[] slab = GC.AllocateArray<byte>(1_000_000, pinned: true);
Memory<byte> slabMemory = slab.AsMemory();

// Memory can be sliced without copying (just adjusts offset + length)
Memory<byte> frame = slabMemory.Slice(frameStart, frameLength);

// Can be passed to async methods (Span cannot)
await socket.SendAsync(frame, ct);  // sends slab[frameStart..frameEnd] directly
```

### Key Insight: Slicing ≠ Copying

```csharp
// THIS DOES NOT COPY:
Memory<byte> region = slab.AsMemory(100, 500);  // just a view

// THIS COPIES (only when you explicitly copy):
source.CopyTo(destination);  // the ONLY copy in our design
```

---

## Concurrency Model

### Lock-Free Design

| Component | Synchronization | Why |
|-----------|----------------|-----|
| WriteChannel.WriteAsync | Interlocked on _pendingPos | Frame construction fully concurrent |
| WriteChannel.WriteAckFrame | Interlocked on _pendingPos | Control frames same path as data |
| Mux writer thread | None (single thread) | Only one thread reads ready regions |
| Mux reader thread | None (single thread) | Only one thread dispatches frames |
| ReadChannel.ReadAsync | ManualResetValueTaskSource | Lock-free completion |
| Channel registry | ConcurrentDictionary | Standard concurrent access |

### Thread Model

```
Thread 1: Mux Writer (dedicated) — THE DUMB ROUTER
  - Wakes on signal or timer
  - Iterates channels by priority
  - Takes ready frames (pre-built Memory<byte>) from each channel
  - Writes to transport. That's it. Zero computation.
  - Single thread = no transport lock needed

Thread 2: Mux Reader (dedicated) — THE DISPATCHER
  - Reads 8-byte header from transport
  - Looks up channel by index
  - Tells channel to receive its payload
  - Does NOT parse payload, manage state, or make decisions

Threads 3..N+2: User threads — THE HEAVY LIFTERS
  - Call WriteAsync on WriteChannel → builds COMPLETE frames in slab:
    stamps header, copies payload, manages positions, generates ACKs
  - Call ReadAsync on ReadChannel → consumes from slab, triggers ACK generation
  - 1000 channels on 1000 threads = 1000x concurrent frame construction
  - ALL computation (framing, credits, reconnection) parallelized here
```

### Why This Parallelizes Perfectly

```
Traditional mux (old NetConduit, most libraries):
  Channel 1 writes data ─┐
  Channel 2 writes data ──┼──► MUX stamps headers ──► transport
  Channel 3 writes data ──┘     (serialized bottleneck)

Reborn architecture:
  Channel 1 stamps + builds frame ─────────────────┐
  Channel 2 stamps + builds frame (CONCURRENT) ────┼──► MUX picks up ──► transport
  Channel 3 stamps + builds frame (CONCURRENT) ────┘    (just sends Memory<byte>)
```

With 1000 channels: traditional mux has 1000x work on its hot path.
Reborn mux has the SAME work regardless of channel count (just picks up more slices).
The CPU work scales across user threads, not the mux thread.

### Why Single Writer Thread Wins

Current NetConduit problem: Multiple user threads compete for `_streamLock` to drain to
transport. This causes:
- SemaphoreSlim overhead (~200ns per acquire)
- Thread contention (waiting threads waste CPU)
- Non-deterministic flush ordering

Single writer thread:
- Zero contention on transport writes
- Deterministic priority ordering
- Natural batching (collects from all channels in one pass)
- FlushAsync called once per batch (not per frame)
- Mux hot path is ~5 instructions per channel: TakeReady → WriteAsync → MarkSent

---

## Performance Targets

| Scenario | Current NC | Target | Method |
|----------|-----------|--------|--------|
| 1ch/1MB (bulk) | 612 MB/s | >1500 MB/s | Single copy + no ring buffer |
| 10ch/1MB | 958 MB/s | >1800 MB/s | Lock-free + single writer |
| 100ch/1MB | 801 MB/s | >1400 MB/s | Priority scheduling + slab batching |
| 1ch/64B (game-tick) | 1.8M msg/s | >3M msg/s | No ArrayPool, no queue overhead |
| Raw .NET TCP | ~2000 MB/s | — | Platform ceiling |
| Overhead ratio | 35-50% | <10% | Target: single-copy = only overhead is header stamp |

---

## Implementation Plan

### Phase 1: Core (Minimal Viable Mux)

1. Slab allocator + WriteChannel with circular buffer + position tracking
2. FrameHeader (8 bytes) read/write
3. Single writer thread (drain channels → transport)
4. Single reader thread (transport → dispatch to channels)
5. ReadChannel with direct delivery + receive slab
6. Channel open/close (Init/Fin frames)
7. Basic flow control (slab-based backpressure + ACK)

### Phase 2: Reliability

8. Reconnection (slab replay per-channel)
9. Handshake + session identity
10. Ping/Pong keepalive
11. GoAway graceful shutdown
12. Error handling + ChannelClosedException

### Phase 3: Features

13. Channel priority (writer thread ordering)
14. String channel IDs
15. Channel enumeration (AcceptChannelsAsync)
16. Stats + Events
17. Flush modes (Batched/Immediate/Manual)
18. Transport implementations (TCP, IPC, QUIC, UDP, WebSocket)

### Phase 4: Higher-Level

19. Transits (StreamTransit, DuplexStreamTransit)
20. MessageTransit\<T\>
21. DeltaTransit\<T\>
22. AOT support (JsonTypeInfo overloads)
23. WebSocketMuxListener

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Pinned memory fragments GC heap | High channel count × large slabs | Use POH (Pinned Object Heap) in .NET 5+; slabs are long-lived anyway |
| Circular buffer wraparound complexity | Off-by-one bugs | Extensive unit tests; consider linear slab with compaction instead |
| Single writer thread bottleneck | Very high channel count (1000+) | Benchmark; if needed, partition channels across N writer threads |
| Slab size per channel × 1000 channels = 1GB | Memory pressure | Configurable per-channel; small default (256KB) with growth |
| User holds reference to slab memory | Use-after-free | Slab is always valid while channel is open; positions prevent overwrite |

---

## Summary: Why This Beats Everything

| Property | Go Smux | Current NC | Reborn NC |
|----------|---------|-----------|-----------|
| Write copies | 1 (user→kernel) | 4-5 | 1 |
| Read copies | 1 | 1-2 | 1 |
| Reconnection overhead | None (no feature) | +1 copy always | 0 (slab IS buffer) |
| Allocations per frame | 19 allocs/op | ArrayPool Rent+Return | 0 |
| Lock on write | mutex.Lock | SemaphoreSlim + Monitor | Interlocked (atomic) |
| Flush strategy | Per-write | Hybrid (complex) | Single writer + batch |
| Header size | 8 bytes | 9 bytes | 8 bytes |
| Credit system | None | Complex adaptive | Slab-based (natural) |
| Memory per channel | Shared pool | Pipe + ArrayPool | Fixed slab (predictable) |
