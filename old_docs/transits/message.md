# MessageTransit

Send and receive JSON-serialized messages. Ideal for RPC, events, and command patterns. See [Transit Overview](index.md) for alternatives.

## Basic Usage

```csharp
using NetConduit.Transit.Message;
using System.Text.Json.Serialization;

// Define message type
public record ChatMessage(string User, string Text);

// Source-generated serialization (Native AOT compatible)
[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Create transit (same type for send/receive)
await using var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Send
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));

// Receive
var msg = await transit.ReceiveAsync();
Console.WriteLine($"{msg.User}: {msg.Text}");
```

## Methods

### SendAsync

Send a single message:

```csharp
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));
await transit.SendAsync(new ChatMessage("Bob", "Hi there!"));
```

### ReceiveAsync

Receive a single message (blocks until message arrives):

```csharp
var message = await transit.ReceiveAsync(cancellationToken);
if (message is not null)
{
    Console.WriteLine($"{message.User}: {message.Text}");
}
// Returns null when channel closes
```

### ReceiveAllAsync (Recommended)

Enumerate all messages as an async stream:

```csharp
await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    Console.WriteLine($"{message.User}: {message.Text}");
}
// Loop exits when channel closes or cancellation requested
```

## Properties

| Property         | Type      | Description                                    |
| ---------------- | --------- | ---------------------------------------------- |
| `IsConnected`    | `bool`    | True if transit has open channels              |
| `WriteChannelId` | `string?` | ID of the write channel (null if receive-only) |
| `ReadChannelId`  | `string?` | ID of the read channel (null if send-only)     |

## Different Send/Receive Types

For RPC-style request/response with separate types:

```csharp
public record Request(string Method, object[] Args);
public record Response(bool Success, object? Result, string? Error);

[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
public partial class RpcContext : JsonSerializerContext { }

// Client sends Request, receives Response
var clientTransit = await mux.OpenMessageTransitAsync(
    "rpc",
    RpcContext.Default.Request,
    RpcContext.Default.Response);

// Server receives Request, sends Response
var serverTransit = await mux.AcceptMessageTransitAsync(
    "rpc",
    RpcContext.Default.Response,  // Server sends Response
    RpcContext.Default.Request);  // Server receives Request
```

## Send-Only / Receive-Only

For one-way communication:

```csharp
// Publisher (send only)
var publisher = mux.OpenSendOnlyMessageTransit(
    "events",
    EventContext.Default.Event);
await publisher.SendAsync(new Event("user-joined", data));

// Subscriber (receive only)
var subscriber = await mux.AcceptReceiveOnlyMessageTransitAsync(
    "events",
    EventContext.Default.Event);

await foreach (var evt in subscriber.ReceiveAllAsync(ct))
{
    HandleEvent(evt);
}
```

## Without Source Generation (Non-AOT)

For development or when AOT isn't needed:

```csharp
// Uses reflection-based serialization
var transit = await mux.OpenMessageTransitAsync<ChatMessage>("chat");
```

⚠️ Reflection-based overloads are marked `[RequiresUnreferencedCode]` and don't work with Native AOT.

## Configuration

### Max Message Size

```csharp
// Limit message size (default: 16MB)
var transit = await mux.OpenMessageTransitAsync(
    "chat",
    ChatContext.Default.ChatMessage,
    maxMessageSize: 1024 * 1024);  // 1MB
```

### Custom JSON Options (Reflection mode)

```csharp
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

var transit = await mux.OpenMessageTransitAsync<ChatMessage>(
    "chat",
    jsonOptions: jsonOptions);
```

## Direct Construction

For full control, construct `MessageTransit<TSend, TReceive>` directly with raw channels:

```csharp
var writeChannel = mux.OpenChannel("requests");
var readChannel = await mux.AcceptChannelAsync("responses");

var transit = new MessageTransit<Request, Response>(
    writeChannel, readChannel,
    RpcContext.Default.Request,
    RpcContext.Default.Response);
```

## Message Ordering

Messages are guaranteed to arrive in send order (FIFO):

```csharp
// Sender
await transit.SendAsync(msg1);
await transit.SendAsync(msg2);
await transit.SendAsync(msg3);

// Receiver always gets: msg1, msg2, msg3 (in order)
```
