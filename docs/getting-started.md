# Getting Started

## Installation

```bash
# Core package (required)
dotnet add package NetConduit

# Choose your transport(s):
dotnet add package NetConduit.Tcp        # TCP sockets
dotnet add package NetConduit.WebSocket  # WebSocket (client + ASP.NET Core)
dotnet add package NetConduit.Udp        # UDP with reliability layer
dotnet add package NetConduit.Ipc        # Named pipes / Unix sockets
dotnet add package NetConduit.Quic       # QUIC (.NET 9+, requires OS support)
```

See [Transports](transports/index.md) for details on each transport: [TCP](transports/tcp.md), [WebSocket](transports/websocket.md), [UDP](transports/udp.md), [IPC](transports/ipc.md), [QUIC](transports/quic.md).

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
_ = mux.Start();

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
_ = mux.Start();

var channel = await mux.OpenChannelAsync(new() { ChannelId = "greeting" });
await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, Server!"));
await channel.DisposeAsync();
```

## Using Transits (Recommended)

[Transits](transits/index.md) provide higher-level messaging patterns.

### [MessageTransit](transits/message.md)

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

### [DeltaTransit](transits/delta.md)

For state synchronization - only changed fields are sent:

```csharp
public record GameState(int Score, int Health);

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Receiver
await using var receiver = await mux.AcceptReceiveOnlyDeltaTransitAsync("state", GameContext.Default.GameState);
await foreach (var state in receiver.ReceiveAllAsync(ct))
    UpdateUI(state);

// Sender
await using var sender = await mux.OpenSendOnlyDeltaTransitAsync("state", GameContext.Default.GameState);
await sender.SendAsync(new GameState(100, 80));
await sender.SendAsync(new GameState(150, 80));  // Only Score delta sent
```

## Key Concepts

### [Channels](concepts/channels.md) are Simplex (One-Way)

`OpenChannelAsync` returns a **WriteChannel**, `AcceptChannelAsync` returns a **ReadChannel**.

```csharp
// Client → WriteChannel
var sendChannel = await mux.OpenChannelAsync(new() { ChannelId = "data" });
await sendChannel.WriteAsync(data);

// Server → ReadChannel  
var receiveChannel = await mux.AcceptChannelAsync("data");
await receiveChannel.ReadAsync(buffer);
```

For bidirectional, use [DuplexStreamTransit](transits/duplex-stream.md) or open two channels.

### Channels Inherit from Stream

[Channels](concepts/channels.md) inherit from `Stream`:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "file" });
await sourceStream.CopyToAsync(channel);
```

## Next Steps

- [Choose a transport](transports/index.md) for your use case
- [Use transits](transits/index.md) for structured messaging
- [Understand backpressure](concepts/backpressure.md) for flow control
- [Browse API reference](api/index.md) for configuration options
