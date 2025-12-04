# NetConduit - Stream Multiplexer Plan

## Overview

Transport-agnostic stream multiplexer for .NET. Creates multiple virtual channels over a single bidirectional stream.

**Core Concept:** `N streams → 1 stream (mux) → N streams (demux)`

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Application Layer                               │
├──────────────────────────────────────────────────────────────────────────────┤
│                              Transit Layer                                   │
│  ┌────────────────────────┐  ┌────────────────────────┐  ┌────────────────┐ │
│  │   MessageTransit     │  │     BytesTransit       │  │ CustomTransit  │ │
│  │  (length-prefixed+JSON)│  │   (raw byte stream)    │  │  (user impl)   │ │
│  └───────────┬────────────┘  └───────────┬────────────┘  └───────┬────────┘ │
├──────────────┴───────────────────────────┴───────────────────────┴──────────┤
│                              NetConduit                                      │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                       StreamMultiplexer                                 │ │
│  │  - Frame encoding/decoding (bytes only)                                │ │
│  │  - Channel management (string ChannelId public, uint index internal)   │ │
│  │  - Credit-based backpressure                                           │ │
│  │  - Priority queuing                                                    │ │
│  │  - Control channel (ping/pong, credits)                                │ │
│  │  - Statistics/Metrics                                                  │ │
│  │  - GOAWAY for graceful shutdown                                        │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────────────────┤
│                            Transport Layer                                   │
│  ┌────────────────────┐  ┌───────────────────┐  ┌────────────────────────┐  │
│  │  NetConduit.Tcp    │  │ NetConduit.WS     │  │ Any Stream (custom)    │  │
│  └────────────────────┘  └───────────────────┘  └────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Packages

| Package | Description |
|---------|-------------|
| `NetConduit` | Core multiplexer, channels, frames, backpressure, priorities, control, stats, and built-in transits (MessageTransit, StreamTransit, DuplexStreamTransit) |
| `NetConduit.Tcp` | TCP transport helpers |
| `NetConduit.WebSocket` | WebSocket transport helpers |

## Test Projects

| Project | Description |
|---------|-------------|
| `NetConduit.UnitTests` | Unit tests for core multiplexer |
| `NetConduit.Tcp.IntegrationTests` | Integration tests for TCP transport |
| `NetConduit.WebSocket.IntegrationTests` | Integration tests for WebSocket transport |

---

## Target Frameworks

- .NET 8 (LTS)
- .NET 9
- .NET 10

No .NET Standard. Modern APIs only.

---

## Native AOT Compatibility

Full AOT support required:

- No `System.Reflection` in core
- Source-generated `JsonSerializerContext` for serialization
- `PublishAot=true` tested in CI
- `<IsAotCompatible>true</IsAotCompatible>` in all projects
- Trimming-safe, no warnings

---

## Connection Flow

```
Side A                                    Side B
    │                                        │
    │───[HANDSHAKE: session_id=X]───────────►│
    │◄──[HANDSHAKE: session_id=X]────────────│  (echo confirms connection)
    │                                        │
    │   (connection established)             │
    │                                        │
    │───[INIT idx=<allocated>, id="video"]──►│  Open channel "video" (A→B)
    │◄──[ACK idx=N, credits=64KB]────────────│  Channel ready
    │                                        │
    │◄──[INIT idx=<allocated>, id="ctrl"]────│  Open channel "ctrl" (B→A)
    │───[ACK idx=M, credits=64KB]───────────►│  Channel ready
    │                                        │
    │───[DATA idx=N, payload]───────────────►│  Send on "video" (A→B)
    │◄──[DATA idx=M, payload]────────────────│  Send on "ctrl" (B→A)
    │                                        │
    │───[FIN idx=N]─────────────────────────►│  Close "video"
    │◄──[FIN idx=M]──────────────────────────│  Close "ctrl"
```

**Channels are simplex (one-way).** Each channel flows from opener to accepter. For bidirectional communication, open two channels.

**Note:** Channel indices are scoped per-direction. Each side allocates indices from 1 for channels they open. The same index value can exist in both directions without conflict because they represent different channels.

---

## Frame Format (9 bytes header)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ [Channel Index: 4B BE] [Flags: 1B] [Length: 4B BE] [Payload: N bytes]       │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Channel Index**: 4 bytes, big-endian (internal wire protocol identifier)
- **Flags**: 1 byte (single value, not combinable)
- **Length**: 4 bytes, big-endian (max 16MB configurable)
- **Payload**: Variable length

**Note:** The 4-byte `ChannelIndex` is an internal wire protocol optimization. The public API uses string `ChannelId` which is transmitted only during INIT handshake.

---

## Frame Flags

| Flag | Value | Description |
|------|-------|-------------|
| DATA | 0x00 | Regular data frame |
| INIT | 0x01 | Open channel (payload: priority) |
| FIN  | 0x02 | Close channel (graceful) |
| ACK  | 0x03 | Credit grant (payload: credits u32) |
| ERR  | 0x04 | Error (payload: code u16 + message UTF-8) |

Flags are **not combinable**—each frame has exactly one type.

---

## INIT Payload

```
┌──────────────────────────────────────────────────────────────┐
│ [priority: u8] [id_length: u16 BE] [channel_id: 0-1024B UTF8] │
└──────────────────────────────────────────────────────────────┘
```

- `priority`: 1 byte, higher = higher priority (0-255)
- `id_length`: 2 bytes, big-endian, length of channel_id in bytes (0-1024)
- `channel_id`: 0-1024 bytes, UTF-8 encoded string identifier (required, unique per side)

**Min payload:** 3 bytes (priority + 0-length id)
**Max payload:** 1027 bytes (priority + 2-byte length + 1024-byte id)

---

## Channel Identification

### Public API: String ChannelId

Channels are identified by a **required string `ChannelId`** (0-1024 bytes UTF-8). This is the primary identifier used in application code:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "video_stream" });
var accepted = await mux.AcceptChannelAsync("video_stream", ct);
```

- **Unique per side:** Cannot open two channels with the same `ChannelId` from one side
- **Transmitted once:** Only sent in the INIT frame during channel open
- **Lookup:** `GetWriteChannel("name")`, `GetReadChannel("name")`

### Internal Wire Protocol: ChannelIndex (4 bytes)

| Range | Description |
|-------|-------------|
| 0x00000000 | Control channel (bidirectional, special) |
| 0x00000001 - 0xFFFFFFFE | Data channels (per-direction index space) |
| 0xFFFFFFFF | Reserved |

The `ChannelIndex` is an **internal optimization** for compact frame headers. Both sides maintain a `ChannelId ↔ ChannelIndex` mapping established during INIT/ACK handshake.

**Per-Direction Index Space:** Channel indices are scoped per-direction:
- **Outgoing channels (WriteChannel):** Indices allocated locally starting from 1
- **Incoming channels (ReadChannel):** Indices received from remote (their allocation)

The same index value (e.g., 1) can exist in both directions simultaneously without conflict:
- Side A's WriteChannel index 1 → Side B's ReadChannel index 1
- Side B's WriteChannel index 1 → Side A's ReadChannel index 1

These are four distinct channels. The direction is determined by who sent the INIT frame.

---

## Priority Queuing

Built into core. When bandwidth constrained, higher priority frames sent first.

| Constant | Value | Use Case |
|----------|-------|----------|
| Lowest | 0 | Background, best-effort |
| Low | 64 | Bulk transfers |
| Normal | 128 | Default |
| High | 192 | Interactive, low-latency |
| Highest | 255 | Control messages, signaling |

Priority set per-channel at open time. Any value 0-255 allowed.

---

## Transits (Byte Interpreters)

Transits wrap `MultiplexerChannel` pairs and interpret raw bytes. The multiplexer only handles bytes; transits add semantics.

**Note:** `MultiplexerChannel` is already a `Stream` (simplex). For raw byte streaming, use the channel directly—no transit needed.

### Transit Interfaces

- **ITransit**: Base interface with channel pair and connection state
- **IMessageTransit<TSend, TReceive>**: Send/receive discrete messages over channel pair

### Built-in Transits

| Transit | Description |
|---------|-------------|
| MessageTransit | Length-prefixed messages with JSON serialization (AOT-safe) |
| StreamTransit | Wraps single channel as `Stream` (simplex) |
| DuplexStreamTransit | Wraps channel pair as bidirectional `Stream` |

### Message Wire Format (MessageTransit)

```
┌───────────────────────────────────┐
│ [length: u32 BE] [payload: bytes] │
└───────────────────────────────────┘
```

### Transit + Metadata Pattern

Metadata not in core. Transits can implement their own handshake by sending metadata as first message.

---

## Backpressure (Credit-Based Flow Control)

Built into core. Credits are in **bytes**. Sender blocks when credits exhausted; receiver auto-grants after consuming data.

### Configuration Options

- `InitialCredits`: Initial buffer allowance (default: 64KB)
- `CreditGrantThreshold`: Grant when X% consumed (default: 0.5)
- `SendTimeout`: Error if blocked too long (default: 30s)

### Flow

```
Sender                                    Receiver
  │                                          │
  │──[INIT ch=1, credits=0]─────────────────►│
  │◄──[ACK ch=1, credits=65536]──────────────│  64KB granted
  │                                          │
  │──[DATA ch=1, 32KB]──────────────────────►│  credits: 65536→32768
  │──[DATA ch=1, 32KB]──────────────────────►│  credits: 32768→0
  │                                          │
  │   (sender awaits, up to SendTimeout)     │
  │                                          │  (receiver reads 32KB)
  │◄──[ACK ch=1, credits=32768]──────────────│  Auto-grant after read
  │                                          │
  │──[DATA ch=1, 16KB]──────────────────────►│  credits: 32768→16384
```

### Timeout Behavior

If sender blocked exceeds `SendTimeout`:
- `WriteAsync` throws `TimeoutException`
- Channel remains open (caller decides)
- Set `SendTimeout = Timeout.InfiniteTimeSpan` to wait forever

---

## Control Channel (0x00000000)

Always open. Uses DATA frames with subtype in first payload byte.

### Control Subtypes

| Subtype | Name | Payload |
|---------|------|---------|
| 0x01 | PING | timestamp (u64) |
| 0x02 | PONG | echo timestamp (u64) |
| 0x03 | HANDSHAKE | session_id (u64) for reconnection |
| 0x04 | CREDIT_GRANT | channel_index (u32) + credits (u32) |
| 0x05 | ERROR | error_code (u16) + message (UTF-8) |
| 0x06 | GOAWAY | error_code (u16) + last_channel_index (u32) |
| 0x07 | RECONNECT | session_id (u64) + channel_states |

### Heartbeat

- Initiator sends PING every 30 seconds (configurable)
- Acceptor must respond with PONG within 10 seconds
- 3 missed PONGs = connection dead, GOAWAY sent

### Graceful Shutdown (GOAWAY)

GOAWAY notifies peer before closing. Payload: `[error_code: u16] [last_channel_id: u32]`

After GOAWAY:
- No new channels accepted
- Existing channels continue until closed
- Peer should close channels gracefully

---

## Channel State Machine

```
                    ┌─────────┐
                    │  CLOSED │
                    └────┬────┘
                         │ send/recv INIT
                         ▼
                    ┌─────────┐
           ┌────────│ OPENING │────────┐
           │        └────┬────┘        │
      recv ERR           │ recv ACK    timeout
           │             ▼             │
           │        ┌─────────┐        │
           │        │  OPEN   │        │
           │        └────┬────┘        │
           │             │             │
           │        send/recv FIN      │
           │             │             │
           ▼             ▼             ▼
                    ┌─────────┐
                    │  CLOSED │
                    └─────────┘
```

---

## Error Handling

### Error Frame Payload

```
[error_code: u16] [message: UTF-8]
```

### Error Codes

| Code | Name | Description |
|------|------|-------------|
| 0x0001 | UNKNOWN_CHANNEL | Channel ID not found |
| 0x0002 | CHANNEL_EXISTS | Channel ID already in use |
| 0x0003 | PROTOCOL_ERROR | Malformed frame or invalid state |
| 0x0004 | FLOW_CONTROL_ERROR | Sent data exceeding credits |
| 0x0005 | TIMEOUT | Operation timed out |
| 0x0006 | INTERNAL | Internal error |
| 0x0007 | REFUSED | Channel open refused |
| 0x0008 | CANCEL | Operation cancelled |

---

## Core Types (Sketch)

### StreamMultiplexer

Main entry point. Wraps two streams (read + write).

- Constructor: `(Stream readStream, Stream writeStream, MultiplexerOptions options)`
- For bidirectional streams, pass same instance: `new StreamMultiplexer(stream, stream, options)`
- `RunAsync()`: Starts read/write loops
- `OpenChannelAsync(ChannelOptions)`: Opens new channel with required ChannelId, returns `WriteChannel`
- `AcceptChannelsAsync()`: Yields incoming `ReadChannel`s
- `AcceptChannelAsync(string channelId)`: Waits for specific named channel
- `GetWriteChannel(string channelId)`: Lookup opened channel by name
- `GetReadChannel(string channelId)`: Lookup accepted channel by name
- `GetStats()`: Returns multiplexer statistics
- `GoAwayAsync()`: Graceful shutdown

Properties:
- `OpenedChannelIds`: `IReadOnlyCollection<string>` - channels this side opened
- `AcceptedChannelIds`: `IReadOnlyCollection<string>` - channels accepted from remote
- `ActiveChannelIds`: `IReadOnlyCollection<string>` - all active channels
- `ActiveChannelCount`: `int` - total active channel count

Events:
- `OnChannelOpened`: `Action<string>` - channel ID opened
- `OnChannelClosed`: `Action<string, Exception?>` - channel ID closed
- `OnError`: `Action<Exception>` - multiplexer error

### MultiplexerOptions

- `MaxFrameSize`: Max payload size (default: 16MB)
- `PingInterval`: Heartbeat interval (default: 30s)
- `PingTimeout`: PONG wait time (default: 10s)
- `MaxMissedPings`: Before disconnect (default: 3)
- `GoAwayTimeout`: Shutdown grace period (default: 30s)
- `SessionId`: Unique session identifier for reconnection (auto-generated if null)
- `EnableReconnection`: Whether to support reconnection (default: true)

### ChannelOptions

- `ChannelId`: **Required** string identifier (0-1024 bytes UTF-8)
- `InitialCredits`: Initial byte credits (default: 64KB)
- `CreditGrantThreshold`: Auto-grant threshold (default: 0.5)
- `SendTimeout`: Max wait for credits (default: 30s)
- `Priority`: Channel priority (default: Normal)

```csharp
public sealed class ChannelOptions
{
    public required string ChannelId { get; init; }  // 0-1024 bytes UTF-8
    public uint InitialCredits { get; init; } = 65536;
    public double CreditGrantThreshold { get; init; } = 0.5;
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public ChannelPriority Priority { get; init; } = ChannelPriority.Normal;
}
```

### WriteChannel

Opened by this side. Inherits from `Stream` (write-only).

- `ChannelId`: String identifier (public)
- `ChannelIndex`: 4-byte wire protocol ID (internal)
- `State`: Opening/Open/Closing/Closed
- `Priority`: Channel priority
- `AvailableCredits`: Current send credits
- `Stats`: Per-channel statistics
- `WriteAsync()`: Write bytes (blocks on backpressure)
- `CloseAsync()`: Graceful close

### ReadChannel

Accepted from remote side. Inherits from `Stream` (read-only).

- `ChannelId`: String identifier (public)
- `ChannelIndex`: 4-byte wire protocol ID (internal)
- `State`: Open/Closing/Closed
- `Priority`: Channel priority
- `Stats`: Per-channel statistics
- `ReadAsync()`: Read bytes
- `CloseAsync()`: Graceful close

### Statistics

**ChannelStats:**
- BytesSent/Received, FramesSent/Received
- CreditsGranted/Consumed, OpenDuration

**MultiplexerStats:**
- BytesSent/Received, OpenChannels
- TotalChannelsOpened/Closed, Uptime
- LastPingRtt, MissedPings

---

## Transport Helpers

### NetConduit.Tcp

- `TcpMultiplexer.ConnectAsync(host, port, options)`: Client connect
- `TcpMultiplexer.AcceptAsync(listener, options)`: Server accept

### NetConduit.WebSocket

- `WebSocketMultiplexer.ConnectAsync(uri, options)`: Client connect
- `WebSocketMultiplexer.Accept(webSocket, options)`: Server accept

### Custom Transports

Any `Stream` with read/write works: named pipes, serial ports, custom streams.

---

## Performance Techniques

| Technique | Purpose |
|-----------|---------|
| `System.IO.Pipelines` | Zero-copy reads |
| `ArrayPool<byte>.Shared` | Reusable buffers |
| `System.Threading.Channels` | Lock-free queuing |
| `ConcurrentDictionary` | Lock-free channel lookup |
| `SemaphoreSlim` | Credit gating (no spin-wait) |
| `ValueTask` | Avoid allocations on hot paths |
| `stackalloc` | Header serialization |
| `BinaryPrimitives` | Fast big-endian encoding |
| **No reflection** | Full trimming/AOT |

### Memory Layout

- Frame headers: `stackalloc byte[9]`
- Payload buffers: Rented from `ArrayPool`
- Channel queues: Bounded `Channel<T>` with backpressure

---

## Implementation Roadmap

### Phase 1: Core Multiplexer (MVP) ✅
- [x] Frame encoding/decoding (9-byte header)
- [x] Channel management with ChannelIndex (4-byte internal) allocation
- [x] Credit-based backpressure (bytes)
- [x] Priority queuing
- [x] Control channel (ping/pong, credits, GOAWAY)
- [x] Statistics/metrics
- [x] Basic error handling
- [x] Unit tests (155 tests - 150 passing, 5 million-scale skipped)
- [x] AOT compatibility validation (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`, no warnings)

### Phase 1.5: String ChannelId ✅
- [x] Add `required string ChannelId` to `ChannelOptions`
- [x] Rename `ChannelId` → `ChannelIndex` (internal) in `WriteChannel`/`ReadChannel`
- [x] Add public `string ChannelId` property to channels
- [x] Update INIT payload: `[priority: u8][id_length: u16 BE][channel_id: 0-1024B UTF8]`
- [x] Add `ChannelId ↔ ChannelIndex` mapping dictionaries
- [x] `OpenChannelAsync()` requires `ChannelOptions`, validates unique `ChannelId`
- [x] Add `AcceptChannelAsync(string channelId)` - wait for specific channel
- [x] Add `GetWriteChannel(string)` / `GetReadChannel(string)` lookup methods
- [x] Update properties: `OpenedChannelIds`, `AcceptedChannelIds`, `ActiveChannelIds` → `IReadOnlyCollection<string>`
- [x] Update events: `OnChannelOpened` → `Action<string>`, `OnChannelClosed` → `Action<string, Exception?>`
- [x] Update all existing tests to use string `ChannelId`

### Phase 1.6: Connection Improvements ✅
- [x] Remove `MultiplexerOptions.IsInitiator` - no role pre-assignment
- [x] Implement per-direction channel index space (publisher allocates, receiver records)
- [x] Update handshake to use session_id for reconnection support
- [x] Split classes into separate files (one class per file)

### Phase 1.7: Data Integrity Tests ✅
- [x] Add heavy data verification tests with checksum validation
- [x] Add multi-transit stress tests (multiple streams, concurrent operations)
- [x] Add byte-exact verification under high load
- [x] Add channel index limit tests (per-direction allocation)
- [x] Add per-direction index space verification tests
- [x] Add chaos/robustness tests (random operations, mixed transport)
- [x] Add scaled stress tests (100, 1K, 10K, 50K, 100K channels)
- [x] Skip million-channel tests (too resource-intensive)
- [x] All stress test timeouts capped at 5 minutes max
- [x] Rename test project to `NetConduit.UnitTests`

### Phase 2: Built-in Transits (in NetConduit package) ✅
- [x] `ITransit` interface (wraps channel or channel pair)
- [x] `IMessageTransit<TSend, TReceive>` interface (send/receive discrete messages)
- [x] `MessageTransit<TSend, TReceive>` (length-prefixed + JSON, AOT-safe with JsonTypeInfo)
- [x] `StreamTransit` (single channel as Stream - simplex)
- [x] `DuplexStreamTransit` (channel pair as bidirectional Stream)
- [x] Extension methods: `AsStream()`, `AsDuplexStream()`
- [x] Transit unit tests in `NetConduit.UnitTests` (16 tests)

### Phase 3: Transports ✅
- [x] `NetConduit.Tcp` package (TcpMultiplexer, TcpMultiplexerConnection)
- [x] `NetConduit.WebSocket` package (WebSocketMultiplexer, WebSocketMultiplexerConnection, WebSocketStream)
- [x] `NetConduit.Tcp.IntegrationTests` project (9 tests)
- [x] `NetConduit.WebSocket.IntegrationTests` project (7 tests)

### Phase 3.5: Auto-Reconnect ✅
- [x] Add `ReconnectAsync(Stream, Stream)` method
- [x] Add `OnDisconnected` and `OnReconnected` events
- [x] Add `IsConnected`, `IsReconnecting` properties
- [x] Add `NotifyDisconnected()` method for manual disconnect notification
- [x] Add reconnection protocol (RECONNECT, RECONNECT_ACK control frames)
- [x] Add `SessionMismatch` error code for reconnection failures
- [x] Add `ChannelSyncState` for tracking byte positions and buffering
- [x] Implement byte-level sequence tracking per channel
- [x] Buffer pending writes during disconnection with configurable buffer size
- [x] Full channel state serialization/restoration on reconnection
- [x] Replay unacknowledged data after reconnection (no data loss/corruption)
- [x] Add reconnection unit tests (22 tests including data continuity and replay verification)

### Phase 4: Samples ✅
- [x] `NetConduit.Benchmarks` - Performance benchmarks comparing raw TCP vs multiplexed TCP
  - TcpVsMuxBenchmark: 100, 1000, 10000 concurrent channels
  - ThroughputBenchmark: 1, 10, 100 concurrent channels with 100MB total transfer
- [x] `NetConduit.Samples.ChatCli` - Simple CLI chat application demonstrating bidirectional messaging
- [x] `NetConduit.Samples.FileTransfer` - File transfer demo with progress and multiple concurrent transfers
- [x] `NetConduit.Samples.RpcFramework` - RPC-style request/response pattern over multiplexed channels
- [x] `NetConduit.Samples.VideoStream` - Simulated video/audio streaming with priority channels

### Phase 5: Documentation & Release ✅
- [x] XML documentation (all public APIs documented with `<summary>`, `<param>`, etc.)
- [x] README with examples (comprehensive README.md with quick start, API overview, architecture)
- [x] NuGet packaging (package metadata, license, repository URL configured)
- [ ] GitHub release (pending)

### Phase 6: Extensions (Future)
- [ ] `NetConduit.Compression` (LZ4, Brotli)
- [ ] `NetConduit.Encryption` (AES-GCM)
- [ ] Channel pooling
- [ ] Custom transit examples

---

## Future Extensions (Optional Packages)

### Compression (NetConduit.Compression)
Per-channel compression with LZ4 or Brotli.

### Encryption (NetConduit.Encryption)
Per-channel encryption with AES-GCM.

### Channel Pooling
Reuse channels for request-response patterns instead of open/close per request.

---

## Auto-Reconnection Protocol

### Overview
The multiplexer supports automatic reconnection with channel state restoration after network disconnection. This enables seamless recovery without losing in-flight data.

### Session Management
- Each multiplexer generates a unique `SessionId` (GUID) on creation
- `SessionId` is exchanged during initial handshake
- On reconnection, same `SessionId` indicates same logical session

### Reconnection Flow

```
Side A                                    Side B
    │                                        │
    │   (network disconnection)              │
    │                                        │
    │   (reconnect with new streams)         │
    │                                        │
    │───[RECONNECT: session_id=X]───────────►│
    │◄──[RECONNECT_ACK: channel_states]──────│
    │───[CHANNEL_SYNC: confirmed_states]────►│
    │                                        │
    │   (channels restored, continue)        │
```

### Channel State Restoration
- Pending write data is buffered locally during disconnect
- Channel states (open, credits, sequence numbers) are tracked
- On reconnect, both sides synchronize channel states
- Buffered data is retransmitted if needed

### Configuration
```csharp
new MultiplexerOptions 
{
    SessionId = Guid.NewGuid(),           // Unique session identifier
    EnableReconnection = true,            // Enable reconnect support
    ReconnectBufferSize = 1024 * 1024,    // Buffer size for pending data (1MB)
    ReconnectTimeout = TimeSpan.FromSeconds(60)  // Max time to wait for reconnect
}
```

### API
```csharp
// Event when disconnected (but reconnection possible)
mux.OnDisconnected += () => Console.WriteLine("Disconnected, waiting for reconnect...");

// Event when reconnected
mux.OnReconnected += () => Console.WriteLine("Reconnected!");

// Manual reconnection with new streams
await mux.ReconnectAsync(newReadStream, newWriteStream, ct);

// Check connection state
bool isConnected = mux.IsConnected;
bool isReconnecting = mux.IsReconnecting;
```

---

## Related Documents

- [V1 Implementation](./old/v1/) - Previous implementation reference
- README.md - Quick start guide (to be created)
