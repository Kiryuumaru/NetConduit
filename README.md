# NetConduit

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Transport-agnostic stream multiplexer for .NET.** Run many independent virtual channels over a single bidirectional stream — TCP, WebSocket, UDP, IPC, or QUIC — with credit-based backpressure, priority queuing, keepalive, graceful shutdown, and automatic reconnection with replay.

| Core | NuGet | Description |
| --- | --- | --- |
| [`NetConduit`](https://www.nuget.org/packages/NetConduit) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.svg?label=)](https://www.nuget.org/packages/NetConduit) | Multiplexer, channels, framing, reconnection |

| Transits | NuGet | Description |
| --- | --- | --- |
| [`NetConduit.Transit.Stream`](https://www.nuget.org/packages/NetConduit.Transit.Stream) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.Stream.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.Stream) | Simplex `Stream` over one channel |
| [`NetConduit.Transit.DuplexStream`](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.DuplexStream.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream) | Bidirectional `Stream` over a channel pair |
| [`NetConduit.Transit.Message`](https://www.nuget.org/packages/NetConduit.Transit.Message) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.Message.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.Message) | Length-prefixed typed JSON messages |
| [`NetConduit.Transit.DeltaMessage`](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transit.DeltaMessage.svg?label=)](https://www.nuget.org/packages/NetConduit.Transit.DeltaMessage) | State sync via JSON deltas |

| Transports | NuGet | Description |
| --- | --- | --- |
| [`NetConduit.Transport.Tcp`](https://www.nuget.org/packages/NetConduit.Transport.Tcp) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Tcp.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Tcp) | TCP sockets |
| [`NetConduit.Transport.WebSocket`](https://www.nuget.org/packages/NetConduit.Transport.WebSocket) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.WebSocket.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.WebSocket) | WebSocket (`ClientWebSocket` + ASP.NET) |
| [`NetConduit.Transport.Udp`](https://www.nuget.org/packages/NetConduit.Transport.Udp) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Udp.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Udp) | UDP with a reliability shim |
| [`NetConduit.Transport.Ipc`](https://www.nuget.org/packages/NetConduit.Transport.Ipc) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Ipc.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Ipc) | TCP loopback (Windows) / Unix domain sockets (Linux/macOS) |
| [`NetConduit.Transport.Quic`](https://www.nuget.org/packages/NetConduit.Transport.Quic) | [![NuGet](https://img.shields.io/nuget/v/NetConduit.Transport.Quic.svg?label=)](https://www.nuget.org/packages/NetConduit.Transport.Quic) | QUIC over TLS 1.3 |

Each transport and transit package depends on `NetConduit`, so installing one pulls in the core.

## At a glance

```
                +-----------------------------------------+
                |               Application               |
                +-----------------------------------------+
                |     Transits (optional, layered)        |
                |  Stream  DuplexStream  Message  Delta   |
                +-----------------------------------------+
                |          NetConduit (core)              |
                |  framing | channels | flow control |    |
                |  priority | keepalive | reconnect       |
                +-----------------------------------------+
                |   Transport (single bidirectional       |
                |   IStreamPair)                          |
                |   TCP  WS  UDP  IPC  QUIC               |
                +-----------------------------------------+
```

One physical stream carries any number of independent virtual channels. Each channel has its own buffer, priority, and lifecycle.

## Install

Pick one transport and any transits you need:

```bash
dotnet add package NetConduit.Transport.Tcp
dotnet add package NetConduit.Transit.Message
```

Target frameworks: **net8.0**, **net9.0**, **net10.0**. AOT-compatible.

## Quick start

A TCP server and client exchanging typed JSON messages.

```csharp
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Models;
using NetConduit.Transit.Message;
using NetConduit.Transport.Tcp;

public record Hello(string Name, int Count);

[JsonSerializable(typeof(Hello))]
internal partial class AppJson : JsonSerializerContext;
```

```csharp
// Server
using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5000);
listener.Start();
await using var server = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
server.Start();
await server.WaitForReadyAsync();

await using var inbox = await server.AcceptMessageTransitAsync<Hello>("hello", AppJson.Default.Hello);
await foreach (var msg in inbox.ReceiveAllAsync())
    Console.WriteLine($"got: {msg!.Name} x{msg.Count}");
```

```csharp
// Client
await using var client = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("127.0.0.1", 5000));
client.Start();
await client.WaitForReadyAsync();

await using var outbox = await client.OpenMessageTransitAsync<Hello>("hello", AppJson.Default.Hello);
await outbox.SendAsync(new Hello("Alice", 1));
await outbox.SendAsync(new Hello("Alice", 2));
```

That's it. The server can open additional channels for file transfers, control plane, state sync, etc. — all over the same TCP connection.

## What you get

- **Many channels per stream.** Open and accept named channels at will. Each is independently flow-controlled and prioritized.
- **Pluggable transport.** Swap TCP for WebSocket, UDP, IPC, or QUIC by changing one factory call.
- **Transits.** Optional layers that turn a channel pair into a `Stream`, a typed message queue, or a state-sync delta channel.
- **Backpressure.** Per-channel slab buffers with credit-based flow control. Slow consumers slow down their producer without blocking other channels.
- **Priority queuing.** Five priority levels (`Lowest` … `Highest`) control writer ordering when channels compete for the wire.
- **Heartbeats.** Configurable ping/pong with a missed-ping threshold detects dead connections quickly.
- **Graceful shutdown.** `GoAwayAsync` drains in-flight data before tearing down.
- **Reconnection with replay.** When configured, the multiplexer rebuilds the connection on transport failure and replays buffered frames so open channels survive.
- **AOT and trim safe.** Core uses no reflection. Message and delta transits accept source-generated `JsonTypeInfo<T>`.

## Documentation

- [Getting started](docs/getting-started.md)
- [Concepts](docs/concepts/index.md) — multiplexer, channels, transports, transits, backpressure, reconnection, events
- [Transports](docs/transports/index.md) — TCP, WebSocket, UDP, IPC, QUIC
- [Transits](docs/transits/index.md) — Stream, DuplexStream, Message, DeltaMessage
- [API reference](docs/api/index.md)
- [Samples](docs/samples/index.md)
- [Benchmarks](docs/benchmarks.md)

## Samples

Each sample is a runnable console app. See its README for usage.

| Sample | Demonstrates |
| --- | --- |
| [SimpleTcpTunnel](samples/SimpleTcpTunnel/README.md) | Port forwarding through a relay over TCP or WebSocket |
| [GroupChatSample](samples/GroupChatSample/README.md) | Multi-client chat broker (TCP or WebSocket) |
| [PongGame](samples/PongGame/README.md) | Real-time game state sync via `DeltaMessageTransit` |
| [FileTransferSample](samples/FileTransferSample/README.md) | Parallel file streaming with one channel per file |
| [RemoteShellSample](samples/RemoteShellSample/README.md) | SSH-like shell over a single TCP connection |
| [RpcFrameworkSample](samples/RpcFrameworkSample/README.md) | Async RPC over a typed message transit |
| [ScoreboardSample](samples/ScoreboardSample/README.md) | Persistent leaderboard with client reconnection |

## License

MIT — see [LICENSE](LICENSE).
