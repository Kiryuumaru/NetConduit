# NetConduit

[![NuGet](https://img.shields.io/nuget/v/NetConduit.svg)](https://www.nuget.org/packages/NetConduit)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Transport-agnostic stream multiplexer for .NET.** Creates multiple virtual channels over a single bidirectional stream.

```
N streams → 1 stream (mux) → N streams (demux)
```

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Application                                     │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transit Layer (Optional)                                                    │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │MessageTransit│  │ DeltaTransit │  │ DuplexStream │  │    Stream    │      │
│  └──────────────┘  └──────────────┘  └──────────────┘  └──────────────┘      │
├──────────────────────────────────────────────────────────────────────────────┤
│                              NetConduit                                      │
│  Frame encoding • Channel management • Backpressure • Priority queuing       │
├──────────────────────────────────────────────────────────────────────────────┤
│  Transport: TCP │ WebSocket │ UDP │ IPC │ QUIC │ Any Stream                  │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Features

- **Multiple channels** over a single TCP/WebSocket/any stream connection
- **Credit-based backpressure** for flow control
- **Priority queuing** - higher priority frames sent first
- **Auto-reconnection** with channel state restoration
- **Native AOT compatible** - no reflection in core
- **Modern .NET** - targets .NET 8, 9, and 10

## Quick Start

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

## Documentation

📖 **[Full Documentation](docs/index.md)** - Complete guides, API reference, and examples

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Installation and first steps |
| [Transports](docs/transports/index.md) | TCP, WebSocket, UDP, IPC, QUIC |
| [Transits](docs/transits/index.md) | MessageTransit, DeltaTransit, DuplexStream, Stream |
| [Concepts](docs/concepts/index.md) | Channels, backpressure, priority, reconnection |
| [API Reference](docs/api/index.md) | Configuration options, statistics |
| [Samples](docs/samples/index.md) | Complete example applications |

## Samples

| Sample | Description |
|--------|-------------|
| [GroupChat](samples/NetConduit.Samples.GroupChat) | Multi-user chat with MessageTransit (TCP/WebSocket) |
| [FileTransfer](samples/NetConduit.Samples.FileTransfer) | Concurrent file transfers with progress |
| [Pong](samples/NetConduit.Samples.Pong) | Real-time multiplayer game with DeltaTransit |
| [RemoteShell](samples/NetConduit.Samples.RemoteShell) | SSH-like remote command execution |
| [RpcFramework](samples/NetConduit.Samples.RpcFramework) | Request/response RPC pattern |
| [TcpTunnel](samples/NetConduit.Samples.TcpTunnel) | Port forwarding via relay (like ngrok) |

## License

MIT License - see [LICENSE](LICENSE) for details.
