# TCP Transport

The most common transport for server-to-server or LAN communication. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.Tcp
```

## Client

```csharp
using NetConduit;
using NetConduit.Tcp;

// Create client options (with optional configuration)
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.EnableReconnection = true;
    o.ReconnectTimeout = TimeSpan.FromSeconds(60);
});

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels...
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });
await channel.WriteAsync(data);
```

## Server

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Net;
using System.Net.Sockets;

// Start TCP listener
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

// Create server options (handles single client, reconnection-aware)
var options = TcpMultiplexer.CreateServerOptions(listener);

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Accept channels
await foreach (var channel in mux.AcceptChannelsAsync())
{
    _ = HandleChannelAsync(channel);
}
```

## Multi-Client Server

For multiple concurrent clients, create a multiplexer per connection:

```csharp
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

while (true)
{
    var tcpClient = await listener.AcceptTcpClientAsync();
    _ = HandleClientAsync(tcpClient);
}

async Task HandleClientAsync(TcpClient tcpClient)
{
    var stream = tcpClient.GetStream();
    var options = new MultiplexerOptions
    {
        StreamFactory = async (ct) => new StreamPair(stream)
    };
    
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    
    // Handle this client's channels...
    await foreach (var channel in mux.AcceptChannelsAsync())
    {
        _ = ProcessChannelAsync(channel);
    }
}
```

## Configuration

### [Reconnection](../concepts/reconnection.md)

```csharp
var options = TcpMultiplexer.CreateOptions("localhost", 5000, configure: o =>
{
    o.EnableReconnection = true;
    o.ReconnectTimeout = TimeSpan.FromSeconds(60);
    o.ReconnectBufferSize = 1024 * 1024;  // 1MB buffer for pending data
});

var mux = StreamMultiplexer.Create(options);

mux.OnDisconnected += (reason, ex) => Console.WriteLine("Disconnected");
mux.OnAutoReconnecting += (args) => Console.WriteLine($"Reconnecting (attempt {args.AttemptNumber})...");
```

## Tips

**Handle connection failures:**
```csharp
try
{
    await mux.WaitForReadyAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
}
```

**Clean shutdown:**
```csharp
await mux.DisposeAsync();  // Sends GOAWAY, waits for graceful close
listener.Stop();
```

## Performance

TCP is highly optimized in NetConduit:
- Single connection handles thousands of channels
- At 1000+ channels, mux outperforms raw TCP by ~15%
- No socket exhaustion issues at high channel counts

See [benchmarks](../index.md) for detailed numbers.
