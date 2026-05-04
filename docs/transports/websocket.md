# WebSocket Transport

WebSocket client and server support. HTTP-friendly for firewall traversal and browser integration. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.WebSocket
```

## Client

```csharp
using NetConduit;
using NetConduit.WebSocket;

var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/mux");
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

Overloads:

```csharp
// String URL
var options = WebSocketMultiplexer.CreateOptions("wss://example.com/mux");

// Uri
var options = WebSocketMultiplexer.CreateOptions(new Uri("wss://example.com/mux"));

// With custom WebSocket options (auth, headers, etc.)
var options = WebSocketMultiplexer.CreateOptions("wss://example.com/mux", ws =>
{
    ws.SetRequestHeader("Authorization", "Bearer token");
});
```

## Server (Single Client)

From an existing `WebSocket` instance (ASP.NET Core middleware):

```csharp
using NetConduit;
using NetConduit.WebSocket;

app.Map("/mux", async (HttpContext ctx) =>
{
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var options = WebSocketMultiplexer.CreateServerOptions(ws);

    await using var mux = StreamMultiplexer.Create(options);
    mux.Start();
    await mux.WaitForReadyAsync();

    await foreach (var channel in mux.AcceptChannelsAsync())
    {
        _ = HandleChannelAsync(channel);
    }
});
```

## Server (Multi-Client with Reconnection)

`WebSocketMuxListener` manages multiple WebSocket sessions with reconnection support:

```csharp
using NetConduit.WebSocket;

await using var listener = new WebSocketMuxListener();

// In ASP.NET Core middleware:
app.Map("/mux", async (HttpContext ctx) =>
{
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await listener.HandleAsync(ws);
});

// Process accepted multiplexers:
await foreach (var mux in listener.AcceptMuxesAsync(ct))
{
    _ = Task.Run(async () =>
    {
        await foreach (var channel in mux.AcceptChannelsAsync())
        {
            _ = HandleChannelAsync(channel);
        }
    });
}
```

## Reconnectable Client

For clients that reconnect to a `WebSocketMuxListener`:

```csharp
var (options, bind) = WebSocketMuxListener.CreateReconnectableClientOptions("ws://localhost:5000/mux");
var mux = StreamMultiplexer.Create(options);
bind(mux);
mux.Start();
await mux.WaitForReadyAsync();
```

## API

| Method | Signature | Description |
|--------|-----------|-------------|
| `CreateOptions` | `WebSocketMultiplexer.CreateOptions(Uri uri, Action<ClientWebSocketOptions>? clientOptions = null)` | Client options from URI |
| `CreateOptions` | `WebSocketMultiplexer.CreateOptions(string url, Action<ClientWebSocketOptions>? clientOptions = null)` | Client options from URL string |
| `CreateServerOptions` | `WebSocketMultiplexer.CreateServerOptions(WebSocket webSocket)` | Server options from existing WebSocket |

### WebSocketMuxListener

| Member | Signature | Description |
|--------|-----------|-------------|
| Constructor | `WebSocketMuxListener(Func<MultiplexerOptions, MultiplexerOptions>? customize = null)` | Create listener with optional options customization |
| `HandleAsync` | `Task HandleAsync(WebSocket webSocket, Guid? sessionId = null, CancellationToken ct = default)` | Handle incoming WebSocket |
| `AcceptMuxesAsync` | `IAsyncEnumerable<IStreamMultiplexer> AcceptMuxesAsync(CancellationToken ct = default)` | Accept multiplexer sessions |
| `RemoveSession` | `bool RemoveSession(Guid sessionId)` | Remove a session |
| Static | `CreateReconnectableClientOptions(Uri baseUri, ...)` | Create reconnectable client config |
