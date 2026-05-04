# TCP Transport

Reliable TCP sockets. The simplest and most common transport. See [Transport Comparison](index.md) for alternatives.

## Installation

```bash
dotnet add package NetConduit.Tcp
```

## Client

```csharp
using NetConduit;
using NetConduit.Tcp;

var options = TcpMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

Overloads:

```csharp
// Host + port
var options = TcpMultiplexer.CreateOptions("example.com", 9000);

// IPEndPoint
var options = TcpMultiplexer.CreateOptions(new IPEndPoint(IPAddress.Loopback, 9000));
```

## Server (Single Client)

```csharp
using NetConduit;
using NetConduit.Tcp;
using System.Net;
using System.Net.Sockets;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

var options = TcpMultiplexer.CreateServerOptions(listener);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

await foreach (var channel in mux.AcceptChannelsAsync())
{
    _ = HandleChannelAsync(channel);
}
```

## Server (Multi-Client)

For multiple concurrent TCP clients, create a multiplexer per connection:

```csharp
var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

while (true)
{
    var tcpClient = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        var accepted = false;
        var options = new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                if (accepted) throw new InvalidOperationException("No reconnect");
                accepted = true;
                return Task.FromResult<IStreamPair>(new StreamPair(tcpClient.GetStream(), tcpClient));
            }
        };

        await using var mux = StreamMultiplexer.Create(options);
        mux.Start();
        await mux.WaitForReadyAsync();

        await foreach (var channel in mux.AcceptChannelsAsync())
        {
            _ = HandleChannelAsync(channel);
        }
    });
}
```

## API

| Method | Signature | Description |
|--------|-----------|-------------|
| `CreateOptions` | `TcpMultiplexer.CreateOptions(string host, int port)` | Client options with host/port |
| `CreateOptions` | `TcpMultiplexer.CreateOptions(IPEndPoint endpoint)` | Client options with endpoint |
| `CreateServerOptions` | `TcpMultiplexer.CreateServerOptions(TcpListener listener)` | Server options from listener |
