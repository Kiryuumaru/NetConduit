# TCP transport

Package: [`NetConduit.Transport.Tcp`](https://www.nuget.org/packages/NetConduit.Transport.Tcp).

Wraps `System.Net.Sockets.TcpClient` / `TcpListener`. Sets `NoDelay = true` on every connection. Supports reconnection on the client side.

## API

```csharp
public static class TcpMultiplexer
{
    public static MultiplexerOptions CreateOptions(string host, int port);
    public static MultiplexerOptions CreateOptions(IPEndPoint endpoint);
    public static MultiplexerOptions CreateServerOptions(TcpListener listener);
}
```

| Helper | Behavior |
| --- | --- |
| `CreateOptions(host, port)` | Each factory call resolves `host`, opens a new `TcpClient`, returns its stream. Reconnect-friendly. |
| `CreateOptions(endpoint)` | Same but takes an `IPEndPoint`. |
| `CreateServerOptions(listener)` | Accepts one `TcpClient` from `listener` on first factory call; throws on subsequent calls. Server cannot reconnect (use a custom factory for that — see [Reconnection](../concepts/reconnection.md#server-side-reconnection)). |

## Client

```csharp
using NetConduit;
using NetConduit.Transport.Tcp;

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateOptions("127.0.0.1", 5000));
mux.Start();
await mux.WaitForReadyAsync();

var ch = await mux.OpenChannelAsync("requests");
await ch.WriteAsync(Encoding.UTF8.GetBytes("hello"));
```

## Server

```csharp
using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Transport.Tcp;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

await using var mux = StreamMultiplexer.Create(TcpMultiplexer.CreateServerOptions(listener));
mux.Start();
await mux.WaitForReadyAsync();

await foreach (var ch in mux.AcceptChannelsAsync())
    _ = HandleAsync(ch);
```

## Reconnection

The client factory creates a fresh `TcpClient` on every call, so reconnection works out of the box when `MaxAutoReconnectAttempts > 0`:

```csharp
var opts = TcpMultiplexer.CreateOptions("relay.example.com", 5000) with
{
    MaxAutoReconnectAttempts = 5,
};
```

(The factory inside `opts` is a `StreamFactoryDelegate` — `MultiplexerOptions` is a `record` so `with` works for tuning.)

## Platform

Windows, Linux, macOS, anywhere `System.Net.Sockets` is supported.
