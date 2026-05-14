# `IStreamPair` / `StreamPair` / `StreamFactoryDelegate`

A `StreamPair` is the transport NetConduit multiplexes over. Transport packages each ship their own factories that produce one of these; you only deal with them directly when writing a custom transport.

Namespace: `NetConduit.Interfaces` and `NetConduit`.

## `IStreamPair`

```csharp
public interface IStreamPair : IAsyncDisposable
{
    Stream ReadStream  { get; }
    Stream WriteStream { get; }
}
```

The mux reads framed bytes from `ReadStream` and writes them to `WriteStream`. The two may be the same `Stream` instance (most transports) or distinct (proxies, pipes).

Disposing the pair must close the underlying transport.

## `StreamPair`

```csharp
public sealed class StreamPair : IStreamPair
{
    public StreamPair(Stream readStream, Stream writeStream, IAsyncDisposable? owner = null);
    public StreamPair(Stream stream, IAsyncDisposable? owner = null);
    public StreamPair(Stream readStream, Stream writeStream, IDisposable owner);
    public StreamPair(Stream stream, IDisposable owner);

    public Stream ReadStream  { get; }
    public Stream WriteStream { get; }

    public ValueTask DisposeAsync();
}
```

The optional `owner` is disposed when the pair is disposed. Use this to tie the lifetime of a `Socket`, `WebSocket`, or `NamedPipeServerStream` to the pair.

When `readStream` and `writeStream` are the same instance, `DisposeAsync` disposes it once.

## `StreamFactoryDelegate`

```csharp
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken cancellationToken);
```

A factory used by `MultiplexerOptions.StreamFactory`. Each invocation must produce a **fresh** transport — the mux re-invokes it on reconnect attempts.

## Custom transport example

```csharp
StreamFactoryDelegate factory = async ct =>
{
    var client = new TcpClient();
    await client.ConnectAsync("example.com", 9000, ct);
    var net = client.GetStream();
    return new StreamPair(net, owner: client);
};

await using var mux = StreamMultiplexer.Create(new MultiplexerOptions
{
    StreamFactory = factory,
});
mux.Start();
await mux.WaitForReadyAsync();
```

In practice, prefer the ready-made transports (`TcpMultiplexer`, etc.) which configure `StreamFactory`, role-specific channel indices, and connection management correctly.
