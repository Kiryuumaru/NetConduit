# ScoreboardSample

A persistent multi-player leaderboard. Each player submits scores; the server merges them into a shared `Leaderboard` state that is delta-synced back to every connected client. Survives disconnects by reconnecting and replaying.

## What it shows

- A **custom `StreamFactoryDelegate`** that re-accepts (server) or re-dials (client) on every invocation, enabling automatic reconnection.
- `MessageTransit<ScoreSubmission, ScoreSubmission>` for one-way submissions.
- `DeltaMessageTransit<Leaderboard>` for the broadcast state.
- Server-side leaderboard persistence across player sessions.

## Topology

```
                     +-------------+
   player1   ------> |  scores     | (msg)
                     |             |
                     |   server    |     leaderboard (delta)
   player2   <------ |             | --------------------> all players
                     +-------------+
```

## Run

Server (default port 5000):

```powershell
dotnet run --project samples/ScoreboardSample -- server 5000
```

Player:

```powershell
dotnet run --project samples/ScoreboardSample -- player 5000 localhost Alice
```

| Arg | Meaning |
| --- | --- |
| 1 | `server` or `player` |
| 2 | port (default `5000`) |
| 3 | host (player only, default `localhost`) |
| 4 | player name (player only, default `Player###`) |

## Reconnection detail

Both sides set `MaxAutoReconnectAttempts > 0` (server `5`, client `10`) and supply a `StreamFactory` that:

- **Server:** awaits `listener.AcceptTcpClientAsync()` each invocation, returning a fresh `StreamPair` per accept.
- **Client:** opens a new `TcpClient` and connects to the host:port each invocation.

When the transport drops, the multiplexer re-invokes the factory up to the configured number of times. Because `MaxAutoReconnectAttempts > 0`, the **replay buffer is enabled** and channels stay open across reconnects — unacked bytes are re-sent after the new handshake.

> Tip: kill the server while a player is running. After restart, the player auto-reconnects and the leaderboard resumes.

## Key code shape

```csharp
StreamFactoryDelegate factory = async ct =>
{
    var client = new TcpClient();
    await client.ConnectAsync(host, port, ct);
    client.NoDelay = true;
    return new StreamPair(client.GetStream(), owner: client);
};

await using var mux = StreamMultiplexer.Create(new MultiplexerOptions
{
    StreamFactory = factory,
    MaxAutoReconnectAttempts = 10,   // enables replay buffer
});
```
