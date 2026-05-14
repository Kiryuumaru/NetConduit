# Enums

All enums live in `NetConduit.Enums`.

## `ChannelState`

```csharp
public enum ChannelState : byte
{
    Opening,    // INIT sent, awaiting ACK
    Open,       // active
    Closing,    // FIN sent, draining
    Closed,     // fully closed
}
```

State machine: `Opening -> Open -> Closing -> Closed`. A channel can also transition `Opening -> Closed` directly if the remote refuses (`ERR` frame) or the transport fails before the handshake.

## `ChannelPriority`

```csharp
public enum ChannelPriority : byte
{
    Lowest  = 0,
    Low     = 64,
    Normal  = 128,   // default
    High    = 192,
    Highest = 255,
}
```

Higher values are served first by the writer loop. See [Priority](../concepts/priority.md).

## `DisconnectReason`

```csharp
public enum DisconnectReason
{
    GoAwayReceived,   // remote initiated graceful shutdown
    TransportError,   // transport stream errored
    LocalDispose,     // we disposed the mux
}
```

Carried by `DisconnectedEventArgs.Reason` and `IStreamMultiplexer.DisconnectReason`.

## `ChannelCloseReason`

```csharp
public enum ChannelCloseReason
{
    LocalClose,        // we called CloseAsync/DisposeAsync
    RemoteFin,         // remote sent FIN
    RemoteError,       // remote sent ERR
    TransportFailed,   // transport died and reconnect exhausted (or disabled)
    MuxDisposed,       // parent multiplexer was disposed
}
```

Carried by `ChannelCloseEventArgs.Reason`, `IWriteChannel.CloseReason`, `IReadChannel.CloseReason`, and `ChannelClosedException.CloseReason`.

## `ErrorCode`

```csharp
public enum ErrorCode : ushort
{
    None             = 0x0000,
    UnknownChannel   = 0x0001,
    ChannelExists    = 0x0002,
    ProtocolError    = 0x0003,
    FlowControlError = 0x0004,
    Timeout          = 0x0005,
    Internal         = 0x0006,
    Refused          = 0x0007,
    Cancel           = 0x0008,
    SessionMismatch  = 0x0009,
}
```

Codes appear in `ERR` frames on the wire and in `MultiplexerException.ErrorCode`. See [Framing protocol](../concepts/framing-protocol.md#err-frame).

## `DeltaOp`

```csharp
public enum DeltaOp : byte
{
    Set          = 0,    // add or update property
    Remove       = 1,    // remove property
    SetNull      = 2,    // set property to null
    ArrayInsert  = 11,   // insert at array index
    ArrayRemove  = 12,   // remove at array index
    ArrayReplace = 14,   // replace whole array
}
```

Used internally by [`DeltaMessageTransit`](../transits/delta-message.md).

## `FrameFlags`

```csharp
public enum FrameFlags : byte
{
    Data = 0x00,   // regular payload
    Init = 0x01,   // open channel
    Fin  = 0x02,   // graceful close
    Ack  = 0x03,   // flow-control credit
    Err  = 0x04,   // error
    Ping = 0x05,   // keepalive request
    Pong = 0x06,   // keepalive reply
    Ctrl = 0x07,   // control subframe (GoAway, Reconnect, ReconnectAck)
}
```

See [Framing protocol](../concepts/framing-protocol.md) for the on-wire layout.
