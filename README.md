# NetConduit

[![NuGet](https://img.shields.io/nuget/v/NetConduit.svg)](https://www.nuget.org/packages/NetConduit)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Transport-agnostic stream multiplexer for .NET.** Creates multiple virtual channels over a single bidirectional stream.

```
N streams → 1 stream (mux) → N streams (demux)
```

## Features

- **Multiple channels** over a single TCP/WebSocket/any stream connection
- **Credit-based backpressure** for flow control
- **Priority queuing** - higher priority frames sent first
- **Auto-reconnection** with channel state restoration
- **Native AOT compatible** - no reflection in core
- **Modern .NET** - targets .NET 8, 9, and 10

## Documentation

📖 **[Full Documentation](docs/index.md)** - Complete guides, API reference, and examples

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Installation and first steps |
| [Transports](docs/transports/index.md) | TCP, WebSocket, UDP, IPC, QUIC |
| [Transits](docs/transits/index.md) | MessageTransit, DeltaTransit, DuplexStream |
| [Concepts](docs/concepts/index.md) | Channels, backpressure, priority, reconnection |
| [API Reference](docs/api/index.md) | Configuration options, statistics |

## Installation

```bash
dotnet add package NetConduit       # Core
dotnet add package NetConduit.Tcp   # TCP transport
```

Optional transports:
```bash
dotnet add package NetConduit.WebSocket  # WebSocket
dotnet add package NetConduit.Udp        # UDP with reliability
dotnet add package NetConduit.Ipc        # Named pipes / Unix sockets
dotnet add package NetConduit.Quic       # QUIC (.NET 9+)
```

## Quick Start

### Client

```csharp
using NetConduit;
using NetConduit.Tcp;

var options = TcpMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

var channel = await mux.OpenChannelAsync(new() { ChannelId = "my-channel" });
await channel.WriteAsync("Hello, Server!"u8.ToArray());
await channel.DisposeAsync();
```

### Server

```csharp
using NetConduit;
using NetConduit.Tcp;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

var options = TcpMultiplexer.CreateServerOptions(listener);
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

await foreach (var channel in mux.AcceptChannelsAsync())
{
    var buffer = new byte[1024];
    var bytesRead = await channel.ReadAsync(buffer);
    Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));
}
```

### MessageTransit

Send/receive typed JSON messages over channels:

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Open transit
var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Send messages
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));

// Receive all messages (recommended pattern)
await foreach (var msg in transit.ReceiveAllAsync(cancellationToken))
{
    Console.WriteLine($"[{msg.User}] {msg.Text}");
}
```

See [MessageTransit docs](docs/transits/message.md) for more patterns.

### DeltaTransit

Send only what changed, not the entire state:

```csharp
await using var sender = await mux.OpenSendOnlyDeltaTransitAsync("state", MyContext.Default.GameState);
await using var receiver = await mux.AcceptReceiveOnlyDeltaTransitAsync("state", MyContext.Default.GameState);

// Send state updates (only deltas after first send)
var state = new GameState { Score = 100, Health = 80 };
await sender.SendAsync(state);  // Full state

state = state with { Score = 150 };
await sender.SendAsync(state);  // Sends only: [0, ["Score"], 150]

// Receive all state updates (recommended pattern)
await foreach (var state in receiver.ReceiveAllAsync(cancellationToken))
{
    UpdateGameWorld(state);
}
```

See [DeltaTransit docs](docs/transits/delta.md) for details.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                         Application                          │
├──────────────────────────────────────────────────────────────┤
│  Transit Layer (Optional)                                    │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐  │
│  │ MessageTransit │  │ DeltaTransit   │  │ DuplexStream   │  │
│  └───────┬────────┘  └───────┬────────┘  └───────┬────────┘  │
├──────────┴───────────────────────────────────────────────────┤
│                         NetConduit                           │
│  Frame encoding • Channel management • Backpressure          │
│  Priority queuing • Heartbeat • Graceful shutdown            │
├──────────────────────────────────────────────────────────────┤
│  Transport: TCP │ WebSocket │ UDP │ IPC │ QUIC │ Any Stream  │
└──────────────────────────────────────────────────────────────┘
```

## Benchmarks

Raw TCP vs Multiplexed TCP comparison (1000 channels):

| Test | Raw TCP | Mux TCP | Result |
|------|---------|---------|--------|
| 1000 × 1KB | 1,282 ms | 1,097 ms | **Mux 15% faster** |
| 1000 × 100KB | 675 ms | 571 ms | **Mux 15% faster** |
| 1000 × 1MB | FAILED* | 1,398 ms | **Mux works** |

*Raw TCP fails at 1000 connections due to socket exhaustion

Run benchmarks:
```bash
dotnet run -c Release --project benchmarks/NetConduit.Benchmarks
```

## Samples

| Sample | Description |
|--------|-------------|
| [ChatCli](samples/NetConduit.Samples.ChatCli) | Multi-user chat with MessageTransit |
| [FileTransfer](samples/NetConduit.Samples.FileTransfer) | File transfer with progress |
| [VideoStream](samples/NetConduit.Samples.VideoStream) | Real-time video streaming |
| [RpcFramework](samples/NetConduit.Samples.RpcFramework) | Request/response RPC pattern |

## License

MIT License - see [LICENSE](LICENSE) for details.
