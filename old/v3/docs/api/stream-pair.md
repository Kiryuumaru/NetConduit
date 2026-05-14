# StreamPair

Wraps transport streams for the multiplexer. Implements `IStreamPair`.

## IStreamPair Interface

```csharp
public interface IStreamPair : IAsyncDisposable
{
    Stream ReadStream { get; }
    Stream WriteStream { get; }
}
```

## Constructors

```csharp
// Single bidirectional stream (e.g., NetworkStream)
var pair = new StreamPair(stream);

// Split read/write streams
var pair = new StreamPair(readStream, writeStream);

// With IAsyncDisposable owner (disposed when StreamPair is disposed)
var pair = new StreamPair(stream, tcpClient);

// With IDisposable owner
var pair = new StreamPair(stream, disposableOwner);

// Split streams with owner
var pair = new StreamPair(readStream, writeStream, owner);
```

## Usage in StreamFactory

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("localhost", 5000, ct);
        // TcpClient is disposed when StreamPair is disposed
        return new StreamPair(tcp.GetStream(), tcp);
    }
};
```

## Owner Disposal

The optional `owner` parameter allows you to tie the lifetime of the transport connection to the stream pair:

```csharp
// TcpClient disposed automatically when multiplexer reconnects or shuts down
new StreamPair(tcpClient.GetStream(), tcpClient);

// WebSocket disposed automatically
new StreamPair(webSocketStream, webSocket);
```
