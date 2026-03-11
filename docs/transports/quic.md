# QUIC Transport

Modern transport protocol with built-in multiplexing, 0-RTT connection establishment, and improved performance on lossy networks.

## Requirements

- **.NET 9 or later**
- **OS Support:**
  - Windows 11, Windows Server 2022+
  - Linux with `libmsquic` installed
  - macOS with `libmsquic` (limited support)

## Installation

```bash
dotnet add package NetConduit.Quic
```

Check QUIC availability at runtime:

```csharp
if (!QuicConnection.IsSupported)
{
    Console.WriteLine("QUIC not supported on this system");
    return;
}
```

## Client

```csharp
using NetConduit;
using NetConduit.Quic;

// Create client options (allowInsecure for development only)
var options = QuicMultiplexer.CreateOptions("localhost", 5000, allowInsecure: true);

// For production - specify expected certificate
var prodOptions = QuicMultiplexer.CreateOptions(
    "example.com", 
    5000,
    expectedCertificate: serverCert);

// Create and start multiplexer
var mux = StreamMultiplexer.Create(options);
var runTask = mux.Start();
await mux.WaitForReadyAsync();

// Use channels
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });
```

## Server

```csharp
using NetConduit;
using NetConduit.Quic;
using System.Net;
using System.Security.Cryptography.X509Certificates;

// Load or create certificate
var certificate = X509Certificate2.CreateFromPemFile("cert.pem", "key.pem");

// Create QUIC listener
var listener = await QuicMultiplexer.ListenAsync(
    new IPEndPoint(IPAddress.Any, 5000),
    certificate);

// Create server options from listener
var options = QuicMultiplexer.CreateServerOptions(listener);

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

## Certificates

### Development Certificate

```csharp
// Use .NET dev cert (development only!)
var devCert = X509Certificate2.CreateFromPemFile(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aspnet", "https", "aspnetcore-https.pem"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aspnet", "https", "aspnetcore-https-key.pem"));
```

### Self-Signed Certificate

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var rsa = RSA.Create(2048);
var req = new CertificateRequest(
    "CN=localhost",
    rsa,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);

req.CertificateExtensions.Add(
    new X509BasicConstraintsExtension(false, false, 0, false));
req.CertificateExtensions.Add(
    new X509EnhancedKeyUsageExtension(
        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server auth
        false));

var cert = req.CreateSelfSigned(
    DateTimeOffset.Now,
    DateTimeOffset.Now.AddYears(1));

// Export with private key for server use
var pfxBytes = cert.Export(X509ContentType.Pfx, "password");
```

### Production Certificate

Use a proper CA-signed certificate:

```csharp
var cert = new X509Certificate2("server.pfx", "password");
```

## Configuration

### QUIC-Specific Options

```csharp
var options = QuicMultiplexer.CreateOptions("localhost", 5000, allowInsecure: true);

// Configure QUIC connection
options.ConfigureQuicConnection = (quicOptions) =>
{
    quicOptions.MaxInboundBidirectionalStreams = 100;
    quicOptions.MaxInboundUnidirectionalStreams = 100;
    quicOptions.IdleTimeout = TimeSpan.FromMinutes(2);
};
```

### ALPN Protocol

```csharp
options.ApplicationProtocols = new[] { "netconduit" };
```

## QUIC vs NetConduit Multiplexing

QUIC has built-in stream multiplexing. NetConduit adds:

- **Named channels** - QUIC uses numeric stream IDs
- **Priority queuing** - Application-level priorities
- **Credit-based backpressure** - Fine-grained flow control
- **Transits** - Higher-level messaging patterns

You can use either QUIC's native streams or NetConduit's channels:

```csharp
// Use NetConduit channels over QUIC
var channel = await mux.OpenChannelAsync(new() { ChannelId = "chat" });

// Or access QUIC streams directly if needed
// (transport-specific API)
```

## Tips

**Check QUIC support:**
```csharp
if (!QuicConnection.IsSupported)
{
    // Fall back to TCP/WebSocket
    options = TcpMultiplexer.CreateOptions("localhost", 5000);
}
```

**Linux msquic installation:**
```bash
# Ubuntu/Debian
sudo apt install libmsquic

# Or from Microsoft packages
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install libmsquic
```

**0-RTT considerations:**
QUIC's 0-RTT is great for latency but has replay attack considerations. For sensitive operations, wait for full handshake.

## When to Use QUIC

| Scenario | Use QUIC? |
|----------|-----------|
| Modern cloud infrastructure | ✅ Yes |
| Mobile clients (lossy networks) | ✅ Yes |
| High-latency connections | ✅ Yes (0-RTT) |
| Legacy systems | ❌ Use TCP |
| Browser clients | ❌ Use WebSocket |
| No TLS certificate available | ❌ Use TCP |

## Performance

QUIC advantages:
- **0-RTT** - Faster connection establishment
- **No head-of-line blocking** - Unlike TCP, one lost packet doesn't block others
- **Better mobile performance** - Handles network changes gracefully
- **Built-in encryption** - TLS 1.3 required

Trade-offs:
- Requires TLS certificate
- Limited OS support
- Slightly higher CPU for encryption
