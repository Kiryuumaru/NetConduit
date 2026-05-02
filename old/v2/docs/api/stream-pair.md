# StreamPair and DuplexStream

Utility classes for working with bidirectional streams and custom transports.

## StreamPair

`StreamPair` packages a read stream and a write stream together, implementing `IStreamPair`. This is how transports provide their streams to the multiplexer.

```csharp
public interface IStreamPair : IAsyncDisposable
{
    Stream ReadStream { get; }
    Stream WriteStream { get; }
}
```

### Constructors

Create from separate read/write streams:

```csharp
var pair = new StreamPair(readStream, writeStream);
```

Create from a single bidirectional stream:

```csharp
var pair = new StreamPair(networkStream);
```

### Ownership

`StreamPair` can own additional resources that are disposed when the pair is disposed:

```csharp
// Dispose the TcpClient when the pair is disposed
var pair = new StreamPair(tcpClient.GetStream(), tcpClient);

// Dispose multiple resources
var pair = new StreamPair(stream, tcpClient, sslStream, socket);

// IAsyncDisposable owner
var pair = new StreamPair(readStream, writeStream, asyncDisposableOwner);

// IDisposable owner
var pair = new StreamPair(readStream, writeStream, disposableOwner);
```

### Custom Transport Example

Use `StreamPair` to create a multiplexer over any stream:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async ct =>
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("server.example.com", 9000, ct);
        tcpClient.NoDelay = true;
        return new StreamPair(tcpClient.GetStream(), tcpClient);
    }
};

await using var mux = StreamMultiplexer.Create(options);
```

`StreamFactory` is a delegate that the multiplexer calls to create (or recreate) the transport connection:

```csharp
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
```

This is called:
- Once at startup to establish the initial connection
- Again on each reconnection attempt (if auto-reconnect is configured)

## DuplexStream

`DuplexStream` combines a read stream and a write stream into a single `Stream` that supports both reading and writing.

```csharp
var duplex = new DuplexStream(readStream, writeStream);

// Now you can read AND write through a single Stream reference
await duplex.WriteAsync(data);
int read = await duplex.ReadAsync(buffer);
```

### From Channels

Create a `DuplexStream` from a `ReadChannel` and `WriteChannel`:

```csharp
var write = await mux.OpenChannelAsync("data");
var read = await mux.AcceptChannelAsync("response");

var duplex = DuplexStream.FromChannels(read, write, ownsChannels: true);
```

When `ownsChannels` is `true`, disposing the `DuplexStream` also disposes the underlying channels.

### Ownership

```csharp
// DuplexStream does NOT own the underlying streams (default)
var duplex = new DuplexStream(readStream, writeStream);

// DuplexStream owns and disposes the underlying streams
var duplex = new DuplexStream(readStream, writeStream, ownsStreams: true);
```

### Use Case: Wrapping for Libraries

Some libraries require a single `Stream` for bidirectional communication. `DuplexStream` adapts the separate read/write channels:

```csharp
var write = await mux.OpenChannelAsync("protocol");
var read = await mux.AcceptChannelAsync("protocol");

await using var stream = DuplexStream.FromChannels(read, write, ownsChannels: true);

// Pass to a library that expects a single bidirectional Stream
await SomeProtocol.HandleAsync(stream);
```

## See Also

- [StreamMultiplexer](stream-multiplexer.md) — Uses `StreamFactory` to create transports
- [MultiplexerOptions](multiplexer-options.md) — `StreamFactory` configuration
- [Transports](../transports/index.md) — Built-in transport implementations
