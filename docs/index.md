# NetConduit Documentation

**Transport-agnostic stream multiplexer for .NET** — Create multiple virtual channels over a single bidirectional stream.

## Quick Navigation

| Section | Description |
|---------|-------------|
| [Getting Started](getting-started.md) | Installation, quick start, first multiplexer |
| [Transports](transports/index.md) | TCP, WebSocket, UDP, IPC, QUIC |
| [Transits](transits/index.md) | MessageTransit, DeltaTransit, DuplexStreamTransit, StreamTransit |
| [Concepts](concepts/index.md) | Channels, backpressure, priority, reconnection, shutdown, heartbeat |
| [API Reference](api/index.md) | Multiplexer, channels, options, statistics, errors |
| [Samples](samples/index.md) | Complete example applications |

## Quick Example

```csharp
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define message type
public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Server: receive all messages
await using var transit = await mux.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

await foreach (var msg in transit.ReceiveAllAsync(cancellationToken))
{
    Console.WriteLine($"[{msg.User}] {msg.Text}");
}

// Client: send messages
await using var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
```

## What is NetConduit?

NetConduit multiplexes multiple logical streams over a single physical connection:

```
N streams → 1 stream (mux) → N streams (demux)
```

**Use cases:**
- Multiple RPC channels over one WebSocket
- Game state + chat + voice over single TCP connection
- Microservice communication with channel isolation
- Tunneling services through firewalls/NAT

## Key Features

| Feature | Description |
|---------|-------------|
| **Multiple channels** | Many logical streams over one connection |
| **Credit-based backpressure** | Flow control prevents overwhelming receivers |
| **Priority queuing** | Higher priority frames sent first |
| **Auto-reconnection** | Channel state restored after disconnect |
| **Native AOT** | No reflection in core library |
| **Modern .NET** | Targets .NET 8, 9, and 10 |

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Application                                      │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transit Layer (Optional)                                                    │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────┐  ┌────────────┐ │
│  │MessageTransit│  │ DeltaTransit │  │DuplexStreamTransit│  │StreamTransit│ │
│  └──────────────┘  └──────────────┘  └───────────────────┘  └────────────┘ │
├──────────────────────────────────────────────────────────────────────────────┤
│                              NetConduit Core                                  │
│  - Frame encoding/decoding                                                   │
│  - Channel management                                                        │
│  - Credit-based backpressure                                                 │
│  - Priority queuing                                                          │
│  - Auto-reconnection                                                         │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transport Layer (Pluggable)                                                 │
│  ┌─────┐  ┌──────────┐  ┌─────┐  ┌─────┐  ┌──────┐                        │
│  │ TCP │  │ WebSocket│  │ UDP │  │ IPC │  │ QUIC │                          │
│  └─────┘  └──────────┘  └─────┘  └─────┘  └──────┘                        │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Dependencies

No external runtime dependencies. The core library uses only BCL types (`System.Threading.Channels`, `System.Text.Json`, `System.Buffers.Binary`).
