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

## Your First Multiplexer

### TCP Server

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Net;
using System.Net.Sockets;
using System.Text;

// 1. Start TCP listener
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("Server listening on port 5000...");

// 2. Create multiplexer with server options
var options = TcpMultiplexer.CreateServerOptions(listener);
var mux = StreamMultiplexer.Create(options);

// 3. Start the multiplexer
var runTask = mux.Start();
await mux.WaitForReadyAsync();
Console.WriteLine("Client connected!");

// 4. Accept channels from client
await foreach (var channel in mux.AcceptChannelsAsync())
{
    Console.WriteLine($"New channel: {channel.ChannelId}");
    
    // Read data from channel
    var buffer = new byte[1024];
    var bytesRead = await channel.ReadAsync(buffer);
    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
    Console.WriteLine($"Received: {message}");
}
```

### TCP Client

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Text;

// 1. Create multiplexer with client options
var options = TcpMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);

// 2. Start and wait for connection
var runTask = mux.Start();
await mux.WaitForReadyAsync();
Console.WriteLine("Connected to server!");

// 3. Open a channel and send data
var channel = await mux.OpenChannelAsync(new() { ChannelId = "greeting" });
await channel.WriteAsync(Encoding.UTF8.GetBytes("Hello, Server!"));

// 4. Close channel gracefully
await channel.DisposeAsync();

// 5. Shutdown multiplexer
await mux.DisposeAsync();
```

## Using Transits (Recommended)

Transits provide higher-level messaging patterns. Here's the most common pattern:

### MessageTransit with ReceiveAllAsync

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define your message type
public record ChatMessage(string User, string Text);

[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Server: accept and process messages
await using var transit = await mux.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    Console.WriteLine($"[{message.User}] {message.Text}");
}

// Client: send messages
await using var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
await transit.SendAsync(new ChatMessage("Alice", "How are you?"));
```

### DeltaTransit with ReceiveAllAsync

For state synchronization:

```csharp
public record GameState(int Score, int Health);

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Receiver: process state updates
await using var receiver = await mux.AcceptReceiveOnlyDeltaTransitAsync("state", GameContext.Default.GameState);

await foreach (var state in receiver.ReceiveAllAsync(cancellationToken))
{
    UpdateUI(state);
}

// Sender: push state changes (only deltas are sent)
await using var sender = await mux.OpenSendOnlyDeltaTransitAsync("state", GameContext.Default.GameState);

var state = new GameState(100, 80);
await sender.SendAsync(state);

state = state with { Score = 150 };  // Only Score change is sent
await sender.SendAsync(state);
```

## Key Concepts

### Channels are Simplex (One-Way)

When you open a channel, you get a **WriteChannel** for sending. The other side accepts it as a **ReadChannel** for receiving.

```csharp
// Client opens → gets WriteChannel
var sendChannel = await clientMux.OpenChannelAsync(new() { ChannelId = "data" });
await sendChannel.WriteAsync(data);

// Server accepts → gets ReadChannel
var receiveChannel = await serverMux.AcceptChannelAsync("data");
var bytesRead = await receiveChannel.ReadAsync(buffer);
```

For bidirectional communication, use [DuplexStreamTransit](transits/duplex-stream.md) or open two channels.

### Channels are Streams

Channels inherit from `Stream`, so they work with any .NET streaming API:

```csharp
var channel = await mux.OpenChannelAsync(new() { ChannelId = "text" });

// Use with StreamWriter
using var writer = new StreamWriter(channel);
await writer.WriteLineAsync("Hello!");
await writer.FlushAsync();

// Use with CopyToAsync
await sourceStream.CopyToAsync(channel);
```

### Graceful Shutdown

Always dispose the multiplexer when done:

```csharp
// Sends GOAWAY frame, waits for channels to close gracefully
await mux.DisposeAsync();
```

## Next Steps

- [Choose a transport](transports/index.md) for your use case
- [Use transits](transits/index.md) for structured messaging (MessageTransit, DeltaTransit)
- [Understand backpressure](concepts/backpressure.md) for flow control
- [Browse API reference](api/index.md) for configuration options
