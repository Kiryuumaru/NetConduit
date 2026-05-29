# WebSocket transport

Package: [`NetConduit.Transport.WebSocket`](https://www.nuget.org/packages/NetConduit.Transport.WebSocket).

Wraps `System.Net.WebSockets.ClientWebSocket` and `System.Net.WebSockets.WebSocket`. An internal `WebSocketStream` adapter converts message-mode WebSockets into a byte stream the multiplexer can use.

## API

```csharp
public static class WebSocketMultiplexer
{
    public static MultiplexerOptions CreateOptions(
        Uri uri,
        Action<ClientWebSocketOptions>? clientOptions = null);

    public static MultiplexerOptions CreateOptions(
        string url,
        Action<ClientWebSocketOptions>? clientOptions = null);

    public static MultiplexerOptions CreateServerOptions(WebSocket webSocket);
}
```

| Helper | Behavior |
| --- | --- |
| `CreateOptions(uri[, clientOptions])` | Each factory call opens a fresh `ClientWebSocket` to an absolute `ws://` or `wss://` URI. `clientOptions` lets you set headers, sub-protocols, credentials. Reconnect-friendly. |
| `CreateServerOptions(webSocket)` | Wraps an already-accepted server-side `WebSocket`. Use after `HttpListener.AcceptWebSocketAsync` or ASP.NET's `HttpContext.WebSockets.AcceptWebSocketAsync`. Single accept only. |

## Client

```csharp
using NetConduit;
using NetConduit.Transport.WebSocket;

var opts = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/chat",
    clientOptions: o =>
    {
        o.SetRequestHeader("Authorization", "Bearer …");
    });

await using var mux = StreamMultiplexer.Create(opts);
mux.Start();
await mux.WaitForReadyAsync();
```

## Server (ASP.NET Core minimal API)

```csharp
using NetConduit;
using NetConduit.Transport.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();
app.Map("/chat", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    await using var mux = StreamMultiplexer.Create(WebSocketMultiplexer.CreateServerOptions(ws));
    mux.Start();
    await mux.WaitForReadyAsync(ctx.RequestAborted);

    await foreach (var ch in mux.AcceptChannelsAsync(ctx.RequestAborted))
        _ = HandleAsync(ch, ctx.RequestAborted);
});

app.Run();
```

## Server (`HttpListener`)

```csharp
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/chat/");
listener.Start();

var ctx = await listener.GetContextAsync();
var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);

await using var mux = StreamMultiplexer.Create(WebSocketMultiplexer.CreateServerOptions(wsCtx.WebSocket));
mux.Start();
```

## Reconnectable server

`CreateServerOptions(webSocket)` wraps one already-accepted WebSocket. For a server that survives client churn from an `HttpListener`-based host, write a custom factory that re-accepts on every call. See [Reconnection → WebSocket (`HttpListener`)](../concepts/reconnection.md#websocket-httplistener) for a copy-paste snippet. ASP.NET Core hosts naturally spawn a fresh multiplexer per WebSocket upgrade, so the reconnect pattern is not used there.

## When WebSocket is the right pick

- Browser clients.
- Networks that allow only HTTP/HTTPS outbound.
- Hosting beside an existing ASP.NET app.

The protocol overhead is modest (a few bytes of WebSocket framing per write). For raw throughput on internal networks, [TCP](tcp.md) is slightly faster.

## Platform

Cross-platform. `ClientWebSocket` requires .NET 8+ for the modern API surface used here; targets are net8.0/net9.0/net10.0.
