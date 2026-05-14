# GroupChatSample

Multi-client broadcast chat. Each client opens a `MessageTransit` channel pair to the server; the server fans out incoming messages to all other clients. Picks **TCP or WebSocket** at the command line so you can compare them.

## What it shows

- One server multiplexer per accepted client.
- `MessageTransit<ChatMessage, ChatMessage>` over both TCP and WebSocket transports.
- Broadcast pattern: server forwards each received message to all peers.
- AOT-safe JSON via `JsonSerializerContext`.

## Topology

```
                  +-----------+
   client A <---->|           |<----> client B
                  |  server   |
   client C <---->|           |<----> client D
                  +-----------+
```

## Run

TCP server + client:

```powershell
dotnet run --project samples/GroupChatSample -- server tcp 5000
dotnet run --project samples/GroupChatSample -- client tcp 5000 127.0.0.1 Alice
dotnet run --project samples/GroupChatSample -- client tcp 5000 127.0.0.1 Bob
```

WebSocket server + client:

```powershell
dotnet run --project samples/GroupChatSample -- server ws 5000/chat
dotnet run --project samples/GroupChatSample -- client ws 5000/chat 127.0.0.1 Alice
```

| Arg | Meaning |
| --- | --- |
| 1 | `server` or `client` |
| 2 | `tcp` or `ws` |
| 3 | `port` (tcp) or `port/path` (ws) |
| 4 | host (client only) |
| 5 | display name (client only) |

## Key code shape

```csharp
// Server, per accepted connection
var transit = await mux.AcceptMessageTransitAsync(
    "chat", ChatJson.Default.ChatMessage);

await foreach (var msg in transit.ReceiveAllAsync())
{
    foreach (var peer in connectedPeers)
        await peer.SendAsync(msg);
}
```

```csharp
// Client
var transit = await mux.OpenMessageTransitAsync(
    "chat", ChatJson.Default.ChatMessage);

_ = Task.Run(async () => {
    await foreach (var m in transit.ReceiveAllAsync())
        Console.WriteLine($"{m.From}: {m.Text}");
});

while ((line = Console.ReadLine()) is not null)
    await transit.SendAsync(new ChatMessage(myName, line));
```
