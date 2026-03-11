# UDP Transport

UDP with a built-in reliability layer. Best for scenarios requiring low latency with optional reliability.

## Installation

```bash
dotnet add package NetConduit.Udp
```

## Client

```csharp
using NetConduit;
using NetConduit.Udp;

// Create client options
var options = UdpMultiplexer.CreateOptions("localhost", 5000);

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels - reliability is automatic
var channel = await mux.OpenChannelAsync(new() { ChannelId = "game-state" });
await channel.WriteAsync(gameData);
```

## Server

```csharp
using NetConduit;
using NetConduit.Udp;

// Create server options listening on port
var options = UdpMultiplexer.CreateServerOptions(port: 5000);

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

For multiple UDP clients:

```csharp
var server = new UdpServer(5000);

server.OnClientConnected += async (clientEndpoint) =>
{
    var options = UdpMultiplexer.CreateServerOptions(server, clientEndpoint);
    var mux = StreamMultiplexer.Create(options);
    var runTask = mux.Start();
    await mux.WaitForReadyAsync();
    
    // Handle this client...
};

await server.StartAsync();
```

## Reliability Layer

NetConduit's UDP transport includes automatic reliability:

- **Packet ordering** - Packets delivered in order
- **Retransmission** - Lost packets automatically retried
- **Acknowledgments** - Delivery confirmation
- **Congestion control** - Adaptive sending rate

This gives you TCP-like reliability over UDP when needed.

## Configuration

### Reliability Settings

```csharp
var options = UdpMultiplexer.CreateOptions("localhost", 5000);

// Configure reliability
options.UdpReliability = new UdpReliabilityOptions
{
    MaxRetries = 5,
    RetryTimeout = TimeSpan.FromMilliseconds(100),
    AckTimeout = TimeSpan.FromMilliseconds(50)
};
```

### Buffer Sizes

```csharp
options.ConfigureUdpClient = (client) =>
{
    client.Client.ReceiveBufferSize = 128 * 1024;
    client.Client.SendBufferSize = 128 * 1024;
};
```

## Tips

**Low latency gaming:**
```csharp
// For game state that can tolerate some loss,
// consider smaller retry counts
options.UdpReliability = new UdpReliabilityOptions
{
    MaxRetries = 2,  // Don't wait too long for old data
    RetryTimeout = TimeSpan.FromMilliseconds(50)
};
```

**NAT traversal:**
UDP can work with NAT hole-punching techniques for peer-to-peer scenarios.

**MTU considerations:**
Default max payload respects typical MTU (~1400 bytes). Larger messages are automatically fragmented.

## When to Use UDP

| Scenario | Use UDP? |
|----------|----------|
| Real-time games | ✅ Yes |
| Voice/video streaming | ✅ Yes |
| IoT sensors (same network) | ✅ Yes |
| File transfer | ❌ Use TCP |
| Web API calls | ❌ Use WebSocket |
| Cross-internet reliability | ⚠️ Consider QUIC |

## Performance

UDP transport provides:
- Lower latency than TCP (no head-of-line blocking)
- Better performance on lossy networks
- Suitable for high-frequency small messages

See benchmarks for detailed comparisons.
