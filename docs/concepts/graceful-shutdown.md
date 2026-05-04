# Graceful Shutdown

The GoAway protocol for clean multiplexer shutdown. See [Concepts Overview](index.md) for related concepts.

## How It Works

`GoAwayAsync` signals the remote side that no new channels will be opened:

1. Local side sends GoAway frame
2. Remote side receives GoAway, stops accepting new channels
3. Existing channels continue until they close naturally or timeout
4. After all channels close (or `GoAwayTimeout` expires), the multiplexer shuts down

## Usage

```csharp
// Initiate graceful shutdown
await mux.GoAwayAsync(cancellationToken);

// Then dispose
await mux.DisposeAsync();
```

Or just dispose (sends GoAway automatically):

```csharp
await mux.DisposeAsync();
```

## GoAway Timeout

Configure how long to wait for channels to drain:

```csharp
var options = new MultiplexerOptions
{
    StreamFactory = ...,
    GoAwayTimeout = TimeSpan.FromSeconds(30)  // Default: 30s
};
```

After the timeout, remaining channels are forcefully closed.

## Detecting Shutdown

```csharp
// Check if shutdown is in progress
if (mux.IsShuttingDown)
{
    // Don't try to open new channels
}

// Disconnect event fires when shutdown completes
mux.OnDisconnected += (reason, ex) =>
{
    if (reason == DisconnectReason.GoAwayReceived)
        Console.WriteLine("Remote initiated shutdown");
};
```

## Disconnect Reasons

| Reason | Description |
|--------|-------------|
| `GoAwayReceived` | Remote side sent GoAway |
| `TransportError` | Transport failed |
| `LocalDispose` | Local DisposeAsync called |
