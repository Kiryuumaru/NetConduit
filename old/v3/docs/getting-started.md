# Getting Started

## Installation

```bash
# Core package (required)
dotnet add package NetConduit

# Choose your transport(s):
dotnet add package NetConduit.Tcp        # TCP sockets
dotnet add package NetConduit.WebSocket  # WebSocket (client + ASP.NET Core)
dotnet add package NetConduit.Udp        # UDP with reliability layer
dotnet add package NetConduit.Ipc        # TCP loopback (Windows) / Unix sockets
dotnet add package NetConduit.Quic       # QUIC (requires OS support)
```

See [Transports](transports/index.md) for details on each transport.

## Your First Multiplexer

Create a [TCP](transports/tcp.md) server and client that communicate over [channels](concepts/channels.md).

### TCP Server

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Net;
using System.Net.Sockets;
using System.Text;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
mux.Start();
await mux.WaitForReadyAsync();

await foreach (var channel in mux.AcceptChannelsAsync())
{
    var buffer = new byte[1024];
    var bytesRead = await channel.ReadAsync(buffer);
    Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));
}
```

### TCP Client

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Text;

var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("localhost", 5000));
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("greeting");
await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, Server!"));
await channel.DisposeAsync();
```

## Using Transits (Recommended)

[Transits](transits/index.md) provide higher-level messaging patterns over raw channels.

### MessageTransit

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Server
await using var transit = await mux.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
await foreach (var msg in transit.ReceiveAllAsync(ct))
    Console.WriteLine($"[{msg.User}] {msg.Text}");

// Client
await using var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
```

### DeltaTransit

For state synchronization — only changed fields are sent:

```csharp
public record GameState(int Score, int Health);

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Sender
var sender = mux.OpenSendOnlyDeltaTransit("state", GameContext.Default.GameState);
await sender.SendAsync(new GameState(100, 50));
await sender.SendAsync(new GameState(150, 50));  // Only sends Score change

// Receiver
await using var receiver = await mux.AcceptReceiveOnlyDeltaTransitAsync("state", GameContext.Default.GameState);
await foreach (var state in receiver.ReceiveAllAsync(ct))
    UpdateUI(state);
```

### DuplexStreamTransit

Bidirectional byte streaming — like a virtual TCP connection:

```csharp
// Side A
await using var streamA = await mux.OpenDuplexStreamAsync("tunnel");
await streamA.WriteAsync(data);
var n = await streamA.ReadAsync(buffer);

// Side B
await using var streamB = await mux.AcceptDuplexStreamAsync("tunnel");
var n = await streamB.ReadAsync(buffer);
await streamB.WriteAsync(response);
```

## Lifecycle

```
Create → Start → WaitForReady → Use → GoAway/Dispose
```

```csharp
// Create
var mux = StreamMultiplexer.Create(options);

// Start background loops (handshake, read/write)
mux.Start();

// Wait for connection to be ready
await mux.WaitForReadyAsync();

// Use channels and transits...

// Graceful shutdown (optional)
await mux.GoAwayAsync();

// Cleanup
await mux.DisposeAsync();
```

## Next Steps

- [Transports](transports/index.md) — Choose TCP, WebSocket, UDP, IPC, or QUIC
- [Transits](transits/index.md) — Higher-level messaging patterns
- [Concepts](concepts/index.md) — Channels, backpressure, priority, reconnection
- [API Reference](api/index.md) — Full API documentation
- [Samples](samples/index.md) — Complete example applications
