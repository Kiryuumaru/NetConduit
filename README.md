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

# UDP transport (with built-in reliability layer if you want TCP like UDP for some reason)
dotnet add package NetConduit.Udp

# IPC transport (named pipes on Windows, Unix sockets on Linux/macOS)
dotnet add package NetConduit.Ipc

# QUIC transport (.NET 9+ only, requires OS support)
dotnet add package NetConduit.Quic
```

## Quick Start

### TCP Client

```csharp
using NetConduit;
using NetConduit.Tcp;

// Create options with StreamFactory for connection + reconnection
var options = TcpMultiplexer.CreateOptions("localhost", 5000);

// Create multiplexer from options
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Open a channel and send data
var channel = await mux.OpenChannelAsync(
    new ChannelOptions { ChannelId = "my-channel" });

await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, Server!"));
await channel.DisposeAsync();  // Sends FIN, closes channel gracefully
```

### TCP Server

```csharp
using NetConduit;
using NetConduit.Tcp;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

// Accept client and create multiplexer with reconnection support
var options = TcpMultiplexer.CreateServerOptions(listener);
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Accept channels from client
await foreach (var channel in mux.AcceptChannelsAsync())
{
    // Each channel is a Stream - read data
    var buffer = new byte[1024];
    var bytesRead = await channel.ReadAsync(buffer);
    Console.WriteLine($"Received on {channel.ChannelId}: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
}
```

### WebSocket

```csharp
using NetConduit;
using NetConduit.WebSocket;

// Client - create options with StreamFactory
var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/ws");
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Server (ASP.NET Core) - create options for accepted WebSocket
app.MapGet("/ws", async (HttpContext context) =>
{
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var options = WebSocketMultiplexer.CreateServerOptions(webSocket);
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    // ...
});
```

### UDP

```csharp
using NetConduit;
using NetConduit.Udp;

// Client - create options with StreamFactory
var clientOptions = UdpMultiplexer.CreateOptions("localhost", 5000);
var client = StreamMultiplexer.Create(clientOptions);
var clientRunTask = client.Start();
await client.WaitForReadyAsync();

// Server - create options to accept connection
var serverOptions = UdpMultiplexer.CreateServerOptions(port: 5000);
var server = StreamMultiplexer.Create(serverOptions);
var serverRunTask = server.Start();
await server.WaitForReadyAsync();

// Use channels normally - UDP reliability is handled automatically
var channel = await client.OpenChannelAsync(new() { ChannelId = "data" });
await channel.WriteAsync(data);
```

### IPC (Inter-Process Communication)

```csharp
using NetConduit;
using NetConduit.Ipc;

// On Windows: uses named pipes
// On Linux/macOS: uses Unix domain sockets

// Client - create options with StreamFactory
var clientOptions = IpcMultiplexer.CreateOptions("my-app-ipc");
var client = StreamMultiplexer.Create(clientOptions);
var clientRunTask = client.Start();
await client.WaitForReadyAsync();

// Server - create options to accept connections
var serverOptions = IpcMultiplexer.CreateServerOptions("my-app-ipc");
var server = StreamMultiplexer.Create(serverOptions);
var serverRunTask = server.Start();
await server.WaitForReadyAsync();

// Use channels normally
var channel = await client.OpenChannelAsync(new() { ChannelId = "rpc" });
```

### QUIC

```csharp
using NetConduit;
using NetConduit.Quic;
using System.Net;

// Requires .NET 9+ and OS support (Windows 11+, Linux with msquic)

// Client - create options with StreamFactory
var clientOptions = QuicMultiplexer.CreateOptions("localhost", 5000, allowInsecure: true);
var client = StreamMultiplexer.Create(clientOptions);
var clientRunTask = client.Start();
await client.WaitForReadyAsync();

// Server - create listener with certificate, then create options
var listener = await QuicMultiplexer.ListenAsync(
    new IPEndPoint(IPAddress.Any, 5000), 
    certificate);
var serverOptions = QuicMultiplexer.CreateServerOptions(listener);
var server = StreamMultiplexer.Create(serverOptions);
var serverRunTask = server.Start();
await server.WaitForReadyAsync();

// Use channels normally - benefits from QUIC's built-in multiplexing
var channel = await client.OpenChannelAsync(new() { ChannelId = "stream" });
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

Adaptive credit-based flow control prevents fast senders from overwhelming slow receivers:

```csharp
var options = new ChannelOptions
{
    ChannelId = "controlled",
    MinCredits = 64 * 1024,            // 64KB minimum buffer
    MaxCredits = 4 * 1024 * 1024,      // 4MB maximum buffer (starts here)
    CreditGrantThreshold = 0.5,        // Auto-grant when 50% consumed
    SendTimeout = TimeSpan.FromSeconds(30)  // Timeout if credits exhausted
};

var channel = await mux.OpenChannelAsync(options);
```

### Transits

Transits add semantic meaning to channels. Use extension methods on the multiplexer for easy creation:

```csharp
using NetConduit.Transits;

// Open a write-only stream
var writeStream = await mux.OpenStreamAsync("upload");

// Accept a read-only stream
var readStream = await mux.AcceptStreamAsync("download");

// Open a duplex stream with single channel ID
// Creates "chat>>" for writing and accepts "chat<<" for reading
var duplex = await mux.OpenDuplexStreamAsync("chat");

// Or specify separate channel IDs
var duplex2 = await mux.OpenDuplexStreamAsync("my-out", "their-out");

// Open a message transit with single channel ID
// Creates "rpc>>" for sending and accepts "rpc<<" for receiving
var transit = await mux.OpenMessageTransitAsync<Request, Response>(
    "rpc",
    MyContext.Default.Request,
    MyContext.Default.Response);
```

#### MessageTransit - Send/receive JSON messages

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define message types with AOT-compatible serialization
public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Create transit with single channel ID (uses "chat>>" and "chat<<")
var transit = await mux.OpenMessageTransitAsync<ChatMessage, ChatMessage>(
    "chat",
    ChatContext.Default.ChatMessage,
    ChatContext.Default.ChatMessage);

// Send/receive messages
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
var msg = await transit.ReceiveAsync();
Console.WriteLine($"{msg.User}: {msg.Text}");
```

#### DuplexStreamTransit - Bidirectional stream

```csharp
// Open duplex stream with single channel ID (uses "data>>" and "data<<")
var duplex = await mux.OpenDuplexStreamAsync("data");

// Use with any Stream API
await duplex.WriteAsync(data);
var bytesRead = await duplex.ReadAsync(buffer);
```

#### StreamTransit - Simplex stream

```csharp
// Open/accept streams directly from multiplexer
var writeStream = await mux.OpenStreamAsync("upload");
var readStream = await mux.AcceptStreamAsync("download");
```

### Disconnection Events

NetConduit provides detailed disconnection events for both the multiplexer and individual channels:

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);

// Multiplexer-level disconnection event
mux.OnDisconnected += (reason, exception) =>
{
    Console.WriteLine($"Disconnected: {reason}");
    // DisconnectReason: GoAwayReceived, TransportError, LocalDispose
    if (exception != null)
        Console.WriteLine($"Error: {exception.Message}");
};

// Check disconnect reason after disconnect
if (mux.DisconnectReason.HasValue)
    Console.WriteLine($"Was disconnected due to: {mux.DisconnectReason}");
```

Channel-level close events provide granular control:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

channel.OnClosed += (reason, exception) =>
{
    Console.WriteLine($"Channel closed: {reason}");
    // ChannelCloseReason: LocalClose, RemoteFin, RemoteError, TransportFailed, MuxDisposed
};

// Check close reason
if (channel.CloseReason.HasValue)
    Console.WriteLine($"Channel was closed due to: {channel.CloseReason}");
```

Writing to a closed channel throws `ChannelClosedException`:

```csharp
try
{
    await channel.WriteAsync(data);
}
catch (ChannelClosedException ex)
{
    Console.WriteLine($"Cannot write to channel '{ex.ChannelId}': {ex.CloseReason}");
}
```

### Graceful Shutdown

Configure graceful shutdown timeout for clean disconnection:

```csharp
var options = new MultiplexerOptions
{
    GracefulShutdownTimeout = TimeSpan.FromSeconds(5)  // Default: 5 seconds
};

// DisposeAsync sends GOAWAY, waits for channels to close gracefully,
// then aborts remaining channels after timeout
await mux.DisposeAsync();
```

### Reconnection

NetConduit supports automatic reconnection with channel state restoration via StreamFactory:

```csharp
// StreamFactory enables automatic reconnection - when connection drops,
// multiplexer calls StreamFactory again to establish new connection
var options = TcpMultiplexer.CreateOptions("localhost", 5000);
options.EnableReconnection = true;
options.ReconnectTimeout = TimeSpan.FromSeconds(60);
options.ReconnectBufferSize = 1024 * 1024;  // 1MB buffer for pending data

var mux = StreamMultiplexer.Create(options);

mux.OnDisconnected += (reason, ex) => Console.WriteLine("Disconnected, reconnecting...");
mux.OnReconnected += () => Console.WriteLine("Reconnected!");

var runTask = mux.Start();
await mux.WaitForReadyAsync();

// On disconnect, multiplexer automatically calls StreamFactory to reconnect
// Channels remain open, pending data is buffered and sent after reconnection
```

## Configuration

### MultiplexerOptions

| Option | Default | Description |
|--------|---------|-------------|
| `StreamFactory` | *required* | Async factory that creates stream pairs for connection/reconnection |
| `MaxFrameSize` | 16MB | Maximum payload per frame |
| `PingInterval` | 30s | Heartbeat interval |
| `PingTimeout` | 10s | Max wait for pong |
| `MaxMissedPings` | 3 | Missed pings before disconnect |
| `EnableReconnection` | true | Enable reconnection support |
| `ReconnectTimeout` | 60s | Max wait for reconnection |
| `GracefulShutdownTimeout` | 5s | Timeout for graceful mux/channel shutdown |
| `FlushMode` | Batched | Frame flushing strategy |
| `FlushInterval` | 1ms | Batched flush interval |

### ChannelOptions

| Option | Default | Description |
|--------|---------|-------------|
| `ChannelId` | *required* | Unique channel identifier (0-1024 bytes UTF-8) |
| `MinCredits` | 64KB | Minimum buffer allowance |
| `MaxCredits` | 4MB | Maximum buffer allowance (starts here, adapts down) |
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

### Backpressure Visibility

Monitor backpressure in real-time with detailed statistics and events:

```csharp
// Per-channel backpressure stats
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });

// Subscribe to backpressure events
channel.OnCreditStarvation += () => 
    Console.WriteLine($"Channel '{channel.ChannelId}' is blocked - waiting for credits");

channel.OnCreditRestored += (waitTime) => 
    Console.WriteLine($"Credits restored after {waitTime.TotalMilliseconds}ms");

// Check channel stats
Console.WriteLine($"Credit starvation events: {channel.Stats.CreditStarvationCount}");
Console.WriteLine($"Total wait time: {channel.Stats.TotalWaitTimeForCredits}");
Console.WriteLine($"Longest single wait: {channel.Stats.LongestWaitForCredits}");
Console.WriteLine($"Currently waiting: {channel.Stats.IsWaitingForCredits}");

// Mux-level aggregated backpressure stats
Console.WriteLine($"Total starvation events: {mux.Stats.TotalCreditStarvationEvents}");
Console.WriteLine($"Channels waiting: {mux.Stats.ChannelsCurrentlyWaitingForCredits}");
Console.WriteLine($"System backpressure: {mux.Stats.IsExperiencingBackpressure}");
```

| Channel Stat | Description |
|--------------|-------------|
| `CreditStarvationCount` | Times credits hit zero while sending |
| `TotalWaitTimeForCredits` | Cumulative time waiting for credits |
| `LongestWaitForCredits` | Longest single credit wait |
| `IsWaitingForCredits` | Currently blocked waiting for credits |
| `CurrentWaitDuration` | Duration of current wait (if waiting) |

| Mux Stat | Description |
|----------|-------------|
| `TotalCreditStarvationEvents` | All starvation events across channels |
| `ChannelsCurrentlyWaitingForCredits` | Count of blocked channels |
| `TotalCreditWaitTime` | Cumulative wait time (all channels) |
| `IsExperiencingBackpressure` | Any channel currently blocked |

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
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ │
│  │   .Tcp     │ │ .WebSocket │ │    .Udp    │ │    .Ipc    │ │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ │
│  ┌────────────┐ ┌──────────────────────────────────────────┐ │
│  │   .Quic    │ │            Any Stream                    │ │
│  └────────────┘ └──────────────────────────────────────────┘ │
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

### Benchmarks

### Raw TCP vs Multiplexed TCP Comparison

Compares N separate TCP connections (Raw TCP) vs 1 TCP connection with N multiplexed channels (Mux TCP):

| Channels | Data/Channel | Raw TCP | Mux TCP | Ratio | Notes |
|----------|--------------|---------|---------|-------|-------|
| 1 | 1 KB | 60 ms | 61 ms | 1.02x | Near parity |
| 1 | 100 KB | 61 ms | 66 ms | 1.09x | Near parity |
| 1 | 1 MB | 61 ms | 67 ms | 1.09x | Near parity |
| 10 | 1 KB | 61 ms | 82 ms | 1.34x | Similar performance |
| 10 | 100 KB | 59 ms | 88 ms | 1.49x | Similar performance |
| 10 | 1 MB | 64 ms | 97 ms | 1.52x | Similar performance |
| 100 | 1 KB | 61 ms | 197 ms | 3.22x | Raw TCP faster |
| 100 | 100 KB | 73 ms | 128 ms | 1.75x | Raw TCP faster |
| 100 | 1 MB | 82 ms | 238 ms | 2.90x | Raw TCP faster |
| 1000 | 1 KB | 1,282 ms | 1,097 ms | **0.86x** | **Mux faster!** |
| 1000 | 100 KB | 675 ms | 571 ms | **0.85x** | **Mux faster!** |
| 1000 | 1 MB | **FAILED*** | 1,398 ms | - | **Mux works, Raw TCP fails** |

*Raw TCP fails at 1000 connections × 1MB due to socket exhaustion (OS ephemeral port limits)

**Key Insights:**
- **Low channel counts (1-10)**: Near parity performance, Mux adds minimal overhead (~1.02-1.52x)
- **High channel counts (1000)**: **Mux outperforms Raw TCP** by 14-15% due to connection pooling efficiency
- **Resource efficiency**: Mux uses 1 TCP connection vs N connections, drastically reducing OS overhead
- **Extreme concurrency**: At 100+ channels with 1MB data, Raw TCP hits OS socket limits while Mux's single-connection design avoids this
- **Trade-off**: Small overhead at low counts, significant advantage at high counts
- **Best use cases**: Mux excels when you need many logical streams over limited connections (WebSocket, mobile, firewalls, NAT traversal)

### Running Benchmarks

```bash
cd benchmarks/NetConduit.Benchmarks
dotnet run -c Release
```

Available benchmark classes:
- `TcpVsMuxBenchmark` - Direct Raw TCP vs Multiplexed comparison
- `TcpThroughputBenchmark` - TCP throughput with varying channel counts and data sizes
- `UdpThroughputBenchmark` - UDP transport throughput benchmarks
- `IpcThroughputBenchmark` - IPC transport throughput benchmarks
- `WebSocketThroughputBenchmark` - WebSocket transport throughput benchmarks
- `TransportComparisonBenchmark` - Compare all transports side-by-side

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions welcome! Please read our contributing guidelines before submitting PRs.
