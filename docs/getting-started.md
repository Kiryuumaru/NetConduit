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
