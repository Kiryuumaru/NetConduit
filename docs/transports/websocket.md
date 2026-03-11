# WebSocket Transport

Ideal for web applications, browser communication, and traversing firewalls/proxies.

## Installation

```bash
dotnet add package NetConduit.WebSocket
```

## Client

```csharp
using NetConduit;
using NetConduit.WebSocket;

// Create client options with WebSocket URL
var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/ws");

// For secure WebSocket
var secureOptions = WebSocketMultiplexer.CreateOptions("wss://example.com/ws");

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels...
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });
```

## Server (ASP.NET Core)

```csharp
using NetConduit;
using NetConduit.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

app.MapGet("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var options = WebSocketMultiplexer.CreateServerOptions(webSocket);
    
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    
    // Handle channels
    await foreach (var channel in mux.AcceptChannelsAsync())
    {
        _ = HandleChannelAsync(channel);
    }
});

app.Run();
```

## Server (Standalone)

For non-ASP.NET scenarios:

```csharp
using System.Net;
using System.Net.WebSockets;

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();

while (true)
{
    var context = await listener.GetContextAsync();
    
    if (context.Request.IsWebSocketRequest)
    {
        var wsContext = await context.AcceptWebSocketAsync(null);
        _ = HandleWebSocketAsync(wsContext.WebSocket);
    }
}

async Task HandleWebSocketAsync(WebSocket webSocket)
{
    var options = WebSocketMultiplexer.CreateServerOptions(webSocket);
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    
    // Handle channels...
}
```

## Configuration

### Client Options

```csharp
var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/ws");

// Configure underlying ClientWebSocket
options.ConfigureWebSocket = (ws) =>
{
    ws.Options.SetRequestHeader("Authorization", "Bearer token");
    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
};
```

### Reconnection

```csharp
var options = WebSocketMultiplexer.CreateOptions("ws://localhost:5000/ws");
options.EnableReconnection = true;
options.ReconnectTimeout = TimeSpan.FromSeconds(60);

var mux = StreamMultiplexer.Create(options);

mux.OnDisconnected += (reason, ex) => Console.WriteLine("WebSocket disconnected");
mux.OnReconnected += () => Console.WriteLine("WebSocket reconnected!");
```

## Subprotocols

Specify WebSocket subprotocol:

```csharp
options.ConfigureWebSocket = (ws) =>
{
    ws.Options.AddSubProtocol("netconduit");
};
```

Server side (ASP.NET Core):

```csharp
var webSocket = await context.WebSockets.AcceptWebSocketAsync("netconduit");
```

## Tips

**CORS for browser clients:**
```csharp
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod());
```

**Handle close codes:**
```csharp
mux.OnDisconnected += (reason, ex) =>
{
    if (ex is WebSocketException wsEx)
    {
        Console.WriteLine($"WebSocket close code: {wsEx.WebSocketErrorCode}");
    }
};
```

**Proxy-friendly:**
WebSocket works through most HTTP proxies, making it ideal for firewall traversal.

## Browser Interop

NetConduit's WebSocket transport uses standard WebSocket protocol. For browser-side JavaScript:

```javascript
const ws = new WebSocket('ws://localhost:5000/ws');

// Note: You'll need to implement the NetConduit frame protocol
// on the JavaScript side, or use a wrapper library
```

Consider using the WebSocket transport when you need browser compatibility or HTTP infrastructure integration.
