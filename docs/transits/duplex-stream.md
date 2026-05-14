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
