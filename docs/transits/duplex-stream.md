# DuplexStreamTransit

A bidirectional `Stream` over two paired channels. Acts like a virtual TCP connection. See [Transit Overview](index.md) for alternatives.

For one-way streaming, use [StreamTransit](stream.md).

## Basic Usage

```csharp
using NetConduit.Transits;

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
