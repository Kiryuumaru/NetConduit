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

## Installation

```bash
# Core package
dotnet add package NetConduit

# TCP transport helper
dotnet add package NetConduit.Tcp

# WebSocket transport helper  
dotnet add package NetConduit.WebSocket
```

## Quick Start

### TCP Server

```csharp
using NetConduit;
using NetConduit.Tcp;

// Accept a TCP connection using extension method
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
var connection = await listener.AcceptMuxAsync();
var runTask = await connection.StartAsync();

// Accept channels from clients
await foreach (var channel in connection.Multiplexer.AcceptChannelsAsync())
{
    // Each channel is a Stream - read data
    var buffer = new byte[1024];
    var bytesRead = await channel.ReadAsync(buffer);
    Console.WriteLine($"Received on {channel.ChannelId}: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
}
```

### TCP Client

```csharp
using NetConduit;
using NetConduit.Tcp;

// Connect to server using extension method
var client = new TcpClient();
var connection = await client.ConnectMuxAsync("localhost", 5000);
var runTask = await connection.StartAsync();

// Open a channel and send data
var channel = await connection.Multiplexer.OpenChannelAsync(
    new ChannelOptions { ChannelId = "my-channel" });

await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, Server!"));
await channel.DisposeAsync();  // Sends FIN, closes channel gracefully
```

### WebSocket

```csharp
using NetConduit;
using NetConduit.WebSocket;

// Client using extension method
var webSocket = new ClientWebSocket();
var connection = await webSocket.ConnectMuxAsync("ws://localhost:5000/ws");
var runTask = await connection.StartAsync();

// Server (ASP.NET Core) using extension method
app.MapGet("/ws", async (HttpContext context) =>
{
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = webSocket.AsMux();
    var runTask = await connection.StartAsync();
    // ...
});
```

## Core Concepts

### Channels

Channels are **simplex (one-way)** streams:
- **WriteChannel**: Opened by this side for sending data
- **ReadChannel**: Accepted from remote side for receiving data

For bidirectional communication, open two channels:

```csharp
// Side A opens a channel - gets WriteChannel
var sendChannel = await muxA.OpenChannelAsync(new() { ChannelId = "A-to-B" });

// Side B accepts it - gets ReadChannel  
var receiveChannel = await muxB.AcceptChannelAsync("A-to-B");

// For reverse direction, Side B opens another channel
var reverseSend = await muxB.OpenChannelAsync(new() { ChannelId = "B-to-A" });
var reverseReceive = await muxA.AcceptChannelAsync("B-to-A");
```

### Raw Stream Usage

Channels inherit from `Stream`, so they work with any streaming API:

```csharp
// Open a channel
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

// Use as Stream - works with StreamReader/Writer, CopyToAsync, etc.
using var writer = new StreamWriter(channel);
await writer.WriteLineAsync("Hello!");
await writer.FlushAsync();
```

### Priority

Set channel priority at open time (0-255, higher = higher priority):

```csharp
// Control messages - highest priority
var controlChannel = await mux.OpenChannelAsync(new() 
{ 
    ChannelId = "control",
    Priority = ChannelPriority.Highest  // 255
});

// Bulk data - lower priority
var dataChannel = await mux.OpenChannelAsync(new() 
{ 
    ChannelId = "bulk-data",
    Priority = ChannelPriority.Low  // 64
});
```

### Backpressure

Credit-based flow control prevents fast senders from overwhelming slow receivers:

```csharp
var options = new ChannelOptions
{
    ChannelId = "controlled",
    InitialCredits = 1024 * 1024,     // 1MB buffer allowance
    CreditGrantThreshold = 0.5,        // Auto-grant when 50% consumed
    SendTimeout = TimeSpan.FromSeconds(30)  // Timeout if credits exhausted
};

var channel = await mux.OpenChannelAsync(options);
```

### Transits

Transits add semantic meaning to channels:

#### MessageTransit - Send/receive JSON messages

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define message types with AOT-compatible serialization
public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Create transit
var transit = new MessageTransit<ChatMessage, ChatMessage>(
    writeChannel, readChannel,
    ChatContext.Default.ChatMessage,
    ChatContext.Default.ChatMessage);

// Send/receive messages
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
var msg = await transit.ReceiveAsync();
if (msg is not null)
    Console.WriteLine($"{msg.User}: {msg.Text}");
```

#### DuplexStreamTransit - Bidirectional stream

```csharp
// Wrap channel pair as bidirectional Stream
var duplex = new DuplexStreamTransit(writeChannel, readChannel);

// Use with any Stream API
await duplex.WriteAsync(data);
var bytesRead = await duplex.ReadAsync(buffer);
```

### Reconnection

NetConduit supports automatic reconnection with channel state restoration:

```csharp
var options = new MultiplexerOptions
{
    EnableReconnection = true,
    ReconnectTimeout = TimeSpan.FromSeconds(60),
    ReconnectBufferSize = 1024 * 1024  // 1MB buffer for pending data
};

var mux = new StreamMultiplexer(stream, stream, options);

mux.OnDisconnected += () => Console.WriteLine("Disconnected, waiting for reconnect...");
mux.OnReconnected += () => Console.WriteLine("Reconnected!");

// After network disruption, reconnect with new streams
await mux.ReconnectAsync(newReadStream, newWriteStream);
```

## Configuration

### MultiplexerOptions

| Option | Default | Description |
|--------|---------|-------------|
| `MaxFrameSize` | 16MB | Maximum payload per frame |
| `PingInterval` | 30s | Heartbeat interval |
| `PingTimeout` | 10s | Max wait for pong |
| `MaxMissedPings` | 3 | Missed pings before disconnect |
| `EnableReconnection` | true | Enable reconnection support |
| `ReconnectTimeout` | 60s | Max wait for reconnection |
| `FlushMode` | Batched | Frame flushing strategy |
| `FlushInterval` | 1ms | Batched flush interval |

### ChannelOptions

| Option | Default | Description |
|--------|---------|-------------|
| `ChannelId` | *required* | Unique channel identifier (0-1024 bytes UTF-8) |
| `InitialCredits` | 1MB | Initial send buffer allowance |
| `CreditGrantThreshold` | 0.5 | Auto-grant when X% consumed |
| `SendTimeout` | 30s | Max wait for credits |
| `Priority` | Normal (128) | Channel priority (0-255) |

## Statistics

```csharp
// Multiplexer stats
var stats = mux.Stats;
Console.WriteLine($"Bytes sent: {stats.BytesSent}");
Console.WriteLine($"Open channels: {stats.OpenChannels}");
Console.WriteLine($"Last ping RTT: {stats.LastPingRtt}");

// Per-channel stats
var channelStats = channel.Stats;
Console.WriteLine($"Channel bytes: {channelStats.BytesSent}");
```

## Samples

The repository includes complete sample applications:

| Sample | Description |
|--------|-------------|
| [ChatCli](samples/NetConduit.Samples.ChatCli) | CLI chat app with bidirectional messaging |
| [FileTransfer](samples/NetConduit.Samples.FileTransfer) | File transfer with progress and concurrent transfers |
| [RpcFramework](samples/NetConduit.Samples.RpcFramework) | Request/response RPC pattern |
| [VideoStream](samples/NetConduit.Samples.VideoStream) | Simulated video/audio streaming with priority channels |

Run samples:
```bash
# Chat server
cd samples/NetConduit.Samples.ChatCli
dotnet run -- server 5000 Alice

# Chat client (another terminal)
dotnet run -- client 5000 localhost Bob
```

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                         Application                          │
├──────────────────────────────────────────────────────────────┤
│  Transit Layer (Optional)                                    │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐  │
│  │ MessageTransit │  │ StreamTransit  │  │ DuplexStream   │  │
│  └───────┬────────┘  └───────┬────────┘  └───────┬────────┘  │
├──────────┴───────────────────┴───────────────────┴───────────┤
│                         NetConduit                           │
│  - Frame encoding/decoding (9-byte header)                   │
│  - Channel management (string ChannelId)                     │
│  - Credit-based backpressure                                 │
│  - Priority queuing                                          │
│  - Ping/pong heartbeat                                       │
│  - GOAWAY graceful shutdown                                  │
├──────────────────────────────────────────────────────────────┤
│  Transport Layer                                             │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐  │
│  │ NetConduit.Tcp │  │ NetConduit.WS  │  │  Any Stream    │  │
│  └────────────────┘  └────────────────┘  └────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

## Frame Format

```
┌─────────────────────────────────────────────────────────────┐
│ Channel Index (4B) │ Flags (1B) │ Length (4B) │ Payload     │
└─────────────────────────────────────────────────────────────┘
```

- 9-byte header, big-endian encoding
- Max 16MB payload (configurable)
- Frame types: DATA, INIT, FIN, ACK, ERR

## Performance

NetConduit uses several techniques for high performance:

- `System.IO.Pipelines` for zero-copy reads
- `ArrayPool<byte>` for buffer reuse
- `Channel<T>` for lock-free queuing
- `stackalloc` for header serialization
- `BinaryPrimitives` for fast encoding

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions welcome! Please read our contributing guidelines before submitting PRs.
