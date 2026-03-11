# MessageTransit

Send and receive JSON-serialized messages. Ideal for RPC, events, and command patterns.

## Basic Usage

```csharp
using NetConduit.Transits;
using System.Text.Json.Serialization;

// Define message type
public record ChatMessage(string User, string Text);

// Source-generated serialization (Native AOT compatible)
[JsonSerializable(typeof(ChatMessage))]
public partial class ChatContext : JsonSerializerContext { }

// Create transit (same type for send/receive)
var transit = await mux.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Send
await transit.SendAsync(new ChatMessage("Alice", "Hello!"));

// Receive
var msg = await transit.ReceiveAsync();
Console.WriteLine($"{msg.User}: {msg.Text}");

// Cleanup
await transit.DisposeAsync();
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

**The cleanest pattern for message loops** - enumerate all messages as an async stream:

```csharp
await foreach (var message in transit.ReceiveAllAsync(cancellationToken))
{
    Console.WriteLine($"{message.User}: {message.Text}");
}
// Loop exits when channel closes or cancellation requested
```

This is the recommended way to process messages. It's equivalent to:
```csharp
while (!cancellationToken.IsCancellationRequested)
{
    var message = await transit.ReceiveAsync(cancellationToken);
    if (message is null) break;
    // handle message
}
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | True if transit has open channels |
| `WriteChannelId` | `string?` | ID of the write channel (null if receive-only) |
| `ReadChannelId` | `string?` | ID of the read channel (null if send-only) |

## Different Send/Receive Types

For RPC-style request/response:

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

## Open vs Accept

Pairing transits on two sides:

```csharp
// Side A opens
var transitA = await muxA.OpenMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Side B accepts (pairs with A's open)
var transitB = await muxB.AcceptMessageTransitAsync("chat", ChatContext.Default.ChatMessage);

// Now A and B can communicate bidirectionally
await transitA.SendAsync(msg);
var received = await transitB.ReceiveAsync();
```

## Send-Only / Receive-Only

For one-way communication:

```csharp
// Publisher (send only)
var publisher = await mux.OpenSendOnlyMessageTransitAsync(
    "events", 
    EventContext.Default.Event);
await publisher.SendAsync(new Event("user-joined", data));

// Subscriber (receive only)
var subscriber = await mux.AcceptReceiveOnlyMessageTransitAsync(
    "events",
    EventContext.Default.Event);

// Process all events
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

⚠️ This doesn't work with Native AOT compilation.

## Configuration

### Max Message Size

```csharp
// Limit message size (default: 16MB)
var transit = await mux.OpenMessageTransitAsync(
    "chat",
    ChatContext.Default.ChatMessage, 
    maxMessageSize: 1024 * 1024);  // 1MB
```

### Custom JSON Options

When using reflection-based serialization:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

var transit = await mux.OpenMessageTransitAsync<ChatMessage>(
    "chat",
    jsonOptions: options);
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

## Patterns

### Server Message Loop

```csharp
// Server processing requests
await foreach (var request in serverTransit.ReceiveAllAsync(ct))
{
    var response = await ProcessRequest(request);
    await serverTransit.SendAsync(response);
}
```

### Bidirectional Chat

```csharp
// Read messages in background
_ = Task.Run(async () =>
{
    await foreach (var msg in transit.ReceiveAllAsync(cts.Token))
    {
        Console.WriteLine($"[{msg.User}] {msg.Text}");
    }
});

// Send messages from console input
while (Console.ReadLine() is { } line)
{
    await transit.SendAsync(new ChatMessage(username, line));
}
```

### With Timeout

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    var response = await transit.ReceiveAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timeout waiting for response");
}
```

### Pub-Sub (Broadcast)

```csharp
// Publisher maintains list of subscriber transits
var subscribers = new List<MessageTransit<Event, object>>();

// Broadcast to all
foreach (var sub in subscribers)
    await sub.SendAsync(new Event("update", payload));
```

## Error Handling

```csharp
try
{
    await foreach (var msg in transit.ReceiveAllAsync(ct))
    {
        HandleMessage(msg);
    }
}
catch (OperationCanceledException)
{
    // Cancellation requested
}
catch (ChannelClosedException ex)
{
    // Channel was closed
    Console.WriteLine($"Channel closed: {ex.CloseReason}");
}
catch (JsonException ex)
{
    // Deserialization failed
    Console.WriteLine($"Invalid message: {ex.Message}");
}
```

## Thread Safety

- `SendAsync` is thread-safe (uses internal lock)
- `ReceiveAsync` is thread-safe (uses internal lock)
- Safe to call send and receive concurrently from different tasks

## Tips

**Use records for messages:**
```csharp
public record ChatMessage(string User, string Text, DateTime Timestamp);
```

**Always use `await using` for automatic cleanup:**
```csharp
await using var transit = await mux.OpenMessageTransitAsync("chat", typeInfo);
// transit is automatically disposed when scope exits
```
