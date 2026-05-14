# Concepts

Read these in order, or jump to the topic you need.

| Topic | Summary |
| --- | --- |
| [Multiplexer](multiplexer.md) | What `StreamMultiplexer` is, its lifecycle, ready vs. connected, sessions. |
| [Channels](channels.md) | Channel IDs, open vs. accept, state transitions, write/read split. |
| [Transports](transports.md) | The role of `IStreamPair` and the `StreamFactoryDelegate`. |
| [Transits](transits.md) | What a transit is and when to use one. |
| [Framing protocol](framing-protocol.md) | The wire format: 8-byte header, frame types, control subtypes. |
| [Backpressure](backpressure.md) | Slabs, credit-based flow control, `SendTimeout`. |
| [Priority](priority.md) | How writer ordering works across channels. |
| [Heartbeat](heartbeat.md) | `PingInterval`, `PingTimeout`, missed pings. |
| [Reconnection](reconnection.md) | When the multiplexer reconnects, how replay works. |
| [Graceful shutdown](graceful-shutdown.md) | `GoAwayAsync`, drain semantics, dispose order. |
| [Events](events.md) | Event ordering, when each event fires. |
| [Statistics](statistics.md) | `MultiplexerStats` and `ChannelStats`. |
| [AOT and source generators](aot.md) | Trim-safe APIs and JSON source generation. |
