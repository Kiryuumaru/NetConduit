# NetConduit

**Transport-agnostic stream multiplexer for .NET** — Multiple virtual channels over a single bidirectional stream.

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
dotnet add package NetConduit
dotnet add package NetConduit.Tcp
```

```csharp
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;
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

## Packages

| Package | Description |
|---------|-------------|
| `NetConduit` | Core multiplexer + transits |
| `NetConduit.Tcp` | TCP transport |
| `NetConduit.WebSocket` | WebSocket transport |
| `NetConduit.Udp` | UDP with reliability layer |
| `NetConduit.Ipc` | IPC (loopback/Unix sockets) |
| `NetConduit.Quic` | QUIC transport |

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│  Application                                                   │
├────────────────────────────────────────────────────────────────┤
│  Transits: MessageTransit, DeltaTransit, DuplexStream, Stream  │
├────────────────────────────────────────────────────────────────┤
│  Core: Framing, Channels, Backpressure, Priority, Reconnect    │
├────────────────────────────────────────────────────────────────┤
│  Transports: TCP, WebSocket, UDP, IPC, QUIC                    │
└────────────────────────────────────────────────────────────────┘
```

## Samples

| Sample | Description |
|--------|-------------|
| GroupChat | Multi-user chat (TCP + WebSocket) |
| FileTransfer | Parallel file transfers with progress |
| Pong | Multiplayer game with delta state sync |
| RemoteShell | SSH-like remote command execution |
| RpcFramework | Type-safe JSON-RPC pattern |
| TcpTunnel | Port forwarding like ngrok |
| Scoreboard | Live leaderboard with reconnection |

```bash
dotnet run --project samples/NetConduit.Samples.GroupChat -- server tcp 5000
dotnet run --project samples/NetConduit.Samples.GroupChat -- client tcp 5000 127.0.0.1 Alice
```

## Documentation

Full documentation at [`docs/`](docs/index.md):

- [Getting Started](docs/getting-started.md)
- [Transports](docs/transports/index.md) — TCP, WebSocket, UDP, IPC, QUIC
- [Transits](docs/transits/index.md) — MessageTransit, DeltaTransit, DuplexStream, Stream
- [Concepts](docs/concepts/index.md) — Channels, backpressure, priority, reconnection
- [API Reference](docs/api/index.md)

## Building

```bash
dotnet build
dotnet test
```

## License

See [LICENSE](LICENSE).
