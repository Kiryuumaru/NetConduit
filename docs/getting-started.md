# Getting started

## Install

Pick a transport package, then add the transits you need. Each transport and transit package depends on the core `NetConduit` package.

```bash
dotnet add package NetConduit.Transport.Tcp
dotnet add package NetConduit.Transit.Message
```

Target frameworks: **net8.0**, **net9.0**, **net10.0**. AOT-compatible.

See [Packages](packages.md) for the full matrix.

## Mental model

NetConduit replaces "one TCP socket per concern" with "one transport, many channels":

```
                  +----------+                  +----------+
   open "files" ->|          |   ===========    |          |-> accept "files"
   open "chat"  ->|   mux    |== one stream ==> |   mux    |-> accept "chat"
   open "rpc"   ->|          |   ===========    |          |-> accept "rpc"
                  +----------+                  +----------+
                       ^                              ^
                       | StreamFactory                | StreamFactory
                       | (TCP / WS / UDP / IPC / QUIC)|
```

A channel is identified by a string ID. Both sides agree on the ID; one calls `OpenChannel`, the other calls `AcceptChannel`. Either side may open or accept.

## Your first server and client

A minimal TCP example that exchanges a typed JSON message.

### Shared message type

```csharp
using System.Text.Json.Serialization;

public record Hello(string Name, int Count);

[JsonSerializable(typeof(Hello))]
internal partial class AppJson : JsonSerializerContext;
```

### Server

```csharp
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Transit.Message;
using NetConduit.Transport.Tcp;

var listener = new TcpListener(IPAddress.Loopback, 5000);
listener.Start();

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
mux.Start();
await mux.WaitForReadyAsync();

await using var inbox = await mux.AcceptMessageTransitAsync<Hello>(
    "hello",
    AppJson.Default.Hello);

await foreach (var msg in inbox.ReceiveAllAsync())
    Console.WriteLine($"got: {msg!.Name} x{msg.Count}");
```

### Client

```csharp
using NetConduit;
using NetConduit.Transit.Message;
using NetConduit.Transport.Tcp;

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("127.0.0.1", 5000));
mux.Start();
await mux.WaitForReadyAsync();

await using var outbox = await mux.OpenMessageTransitAsync<Hello>(
    "hello",
    AppJson.Default.Hello);

await outbox.SendAsync(new Hello("Alice", 1));
await outbox.SendAsync(new Hello("Alice", 2));
```

## Lifecycle

```
Create  ->  Start  ->  WaitForReadyAsync  ->  Open / Accept channels  ->  GoAwayAsync / DisposeAsync
```

- `Create(options)` — build a multiplexer instance.
- `Start()` — kicks off the connect + handshake + read/write loops.
- `WaitForReadyAsync()` — waits for the first successful handshake. Required before opening or accepting channels.
- `OpenChannel` / `AcceptChannel` (and their transit equivalents) — return immediately in pending state; call `WaitForReadyAsync` on the channel/transit (or use the `…Async` extension) to await remote confirmation.
- `GoAwayAsync()` — graceful shutdown. Drains channels, then signals the remote.
- `await using` / `DisposeAsync()` — cancels everything and disposes the transport.

See [Multiplexer concepts](concepts/multiplexer.md) for details on each phase.

## What to read next

- [Concepts: Channels](concepts/channels.md) — open vs. accept, channel IDs, state transitions.
- [Concepts: Transits](concepts/transits.md) — when to wrap a channel with a transit.
- [Transports overview](transports/index.md) — which transport to pick.
- [API: `StreamMultiplexer`](api/stream-multiplexer.md) — full surface.
# Getting started

## Install

Pick a transport package, then add the transits you need. Each transport and transit package depends on the core `NetConduit` package.

```bash
dotnet add package NetConduit.Transport.Tcp
dotnet add package NetConduit.Transit.Message
```

Target frameworks: **net8.0**, **net9.0**, **net10.0**. AOT-compatible.

See [Packages](packages.md) for the full matrix.

## Mental model

NetConduit replaces "one TCP socket per concern" with "one transport, many channels":

```
                  +----------+                  +----------+
   open "files" ->|          |   ===========    |          |-> accept "files"
   open "chat"  ->|   mux    |== one stream ==> |   mux    |-> accept "chat"
   open "rpc"   ->|          |   ===========    |          |-> accept "rpc"
                  +----------+                  +----------+
                       ^                              ^
                       | StreamFactory                | StreamFactory
                       | (TCP / WS / UDP / IPC / QUIC)|
```

A channel is identified by a string ID. Both sides agree on the ID; one calls `OpenChannel`, the other calls `AcceptChannel`. Either side may open or accept.

## Your first server and client

A minimal TCP example that exchanges a typed JSON message.

### Shared message type

```csharp
using System.Text.Json.Serialization;

public record Hello(string Name, int Count);

[JsonSerializable(typeof(Hello))]
internal partial class AppJson : JsonSerializerContext;
```

### Server

```csharp
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Transit.Message;
using NetConduit.Transport.Tcp;

var listener = new TcpListener(IPAddress.Loopback, 5000);
listener.Start();

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
mux.Start();
await mux.WaitForReadyAsync();

await using var inbox = await mux.AcceptMessageTransitAsync<Hello>(
    "hello",
    AppJson.Default.Hello);

await foreach (var msg in inbox.ReceiveAllAsync())
    Console.WriteLine($"got: {msg!.Name} x{msg.Count}");
```

### Client

```csharp
using NetConduit;
using NetConduit.Transit.Message;
using NetConduit.Transport.Tcp;

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("127.0.0.1", 5000));
mux.Start();
await mux.WaitForReadyAsync();

await using var outbox = await mux.OpenMessageTransitAsync<Hello>(
    "hello",
    AppJson.Default.Hello);

await outbox.SendAsync(new Hello("Alice", 1));
await outbox.SendAsync(new Hello("Alice", 2));
```

## Lifecycle

```
Create  ->  Start  ->  WaitForReadyAsync  ->  Open / Accept channels  ->  GoAwayAsync / DisposeAsync
```

- `Create(options)` — build a multiplexer instance.
- `Start()` — kicks off the connect + handshake + read/write loops.
- `WaitForReadyAsync()` — waits for the first successful handshake. Required before opening or accepting channels.
- `OpenChannel` / `AcceptChannel` (and their transit equivalents) — return immediately in pending state; call `WaitForReadyAsync` on the channel/transit (or use the `…Async` extension) to await remote confirmation.
- `GoAwayAsync()` — graceful shutdown. Drains channels, then signals the remote.
- `await using` / `DisposeAsync()` — cancels everything and disposes the transport.

See [Multiplexer concepts](concepts/multiplexer.md) for details on each phase.

## What to read next

- [Concepts: Channels](concepts/channels.md) — open vs. accept, channel IDs, state transitions.
- [Concepts: Transits](concepts/transits.md) — when to wrap a channel with a transit.
- [Transports overview](transports/index.md) — which transport to pick.
- [API: `StreamMultiplexer`](api/stream-multiplexer.md) — full surface.
# Getting Started

## Installation

```bash
# Core package (required)
dotnet add package NetConduit

# Choose your transport(s):
dotnet add package NetConduit.Transport.Tcp        # TCP sockets
dotnet add package NetConduit.Transport.WebSocket  # WebSocket (client + ASP.NET Core)
dotnet add package NetConduit.Transport.Udp        # UDP with reliability layer
dotnet add package NetConduit.Transport.Ipc        # TCP loopback (Windows) / Unix sockets
dotnet add package NetConduit.Transport.Quic       # QUIC (requires OS support)
```

See [Transports](transports/index.md) for details on each transport.

## Your First Multiplexer

Create a [TCP](transports/tcp.md) server and client that communicate over [channels](concepts/channels.md).

### TCP Server

```csharp
using NetConduit;
using NetConduit.Transport.Tcp;
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
using NetConduit.Transport.Tcp;
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
using NetConduit.Transit.Message;
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

### DeltaMessageTransit

For state synchronization — only changed fields are sent:

```csharp
public record GameState(int Score, int Health);

[JsonSerializable(typeof(GameState))]
public partial class GameContext : JsonSerializerContext { }

// Sender
var sender = mux.OpenSendOnlyDeltaMessageTransit("state", GameContext.Default.GameState);
await sender.SendAsync(new GameState(100, 50));
await sender.SendAsync(new GameState(150, 50));  // Only sends Score change

// Receiver
await using var receiver = await mux.AcceptReceiveOnlyDeltaMessageTransitAsync("state", GameContext.Default.GameState);
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
