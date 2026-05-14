# Framing protocol

NetConduit speaks a simple binary frame protocol over the transport stream. You don't need to know this to use the library — read this only if you're implementing a non-.NET peer, instrumenting the wire, or debugging at the byte level.

## Frame layout

Every frame starts with an 8-byte fixed header followed by an optional payload:

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|        channel index (u16)    |   flags (u8)  |  reserved (u8)|
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       payload length (u32, big-endian)         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                              payload                          ...
```

Numeric fields are big-endian. Payload length is the number of bytes that follow the header.

Limits (internal `FrameConstants` in `NetConduit.Constants`):

| Constant | Value | Meaning |
| --- | --- | --- |
| `HeaderSize` | 8 | Bytes in the fixed header. |
| `MaxFramePayloadSize` | 16 MiB | Hard cap on a single frame's payload. |
| `DefaultSlabSize` | 1 MiB | Default per-channel buffer (and typical frame size). |

## Frame types (`flags` byte)

`flags` selects the frame type:

| Value | Name | Payload |
| --- | --- | --- |
| `0x00` | `Data` | Channel payload bytes. |
| `0x01` | `Init` | Open channel. Payload is the UTF-8 channel ID. |
| `0x02` | `Fin` | Close channel gracefully. No payload. |
| `0x03` | `Ack` | Acknowledge received bytes. Payload is a 4-byte position. |
| `0x04` | `Err` | Channel error. Payload is `errorCode (u16)` + UTF-8 message. |
| `0x05` | `Ping` | Keepalive request. Payload is an 8-byte timestamp. |
| `0x06` | `Pong` | Keepalive response. Payload echoes the timestamp. |
| `0x07` | `Ctrl` | Control subframe — see below. |

## Channel indices

- `0x0000` is the **control channel** for session-level frames (GoAway, Reconnect handshake).
- `0x0001`..`0xFFFE` are user channels.
- `0xFFFF` is reserved.

Channel **indices** (u16) are an internal wire detail. Your code uses channel **IDs** (strings). The multiplexer assigns indices when you `OpenChannel` and announces the (id, index) mapping in an `Init` frame.

## Control subtypes

`Ctrl` frames carry a 1-byte subtype followed by subtype-specific payload:

| Value | Subtype | Purpose |
| --- | --- | --- |
| `0x01` | `GoAway` | Graceful shutdown signal. |
| `0x02` | `Reconnect` | Resume an interrupted session (carries last-acked positions). |
| `0x03` | `ReconnectAck` | Reply to a `Reconnect` confirming the resume. |

## Handshake

On connect, both peers exchange a session identification frame on the control channel. After exchange:

- `RemoteSessionId` is populated.
- `IsReady` becomes `true`.
- The `Ready` event fires (once).

On reconnect, the `Ctrl/Reconnect` exchange resumes the prior session if both sides still hold matching state.

## Flow control

`Ack` frames carry a per-channel cumulative byte position. The sender uses these to advance the read pointer of the channel's slab and free buffer space. See [Backpressure](backpressure.md).

## Errors

| `ErrorCode` | Value | Meaning |
| --- | --- | --- |
| `None` | `0x0000` | Reserved. |
| `UnknownChannel` | `0x0001` | A frame referenced an unknown channel index. |
| `ChannelExists` | `0x0002` | `Init` for an index already in use. |
| `ProtocolError` | `0x0003` | Bad header or unexpected frame type. |
| `FlowControlError` | `0x0004` | Flow-control window exceeded. |
| `Timeout` | `0x0005` | Operation timed out. |
| `Internal` | `0x0006` | Internal multiplexer error. |
| `Refused` | `0x0007` | Channel open refused. |
| `Cancel` | `0x0008` | Operation cancelled. |
| `SessionMismatch` | `0x0009` | Reconnect handshake mismatch. |
