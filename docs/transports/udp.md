# UDP Transport

UDP with a built-in reliability layer. Best for scenarios requiring low latency with optional reliability. See [Transport Comparison](index.md) for alternatives.

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

For multiple UDP clients, create a multiplexer per client with separate server options:

```csharp
// Each client gets its own mux from a separate server options instance
var options = UdpMultiplexer.CreateServerOptions(port: 5000);
var mux = StreamMultiplexer.Create(options);
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
var udpOptions = new ReliableUdpOptions
{
    MaxRetransmits = 5,
    RetransmitTimeout = TimeSpan.FromSeconds(1),
    Mtu = 1200
};

var options = UdpMultiplexer.CreateOptions("localhost", 5000, udpOptions: udpOptions);
```

**ReliableUdpOptions properties:**

| Property | Default | Description |
|----------|---------|-------------|
| `Mtu` | 1200 | Maximum transmission unit |
| `RetransmitTimeout` | 1s | Timeout before retransmitting |
| `MaxRetransmits` | 5 | Max retransmission attempts |
```

## Tips

**Low latency gaming:**
```csharp
var udpOptions = new ReliableUdpOptions
{
    MaxRetransmits = 2,  // Don't wait too long for old data
    RetransmitTimeout = TimeSpan.FromMilliseconds(50)
};
var options = UdpMultiplexer.CreateOptions("localhost", 5000, udpOptions: udpOptions);
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
| File transfer | ❌ Use [TCP](tcp.md) |
| Web API calls | ❌ Use [WebSocket](websocket.md) |
| Cross-internet reliability | ⚠️ Consider [QUIC](quic.md) |

## Performance

UDP transport provides:
- Lower latency than TCP (no head-of-line blocking)
- Better performance on lossy networks
- Suitable for high-frequency small messages

See benchmarks for detailed comparisons.
