# Core Concepts

Understanding NetConduit's fundamental concepts.

## Quick Reference

| Concept | Description | Link |
|---------|-------------|------|
| Channels | One-way streams over multiplexer | [channels.md](channels.md) |
| Backpressure | Flow control to prevent overload | [backpressure.md](backpressure.md) |
| Priority | Frame scheduling based on importance | [priority.md](priority.md) |
| Reconnection | Automatic recovery from disconnects | [reconnection.md](reconnection.md) |
| Events | Disconnection and state change notifications | [events.md](events.md) |

## Mental Model

```
┌─────────────────────────────────────────────────────────────┐
│                     Your Application                        │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐        │
│  │Channel A│  │Channel B│  │Channel C│  │Channel D│        │
│  │ (data)  │  │ (chat)  │  │(control)│  │ (video) │        │
│  └────┬────┘  └────┬────┘  └────┬────┘  └────┬────┘        │
│       │            │            │            │              │
│  ┌────┴────────────┴────────────┴────────────┴────┐        │
│  │              StreamMultiplexer                 │        │
│  │  - Frame encoding/decoding                     │        │
│  │  - Backpressure (credits)                      │        │
│  │  - Priority queuing                            │        │
│  └────────────────────┬───────────────────────────┘        │
│                       │                                     │
│  ┌────────────────────┴───────────────────────────┐        │
│  │           Single Physical Stream               │        │
│  │         (TCP, WebSocket, UDP, etc.)            │        │
│  └────────────────────────────────────────────────┘        │
└─────────────────────────────────────────────────────────────┘
```

## Key Principles

### Channels are Simplex

Each channel flows one direction:
- **WriteChannel**: You write, they read
- **ReadChannel**: They write, you read

For bidirectional, use two channels or [DuplexStreamTransit](../transits/duplex-stream.md).

### Credits Control Flow

Receivers advertise how much they can accept. Senders pause when credits exhausted. This prevents fast producers from overwhelming slow consumers. See [Backpressure](backpressure.md).

### Priority is Local

[Priority](priority.md) determines which frames from this multiplexer get sent first. Higher priority channels get bandwidth preference.

### Multiplexer Owns the Stream

One multiplexer per stream. The multiplexer manages the stream's lifecycle, framing, and cleanup.

## Lifecycle

```
1. Create multiplexer with options
2. Start() to begin processing
3. WaitForReadyAsync() to confirm connected
4. Open/Accept channels as needed
5. DisposeAsync() for graceful shutdown
```

```csharp
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels...

await mux.DisposeAsync();  // Graceful shutdown
```
