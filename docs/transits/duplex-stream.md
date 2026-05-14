# DuplexStream transit

Package: [`NetConduit.Transit.DuplexStream`](https://www.nuget.org/packages/NetConduit.Transit.DuplexStream).

`DuplexStreamTransit` combines one write channel and one read channel into a bidirectional `System.IO.Stream`. Use it where a consumer expects a duplex stream — `SslStream`, HTTP framing, RPC pipes.

## Channel pairing

From a single base channel ID, the transit derives two channels:

| Base | Initiator (`OpenDuplexStream`) | Responder (`AcceptDuplexStream`) |
| --- | --- | --- |
| `"chat"` | writes `chat>>`, reads `chat<<` | reads `chat>>`, writes `chat<<` |

Don't include `>>` or `<<` in base IDs.

## API

```csharp
public sealed class DuplexStreamTransit : System.IO.Stream, ITransit
{
    public DuplexStreamTransit(IWriteChannel writeChannel, IReadChannel readChannel);

    // From ITransit
    public bool IsReady { get; }                // true only when BOTH channels are ready
    public bool IsConnected { get; }
    public string? WriteChannelId { get; }
    public string? ReadChannelId { get; }
    public event EventHandler? Ready;           // fires once when both channels become ready
    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public Task WaitForReadyAsync(CancellationToken ct = default);

    // Standard Stream surface: CanRead, CanWrite, ReadAsync, WriteAsync, FlushAsync, DisposeAsync, ...
    // CanSeek is always false.
}
```

## Extension methods

```csharp
public static class DuplexStreamTransitExtensions
{
    public static DuplexStreamTransit OpenDuplexStream(
        this IStreamMultiplexer mux, string channelId);

    public static Task<DuplexStreamTransit> OpenDuplexStreamAsync(
        this IStreamMultiplexer mux, string channelId,
        CancellationToken cancellationToken = default);

    public static DuplexStreamTransit AcceptDuplexStream(
        this IStreamMultiplexer mux, string channelId);

    public static Task<DuplexStreamTransit> AcceptDuplexStreamAsync(
        this IStreamMultiplexer mux, string channelId,
        CancellationToken cancellationToken = default);
}
```

## Example — request/response on one stream

```csharp
// Initiator
await using var stream = await mux.OpenDuplexStreamAsync("rpc");

await stream.WriteAsync(requestBytes);
int n = await stream.ReadAsync(buffer);   // wait for response bytes
```

```csharp
// Responder
await using var stream = await mux.AcceptDuplexStreamAsync("rpc");

int n = await stream.ReadAsync(buffer);   // read request
await stream.WriteAsync(responseBytes);
```

## Example — `SslStream` over NetConduit

```csharp
await using var inner = await mux.OpenDuplexStreamAsync("tls");
await using var ssl = new SslStream(inner, leaveInnerStreamOpen: false);

await ssl.AuthenticateAsClientAsync("server.example.com");
// use ssl as any other duplex stream
```

## When to use

- Adapting NetConduit to APIs that need a duplex `Stream`.
- Carrying a sub-protocol (HTTP, TLS, custom binary) inside one channel pair without writing framing yourself.

## Limits

- Read and write are independent — closing one direction doesn't close the other automatically. Dispose the transit to close both.
- Both channels must reach `Open` before the transit becomes `Ready`. If you need traffic in one direction before the other is set up, use raw channels instead.
# DuplexStreamTransit

A bidirectional `Stream` over two paired channels. Acts like a virtual TCP connection. See [Transit Overview](index.md) for alternatives.

For one-way streaming, use [StreamTransit](stream.md).

## Basic Usage

```csharp
using NetConduit.Transit.DuplexStream;

// Side A opens
await using var streamA = await mux.OpenDuplexStreamAsync("tunnel");
await streamA.WriteAsync(requestData);
var n = await streamA.ReadAsync(responseBuffer);

// Side B accepts
await using var streamB = await mux.AcceptDuplexStreamAsync("tunnel");
var n = await streamB.ReadAsync(requestBuffer);
await streamB.WriteAsync(responseData);
```

## Stream Compatibility

DuplexStreamTransit inherits from `Stream` with both read and write:

```csharp
var duplex = await mux.OpenDuplexStreamAsync("relay");

// Use with NetworkStream-like patterns
await duplex.WriteAsync(data);
await duplex.FlushAsync();
var bytesRead = await duplex.ReadAsync(buffer);

// CopyToAsync (one direction)
_ = Task.Run(() => sourceStream.CopyToAsync(duplex));
await duplex.CopyToAsync(destinationStream);
```

## TCP Tunneling Pattern

Relay a TCP connection through a multiplexer:

```csharp
// Tunnel entry point
var tcpClient = await listener.AcceptTcpClientAsync();
var duplex = await mux.OpenDuplexStreamAsync("tcp-relay");

// Bidirectional relay
var upload = tcpClient.GetStream().CopyToAsync(duplex);
var download = duplex.CopyToAsync(tcpClient.GetStream());
await Task.WhenAll(upload, download);
```

## Properties

| Property         | Type      | Description                    |
| ---------------- | --------- | ------------------------------ |
| `IsConnected`    | `bool`    | True if either channel is open |
| `WriteChannelId` | `string?` | ID of the write channel        |
| `ReadChannelId`  | `string?` | ID of the read channel         |
| `CanRead`        | `bool`    | Always true                    |
| `CanWrite`       | `bool`    | Always true                    |
| `CanSeek`        | `bool`    | Always false                   |

## Custom Channel IDs

Use explicit channel IDs when the default `>>` / `<<` convention doesn't fit:

```csharp
var duplex = await mux.OpenDuplexStreamAsync(
    writeChannelId: "upstream",
    readChannelId: "downstream");
```

## Direct Construction

```csharp
var writeChannel = mux.OpenChannel("to-server");
var readChannel = await mux.AcceptChannelAsync("from-server");

var duplex = new DuplexStreamTransit(writeChannel, readChannel);
```

## API (Extension Methods)

| Method                    | Signature                                                                                            | Description                 |
| ------------------------- | ---------------------------------------------------------------------------------------------------- | --------------------------- |
| `OpenDuplexStreamAsync`   | `await mux.OpenDuplexStreamAsync(string channelId, CancellationToken ct)`                            | Open bidirectional stream   |
| `AcceptDuplexStreamAsync` | `await mux.AcceptDuplexStreamAsync(string channelId, CancellationToken ct)`                          | Accept bidirectional stream |
| `OpenDuplexStreamAsync`   | `await mux.OpenDuplexStreamAsync(string writeChannelId, string readChannelId, CancellationToken ct)` | Open with explicit IDs      |
