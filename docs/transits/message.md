# Message transit

Package: [`NetConduit.Transit.Message`](https://www.nuget.org/packages/NetConduit.Transit.Message).

`MessageTransit<TSend, TReceive>` sends and receives discrete typed JSON messages over a channel pair. Each message is preceded by a 4-byte big-endian length prefix so message boundaries survive across reads.

Built on top of the same `>>` / `<<` channel pair convention as [DuplexStreamTransit](duplex-stream.md).

## API

```csharp
public sealed class MessageTransit<TSend, TReceive> : ITransit
{
    // AOT-safe constructor
    public MessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonTypeInfo<TSend>? sendTypeInfo,
        JsonTypeInfo<TReceive>? receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024);

    // Non-AOT convenience
    [RequiresUnreferencedCode("…")]
    [RequiresDynamicCode("…")]
    public MessageTransit(
        IWriteChannel? writeChannel,
        IReadChannel? readChannel,
        JsonSerializerOptions? jsonOptions = null,
        int maxMessageSize = 16 * 1024 * 1024);

    // From ITransit
    public bool IsReady { get; }
    public bool IsConnected { get; }
    public string? WriteChannelId { get; }
    public string? ReadChannelId { get; }
    public event EventHandler? Ready;
    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public Task WaitForReadyAsync(CancellationToken ct = default);

    public ValueTask SendAsync(TSend message, CancellationToken cancellationToken = default);
    public ValueTask<TReceive?> ReceiveAsync(CancellationToken cancellationToken = default);
    public IAsyncEnumerable<TReceive> ReceiveAllAsync(CancellationToken cancellationToken = default);
}
```

- Either `writeChannel` or `readChannel` may be `null` (send-only or receive-only transit).
- `maxMessageSize` rejects oversized frames on both ends. Default 16 MiB (matches `FrameConstants.MaxFramePayloadSize`).
- `ReceiveAsync` returns `default(TReceive?)` on EOF.

## Extension methods

The extensions cover `TSend == TReceive` and the asymmetric case, with sync and async variants. AOT-safe overloads take `JsonTypeInfo<T>`; non-AOT overloads take `JsonSerializerOptions`.

```csharp
public static class MessageTransitExtensions
{
    // Symmetric (T <-> T), AOT-safe
    public static MessageTransit<T,T> OpenMessageTransit<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024);

    public static Task<MessageTransit<T,T>> OpenMessageTransitAsync<T>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<T> typeInfo, int maxMessageSize = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default);

    public static MessageTransit<T,T> AcceptMessageTransit<T>(/* same */);
    public static Task<MessageTransit<T,T>> AcceptMessageTransitAsync<T>(/* same */);

    // Asymmetric (TSend, TReceive), AOT-safe
    public static MessageTransit<TSend,TReceive> OpenMessageTransit<TSend,TReceive>(
        this IStreamMultiplexer mux, string channelId,
        JsonTypeInfo<TSend> sendTypeInfo,
        JsonTypeInfo<TReceive> receiveTypeInfo,
        int maxMessageSize = 16 * 1024 * 1024);
    // + Accept and Async variants

    // JsonSerializerOptions overloads (non-AOT) for all of the above
    // — each marked [RequiresUnreferencedCode] / [RequiresDynamicCode]
}
```

## Example — symmetric chat message

```csharp
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Transit.Message;

public record ChatMessage(string From, string Text);

[JsonSerializable(typeof(ChatMessage))]
internal partial class AppJson : JsonSerializerContext;
```

```csharp
// Both sides
await using var transit = await mux.OpenMessageTransitAsync(
    "chat",
    AppJson.Default.ChatMessage);

await transit.SendAsync(new ChatMessage("Alice", "hello"));

await foreach (var msg in transit.ReceiveAllAsync())
    Console.WriteLine($"{msg.From}: {msg.Text}");
```

The other side uses `AcceptMessageTransitAsync` with the same base ID.

## Example — asymmetric request/response

```csharp
public record Request(string Method, string Arg);
public record Response(bool Ok, string? Result);

[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
internal partial class RpcJson : JsonSerializerContext;
```

```csharp
// Client
await using var t = await mux.OpenMessageTransitAsync<Request, Response>(
    "rpc",
    RpcJson.Default.Request,
    RpcJson.Default.Response);

await t.SendAsync(new Request("ping", ""));
var reply = await t.ReceiveAsync();
```

```csharp
// Server
await using var t = await mux.AcceptMessageTransitAsync<Response, Request>(
    "rpc",
    RpcJson.Default.Response,
    RpcJson.Default.Request);

await foreach (var req in t.ReceiveAllAsync())
{
    await t.SendAsync(new Response(true, Handle(req)));
}
```

(Note the type-argument **order swaps** on the receiver: the server's `TSend` is what the client receives, and vice versa.)

## Threading

`SendAsync` and `ReceiveAsync` are thread-safe. Internally each direction is serialized by a semaphore so concurrent callers don't tear messages.

## Frame format

```
+----------+----------+----------+----------+--------- ... ---------+
|       message length (u32, big-endian)    |    UTF-8 JSON bytes   |
+----------+----------+----------+----------+--------- ... ---------+
```

The 4-byte length is per **message** (not per multiplexer frame). NetConduit's own 12-byte frame header sits underneath and may split or merge messages across multiple `Data` frames.
