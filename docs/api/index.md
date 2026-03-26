# API Reference

Quick reference for NetConduit classes and options. See [Getting Started](../getting-started.md) for usage examples.

## Core Types

| Type | Description |
|------|-------------|
| [MultiplexerOptions](multiplexer-options.md) | Configuration for stream multiplexer |
| [ChannelOptions](channel-options.md) | Configuration for individual channels |
| [Statistics](statistics.md) | Runtime statistics for mux and channels |

## Class Overview

### StreamMultiplexer

The main entry point. Implements `IStreamMultiplexer`.

```csharp
// Create
var mux = StreamMultiplexer.Create(options);

// Lifecycle
Task Start(CancellationToken ct = default);
Task WaitForReadyAsync(CancellationToken ct = default);
ValueTask GoAwayAsync(CancellationToken ct = default);
ValueTask DisposeAsync();

// Channels
ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken ct = default);
ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken ct = default);
IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken ct = default);
WriteChannel? GetWriteChannel(string channelId);
ReadChannel? GetReadChannel(string channelId);

// Properties
bool IsConnected { get; }
bool IsRunning { get; }
bool IsShuttingDown { get; }
Guid SessionId { get; }
Guid RemoteSessionId { get; }
DisconnectReason? DisconnectReason { get; }
MultiplexerStats Stats { get; }
MultiplexerOptions Options { get; }
IReadOnlyCollection<string> ActiveChannelIds { get; }
IReadOnlyCollection<string> OpenedChannelIds { get; }
IReadOnlyCollection<string> AcceptedChannelIds { get; }
int ActiveChannelCount { get; }

// Events
event Action<string>? OnChannelOpened;
event Action<string, Exception?>? OnChannelClosed;
event Action<Exception>? OnError;
event Action<DisconnectReason, Exception?>? OnDisconnected;
event Action<AutoReconnectEventArgs>? OnAutoReconnecting;
event Action<Exception>? OnAutoReconnectFailed;
```

### WriteChannel

Channel for sending data. Inherits from `Stream`.

```csharp
// Stream operations
ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
ValueTask FlushAsync(CancellationToken ct = default);
ValueTask CloseAsync();

// Properties
string ChannelId { get; }
ChannelState State { get; }
ChannelPriority Priority { get; }
long AvailableCredits { get; }
ChannelCloseReason? CloseReason { get; }
Exception? CloseException { get; }
ChannelStats Stats { get; }

// Events
event Action<ChannelCloseReason, Exception?>? OnClosed;
event Action? OnCreditStarvation;
event Action<TimeSpan>? OnCreditRestored;
```

### ReadChannel

Channel for receiving data. Inherits from `Stream`.

```csharp
// Stream operations
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

// Properties
string ChannelId { get; }
ChannelState State { get; }
ChannelPriority Priority { get; }
long CurrentWindowSize { get; }
ChannelCloseReason? CloseReason { get; }
Exception? CloseException { get; }
ChannelStats Stats { get; }

// Events
event Action<ChannelCloseReason, Exception?>? OnClosed;
```

## Enums

### ChannelState

```csharp
public enum ChannelState : byte
{
    Opening,    // Waiting for ACK
    Open,       // Ready for data transfer
    Closing,    // FIN sent or received
    Closed      // Fully closed
}
```

### DisconnectReason

```csharp
public enum DisconnectReason
{
    GoAwayReceived,  // Remote sent GOAWAY (graceful shutdown)
    TransportError,  // Underlying stream error (network failure)
    LocalDispose     // Local DisposeAsync() called
}
```

### ChannelCloseReason

```csharp
public enum ChannelCloseReason
{
    LocalClose,       // Local side closed gracefully
    RemoteFin,        // Remote sent FIN (graceful close)
    RemoteError,      // Remote sent error frame
    TransportFailed,  // Underlying transport failed
    MuxDisposed       // Multiplexer was disposed
}
```

### ChannelPriority

```csharp
public enum ChannelPriority : byte
{
    Lowest  = 0,
    Low     = 64,
    Normal  = 128,
    High    = 192,
    Highest = 255
}
```

### FlushMode

```csharp
public enum FlushMode
{
    Immediate,  // Flush after every frame
    Batched,    // Flush periodically based on FlushInterval
    Manual      // Never explicitly flush, rely on stream buffering
}
```

## Transit Types

| Type | Description |
|------|-------------|
| `MessageTransit<TSend, TReceive>` | JSON message send/receive |
| `DeltaTransit<T>` | Delta-based state sync |
| `DuplexStreamTransit` | Bidirectional stream |
| `StreamTransit` | One-way stream wrapper |

## Extension Methods

All in `NetConduit.Transits` namespace:

```csharp
// Message Transit
Task<MessageTransit<T, T>> OpenMessageTransitAsync<T>(channelId, typeInfo, ...)
Task<MessageTransit<T, T>> AcceptMessageTransitAsync<T>(channelId, typeInfo, ...)
Task<MessageTransit<TSend, TReceive>> OpenMessageTransitAsync<TSend, TReceive>(...)
Task<MessageTransit<TSend, object>> OpenSendOnlyMessageTransitAsync<TSend>(...)
Task<MessageTransit<object, TReceive>> AcceptReceiveOnlyMessageTransitAsync<TReceive>(...)

// Delta Transit
Task<DeltaTransit<T>> OpenDeltaTransitAsync<T>(channelId, typeInfo, ...)
Task<DeltaTransit<T>> AcceptDeltaTransitAsync<T>(channelId, typeInfo, ...)
Task<DeltaTransit<T>> OpenSendOnlyDeltaTransitAsync<T>(...)
Task<DeltaTransit<T>> AcceptReceiveOnlyDeltaTransitAsync<T>(...)

// Duplex Stream
Task<DuplexStreamTransit> OpenDuplexStreamAsync(channelId, ...)
Task<DuplexStreamTransit> AcceptDuplexStreamAsync(channelId, ...)

// Stream
Task<StreamTransit> OpenStreamAsync(channelId, ...)
Task<StreamTransit> AcceptStreamAsync(channelId, ...)
```

## Transport Helpers

### TcpMultiplexer

```csharp
MultiplexerOptions CreateOptions(string host, int port, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateOptions(IPEndPoint endpoint, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateServerOptions(TcpListener listener, Action<MultiplexerOptions>? configure = null);
```

### WebSocketMultiplexer

```csharp
MultiplexerOptions CreateOptions(Uri uri, Action<ClientWebSocketOptions>? clientOptions = null, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateOptions(string url, Action<ClientWebSocketOptions>? clientOptions = null, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateServerOptions(WebSocket webSocket, Action<MultiplexerOptions>? configure = null);
```

### UdpMultiplexer

```csharp
MultiplexerOptions CreateOptions(string host, int port, ReliableUdpOptions? udpOptions = null, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateServerOptions(int listenPort, ReliableUdpOptions? udpOptions = null, Action<MultiplexerOptions>? configure = null);
```

### IpcMultiplexer

```csharp
MultiplexerOptions CreateOptions(string endpoint, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateServerOptions(string endpoint, Action<MultiplexerOptions>? configure = null);
```

### QuicMultiplexer

```csharp
MultiplexerOptions CreateOptions(string host, int port, string? alpn = null, bool allowInsecure = true, Action<MultiplexerOptions>? configure = null);
MultiplexerOptions CreateServerOptions(QuicListener listener, Action<MultiplexerOptions>? configure = null);
Task<QuicListener> ListenAsync(IPEndPoint endPoint, X509Certificate2 certificate, string? alpn = null, CancellationToken ct = default);
```
