# API Reference

Quick reference for NetConduit classes and options.

## Core Types

| Type | Description |
|------|-------------|
| [MultiplexerOptions](multiplexer-options.md) | Configuration for stream multiplexer |
| [ChannelOptions](channel-options.md) | Configuration for individual channels |
| [Statistics](statistics.md) | Runtime statistics for mux and channels |

## Class Overview

### StreamMultiplexer

The main entry point:

```csharp
// Create
var mux = StreamMultiplexer.Create(options);

// Lifecycle
Task Start(CancellationToken ct = default);
Task WaitForReadyAsync(CancellationToken ct = default);
ValueTask DisposeAsync();

// Channels
Task<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken ct = default);
Task<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken ct = default);
IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken ct = default);

// Properties
MultiplexerState State { get; }
DisconnectReason? DisconnectReason { get; }
MultiplexerStats Stats { get; }
string SessionId { get; }

// Events
event Action<DisconnectReason, Exception?> OnDisconnected;
event Action OnReconnecting;
event Action OnReconnected;
event Action<Exception> OnReconnectFailed;
```

### WriteChannel

Channel for sending data:

```csharp
// Stream operations
ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
ValueTask FlushAsync(CancellationToken ct = default);

// Properties
string ChannelId { get; }
ChannelState State { get; }
ChannelCloseReason? CloseReason { get; }
ChannelStats Stats { get; }

// Events
event Action<ChannelCloseReason, Exception?> OnClosed;
event Action OnCreditStarvation;
event Action<TimeSpan> OnCreditRestored;
```

### ReadChannel

Channel for receiving data:

```csharp
// Stream operations
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

// Properties
string ChannelId { get; }
ChannelState State { get; }
ChannelCloseReason? CloseReason { get; }
ChannelStats Stats { get; }

// Events
event Action<ChannelCloseReason, Exception?> OnClosed;
```

## Enums

### MultiplexerState

```csharp
public enum MultiplexerState
{
    Created,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Disposed
}
```

### ChannelState

```csharp
public enum ChannelState
{
    Open,
    Closing,
    Closed
}
```

### DisconnectReason

```csharp
public enum DisconnectReason
{
    TransportError,
    PingTimeout,
    GoAwayReceived,
    LocalDispose,
    ProtocolError
}
```

### ChannelCloseReason

```csharp
public enum ChannelCloseReason
{
    LocalClose,
    RemoteFin,
    RemoteError,
    TransportFailed,
    MuxDisposed
}
```

### ChannelPriority

```csharp
public static class ChannelPriority
{
    public const byte Lowest = 0;
    public const byte Low = 64;
    public const byte Normal = 128;
    public const byte High = 192;
    public const byte Highest = 255;
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
MultiplexerOptions CreateOptions(string host, int port);
MultiplexerOptions CreateServerOptions(TcpListener listener);
```

### WebSocketMultiplexer

```csharp
MultiplexerOptions CreateOptions(string uri);
MultiplexerOptions CreateServerOptions(WebSocket webSocket);
```

### UdpMultiplexer

```csharp
MultiplexerOptions CreateOptions(string host, int port);
MultiplexerOptions CreateServerOptions(int port);
```

### IpcMultiplexer

```csharp
MultiplexerOptions CreateOptions(string pipeName);
MultiplexerOptions CreateServerOptions(string pipeName);
```

### QuicMultiplexer

```csharp
MultiplexerOptions CreateOptions(string host, int port, bool allowInsecure = false);
MultiplexerOptions CreateServerOptions(QuicListener listener);
Task<QuicListener> ListenAsync(IPEndPoint endpoint, X509Certificate2 cert);
```
