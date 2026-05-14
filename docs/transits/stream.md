# Stream transit

Package: [`NetConduit.Transit.Stream`](https://www.nuget.org/packages/NetConduit.Transit.Stream).

`StreamTransit` exposes a single channel as a `System.IO.Stream`. The wrapped channel is unidirectional, so the resulting stream is read-only **or** write-only — not both.

For a bidirectional `Stream`, see [DuplexStream transit](duplex-stream.md).

## API

```csharp
public sealed class StreamTransit : System.IO.Stream, ITransit
{
    public StreamTransit(IWriteChannel writeChannel);   // write-only stream
    public StreamTransit(IReadChannel readChannel);     // read-only stream

    // From ITransit
    public bool IsReady { get; }
    public bool IsConnected { get; }
    public string? WriteChannelId { get; }
    public string? ReadChannelId { get; }
    public event EventHandler? Ready;
    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public Task WaitForReadyAsync(CancellationToken ct = default);

    // Standard Stream surface: CanRead, CanWrite, ReadAsync, WriteAsync, FlushAsync, DisposeAsync, ...
    // CanSeek is always false.
}
```

## Extension methods

```csharp
public static class StreamTransitExtensions
{
    public static StreamTransit OpenStream(this IStreamMultiplexer mux, string channelId);
    public static StreamTransit OpenStream(this IStreamMultiplexer mux, ChannelOptions options);

    public static StreamTransit AcceptStream(this IStreamMultiplexer mux, string channelId);
    public static Task<StreamTransit> AcceptStreamAsync(
        this IStreamMultiplexer mux,
        string channelId,
        CancellationToken cancellationToken = default);
}
```

| Method | Role | Stream direction |
| --- | --- | --- |
| `OpenStream` | Initiator | Write-only (`CanWrite == true`). |
| `AcceptStream` | Responder | Read-only (`CanRead == true`). |

The channel ID is used verbatim — no `>>` / `<<` suffixes.

## Example — file upload

```csharp
// Sender
await using var stream = mux.OpenStream("upload");
await stream.WaitForReadyAsync();

await using var src = File.OpenRead("data.bin");
await src.CopyToAsync(stream);
```

```csharp
// Receiver
await using var stream = await mux.AcceptStreamAsync("upload");
await using var dst = File.Create("data.bin");
await stream.CopyToAsync(dst);   // returns when sender closes the channel
```

`CopyToAsync` finishes on the receiver when the sender disposes its `StreamTransit` (which closes the channel and arrives as EOF).

## When to use

- Adapting NetConduit to anything that wants a `Stream` (file APIs, compressors, hashing, codecs).
- Avoiding extra framing when you already have a length-self-describing byte stream.

## Limits

- One-way only. Use [`DuplexStreamTransit`](duplex-stream.md) for `SslStream`-style consumers.
- No seek. `Position` and `Length` are unsupported.
- Reads return as soon as data is available — they may return fewer bytes than the buffer holds. Loop, or use `Stream.CopyToAsync`.
