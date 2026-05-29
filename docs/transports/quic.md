# QUIC transport

Package: [`NetConduit.Transport.Quic`](https://www.nuget.org/packages/NetConduit.Transport.Quic).

Wraps `System.Net.Quic` (.NET 8+). QUIC speaks TLS 1.3, supports stream multiplexing natively, and survives IP-address changes. NetConduit uses **one bidirectional QUIC stream** as the transport and runs its own multiplexer on top.

## Platform support

`QuicMultiplexer` is annotated:

```csharp
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
```

`System.Net.Quic` requires OS-level QUIC support. The library checks `QuicListener.IsSupported` and throws `PlatformNotSupportedException` early when QUIC is unavailable.

Check at runtime if you target environments that might not have it:

```csharp
if (!QuicListener.IsSupported)
    Console.WriteLine("QUIC not available on this OS / runtime");
```

## API

```csharp
public static class QuicMultiplexer
{
    public static MultiplexerOptions CreateOptions(
        string host, int port,
        string? alpn = null,
        bool allowInsecure = false);

    public static Task<QuicListener> ListenAsync(
        IPEndPoint endPoint,
        X509Certificate2 certificate,
        string? alpn = null,
        CancellationToken cancellationToken = default);

    public static MultiplexerOptions CreateServerOptions(QuicListener listener);
}
```

### ALPN

`alpn` is the Application-Layer Protocol Negotiation identifier. Default: `"netconduit"`. Both peers must agree. Pass `null` to use the default; an explicit empty string is rejected.

### `allowInsecure`

Client only. When `true`, certificate validation is **skipped**. Use only for local development.

## Client

```csharp
using NetConduit;
using NetConduit.Transport.Quic;

var opts = QuicMultiplexer.CreateOptions(
    host: "example.com",
    port: 5000,
    alpn: "myapp",
    allowInsecure: false);

await using var mux = StreamMultiplexer.Create(opts);
mux.Start();
await mux.WaitForReadyAsync();
```

## Server

```csharp
using System.Net;
using System.Security.Cryptography.X509Certificates;
using NetConduit;
using NetConduit.Transport.Quic;

var cert = new X509Certificate2("server.pfx", "password");

var listener = await QuicMultiplexer.ListenAsync(
    new IPEndPoint(IPAddress.Any, 5000),
    cert,
    alpn: "myapp");

await using var mux = StreamMultiplexer.Create(QuicMultiplexer.CreateServerOptions(listener));
mux.Start();
await mux.WaitForReadyAsync();
```

The certificate must have a private key. `ListenAsync` rejects certificates without private keys before creating a listener. For dev, a self-signed cert works (clients then need `allowInsecure: true`).

## Reconnectable server

`CreateServerOptions(listener)` accepts one connection. For a server that survives client churn, write a custom factory that re-accepts from the `QuicListener` on every call. See [Reconnection → QUIC](../concepts/reconnection.md#quic) for a copy-paste snippet.

## Behavior notes

- The transport opens **one** outbound bidirectional QUIC stream. The first byte (`0x01`) is a handshake marker so both ends synchronize.
- Client hostnames that resolve to multiple addresses are attempted in parallel; the first successful QUIC connection is used and remaining attempts are cancelled.
- `MaxInboundBidirectionalStreams` is set to 100 (room for future use); only one is actually used by the multiplexer.
- TLS 1.3 only.

## When to pick QUIC

- Modern infrastructure where TCP middleboxes are a problem.
- Mobile/roaming clients that may switch networks.
- You want TLS built into the transport without managing it yourself.

For simpler deployments, [TCP](tcp.md) or [WebSocket](websocket.md) is usually fine.
