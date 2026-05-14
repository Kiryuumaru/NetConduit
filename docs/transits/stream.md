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
# StreamTransit

A one-way `Stream` wrapper for a single channel. Use when you need standard `Stream` compatibility for unidirectional data flow. See [Transit Overview](index.md) for alternatives.

For bidirectional streaming, use [DuplexStreamTransit](duplex-stream.md).

## Basic Usage

```csharp
using NetConduit.Transit.Stream;

// Write-only (sender)
var writeStream = mux.OpenStream("file-upload");
await writeStream.WriteAsync(fileData);
await writeStream.DisposeAsync();

// Read-only (receiver)
await using var readStream = await mux.AcceptStreamAsync("file-upload");
await readStream.CopyToAsync(outputFile);
```

## Stream Compatibility

StreamTransit inherits from `Stream`, so it works with all standard .NET stream APIs:

```csharp
var stream = mux.OpenStream("data");

// StreamWriter/Reader
using var writer = new StreamWriter(stream, leaveOpen: true);
await writer.WriteLineAsync("Hello!");

// CopyToAsync
await sourceFile.CopyToAsync(stream);

// BinaryWriter/Reader
using var binary = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
binary.Write(42);
binary.Write("text");
```

## Properties

| Property         | Type      | Description                                 |
| ---------------- | --------- | ------------------------------------------- |
| `IsConnected`    | `bool`    | True if underlying channel is open          |
| `WriteChannelId` | `string?` | ID of the write channel (null if read-only) |
| `ReadChannelId`  | `string?` | ID of the read channel (null if write-only) |
| `CanRead`        | `bool`    | True if constructed with a ReadChannel      |
| `CanWrite`       | `bool`    | True if constructed with a WriteChannel     |
| `CanSeek`        | `bool`    | Always false                                |

## Direct Construction

```csharp
// Write-only
var writeChannel = mux.OpenChannel("upload");
var stream = new StreamTransit(writeChannel);

// Read-only
var readChannel = await mux.AcceptChannelAsync("upload");
var stream = new StreamTransit(readChannel);
```

## API (Extension Methods)

| Method              | Signature                                                             | Description                           |
| ------------------- | --------------------------------------------------------------------- | ------------------------------------- |
| `OpenStream`        | `mux.OpenStream(string channelId)`                                    | Create write-only stream              |
| `OpenStream`        | `mux.OpenStream(ChannelOptions options)`                              | Create write-only stream with options |
| `AcceptStreamAsync` | `await mux.AcceptStreamAsync(string channelId, CancellationToken ct)` | Accept read-only stream               |
