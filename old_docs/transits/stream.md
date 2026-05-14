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
