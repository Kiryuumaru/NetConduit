# Documentation

NetConduit is a transport-agnostic stream multiplexer for .NET. It carries any number of independent virtual channels over a single bidirectional stream and ships with transports (TCP, WebSocket, UDP, IPC, QUIC) and transits (Stream, DuplexStream, Message, DeltaMessage) you can mix and match.

## Start here

- [Getting started](getting-started.md) — install, write your first server and client.
- [Scope](concepts/scope.md) — what NetConduit does and deliberately does not do.
- [Packages](packages.md) — what each NuGet package contains and depends on.

## Learn the model

- [Concepts](concepts/index.md) — multiplexer lifecycle, channels, transports, transits, framing, backpressure, reconnection, events, AOT.

## Multi-hop routing

- [Mesh multiplexer](concepts/mesh.md) — routed overlay on top of `StreamMultiplexer`. Open a full multiplexer to any reachable node through intermediate neighbors.

## Pick a transport

- [Transports overview](transports/index.md) — comparison table and selection guide.
- [TCP](transports/tcp.md), [WebSocket](transports/websocket.md), [UDP](transports/udp.md), [IPC](transports/ipc.md), [QUIC](transports/quic.md).

## Pick a transit

- [Transits overview](transits/index.md) — when each one is useful.
- [Stream](transits/stream.md), [DuplexStream](transits/duplex-stream.md), [Message](transits/message.md), [DeltaMessage](transits/delta-message.md).

## API

- [API reference](api/index.md) — every public type, member, and option.

## Examples and numbers

- [Samples](samples/index.md) — what the runnable samples in `samples/` demonstrate.
- [Benchmarks](benchmarks.md) — how to run the benchmark suite.
