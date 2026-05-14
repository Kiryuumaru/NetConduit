# API Reference

Reference for the surface in `NetConduit.dll`.

## Multiplexer

| Page | Type |
| --- | --- |
| [`IStreamMultiplexer`](stream-multiplexer.md) | Core multiplexer interface. |
| [`StreamMultiplexer`](stream-multiplexer.md#class-streammultiplexer) | Default implementation. |
| [`IStreamPair`](stream-pair.md) | Transport interface. |
| [`StreamPair`](stream-pair.md#class-streampair) | Default implementation. |
| [`StreamFactoryDelegate`](stream-pair.md#streamfactorydelegate) | Delegate signature for connecting. |

## Channels

| Page | Type |
| --- | --- |
| [`IWriteChannel`](write-channel.md) | Outbound channel. |
| [`IReadChannel`](read-channel.md) | Inbound channel. |
| [`StreamMultiplexerExtensions`](extensions.md) | Convenience extensions for opening/accepting. |

## Options

| Page | Type |
| --- | --- |
| [`MultiplexerOptions`](multiplexer-options.md) | Session-level configuration. |
| [`ChannelOptions`](channel-options.md) | Per-channel configuration. |

## Statistics

| Page | Type |
| --- | --- |
| [`MultiplexerStats`](statistics.md#multiplexerstats) | Session counters. |
| [`ChannelStats`](statistics.md#channelstats) | Per-channel counters. |

## Events and enums

| Page | Type |
| --- | --- |
| [Events](events.md) | All `EventArgs` types. |
| [Enums](enums.md) | `ChannelState`, `ChannelPriority`, `DisconnectReason`, `ChannelCloseReason`, `ErrorCode`, `DeltaOp`, `FrameFlags`. |
| [Errors](errors.md) | `ChannelClosedException`, `MultiplexerException`. |

## Transit interfaces

| Page | Type |
| --- | --- |
| [`ITransit`](../transits/index.md#common-shape) | Base for all transits. |
| [`StreamTransit`](../transits/stream.md) | One-way `Stream`. |
| [`DuplexStreamTransit`](../transits/duplex-stream.md) | Two-way `Stream`. |
| [`MessageTransit<TSend,TReceive>`](../transits/message.md) | JSON messages. |
| [`DeltaMessageTransit<T>`](../transits/delta-message.md) | JSON deltas. |
