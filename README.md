# NetConduit

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Transport-agnostic stream multiplexer for .NET** — Multiple virtual channels over a single bidirectional stream.

| Core | NuGet | Description |
| --- | --- | --- |
| [`NetConduit`](https://www.nuget.org/packages/NetConduit) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.svg?label=)](https://www.nuget.org/packages/NetConduit) | Core multiplexer + base interfaces |

| Transits | NuGet | Description |
| --- | --- | --- |
| [`NetConduit.Transit.Stream`](https://www.nuget.org/packages/NetConduit.Transit.Stream) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.Stream.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.Stream) | Single-channel `Stream` wrapper |
| [`NetConduit.Transit.DuplexStream`](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.DuplexStream.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream) | Bidirectional `Stream` over two channels |
| [`NetConduit.Transit.Message`](https://www.nuget.org/packages/NetConduit.Transit.Message) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.Message.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.Message) | Typed JSON message framing |
| [`NetConduit.Transit.DeltaMessage`](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.DeltaMessage.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage) | State-sync via JSON delta diffs |

| Transports | NuGet | Description |
| --- | --- | --- |
| [`NetConduit.Transport.Tcp`](https://www.nuget.org/packages/NetConduit.Transport.Tcp) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Tcp.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Tcp) | TCP transport |
| [`NetConduit.Transport.WebSocket`](https://www.nuget.org/packages/NetConduit.Transport.WebSocket) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.WebSocket.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.WebSocket) | WebSocket transport |
| [`NetConduit.Transport.Udp`](https://www.nuget.org/packages/NetConduit.Transport.Udp) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Udp.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Udp) | UDP with reliability layer |
| [`NetConduit.Transport.Ipc`](https://www.nuget.org/packages/NetConduit.Transport.Ipc) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Ipc.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Ipc) | IPC (loopback/Unix sockets) |
| [`NetConduit.Transport.Quic`](https://www.nuget.org/packages/NetConduit.Transport.Quic) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Quic.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Quic) | QUIC transport |

```
N streams → 1 stream (mux) → N streams (demux)
```

## Features

- **Multiple channels** — Many logical streams over one connection
- **Credit-based backpressure** — Flow control prevents overwhelming receivers
- **Priority queuing** — Higher priority frames sent first
- **Auto-reconnection** — Channel state restored after disconnect
- **Native AOT** — No reflection in core library
- **Zero dependencies** — Only BCL types
- **Modern .NET** — Targets .NET 8, 9, and 10

## Quick Start

```bash
dotnet add package NetConduit.Transport.Tcp
dotnet add package NetConduit.Transit.Message
```

```csharp
using NetConduit;
using NetConduit.Transport.Tcp;
using NetConduit.Transit.Message;
using System.Text.Json.Serialization;

public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Server
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
await using var server = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
server.Start();
await server.WaitForReadyAsync();

await using var transit = await server.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
await foreach (var msg in transit.ReceiveAllAsync())
    Console.WriteLine($"[{msg.User}] {msg.Text}");

// Client
await using var client = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("localhost", 5000));
client.Start();
await client.WaitForReadyAsync();

await using var transit = await client.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
```

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Application                                                                 │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transit Layer (Optional)                                                    │
│ ┌──────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌─────────────┐ │
│ │MessageTransit│ │DeltaMessageTransit│ │DuplexStreamTransit│ │StreamTransit│ │
│ └──────────────┘ └───────────────────┘ └───────────────────┘ └─────────────┘ │
├──────────────────────────────────────────────────────────────────────────────┤
│  NetConduit Core                                                             │
│  - Frame encoding/decoding                                                   │
│  - Channel management                                                        │
│  - Credit-based backpressure                                                 │
│  - Priority queuing                                                          │
│  - Auto-reconnection                                                         │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transport Layer (Pluggable)                                                 │
│  ┌─────┐  ┌─────────┐  ┌─────┐  ┌─────┐  ┌──────┐                            │
│  │ TCP │  │WebSocket│  │ UDP │  │ IPC │  │ QUIC │                            │
│  └─────┘  └─────────┘  └─────┘  └─────┘  └──────┘                            │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Samples

| Sample       | Description                            |
| ------------ | -------------------------------------- |
| GroupChat    | Multi-user chat (TCP + WebSocket)      |
| FileTransfer | Parallel file transfers with progress  |
| Pong         | Multiplayer game with delta state sync |
| RemoteShell  | SSH-like remote command execution      |
| RpcFramework | Type-safe JSON-RPC pattern             |
| TcpTunnel    | Port forwarding like ngrok             |
| Scoreboard   | Live leaderboard with reconnection     |

```bash
dotnet run --project samples/GroupChatSample -- server tcp 5000
dotnet run --project samples/GroupChatSample -- client tcp 5000 127.0.0.1 Alice
```

## Documentation

Full documentation at [`docs/`](docs/index.md):

- [Getting Started](docs/getting-started.md)
- [Transports](docs/transports/index.md) — TCP, WebSocket, UDP, IPC, QUIC
- [Transits](docs/transits/index.md) — MessageTransit, DeltaMessageTransit, DuplexStream, Stream
- [Concepts](docs/concepts/index.md) — Channels, backpressure, priority, reconnection
- [API Reference](docs/api/index.md)

## Building

```bash
dotnet build
dotnet test
```

## License

See [LICENSE](LICENSE).
