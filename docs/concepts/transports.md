# Transports

A **transport** supplies the one bidirectional stream that the multiplexer rides on top of. The contract is `IStreamPair`:

```csharp
public interface IStreamPair : IAsyncDisposable
{
    Stream ReadStream { get; }
    Stream WriteStream { get; }
}
```

`ReadStream` and `WriteStream` may be the same `Stream` instance (duplex) or two separate `Stream`s (one for each direction).

## The `StreamFactoryDelegate`

The multiplexer never opens a transport itself. It calls your factory:

```csharp
public delegate Task<IStreamPair> StreamFactoryDelegate(CancellationToken ct);
```

Set it on `MultiplexerOptions.StreamFactory`. The factory is called:

- Once on initial connect.
- Once per reconnect attempt, if reconnection is configured.

The cancellation token enforces `MultiplexerOptions.ConnectionTimeout` per attempt.

## Provided transports

You normally do not write a `StreamFactoryDelegate` by hand. The transport packages provide static helpers that build a `MultiplexerOptions` with a sensible factory:

```csharp
// Client
MultiplexerOptions opts = TcpMultiplexer.CreateOptions("127.0.0.1", 5000);

// Server
MultiplexerOptions opts = TcpMultiplexer.CreateServerOptions(listener);
```

See the per-transport pages:

- [TCP](../transports/tcp.md) — `TcpMultiplexer`.
- [WebSocket](../transports/websocket.md) — `WebSocketMultiplexer`.
- [UDP](../transports/udp.md) — `UdpMultiplexer`.
- [IPC](../transports/ipc.md) — `IpcMultiplexer`.
- [QUIC](../transports/quic.md) — `QuicMultiplexer`.

## Client vs. server factories

The provided transports come in two flavors:

| Factory style | Behavior |
| --- | --- |
| `CreateOptions(...)` | Each call to the factory opens a **new** connection. Supports reconnection. |
| `CreateServerOptions(...)` | The factory accepts **one** connection from the supplied listener / socket. Subsequent calls throw, so this multiplexer cannot reconnect. |

For a server that needs to survive reconnects, write your own factory that re-accepts from a listener. The [Reconnection — Server-side reconnection](reconnection.md#server-side-reconnection) section has copy-paste snippets for TCP, QUIC, and WebSocket, plus notes on UDP and IPC. The [Scoreboard sample](../../samples/ScoreboardSample/README.md) is a working end-to-end example.

## Writing a custom transport

Any bidirectional `Stream` (or pair of streams) works. To plug in your own transport:

1. Build an `IStreamPair`. The simplest path is `new StreamPair(myStream)` or `new StreamPair(readStream, writeStream)`. Pass an `IDisposable` or `IAsyncDisposable` owner if there's an outer resource (e.g. a `TcpClient`) that needs to be disposed when the multiplexer tears down.
2. Implement `StreamFactoryDelegate` returning that `IStreamPair`.
3. Assign it to `MultiplexerOptions.StreamFactory`.

```csharp
MultiplexerOptions opts = new()
{
    StreamFactory = async ct =>
    {
        var client = new MyCustomClient();
        await client.ConnectAsync(ct);
        return new StreamPair(client.GetStream(), owner: client);
    },
};
```

See [API: `StreamPair`](../api/stream-pair.md) for the constructor matrix.

## Choosing a transport

| Need | Pick |
| --- | --- |
| General LAN/WAN connectivity | TCP |
| Browser clients or strict HTTP environments | WebSocket |
| Same-machine, lowest latency | IPC |
| TCP unavailable / lossy or telemetry-style links | UDP |
| Modern stack with TLS 1.3 + multiplexing | QUIC |

See [Transports overview](../transports/index.md) for the full comparison.
