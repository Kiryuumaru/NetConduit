# UDP Transport

UDP with a built-in reliability layer. Suitable for low-latency scenarios where TCP's head-of-line blocking is unacceptable. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.Udp
```

## Client

```csharp
using NetConduit;
using NetConduit.Udp;

var options = UdpMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

## Server

```csharp
using NetConduit;
using NetConduit.Udp;

var options = UdpMultiplexer.CreateServerOptions(5000);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

await foreach (var channel in mux.AcceptChannelsAsync())
{
    _ = HandleChannelAsync(channel);
}
```

## Reliability Options

Configure the reliability layer behavior:

```csharp
var udpOptions = new ReliableUdpOptions
{
    Mtu = 1200,                                    // Maximum transmission unit (default: 1200)
    RetransmitTimeout = TimeSpan.FromSeconds(1),   // Time before retransmit (default: 1s)
    MaxRetransmits = 5                             // Max retransmit attempts (default: 5)
};

var options = UdpMultiplexer.CreateOptions("localhost", 5000, udpOptions);
```

## API

| Method                | Signature                                                                                    | Description    |
| --------------------- | -------------------------------------------------------------------------------------------- | -------------- |
| `CreateOptions`       | `UdpMultiplexer.CreateOptions(string host, int port, ReliableUdpOptions? udpOptions = null)` | Client options |
| `CreateServerOptions` | `UdpMultiplexer.CreateServerOptions(int listenPort, ReliableUdpOptions? udpOptions = null)`  | Server options |

### ReliableUdpOptions

| Property            | Type       | Default | Description                                |
| ------------------- | ---------- | ------- | ------------------------------------------ |
| `Mtu`               | `int`      | 1200    | Maximum transmission unit in bytes         |
| `RetransmitTimeout` | `TimeSpan` | 1s      | Time before retransmitting a packet        |
| `MaxRetransmits`    | `int`      | 5       | Maximum retransmit attempts before failure |
