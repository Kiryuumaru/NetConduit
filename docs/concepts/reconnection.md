# Reconnection

Automatic reconnection with channel state recovery. See [Concepts Overview](index.md) for related concepts.

## How It Works

When the transport disconnects, the multiplexer:
1. Detects the disconnection
2. Fires `OnReconnecting` event with attempt number
3. Calls `StreamFactory` to establish a new connection
4. Performs handshake with session ID matching
5. Replays unacknowledged data from the ring buffer
6. Fires `OnConnected` event
7. Channels resume operation transparently

## Configuration

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,

    // Reconnection settings
    MaxAutoReconnectAttempts = 10,              // 0 = unlimited
    AutoReconnectDelay = TimeSpan.FromSeconds(1),
    MaxAutoReconnectDelay = TimeSpan.FromSeconds(30),
    AutoReconnectBackoffMultiplier = 2.0,
    ConnectionTimeout = TimeSpan.FromSeconds(30)
};
```

### Backoff Schedule

With default settings (1s base, 2x multiplier, 30s max):

| Attempt | Delay |
|---------|-------|
| 1 | 1s |
| 2 | 2s |
| 3 | 4s |
| 4 | 8s |
| 5 | 16s |
| 6+ | 30s (capped) |

## Events

```csharp
mux.OnReconnecting += (attempt) =>
{
    Console.WriteLine($"Reconnecting... attempt {attempt}");
};

mux.OnConnected += () =>
{
    Console.WriteLine("Connected!");
};

mux.OnDisconnected += (reason, exception) =>
{
    Console.WriteLine($"Disconnected: {reason}");
};
```

## StreamFactory for Reconnection

The `StreamFactory` delegate is called for each connection attempt:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("server.example.com", 5000, ct);
        return new StreamPair(tcp.GetStream(), tcp);
    },
    MaxAutoReconnectAttempts = 0  // Unlimited retries
};
```

## Session Identity

Each multiplexer has a `SessionId`. During reconnection, the session ID is sent in the handshake to resume the correct session:

```csharp
Console.WriteLine($"Local: {mux.SessionId}");
Console.WriteLine($"Remote: {mux.RemoteSessionId}");
```

## Channel Behavior During Disconnection

- **Writes** block until reconnection succeeds (subject to `SendTimeout`)
- **Reads** block until reconnection succeeds or channel is closed
- **No data is lost** — unacknowledged frames are replayed from the ring buffer
- **Channel state is preserved** — channels remain in their pre-disconnect state

## Disabling Reconnection

For single-shot connections (no reconnection):

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        // Fail on second call to prevent reconnection
        throw new InvalidOperationException("No reconnection");
    }
};
```

Or use a flag:

```csharp
var connected = false;
var options = new MultiplexerOptions
{
    StreamFactory = async (ct) =>
    {
        if (connected) throw new InvalidOperationException("No reconnection");
        connected = true;
        return new StreamPair(stream, owner);
    }
};
```
