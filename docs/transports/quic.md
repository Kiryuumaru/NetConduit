# QUIC Transport

QUIC protocol transport using .NET's built-in QUIC support. Provides 0-RTT connection establishment and native multiplexing at the transport layer. See [Transport Comparison](index.md) for alternatives.

## Requirements

- .NET 8 or later
- OS support: Windows, Linux, macOS
- TLS certificate (required by QUIC specification)

## Installation

```bash
dotnet add package NetConduit.Transport.Quic
```

## Client

```csharp
using NetConduit;
using NetConduit.Transport.Quic;

var options = QuicMultiplexer.CreateOptions("localhost", 5000);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

var channel = mux.OpenChannel("data");
await channel.WriteAsync(data);
```

Options:

```csharp
// With custom ALPN protocol identifier
var options = QuicMultiplexer.CreateOptions("localhost", 5000, alpn: "my-protocol");

// Allow insecure (self-signed) certificates (development only)
var options = QuicMultiplexer.CreateOptions("localhost", 5000, allowInsecure: true);
```

## Server

```csharp
using NetConduit;
using NetConduit.Transport.Quic;
using System.Net;
using System.Security.Cryptography.X509Certificates;

var cert = X509Certificate2.CreateFromPemFile("cert.pem", "key.pem");
var listener = await QuicMultiplexer.ListenAsync(
    new IPEndPoint(IPAddress.Any, 5000),
    cert);

var options = QuicMultiplexer.CreateServerOptions(listener);
var mux = StreamMultiplexer.Create(options);
mux.Start();
await mux.WaitForReadyAsync();

await foreach (var channel in mux.AcceptChannelsAsync())
{
    _ = HandleChannelAsync(channel);
}
```

## API

| Method                | Signature                                                                                                                             | Description                  |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------- |
| `CreateOptions`       | `QuicMultiplexer.CreateOptions(string host, int port, string? alpn = null, bool allowInsecure = false)`                               | Client options               |
| `ListenAsync`         | `QuicMultiplexer.ListenAsync(IPEndPoint endPoint, X509Certificate2 certificate, string? alpn = null, CancellationToken ct = default)` | Create QUIC listener         |
| `CreateServerOptions` | `QuicMultiplexer.CreateServerOptions(QuicListener listener)`                                                                          | Server options from listener |

## Platform Support

Attribute: `[SupportedOSPlatform("windows")]`, `[SupportedOSPlatform("linux")]`, `[SupportedOSPlatform("macos")]`

QUIC requires operating system support for the QUIC protocol (via msquic or equivalent).
